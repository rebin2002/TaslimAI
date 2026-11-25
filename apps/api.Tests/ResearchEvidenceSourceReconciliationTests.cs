using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Ai;
using Taslim.Api.Controllers;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Taslim.Api.Research;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Independent security review regression for the research evidence lane on PR #1239.
///
/// The pipeline normalizes evidence against the source list BEFORE the content-fetch
/// stage runs and before ResearchCitationUrlPolicy.FilterAdmissibleSourceLinks drops
/// sources whose links fail the public-citation boundary. Evidence kept its citation
/// identifiers, so the evidence gathered from a removed source was still handed to the
/// paid report provider inside ResearchReportPrompt even though its source was gone -
/// the exact class of leak the citation guard exists to prevent (the removed source is
/// one a reader can never open, and in the SSRF shapes below one that points at
/// internal metadata endpoints). The provider must receive evidence only for surviving
/// sources. Synthetic fixtures only; no real provider, URL fetch, or payment happens.
/// </summary>
public sealed class ResearchEvidenceSourceReconciliationTests : IClassFixture<EvidenceReconciliationFactory>
{
    internal const string AdmittedMarker = "admitted-public-source-evidence";
    internal const string RemovedLinkLocalMarker = "removed-link-local-metadata-evidence";
    internal const string RemovedLoopbackMarker = "removed-obfuscated-loopback-evidence";

    private readonly EvidenceReconciliationFactory factory;

