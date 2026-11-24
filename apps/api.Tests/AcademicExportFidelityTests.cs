using System.IO.Compression;
using System.Text.Json;
using System.Net.Http.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Taslim.Api.AcademicExport;
using Taslim.Api.AcademicTemplates;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Template-export fidelity for #1269, driven from the payload shape the production writer actually
/// produces. <see cref="ResearchGenerationJobHandler"/> calls <c>JsonSerializer.Serialize</c> on an
/// anonymous object with no <see cref="JsonSerializerOptions"/>, so the OUTER keys are the hand-written
/// camelCase ones (<c>sections</c>, <c>keyFindings</c>) while every NESTED object keeps its C# property
/// names (<c>Heading</c>, <c>Blocks</c>, <c>Type</c>, <c>Text</c>, <c>Items</c>, <c>Rows</c>,
/// <c>Cells</c>, <c>CitationIds</c>). A reader that only accepted the camelCase spelling decoded real
/// saved reports into empty sections, and the documents still passed validation because nothing checked
/// that the body had survived. These tests reopen the produced files and ask that question directly.
/// </summary>
public sealed class AcademicExportFidelityTests : IClassFixture<TaslimApiFactory>
{
    private const string ClassicConfiguration = "classic-thesis.times.150.classic.center";

    private readonly TaslimApiFactory _factory;

    public AcademicExportFidelityTests(TaslimApiFactory factory)
    {
        _factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    // ── the payload, produced the way the handler produces it ───────────────────────────

    /// <summary>Serialised from the real contract types, exactly as the job handler does it: no
    /// options object, camelCase keys only because the anonymous properties are spelled that way.</summary>
    private static string HandlerShapedResultJson(string language = "en") => JsonSerializer.Serialize(new
    {
        researchType = "research",
        title = "Assessment Load and Revision Quality",
        subtitle = (string?)null,
        language,
        executiveSummary = "Two timed samples place workload and revision depth together [S1].",
        keyFindings = new List<ResearchReportBlock>
        {
            new()
            {
                Type = ResearchBlockTypes.Bullets,
                Items =
                [
                    "Weighted workload rose 34% across the window.",
                    "Self-tested recall outperformed re-reading by 0.8 SD.",
                    "Spacing effects survived the follow-up.",
                ],
                CitationIds = ["S1"],
            },
        },
        sections = new List<ResearchSection>
        {
            new()
            {
                Heading = "Method",
                Blocks =
                [
                    new()
                    {
                        Type = ResearchBlockTypes.Paragraph,
                        Text = "Participants were sampled from intact course rosters.",
                        CitationIds = ["S2"],
                    },
                ],
            },
            new()
            {
                // An allowed empty heading: the contents list must skip it, the body must not.
                Heading = "",
                Blocks =
                [
                    new()
                    {
                        Type = ResearchBlockTypes.Paragraph,
                        Text = "Untitled section carried this claim forward [S3].",
                        CitationIds = ["S3"],
                    },
                ],
            },
            new()
            {
                Heading = "Results",
                Blocks =
                [
                    new()
                    {
                        Type = ResearchBlockTypes.Table,
                        Text = "Table 1. Workload against revision depth.",
                        Rows =
                        [
                            new() { Cells = ["Cohort", "Weighted load", "Depth"] },
                            new() { Cells = ["A", "7.4 h", "Deep"] },
                            new() { Cells = ["B", "3.1 h", "Shallow"] },
                        ],
                        CitationIds = ["S1", "S3"],
                    },
                    new()
                    {
                        Type = ResearchBlockTypes.NumberedList,
                        Items = ["First numbered finding.", "Second numbered finding.", "Third numbered finding."],
                        CitationIds = ["S2"],
                    },
                ],
            },
        },
        conclusion = "Workload budgeting deserves the same scrutiny as content selection.",
        sourceCount = 3,
        sources = Array.Empty<object>(),
    });

    private Guid SeedHandlerShapedJob(Guid workspaceId, Guid userId, string language = "en")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var jobId = Guid.NewGuid();
        var now = new DateTime(2026, 11, 21, 9, 0, 0, DateTimeKind.Utc);
        db.GenerationJobs.Add(new GenerationJob
        {
            Id = jobId,
            WorkspaceId = workspaceId,
            CreatedByUserId = userId,
            JobType = "research.generate",
            Status = GenerationJobStatus.Succeeded,
            Title = "Assessment Load and Revision Quality",
            InputJson = "{}",
            ResultJson = HandlerShapedResultJson(language),
            CreatedAt = now,
            CompletedAt = now,
        });
        db.ResearchSources.AddRange(
            // S1: a search hit whose only content is a snippet — NOT a full text anyone read.
            new ResearchSource
            {
                Id = Guid.NewGuid(), WorkspaceId = workspaceId, GenerationJobId = jobId,
                CitationId = "S1", SourceType = "web", Rank = 1, RetrievedAt = now, IsSelected = true,
                Title = "Workload and depth in timed samples", Domain = "journals.example", Publisher = "Example Press",
                Url = "https://journals.example/workload-depth",
                Snippet = "Weighted workload tracked revision depth.", ExtractedText = null,
            },
            // S2: catalogue metadata only.
            new ResearchSource
            {
                Id = Guid.NewGuid(), WorkspaceId = workspaceId, GenerationJobId = jobId,
                CitationId = "S2", SourceType = "web", Rank = 2, RetrievedAt = now, IsSelected = true,
                Title = "Revision Strategies Handbook", Domain = "catalog.example", Publisher = "University Catalogue",
                Url = "https://catalog.example/revision-strategies",
                Snippet = null, ExtractedText = null,
            },
            // S3: an uploaded document — the only row that may be called full text.
            new ResearchSource
            {
                Id = Guid.NewGuid(), WorkspaceId = workspaceId, GenerationJobId = jobId,
                CitationId = "S3", SourceType = "uploaded", Rank = 3, RetrievedAt = now, IsSelected = true,
                Title = "Course workload export", Domain = "uploaded file",
                Snippet = "Export excerpt.",
            });
        db.SaveChanges();
        return jobId;
    }

