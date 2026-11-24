using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.AcademicExport;
using Taslim.Api.AcademicTemplates;
using Taslim.Api.Controllers;
using Taslim.Api.Domain;
using Taslim.Api.Infrastructure;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Issue #1267 integration slice: template library (#1260/#1265) wired to completed research reports
/// (#1259 Phase A persistence). The tests below pin exactly the guarantees that slice promises:
///
///   - tenant isolation: another student cannot see, summarise or render someone else's saved report,
///     and the refusal shape is identical to "missing" so job ids do not leak across tenants;
///   - re-export without any charge: the endpoints create no generation job, no usage transaction and
///     no provider attempt, before or after repeat exports, under any mode;
///   - citations and content preserved; metadata-only rows honestly labelled; the AI-use disclosure
///     never hidden; the produced file is re-opened and inspected, not trusted blindly;
///   - the merged bibliography integrity gate runs on every real export.
/// </summary>
public sealed class AcademicExportIntegrationTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory _factory;

    public AcademicExportIntegrationTests(TaslimApiFactory factory)
    {
        _factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────

    private sealed record Principal(string UserId, Guid WorkspaceId, HttpClient Client);

    private async Task<Principal> RegisterAsync(string label)
    {
        var client = _factory.CreateClient();
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    displayName = label,
                    email = $"academic-export-{Guid.NewGuid():N}@example.com",
                    password = "StrongPassword!123",
                    preferredLanguage = "en",
                }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new Principal(
            body.GetProperty("user").GetProperty("id").GetString()!,
            body.GetProperty("personalWorkspace").GetProperty("id").GetGuid(),
            client);
    }

    private static string ResultJson(string language = "en") => JsonSerializer.Serialize(new
    {
        researchType = "research",
        title = "Sleep Consolidation in Undergraduate Cohorts",
        subtitle = (string?)null,
        language,
        executiveSummary = "Across eleven cohorts, schedule variance tracked recall scores tightly [S1].",
        keyFindings = new object[]
        {
            new { type = "bullets", items = new[] { "Variance predicted recall (r = -0.42)." }, citationIds = new[] { "S1" } },
        },
        sections = new object[]
        {
            new
            {
                heading = "Method",
                blocks = new object[]
                {
                    new { type = "paragraph", text = "Participants were sampled from intact course rosters.", citationIds = new[] { "S2" } },
                    new { type = "paragraph", text = "Recall was scored blind to schedule type.", citationIds = new string[] { } },
                },
            },
            new
            {
                heading = "Results",
                blocks = new object[]
                {
                    new { type = "table", text = (string?)null, rows = new object[] { new { cells = new[] { "Cohort", "Recall" } }, new { cells = new[] { "A", "71%" } } }, citationIds = new[] { "S1", "S3" } },
                },
            },
        },
        conclusion = "Schedule design deserves the same scrutiny as content design.",
        sourceCount = 3,
        sources = Array.Empty<object>(),
    });

    private Guid SeedCompletedResearchJob(Guid workspaceId, Guid createdByUserId, string language = "en")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var jobId = Guid.NewGuid();
        var now = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        db.GenerationJobs.Add(new GenerationJob
        {
            Id = jobId,
            WorkspaceId = workspaceId,
            CreatedByUserId = createdByUserId,
            JobType = "research.generate",
            Status = GenerationJobStatus.Succeeded,
            Title = "Sleep Consolidation in Undergraduate Cohorts",
            InputJson = "{}",
            ResultJson = ResultJson(language),
            CreatedAt = now,
            CompletedAt = now,
        });
        db.ResearchSources.AddRange(
            // S1: a web source whose content was actually fetched.
            new ResearchSource
            {
                Id = Guid.NewGuid(), WorkspaceId = workspaceId, GenerationJobId = jobId,
                CitationId = "S1", SourceType = "web", Rank = 1, RetrievedAt = now, IsSelected = true,
                Title = "Schedule variance and recall", Domain = "journals.example", Publisher = "Example Press",
                Url = "https://journals.example/schedule-recall", CanonicalUrl = "https://journals.example/schedule-recall",
                PublishedAt = new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc),
                Snippet = "Cohort-level variance predicted nightly recall.",
            },
            // S2: catalogue book record — metadata only, never read here.
            new ResearchSource
            {
                Id = Guid.NewGuid(), WorkspaceId = workspaceId, GenerationJobId = jobId,
                CitationId = "S2", SourceType = "web", Rank = 2, RetrievedAt = now, IsSelected = true,
                Title = "Memory and the Academic Calendar", Domain = "catalog.example", Publisher = "University Catalogue",
                Url = "https://catalog.example/memory-calendar",
                PublishedAt = new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Snippet = null, ExtractedText = null,
            },
            // S3: an uploaded workspace file whose text was extracted.
            new ResearchSource
            {
                Id = Guid.NewGuid(), WorkspaceId = workspaceId, GenerationJobId = jobId,
                CitationId = "S3", SourceType = "uploaded", Rank = 3, RetrievedAt = now, IsSelected = true,
                Title = "Course roster notes", Domain = "uploaded file",
                Snippet = "Roster notes excerpt.",
            });
        db.SaveChanges();
        return jobId;
    }

    private static string FirstConfigurationWith(Func<AcademicDesignSpec, bool> predicate) =>
        AcademicTemplateRegistry.Configurations.First(configuration => predicate(configuration.Spec)).ConfigurationId;

    // ── authz / tenant isolation ────────────────────────────────────────────────────────

    [Fact]
    public async Task Anonymous_requests_are_rejected_on_both_routes()
    {
        using var client = _factory.CreateClient();
        var jobId = Guid.NewGuid();
        var summary = await client.GetAsync($"/api/academic-export/jobs/{jobId}/report");
        Assert.Equal(HttpStatusCode.Unauthorized, summary.StatusCode);
        var export = await client.PostAsJsonAsync($"/api/academic-export/jobs/{jobId}/export",
            new { configurationId = "classic-thesis.times.150.classic.center", format = "docx" });
        Assert.False(export.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Cross_tenant_saved_report_is_indistinguishable_from_missing()
    {
        var owner = await RegisterAsync("Export Owner");
        var jobId = SeedCompletedResearchJob(owner.WorkspaceId, Guid.Parse(owner.UserId));
        var intruder = await RegisterAsync("Export Intruder");

        var summary = await intruder.Client.GetAsync($"/api/academic-export/jobs/{jobId}/report");
        Assert.Equal(HttpStatusCode.NotFound, summary.StatusCode);
        var summaryBody = await summary.Content.ReadAsStringAsync();
        Assert.Contains("ACADEMIC_EXPORT_NOT_FOUND", summaryBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Sleep Consolidation", summaryBody, StringComparison.Ordinal);

        var csrf = await intruder.Client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/academic-export/jobs/{jobId}/export")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { configurationId = "classic-thesis.times.150.classic.center", format = "docx" }),
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        var export = await intruder.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, export.StatusCode);
        Assert.DoesNotContain("Sleep Consolidation", await export.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_and_non_research_jobs_are_not_exportable()
    {
        var owner = await RegisterAsync("Export Gate Owner");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.GenerationJobs.Add(new GenerationJob
            {
                Id = Guid.NewGuid(), WorkspaceId = owner.WorkspaceId, CreatedByUserId = Guid.Parse(owner.UserId),
                JobType = "research.generate", Status = GenerationJobStatus.Failed,
                InputJson = "{}", CreatedAt = DateTime.UtcNow,
            });
            db.GenerationJobs.Add(new GenerationJob
            {
                Id = Guid.NewGuid(), WorkspaceId = owner.WorkspaceId, CreatedByUserId = Guid.Parse(owner.UserId),
                JobType = "document.generate", Status = GenerationJobStatus.Succeeded,
                InputJson = "{}", ResultJson = ResultJson(), CreatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }
        using var scope2 = _factory.Services.CreateScope();
        var reader = scope2.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var failedId = reader.GenerationJobs.First(job => job.WorkspaceId == owner.WorkspaceId && job.Status == GenerationJobStatus.Failed).Id;
        var documentId = reader.GenerationJobs.First(job => job.WorkspaceId == owner.WorkspaceId && job.JobType == "document.generate").Id;
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Client.GetAsync($"/api/academic-export/jobs/{failedId}/report")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Client.GetAsync($"/api/academic-export/jobs/{documentId}/report")).StatusCode);
    }

    // ── summary surface ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Summary_honestly_separates_accessed_and_metadata_only_sources_and_leaks_nothing()
    {
        var owner = await RegisterAsync("Export Summary Owner");
        var jobId = SeedCompletedResearchJob(owner.WorkspaceId, Guid.Parse(owner.UserId));

        var response = await owner.Client.GetAsync($"/api/academic-export/jobs/{jobId}/report");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        var summary = JsonDocument.Parse(raw).RootElement;
        Assert.Equal("Sleep Consolidation in Undergraduate Cohorts", summary.GetProperty("title").GetString());
        Assert.Equal(3, summary.GetProperty("sourceCount").GetInt32());
        Assert.Equal(2, summary.GetProperty("accessedSourceCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("metadataOnlySourceCount").GetInt32());
        var metadataOnly = summary.GetProperty("sources").EnumerateArray().Single(source => !source.GetProperty("contentAccessed").GetBoolean());
        Assert.Equal("S2", metadataOnly.GetProperty("citationId").GetString());
        Assert.Contains("not accessed", metadataOnly.GetProperty("accessNote").GetString(), StringComparison.OrdinalIgnoreCase);

        // Privacy guard over the whole body: no backend identity tokens on the academic surface.
        Assert.Empty(AcademicCatalogPrivacyGuard.FindLeaks(raw));
    }

    // ── DOCX export: content, citations, integrity, geometry ────────────────────────────

    [Fact]
    public async Task Docx_export_preserves_content_citations_integrity_and_template_geometry()
    {
        var owner = await RegisterAsync("Export Docx Owner");
        var jobId = SeedCompletedResearchJob(owner.WorkspaceId, Guid.Parse(owner.UserId));
        var configurationId = "classic-thesis.times.150.classic.center";
        var spec = AcademicTemplateRegistry.GetConfiguration(configurationId)!.Spec;

        var (response, jobsBefore, usageBefore) = await ExportAsync(owner, jobId, configurationId, "docx");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("application/vnd.openxmlformats", response.Content.Headers.ContentType!.ToString());
        Assert.Equal("template-reformat", response.Headers.GetValues(AcademicExportController.ModeHeader).Single());
        Assert.Equal("FIRST_ACADEMIC_EXPORT", response.Headers.GetValues(AcademicExportController.ReasonHeader).Single());

        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var package = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var body = package.MainDocumentPart!.Document!.Body!;
        var bodyText = string.Join('\n', body.Descendants<Text>().Select(text => text.Text));

        // The saved payload survives the reformat verbatim.
        Assert.Contains("Sleep Consolidation in Undergraduate Cohorts", bodyText, StringComparison.Ordinal);
        Assert.Contains("Schedule design deserves the same scrutiny", bodyText, StringComparison.Ordinal);
        Assert.Contains("Participants were sampled from intact course rosters. [S2]", bodyText, StringComparison.Ordinal);
        Assert.Contains("Recall was scored blind to schedule type.", bodyText, StringComparison.Ordinal);
        Assert.Contains("71%", bodyText, StringComparison.Ordinal);

        // Bibliography: every persisted source, with the honest access status for S2.
        Assert.Contains("[S1] Schedule variance and recall", bodyText, StringComparison.Ordinal);
        Assert.Contains("[S3] Course roster notes", bodyText, StringComparison.Ordinal);
        Assert.Contains("Catalogue record only — the full text was not accessed in this workspace.", bodyText, StringComparison.Ordinal);
        Assert.Contains("[S2] Memory and the Academic Calendar", bodyText, StringComparison.Ordinal);
        // No synthetic gallery material may ever enter a real export.
        Assert.DoesNotContain("SYNTHETIC LAYOUT PREVIEW", bodyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("format sample", bodyText, StringComparison.OrdinalIgnoreCase);

        // Integrity disclosure is present whenever the design carries the slot, even unfilled.
        if (spec.IncludesAiUseDisclosureSlot)
            Assert.Contains("Statement of Generative-AI Use", bodyText, StringComparison.Ordinal);

        // Geometry matches the chosen design (real file validation, not a byte sniff).
        var sections = body.Descendants<SectionProperties>().ToList();
        Assert.True(sections.Count >= 2, "front-matter and body numbering sections must both exist");
        var margin = sections[0].Elements<PageMargin>().Single();
        Assert.Equal((uint)Math.Round(spec.MarginLeftPt * 20), margin.Left!.Value!);
        // An ltr design must not smuggle bidi geometry into the file.
        Assert.Empty(body.Descendants<BiDi>());
    }

    [Fact]
    public async Task Docx_export_with_student_disclosure_text_shows_the_students_own_statement()
    {
        var owner = await RegisterAsync("Export Disclosure Owner");
        var jobId = SeedCompletedResearchJob(owner.WorkspaceId, Guid.Parse(owner.UserId));
        var configurationId = FirstConfigurationWith(spec => spec.IncludesAiUseDisclosureSlot && !spec.IsRtl);

        var response = await ExportFileAsync(owner, jobId, configurationId, "docx",
            aiDisclosure: "We used a research assistant for literature search only, per programme policy.");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var package = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var bodyText = string.Join('\n', package.MainDocumentPart!.Document!.Body!.Descendants<Text>().Select(text => text.Text));
        Assert.Contains("literature search only", bodyText, StringComparison.Ordinal);
    }

    // ── PDF export ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pdf_export_opens_with_real_pages_and_title()
    {
        var owner = await RegisterAsync("Export Pdf Owner");
        var jobId = SeedCompletedResearchJob(owner.WorkspaceId, Guid.Parse(owner.UserId));
        var response = await ExportFileAsync(owner, jobId, "classic-thesis.times.150.classic.center", "pdf");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal((byte)'%', bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);
        using var document = UglyToad.PdfPig.PdfDocument.Open(bytes);
        Assert.True(document.NumberOfPages >= 3, "cover, front matter and body pages must all exist");
        var anyText = new string(string.Concat(Enumerable.Range(1, document.NumberOfPages)
            .Select(number => document.GetPage(number).Text)).Where(character => !char.IsWhiteSpace(character)).ToArray());
        Assert.Contains("SleepConsolidation", anyText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Direction_mismatch_between_report_and_template_is_refused_honestly()
    {
        var owner = await RegisterAsync("Export Direction Owner");
        var jobId = SeedCompletedResearchJob(owner.WorkspaceId, Guid.Parse(owner.UserId)); // en report
        var rtlConfig = FirstConfigurationWith(spec => spec.IsRtl);
        var response = await ExportFileAsync(owner, jobId, rtlConfig, "docx");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("ACADEMIC_EXPORT_DIRECTION_MISMATCH", body, StringComparison.Ordinal);
        Assert.Contains("Pick a matching template", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Kurdish_report_renders_through_kurdish_template_with_localised_integrity_strings()
    {
        var owner = await RegisterAsync("Export Kurdish Owner");
        var jobId = SeedCompletedResearchJob(owner.WorkspaceId, Guid.Parse(owner.UserId), language: "ckb");
        var response = await ExportFileAsync(owner, jobId, "kurdish-thesis-ckb.naskh.latin.150", "docx");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var package = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var body = package.MainDocumentPart!.Document!.Body!;
        var bodyText = string.Join('\n', body.Descendants<Text>().Select(text => text.Text));
        Assert.Contains("سەرچاوەکان", bodyText, StringComparison.Ordinal); // localised references heading
        // RTL document geometry is real OOXML bidi, not CSS dressing.
        Assert.NotEmpty(body.Descendants<BiDi>());
    }

    // ── credit safety: the headline guarantee ───────────────────────────────────────────

    [Fact]
    public async Task Exports_never_create_jobs_never_bill_and_repeat_free_under_every_mode()
    {
        var owner = await RegisterAsync("Export Credit Owner");
        var jobId = SeedCompletedResearchJob(owner.WorkspaceId, Guid.Parse(owner.UserId));

        int JobsBefore()
        {
            using var scope = _factory.Services.CreateScope();
            return scope.ServiceProvider.GetRequiredService<TaslimDbContext>().GenerationJobs.Count(job => job.WorkspaceId == owner.WorkspaceId);
        }
        int UsageBefore()
        {
            using var scope = _factory.Services.CreateScope();
            return scope.ServiceProvider.GetRequiredService<TaslimDbContext>().UsageTransactions.Count(transaction => transaction.GenerationJobId == jobId);
        }
        int AttemptsBefore()
        {
            using var scope = _factory.Services.CreateScope();
            return scope.ServiceProvider.GetRequiredService<TaslimDbContext>().GenerationProviderAttempts.Count(attempt => attempt.GenerationJobId == jobId);
        }

        Assert.Equal(1, JobsBefore());
        Assert.Equal(0, UsageBefore());
        Assert.Equal(0, AttemptsBefore());

        // 1) first export under template A
        var first = await ExportAsync(owner, jobId, "classic-thesis.times.150.classic.center", "docx");
        Assert.True(first.Response.StatusCode == HttpStatusCode.OK, await first.Response.Content.ReadAsStringAsync());
        var snapshotA = Uri.UnescapeDataString(first.Response.Headers.GetValues(AcademicExportController.SnapshotHeader).Single());

        // 2) re-export with the SAME settings: policy says re-export-same-settings, still free.
        var repeat = await ExportAsync(owner, jobId, "classic-thesis.times.150.classic.center", "docx", previousSnapshot: snapshotA);
        Assert.Equal(HttpStatusCode.OK, repeat.Response.StatusCode);
        Assert.Equal("re-export-same-settings", repeat.Response.Headers.GetValues(AcademicExportController.ModeHeader).Single());

        // 3) different template: policy says template-reformat, still free.
        var other = FirstConfigurationWith(spec => !spec.IsRtl && spec.ConfigurationId != "classic-thesis.times.150.classic.center");
        var reformat = await ExportFileAsync(owner, jobId, other, "docx");
        Assert.Equal(HttpStatusCode.OK, reformat.StatusCode);

        // 4) PDF round too.
        var pdf = await ExportFileAsync(owner, jobId, "classic-thesis.times.150.classic.center", "pdf");
        Assert.True(pdf.StatusCode == HttpStatusCode.OK, await pdf.Content.ReadAsStringAsync());

        // Nothing above created work that could bill: same single saved job, zero usage rows,
        // zero provider attempts — after four successful exports.
        Assert.Equal(1, JobsBefore());
        Assert.Equal(0, UsageBefore());
        Assert.Equal(0, AttemptsBefore());
    }

    [Fact]
    public async Task Tampered_previous_snapshot_degrades_to_first_export_not_to_a_claimed_history()
    {
        var owner = await RegisterAsync("Export Tamper Owner");
        var jobId = SeedCompletedResearchJob(owner.WorkspaceId, Guid.Parse(owner.UserId));
        var foreign = SeedCompletedResearchJob(owner.WorkspaceId, Guid.Parse(owner.UserId)); // other artifact id
        var real = await ExportFileAsync(owner, jobId, "classic-thesis.times.150.classic.center", "docx");
        Assert.Equal(HttpStatusCode.OK, real.StatusCode);

        // A snapshot from the *other* job must not authenticate a "same settings" history here.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.NotEqual(jobId, foreign);

        var csrf = await owner.Client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/academic-export/jobs/{jobId}/export")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                configurationId = "classic-thesis.times.150.classic.center",
                format = "docx",
                previousSnapshotJson = JsonSerializer.Serialize(new
                {
                    snapshotSchemaVersion = 99, // wrong version: must be ignored, never trusted
                    templateId = "taslim.academic.classic-thesis.v1",
                    configurationId = "classic-thesis.times.150.classic.center",
                    researchArtifactId = AcademicExportAuthorizer.ArtifactId(foreign),
                    sourceContentHash = new string('0', 64),
                }),
            }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        var response = await owner.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("FIRST_ACADEMIC_EXPORT", response.Headers.GetValues(AcademicExportController.ReasonHeader).Single());
    }

    // ── input validation ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Unknown_template_bad_format_and_bad_override_are_cleanly_refused()
    {
        var owner = await RegisterAsync("Export Validation Owner");
        var jobId = SeedCompletedResearchJob(owner.WorkspaceId, Guid.Parse(owner.UserId));

        var unknown = await ExportFileAsync(owner, jobId, "no-such-config", "docx");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Contains("ACADEMIC_TEMPLATE_NOT_FOUND", await unknown.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var badFormat = await ExportFileAsync(owner, jobId, "classic-thesis.times.150.classic.center", "exe");
        Assert.Equal(HttpStatusCode.BadRequest, badFormat.StatusCode);
        Assert.Contains("ACADEMIC_EXPORT_REQUEST_INVALID", await badFormat.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var badOverride = await ExportFileAsync(owner, jobId, "classic-thesis.times.150.classic.center", "docx",
            overrides: new Dictionary<string, string> { ["notAnOverrideKey"] = "yes" });
        Assert.Equal(HttpStatusCode.BadRequest, badOverride.StatusCode);
        Assert.Contains("ACADEMIC_EXPORT_OVERRIDE_REJECTED", await badOverride.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bibliography_without_verifiable_identifier_is_refused_before_rendering()
    {
        var owner = await RegisterAsync("Export Integrity Owner");
        var jobId = SeedCompletedResearchJob(owner.WorkspaceId, Guid.Parse(owner.UserId));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var source = db.ResearchSources.Single(item => item.GenerationJobId == jobId && item.CitationId == "S1");
            source.Url = null;
            source.CanonicalUrl = null;
            source.Snippet = null; // metadata-only *and* no verifiable identifier
            db.SaveChanges();
        }

        var response = await ExportFileAsync(owner, jobId, "classic-thesis.times.150.classic.center", "docx");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("ACADEMIC_BIBLIOGRAPHY_INTEGRITY", body, StringComparison.Ordinal);
        // Refusal wording must not leak the promise of paid work: no new job was created.
        using var scope2 = _factory.Services.CreateScope();
        Assert.Equal(1, scope2.ServiceProvider.GetRequiredService<TaslimDbContext>().GenerationJobs.Count(job => job.WorkspaceId == owner.WorkspaceId));
    }

    // ── bounded render concurrency (combined-tree gap) ──────────────────────────────────
    // The export POST is the only synchronous render route in the academic lane. With no endpoint
    // policy it fell through the global concurrency limiter's switch to "no limiter", so one student
    // could queue an unbounded number of full DOCX/PDF builds plus their re-open validation. The fix
    // binds the established expensive-render policy that already covers the other render and mutation
    // surfaces; these three tests pin the wiring, the bound itself, and that a capped burst is both
    // honest and still bill-free.

    [Fact]
    public void Export_post_action_carries_the_established_expensive_render_policy()
    {
        var action = typeof(AcademicExportController).GetMethod(
            nameof(AcademicExportController.Export),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

        Assert.NotNull(action);
        Assert.NotEmpty(action!.GetCustomAttributes<HttpPostAttribute>(inherit: true));
        // The bound belongs on the mutating, CSRF-protected render route.
        Assert.NotNull(action.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>(inherit: true));
        var policy = Assert.Single(action.GetCustomAttributes<EnableRateLimitingAttribute>(inherit: true));
        Assert.Equal(RateLimiting.ExpensiveAi, policy.PolicyName);
    }

    [Fact]
    public async Task Export_policy_bounds_simultaneous_renders_per_student()
    {
        // The controller's own attribute, replayed through the production limiter table: a policy
        // missing from that switch resolves to "no limiter", so this would never reject.
        var attribute = typeof(AcademicExportController)
            .GetMethod(nameof(AcademicExportController.Export),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)!
            .GetCustomAttribute<EnableRateLimitingAttribute>()!;

        var options = new RateLimiterOptions();
        RateLimiting.Configure(options);
        var student = Guid.NewGuid();
        var anotherStudent = Guid.NewGuid();

        using var first = await options.GlobalLimiter!.AcquireAsync(RenderContext(attribute, student));
        using var second = await options.GlobalLimiter!.AcquireAsync(RenderContext(attribute, student));
        using var third = await options.GlobalLimiter!.AcquireAsync(RenderContext(attribute, student));
        using var neighbour = await options.GlobalLimiter!.AcquireAsync(RenderContext(attribute, anotherStudent));

        Assert.True(first.IsAcquired);
        Assert.True(second.IsAcquired);
        Assert.False(third.IsAcquired, "a third simultaneous render from one student must be refused, not queued");
        Assert.True(neighbour.IsAcquired, "the render cap is per student, never a global stall");
    }

    [Fact]
    public async Task Export_route_is_governed_by_the_policy_and_refuses_without_rendering_or_billing()
    {
        var owner = await RegisterAsync("Export Window Owner");
        var jobId = SeedCompletedResearchJob(owner.WorkspaceId, Guid.Parse(owner.UserId));

        // Twelve exports from one student in a row crosses the established window (10/minute),
        // which only happens if the route really is governed: an ungoverned route returns 200 twelve
        // times. Requests stay sequential because the shared in-memory SQLite connection of the test
        // harness is not safe under concurrent use; the in-flight half of the same policy is pinned by
        // Export_policy_bounds_simultaneous_renders_per_student.
        var outcomes = new List<(HttpStatusCode Status, string Body, string? RetryAfter, string? Mode)>();
        for (var index = 0; index < 12; index++)
        {
            var response = await ExportFileAsync(owner, jobId, "classic-thesis.times.150.classic.center", "docx");
            outcomes.Add((
                response.StatusCode,
                await response.Content.ReadAsStringAsync(),
                response.Headers.TryGetValues("Retry-After", out var retry) ? string.Join(",", retry) : null,
                response.Headers.TryGetValues(AcademicExportController.ModeHeader, out var mode) ? string.Join(",", mode) : null));
        }

        Assert.All(outcomes, outcome => Assert.True(
            outcome.Status is HttpStatusCode.OK or HttpStatusCode.TooManyRequests,
            $"unexpected {(int)outcome.Status} {outcome.Status}: {outcome.Body[..Math.Min(outcome.Body.Length, 240)]}"));
        Assert.Equal(10, outcomes.Count(outcome => outcome.Status == HttpStatusCode.OK));
        Assert.Equal(2, outcomes.Count(outcome => outcome.Status == HttpStatusCode.TooManyRequests));

        foreach (var rejected in outcomes.Where(outcome => outcome.Status == HttpStatusCode.TooManyRequests))
        {
            // Refused before any rendering: the documented safe envelope, no export-mode header, and
            // no file bytes in the body.
            Assert.Contains("RATE_LIMITED", rejected.Body, StringComparison.Ordinal);
            Assert.Equal("60", rejected.RetryAfter);
            Assert.Null(rejected.Mode);
            Assert.StartsWith("{", rejected.Body, StringComparison.Ordinal);
        }

        // And the bounded surface still created nothing billable across the whole sweep.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(1, db.GenerationJobs.Count(job => job.WorkspaceId == owner.WorkspaceId));
        Assert.Equal(0, db.UsageTransactions.Count(transaction => transaction.GenerationJobId == jobId));
        Assert.Equal(0, db.GenerationProviderAttempts.Count(attempt => attempt.GenerationJobId == jobId));
    }

    private static HttpContext RenderContext(EnableRateLimitingAttribute attribute, Guid userId)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId.ToString("N"))],
                "test")),
        };
        context.SetEndpoint(new Endpoint(
            requestDelegate: null,
            new EndpointMetadataCollection(attribute),
            displayName: "academic-export"));
        return context;
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────

    private async Task<(HttpResponseMessage Response, int JobsBefore, int UsageBefore)> ExportAsync(
        Principal owner, Guid jobId, string configurationId, string format, string? previousSnapshot = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        int jobsBefore = db.GenerationJobs.Count(job => job.WorkspaceId == owner.WorkspaceId);
        int usageBefore = db.UsageTransactions.Count();

        var response = await ExportFileAsync(owner, jobId, configurationId, format, previousSnapshot: previousSnapshot);
        return (response, jobsBefore, usageBefore);
    }

    private async Task<HttpResponseMessage> ExportFileAsync(
        Principal owner,
        Guid jobId,
        string configurationId,
        string format,
        string? previousSnapshot = null,
        Dictionary<string, string>? overrides = null,
        string? aiDisclosure = null)
    {
        var csrf = await owner.Client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/academic-export/jobs/{jobId}/export")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    configurationId,
                    format,
                    previousSnapshotJson = previousSnapshot,
                    studentOverrides = overrides,
                    aiDisclosureText = aiDisclosure,
                }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        return await owner.Client.SendAsync(request);
    }
}