    public ResearchEvidenceSourceReconciliationTests(EvidenceReconciliationFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Evidence_from_a_source_removed_by_the_citation_guard_never_reaches_the_report_provider()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"evidence-reconcile-{Guid.NewGuid():N}@example.com");
        var created = await SendWithCsrf<CreateResearchGenerationResponse>(client, HttpMethod.Post, "/api/research-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            question = "Compare current renewable financing benchmarks.",
            depth = "standard",
            reportType = "research_report",
            language = "en",
            useWebSources = true,
            attachmentIds = Array.Empty<Guid>(),
        });
        var completed = await WaitForTerminal(client, created.Job.Id);
        Assert.True(completed.Status == "Succeeded", $"Status={completed.Status}; Code={completed.ErrorCode}; Message={completed.ErrorMessage}");

        var prompt = factory.ReportProvider.LastPrompt;
        Assert.NotNull(prompt);

        // The guard already removed the inadmissible sources themselves; that half works.
        var sourceCitations = prompt!.Sources.Select(source => source.CitationId).ToArray();
        Assert.DoesNotContain("S2", sourceCitations);
        Assert.DoesNotContain("S3", sourceCitations);
        Assert.Contains("S1", sourceCitations);

        // Defect expectation: before the fix the prompt still carried S2/S3 evidence.
        Assert.DoesNotContain(prompt.Evidence, item => item.CitationId == "S2");
        Assert.DoesNotContain(prompt.Evidence, item => item.CitationId == "S3");
        var flattened = string.Join("\n", prompt.Evidence.Select(item => $"{item.Excerpt}|{item.Context}"));
        Assert.DoesNotContain(RemovedLinkLocalMarker, flattened, StringComparison.Ordinal);
        Assert.DoesNotContain(RemovedLoopbackMarker, flattened, StringComparison.Ordinal);

        // Reconciliation must exclude, not empty: the admissible source keeps its evidence.
        Assert.Contains(prompt.Evidence, item => item.CitationId == "S1");
        Assert.Contains(AdmittedMarker, flattened, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Persisted_evidence_rows_stay_linked_only_to_surviving_sources()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"evidence-persist-{Guid.NewGuid():N}@example.com");
        var created = await SendWithCsrf<CreateResearchGenerationResponse>(client, HttpMethod.Post, "/api/research-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            question = "Compare current renewable financing benchmarks.",
            depth = "standard",
            reportType = "research_report",
            language = "en",
            useWebSources = true,
            attachmentIds = Array.Empty<Guid>(),
        });
        var completed = await WaitForTerminal(client, created.Job.Id);
        Assert.Equal("Succeeded", completed.Status);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var citations = await db.ResearchEvidence.AsNoTracking()
            .Where(item => item.GenerationJobId == created.Job.Id)
            .Select(item => item.ResearchSource.CitationId)
            .ToListAsync();
        Assert.NotEmpty(citations);
        Assert.All(citations, citation => Assert.Equal("S1", citation));
    }

    private static async Task<AuthResponse> Register(HttpClient client, string email)
    {
        var response = await SendWithCsrfRaw(client, HttpMethod.Post, "/api/auth/register", new { displayName = "Evidence Reconcile Tester", email, password = "StrongPassword!123", preferredLanguage = "en" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<GenerationJobDto> WaitForTerminal(HttpClient client, Guid id)
    {
        for (var attempt = 0; attempt < 160; attempt++)
        {
            using var response = await client.GetAsync($"/api/generation/jobs/{id}");
            if (response.IsSuccessStatusCode)
            {
                var current = await response.Content.ReadFromJsonAsync<GenerationJobDto>();
                if (current is not null && current.Status is "Succeeded" or "Failed" or "Cancelled") return current;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("Research job did not reach a terminal state.");
    }

    private static async Task<HttpResponseMessage> SendWithCsrfRaw(HttpClient client, HttpMethod method, string path, object? body)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        if (body is not null) request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object body)
    {
        using var response = await SendWithCsrfRaw(client, method, path, body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}

public sealed class EvidenceReconciliationFactory : GenerationJobsApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("ResearchGeneration:Enabled", "true");
        builder.UseSetting("ResearchGeneration:MaxSourceCount", "8");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IResearchSearchProvider>();
            services.RemoveAll<IResearchReportProvider>();
            services.AddSingleton<IResearchSearchProvider, MixedAdmissibilitySearchProvider>();
            var reportProvider = new CapturingReportProvider();
            services.AddSingleton<IResearchReportProvider>(reportProvider);
            ReportProvider = reportProvider;
        });
    }

    internal CapturingReportProvider ReportProvider { get; private set; } = null!;
}

/// <summary>
/// Three web sources: one public and admissible, one pointing at the cloud identity
/// metadata endpoint (link-local, RFC 3927) and one spelling loopback as a decimal
/// host literal (RFC 6874/3986 obfuscation the guard canonicalizes). All three carry
/// provider evidence, which is exactly the state the pre-handler pipeline sees.
/// </summary>
public sealed class MixedAdmissibilitySearchProvider : IResearchSearchProvider
{
    public Task<ResearchSearchResult> SearchAsync(ResearchSearchRequest request, ResearchGenerationOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sources = new[]
        {
            new ResearchSourceCandidate("S1", "https://example.gov/energy", "https://example.gov/energy", "Public ministry report", "example.gov", "Example Ministry", null, DateTime.UtcNow, "web", "Public benchmark data.", "Public benchmark data: " + EvidenceReconciliationFactoryHelpers.AdmittedMarker, request.Plan.SearchQueries[0], 1, true, null),
            new ResearchSourceCandidate("S2", "http://169.254.169.254/latest/meta-data/", null, "Metadata endpoint", "169.254.169.254", null, null, DateTime.UtcNow, "web", "Internal metadata.", "Internal metadata: " + EvidenceReconciliationFactoryHelpers.RemovedLinkLocalMarker, request.Plan.SearchQueries[0], 2, true, null),
            new ResearchSourceCandidate("S3", "http://2130706433:9000/internal", null, "Obfuscated loopback", "2130706433", null, null, DateTime.UtcNow, "web", "Private service.", "Private service: " + EvidenceReconciliationFactoryHelpers.RemovedLoopbackMarker, request.Plan.SearchQueries[0], 3, true, null),
        };
        var evidence = sources
            .Select(source => new ResearchEvidenceCandidate(source.CitationId, "benchmark evidence", source.ExtractedText!, null, null))
            .ToArray();
        var usage = new AiUsageMetadata("test", "research-search-test", 100, null, 50, 0m, 0m, 5, "completed", true, SafeMetadataJson: "{\"researchStage\":\"search\"}");
        return Task.FromResult(new ResearchSearchResult(sources, evidence, usage));
    }
}

public static class EvidenceReconciliationFactoryHelpers
{
    public const string AdmittedMarker = ResearchEvidenceSourceReconciliationTests.AdmittedMarker;
    public const string RemovedLinkLocalMarker = ResearchEvidenceSourceReconciliationTests.RemovedLinkLocalMarker;
    public const string RemovedLoopbackMarker = ResearchEvidenceSourceReconciliationTests.RemovedLoopbackMarker;
}

/// <summary>Captures the exact prompt the paid report provider would receive.</summary>
public sealed class CapturingReportProvider : IResearchReportProvider
{
    public ResearchReportPrompt? LastPrompt { get; private set; }

    public Task<ResearchReportProviderResult> GenerateAsync(ResearchReportPrompt prompt, ResearchGenerationOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastPrompt = prompt;
        var draft = new ResearchDraft
        {
            Title = "Renewable financing benchmarks",
            Subtitle = "Public-source overview",
            Language = "en",
            ExecutiveSummary = "The admitted public source indicates a stable benchmark.",
            KeyFindings = [new ResearchReportBlock { Type = ResearchBlockTypes.KeyFinding, Text = "Public benchmark data anchors the comparison. [S1]", CitationIds = ["S1"] }],
            Sections = [new ResearchSection { Heading = "Method", Blocks = [new ResearchReportBlock { Type = ResearchBlockTypes.Paragraph, Text = "Only publicly citable sources informed this section. [S1]", CitationIds = ["S1"] }] }],
            Conclusion = "Review the surviving sources.",
            Sources = ["S1"],
        };
        var usage = new AiUsageMetadata("test", "research-report-test", 200, null, 150, 0m, 0m, 10, "completed", true, SafeMetadataJson: "{\"researchStage\":\"report\"}");
        return Task.FromResult(new ResearchReportProviderResult(draft, usage));
    }
}