    private async Task<(string UserId, Guid WorkspaceId, HttpClient Client)> RegisterAsync(string label)
    {
        var client = _factory.CreateClient();
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    displayName = label,
                    email = $"academic-fidelity-{Guid.NewGuid():N}@example.com",
                    password = "StrongPassword!123",
                    preferredLanguage = "en",
                }),
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        var response = await client.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("user").GetProperty("id").GetString()!, body.GetProperty("personalWorkspace").GetProperty("id").GetGuid(), client);
    }

    private static async Task<byte[]> ExportAsync(HttpClient client, Guid jobId, string configurationId, string format, string? disclosure = null)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Post, $"/api/academic-export/jobs/{jobId}/export")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                configurationId,
                format,
                aiDisclosureText = disclosure,
            }), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        var response = await client.SendAsync(request);
        Assert.True(response.StatusCode == System.Net.HttpStatusCode.OK,
            $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static string DocxText(byte[] bytes)
    {
        using var package = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        return string.Join('\n', package.MainDocumentPart!.Document!.Body!.Descendants<DocumentFormat.OpenXml.Wordprocessing.Text>().Select(t => t.Text));
    }

    private static string PdfText(byte[] bytes)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(bytes);
        return string.Concat(Enumerable.Range(1, document.NumberOfPages).Select(number => document.GetPage(number).Text));
    }

    private static string Collapse(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // ── the reader decodes the persisted spelling ───────────────────────────────────────

    [Fact]
    public void Handler_shaped_payload_decodes_headings_blocks_and_citations()
    {
        var job = new GenerationJob
        {
            Id = Guid.NewGuid(), JobType = "research.generate", Title = "T", InputJson = "{}",
            ResultJson = HandlerShapedResultJson(), Status = GenerationJobStatus.Succeeded,
            CreatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
        };
        var sources = new List<ResearchSource>
        {
            new() { Id = Guid.NewGuid(), CitationId = "S1", SourceType = "web", Rank = 1, Title = "W", Snippet = "s" },
            new() { Id = Guid.NewGuid(), CitationId = "S3", SourceType = "uploaded", Rank = 3, Title = "U" },
        };

        var report = AcademicSavedReportReader.Read(job, sources);

        // Three sections, with their real headings and their real blocks — the pre-fix reader returned
        // three empty shells here.
        Assert.Equal(3, report.Sections.Count);
        Assert.Equal("Method", report.Sections[0].Heading);
        Assert.Equal("", report.Sections[1].Heading);
        Assert.Equal("Results", report.Sections[2].Heading);
        Assert.All(report.Sections, section => Assert.NotEmpty(section.Blocks));

        var table = report.Sections[2].Blocks.Single(block => block.Type == "table");
        Assert.Equal("Table 1. Workload against revision depth.", table.Text);
        Assert.Equal(3, table.Rows!.Count);
        Assert.Equal(new[] { "Cohort", "Weighted load", "Depth" }, table.Rows[0].ToArray());
        Assert.Equal(new[] { "S1", "S3" }, table.CitationIds.ToArray());

        var list = report.KeyFindings.Single();
        Assert.Equal("bullets", list.Type, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(3, list.Items!.Count);

        var numbered = report.Sections[2].Blocks.Single(block => block.Type == "numbered_list");
        Assert.Equal(3, numbered.Items!.Count);
        Assert.Equal(["S2"], numbered.CitationIds.ToArray());

        // Nothing the payload cites may be lost on the way to the bibliography: the citation set is
        // collected from key findings and every section's blocks, in first-mention order.
        Assert.Equal(new[] { "S1", "S2", "S3" }, report.CitedCitationIds.ToArray());
    }

    [Fact]
    public void Both_casings_decode_to_the_same_report()
    {
        var pascal = HandlerShapedResultJson();
        // The historical/alternate spelling a camelCase producer would have written.
        var camel = JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(pascal), new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        var job = new GenerationJob
        {
            Id = Guid.NewGuid(), JobType = "research.generate", Title = "T", InputJson = "{}",
            Status = GenerationJobStatus.Succeeded, CreatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
        };
        var sources = new List<ResearchSource>
        {
            new() { Id = Guid.NewGuid(), CitationId = "S1", SourceType = "web", Rank = 1, Title = "W", Snippet = "s" },
        };

        var fromPascal = AcademicSavedReportReader.Read(new GenerationJob
        {
            Id = job.Id, JobType = job.JobType, Title = job.Title, InputJson = job.InputJson,
            Status = job.Status, CreatedAt = job.CreatedAt, CompletedAt = job.CompletedAt, ResultJson = pascal,
        }, sources);
        var fromCamel = AcademicSavedReportReader.Read(new GenerationJob
        {
            Id = job.Id, JobType = job.JobType, Title = job.Title, InputJson = job.InputJson,
            Status = job.Status, CreatedAt = job.CreatedAt, CompletedAt = job.CompletedAt, ResultJson = camel,
        }, sources);

        Assert.Equal(fromPascal.Sections.Select(s => s.Heading), fromCamel.Sections.Select(s => s.Heading));
        Assert.Equal(
            fromPascal.Sections.SelectMany(s => s.Blocks).Select(b => (b.Type, b.Text, b.Items?.Count ?? 0, b.Rows?.Count ?? 0, b.CitationIds.Count)),
            fromCamel.Sections.SelectMany(s => s.Blocks).Select(b => (b.Type, b.Text, b.Items?.Count ?? 0, b.Rows?.Count ?? 0, b.CitationIds.Count)));
        Assert.Equal(fromPascal.KeyFindings.Single().Items!.Count, fromCamel.KeyFindings.Single().Items!.Count);
    }

    // ── access honesty ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Snippet_only_source_is_labelled_search_result_not_full_text()
    {
        var job = new GenerationJob
        {
            Id = Guid.NewGuid(), JobType = "research.generate", Title = "T", InputJson = "{}",
            ResultJson = HandlerShapedResultJson(), Status = GenerationJobStatus.Succeeded,
            CreatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
        };
        var report = AcademicSavedReportReader.Read(job, new List<ResearchSource>
        {
            new() { Id = Guid.NewGuid(), CitationId = "S1", SourceType = "web", Rank = 1, Title = "Snippet hit", Snippet = "one line" },
            new() { Id = Guid.NewGuid(), CitationId = "S2", SourceType = "web", Rank = 2, Title = "Metadata only" },
            new() { Id = Guid.NewGuid(), CitationId = "S3", SourceType = "uploaded", Rank = 3, Title = "Held file", Snippet = "excerpt" },
            new() { Id = Guid.NewGuid(), CitationId = "S4", SourceType = "web", Rank = 4, Title = "Extracted passages", ExtractedText = "a passage" },
        });

        Assert.Equal("search_result_only", report.Sources.Single(s => s.CitationId == "S1").AccessTier);
        Assert.Equal("metadata_only", report.Sources.Single(s => s.CitationId == "S2").AccessTier);
        Assert.Equal("full_text", report.Sources.Single(s => s.CitationId == "S3").AccessTier);
        Assert.Equal("extracted_text", report.Sources.Single(s => s.CitationId == "S4").AccessTier);

        // Only the uploaded file may be counted as full text; a snippet is not that claim.
        Assert.Equal(1, report.FullTextSourceCount);
        Assert.Equal(1, report.SearchResultOnlySourceCount);
        Assert.Equal(1, report.ExtractedTextSourceCount);

        var labels = report.Labels;
        Assert.Equal(labels.SearchResultOnlyNote, report.Sources.Single(s => s.CitationId == "S1").AccessNote);
        Assert.Equal(labels.MetadataOnlyNote, report.Sources.Single(s => s.CitationId == "S2").AccessNote);
        Assert.Equal(labels.ExtractedTextNote, report.Sources.Single(s => s.CitationId == "S4").AccessNote);
        Assert.Equal(string.Empty, report.Sources.Single(s => s.CitationId == "S3").AccessNote);
    }

    // ── DOCX fidelity ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Docx_from_handler_payload_carries_body_lists_table_and_citations()
    {
        var (userId, workspaceId, client) = await RegisterAsync("Fidelity Docx Owner");
        var jobId = SeedHandlerShapedJob(workspaceId, Guid.Parse(userId));
        var bytes = await ExportAsync(client, jobId, ClassicConfiguration, "docx",
            disclosure: "A generative assistant was used for literature search only.");

        var text = DocxText(bytes);
        // Section headings and their prose survived the round trip.
        Assert.Contains("Method", text, StringComparison.Ordinal);
        Assert.Contains("Participants were sampled from intact course rosters.", text, StringComparison.Ordinal);
        Assert.Contains("Results", text, StringComparison.Ordinal);
        Assert.Contains("Executive", text, StringComparison.Ordinal);
        Assert.Contains("Workload budgeting deserves the same scrutiny", text, StringComparison.Ordinal);
        // Every list item, not the first one only.
        Assert.Contains("Weighted workload rose 34% across the window.", text, StringComparison.Ordinal);
        Assert.Contains("Self-tested recall outperformed re-reading by 0.8 SD.", text, StringComparison.Ordinal);
        Assert.Contains("Spacing effects survived the follow-up.", text, StringComparison.Ordinal);
        Assert.Contains("First numbered finding.", text, StringComparison.Ordinal);
        Assert.Contains("Third numbered finding.", text, StringComparison.Ordinal);
        // The table: caption, cells, and the citations that trail it.
        Assert.Contains("Table 1. Workload against revision depth.", text, StringComparison.Ordinal);
        Assert.Contains("Weighted load", text, StringComparison.Ordinal);
        Assert.Contains("Shallow", text, StringComparison.Ordinal);
        Assert.Contains("Sources:", text, StringComparison.Ordinal);
        // Paragraph citation distinct from table citation, both present.
        Assert.Contains("[S2]", text, StringComparison.Ordinal);
        Assert.Contains("[S1]", text, StringComparison.Ordinal);
        Assert.Contains("[S3]", text, StringComparison.Ordinal);

        // Schema validity, not just readability by a forgiving parser.
        using (var package = WordprocessingDocument.Open(new MemoryStream(bytes), false))
        {
            var errors = new OpenXmlValidator().Validate(package).ToList();
            Assert.Empty(errors);
            var body = package.MainDocumentPart!.Document!.Body!;
            // Exactly one section definition may be a direct body child: the final one.
            Assert.Single(body.ChildElements.OfType<DocumentFormat.OpenXml.Wordprocessing.SectionProperties>());
            Assert.Equal(2, body.Descendants<DocumentFormat.OpenXml.Wordprocessing.SectionProperties>().Count());
        }
    }

    // ── PDF fidelity ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pdf_from_handler_payload_renders_every_list_item_and_untitled_section()
    {
        var (userId, workspaceId, client) = await RegisterAsync("Fidelity Pdf Owner");
        var jobId = SeedHandlerShapedJob(workspaceId, Guid.Parse(userId));
        var bytes = await ExportAsync(client, jobId, ClassicConfiguration, "pdf",
            disclosure: "A generative assistant was used for literature search only.");

        var flat = Collapse(PdfText(bytes));
        // The blocks under an empty heading used to vanish with the heading's contents-list entry.
        Assert.Contains("Untitled section carried this claim forward", flat, StringComparison.Ordinal);
        Assert.Contains("Participants were sampled from intact course rosters", flat, StringComparison.Ordinal);
        // Multi-item lists crashed QuestPDF's single-child container; all three items must be present.
        Assert.Contains("Weighted workload rose 34% across the window.", flat, StringComparison.Ordinal);
        Assert.Contains("Self-tested recall outperformed re-reading by 0.8 SD.", flat, StringComparison.Ordinal);
        Assert.Contains("Spacing effects survived the follow-up.", flat, StringComparison.Ordinal);
        Assert.Contains("First numbered finding.", flat, StringComparison.Ordinal);
        Assert.Contains("Second numbered finding.", flat, StringComparison.Ordinal);
        Assert.Contains("Third numbered finding.", flat, StringComparison.Ordinal);
        // Table caption, cells and the trailing citations.
        Assert.Contains("Table 1. Workload against revision depth.", flat, StringComparison.Ordinal);
        Assert.Contains("Weighted load", flat, StringComparison.Ordinal);
        Assert.Contains("Shallow", flat, StringComparison.Ordinal);
        Assert.Contains("Sources: [S1] [S3]", flat, StringComparison.Ordinal);
        // Access honesty in the bibliography.
        Assert.Contains("Search result only", flat, StringComparison.Ordinal);
        Assert.Contains("Catalogue record only", flat, StringComparison.Ordinal);
    }

    // ── disclosure is never silently dropped ────────────────────────────────────────────

    [Theory]
    [InlineData("A generative assistant was used for literature search only.", "literature search only")]
    [InlineData(null, "student has not completed")]
    public async Task Disclosure_survives_a_template_without_a_slot(string? disclosure, string expectedFragment)
    {
        var slotless = AcademicTemplateRegistry.Configurations
            .First(configuration => !configuration.Spec.IncludesAiUseDisclosureSlot)
            .ConfigurationId;
        var (userId, workspaceId, client) = await RegisterAsync("Fidelity Disclosure Owner");
        var jobId = SeedHandlerShapedJob(workspaceId, Guid.Parse(userId));
        var bytes = await ExportAsync(client, jobId, slotless, "docx", disclosure: disclosure);

        var text = DocxText(bytes);
        Assert.Contains("Statement of Generative-AI Use", text, StringComparison.Ordinal);
        Assert.Contains(expectedFragment, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Disclosure_survives_pdf_and_a_slotless_template_in_both_formats()
    {
        var slotless = AcademicTemplateRegistry.Configurations
            .First(configuration => !configuration.Spec.IncludesAiUseDisclosureSlot)
            .ConfigurationId;
        var (userId, workspaceId, client) = await RegisterAsync("Fidelity Disclosure Pdf Owner");
        var jobId = SeedHandlerShapedJob(workspaceId, Guid.Parse(userId));
        var flat = Collapse(PdfText(await ExportAsync(client, jobId, slotless, "pdf",
            disclosure: "Only search was automated; all prose is the student's own.")));
        Assert.Contains("Statement of Generative-AI Use", flat, StringComparison.Ordinal);
        Assert.Contains("Only search was automated", flat, StringComparison.Ordinal);
    }

    // ── summary endpoint mirrors the tiers ──────────────────────────────────────────────

    [Fact]
    public async Task Summary_reports_the_access_breakdown_separately()
    {
        var (userId, workspaceId, client) = await RegisterAsync("Fidelity Summary Owner");
        var jobId = SeedHandlerShapedJob(workspaceId, Guid.Parse(userId));
        var summary = await client.GetFromJsonAsync<JsonElement>($"/api/academic-export/jobs/{jobId}/report");

        Assert.Equal(1, summary.GetProperty("fullTextSourceCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("searchResultOnlySourceCount").GetInt32());
        Assert.Equal(0, summary.GetProperty("extractedTextSourceCount").GetInt32());
        var rows = summary.GetProperty("sources");
        Assert.Equal("search_result_only", rows[0].GetProperty("accessTier").GetString());
        Assert.False(rows[0].GetProperty("fullTextAccessed").GetBoolean());
        Assert.Equal("full_text", rows[2].GetProperty("accessTier").GetString());
        Assert.True(rows[2].GetProperty("fullTextAccessed").GetBoolean());
    }
}
