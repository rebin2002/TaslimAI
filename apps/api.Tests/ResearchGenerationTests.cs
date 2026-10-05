using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Ai;
using Taslim.Api.Controllers;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Generation;
using Taslim.Api.Persistence;
using Taslim.Api.Research;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ResearchApiFactory : GenerationJobsApiFactory
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
            services.AddSingleton<IResearchSearchProvider, FakeResearchSearchProvider>();
            services.AddSingleton<IResearchReportProvider, FakeResearchReportProvider>();
        });
    }
}

public sealed class ResearchGenerationTests : IClassFixture<ResearchApiFactory>
{
    private readonly ResearchApiFactory factory;

    public ResearchGenerationTests(ResearchApiFactory factory)
    {
        this.factory = factory;
        Task.Delay(100).GetAwaiter().GetResult();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
        Task.Delay(100).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Research_job_creates_cited_report_asset_representations_sources_and_zero_charge_usage()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"research-{Guid.NewGuid():N}@example.com");
        var createdResponse = await SendWithCsrf<CreateResearchGenerationResponse>(client, HttpMethod.Post, "/api/research-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            question = "What are the current opportunities and risks for solar energy in Iraq?",
            depth = "standard",
            reportType = "research_report",
            language = "en",
            useWebSources = true,
            attachmentIds = Array.Empty<Guid>(),
        });
        var completed = await WaitForTerminal(client, createdResponse.Job.Id);
        Assert.True(completed.Status == "Succeeded", $"Status={completed.Status}; Code={completed.ErrorCode}; Message={completed.ErrorMessage}");
        Assert.Equal(100, completed.ProgressPercent);
        Assert.Contains("S1", completed.ResultJson, StringComparison.Ordinal);
        Assert.Equal(2, completed.Outputs.Count);

        var sourceResponse = await client.GetFromJsonAsync<JsonElement>($"/api/research-generation/jobs/{createdResponse.Job.Id}/sources");
        Assert.Equal(2, sourceResponse.GetProperty("sources").GetArrayLength());
        Assert.Equal("S1", sourceResponse.GetProperty("sources")[0].GetProperty("citationId").GetString());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.GenerationJobId == createdResponse.Job.Id);
        Assert.Equal(UsageFeature.Research, usage.Feature);
        Assert.Equal(0m, usage.ChargedAmount);
        Assert.Equal(UsageTransactionStatus.Completed, usage.Status);
        Assert.Contains("search", usage.SafeMetadataJson, StringComparison.Ordinal);
        var asset = await db.Assets.AsNoTracking().Include(item => item.Representations).SingleAsync(item => item.SourceGenerationJobId == createdResponse.Job.Id);
        Assert.Equal(AssetTypes.Research, asset.AssetType);
        Assert.Equal(new[] { AssetRepresentationTypes.Docx, AssetRepresentationTypes.Pdf }, asset.Representations.OrderBy(item => item.RepresentationType).Select(item => item.RepresentationType));
        Assert.Equal(2, await db.ResearchEvidence.CountAsync(item => item.GenerationJobId == createdResponse.Job.Id));
    }

    [Fact]
    public async Task Research_creation_is_workspace_authorized_and_requires_a_source_when_web_is_disabled()
    {
        using var first = factory.CreateClient();
        var firstAuth = await Register(first, $"research-first-{Guid.NewGuid():N}@example.com");
        using var second = factory.CreateClient();
        var secondAuth = await Register(second, $"research-second-{Guid.NewGuid():N}@example.com");
        var crossWorkspace = await SendWithCsrf(second, HttpMethod.Post, "/api/research-generation/jobs", new { workspaceId = firstAuth.PersonalWorkspace.Id, question = "A valid question", useWebSources = true, attachmentIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.Forbidden, crossWorkspace.StatusCode);
        var noSource = await SendWithCsrf(first, HttpMethod.Post, "/api/research-generation/jobs", new { workspaceId = firstAuth.PersonalWorkspace.Id, question = "A valid question", useWebSources = false, attachmentIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.BadRequest, noSource.StatusCode);
        var error = await noSource.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(GenerationJobErrorCodes.ResearchSourceUnavailable, error.GetProperty("error").GetProperty("code").GetString());
        _ = secondAuth;
    }

    [Fact]
    public async Task Research_source_export_is_private_deterministic_and_safe_for_spreadsheets()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, $"research-export-{Guid.NewGuid():N}@example.com");
        var created = await SendWithCsrf<CreateResearchGenerationResponse>(owner, HttpMethod.Post, "/api/research-generation/jobs", new
        {
            workspaceId = ownerAuth.PersonalWorkspace.Id,
            question = "Compare current solar opportunities",
            useWebSources = true,
            attachmentIds = Array.Empty<Guid>(),
        });
        var completed = await WaitForTerminal(owner, created.Job.Id);
        Assert.Equal("Succeeded", completed.Status);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var source = await db.ResearchSources.SingleAsync(item => item.GenerationJobId == created.Job.Id && item.CitationId == "S1");
            source.Title = "=SUM(A1:A2)";
            source.ExtractedText = "private extracted text must not be exported";
            source.SearchQuery = "PRIVATE SEARCH QUERY MUST NOT BE EXPORTED";
            await db.SaveChangesAsync();
        }
        using var response = await owner.GetAsync($"/api/research-generation/jobs/{created.Job.Id}/sources/export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal($"research-sources-{created.Job.Id:N}.csv", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var csv = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("citationId,sourceType,title", csv, StringComparison.Ordinal);
        Assert.Contains("\"'=SUM(A1:A2)\"", csv, StringComparison.Ordinal);
        Assert.Contains("https://example.gov/energy", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("private extracted text must not be exported", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("Official energy evidence.", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("searchQuery", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE SEARCH QUERY MUST NOT BE EXPORTED", csv, StringComparison.Ordinal);
        Assert.True(csv.IndexOf("S1", StringComparison.Ordinal) < csv.IndexOf("S2", StringComparison.Ordinal));

        using var other = factory.CreateClient();
        var otherAuth = await Register(other, $"research-export-other-{Guid.NewGuid():N}@example.com");
        using var forbidden = await other.GetAsync($"/api/research-generation/jobs/{created.Job.Id}/sources/export");
        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
        _ = otherAuth;
    }

    [Fact]
    public async Task Research_recovery_replaces_staged_sources_before_replaying_attempt()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"research-recovery-{Guid.NewGuid():N}@example.com");
        var created = await SendWithCsrf<CreateResearchGenerationResponse>(client, HttpMethod.Post, "/api/research-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            question = "Replay a cited research job safely",
            useWebSources = true,
            attachmentIds = Array.Empty<Guid>(),
        });
        var completed = await WaitForTerminal(client, created.Job.Id);
        Assert.Equal("Succeeded", completed.Status);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var job = await db.GenerationJobs.SingleAsync(item => item.Id == created.Job.Id);
            job.Status = GenerationJobStatus.Running;
            job.ConcurrencyToken = Guid.NewGuid();
            job.CancellationRequested = false;
            await db.SaveChangesAsync();
        }

        using var replayScope = factory.Services.CreateScope();
        var replayDb = replayScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var replayJob = await replayDb.GenerationJobs.AsNoTracking().SingleAsync(item => item.Id == created.Job.Id);
        var handler = replayScope.ServiceProvider.GetServices<IGenerationJobHandler>().Single(item => item.CanHandle(replayJob.JobType));
        var result = await handler.ExecuteAsync(replayJob, new Progress<int>(), CancellationToken.None);

        Assert.Equal(2, result.Outputs.Count);
        Assert.Equal(2, await replayDb.ResearchSources.CountAsync(item => item.GenerationJobId == created.Job.Id));
        Assert.Equal(2, await replayDb.ResearchEvidence.CountAsync(item => item.GenerationJobId == created.Job.Id));
    }

    [Fact]
    public async Task Research_source_details_and_exports_require_a_successful_job()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"research-terminal-sources-{Guid.NewGuid():N}@example.com");
        var created = await SendWithCsrf<CreateResearchGenerationResponse>(client, HttpMethod.Post, "/api/research-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            question = "Expose sources only for a completed report",
            useWebSources = true,
            attachmentIds = Array.Empty<Guid>(),
        });
        var completed = await WaitForTerminal(client, created.Job.Id);
        Assert.Equal("Succeeded", completed.Status);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var job = await db.GenerationJobs.SingleAsync(item => item.Id == created.Job.Id);
            job.Status = GenerationJobStatus.Failed;
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/research-generation/jobs/{created.Job.Id}/sources")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/research-generation/jobs/{created.Job.Id}/sources/export")).StatusCode);
    }

    [Fact]
    public async Task Research_uploaded_sources_keep_distinct_file_identity_when_filenames_match()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"research-duplicate-files-{Guid.NewGuid():N}@example.com");
        var firstFile = NewReadySourceFile(auth, "same-name.txt", "first source text");
        var secondFile = NewReadySourceFile(auth, "same-name.txt", "second source text");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.StoredFiles.AddRange(firstFile, secondFile);
            await db.SaveChangesAsync();
        }

        var created = await SendWithCsrf<CreateResearchGenerationResponse>(client, HttpMethod.Post, "/api/research-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            question = "Compare the two uploaded sources",
            useWebSources = false,
            attachmentIds = new[] { firstFile.Id, secondFile.Id },
        });
        var completed = await WaitForTerminal(client, created.Job.Id);
        Assert.True(completed.Status == "Succeeded", $"Status={completed.Status}; Code={completed.ErrorCode}; Message={completed.ErrorMessage}");

        using var verificationScope = factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var sources = await verificationDb.ResearchSources.AsNoTracking()
            .Where(source => source.GenerationJobId == created.Job.Id)
            .OrderBy(source => source.Rank)
            .ToArrayAsync();
        Assert.Equal(2, sources.Length);
        Assert.Equal(firstFile.Id, sources[0].StoredFileId);
        Assert.Equal(secondFile.Id, sources[1].StoredFileId);
    }

    private static StoredFile NewReadySourceFile(AuthResponse auth, string name, string extractedText) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = auth.PersonalWorkspace.Id,
        UserId = auth.User.Id,
        OriginalFileName = name,
        StoredFileName = $"{Guid.NewGuid():N}.txt",
        ContentType = "text/plain",
        Extension = ".txt",
        SizeBytes = extractedText.Length,
        StorageProvider = FileStorageProviders.Local,
        StorageKey = $"research-test/{Guid.NewGuid():N}.txt",
        Status = StoredFileStatus.Ready,
        CreatedAt = DateTime.UtcNow,
        ProcessedAt = DateTime.UtcNow,
        TextExtractionStatus = FileExtractionStatus.Ready,
        ExtractedText = extractedText,
        ExtractedTextLength = extractedText.Length,
    };

    private static async Task<AuthResponse> Register(HttpClient client, string email)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName = "Research Tester", email, password = "StrongPassword!123", preferredLanguage = "en" });
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

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? body)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        if (body is not null) request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object body)
    {
        using var response = await SendWithCsrf(client, method, path, body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var dto = await response.Content.ReadFromJsonAsync<T>();
        return dto!;
    }
}

public sealed class FakeResearchSearchProvider : IResearchSearchProvider
{
    public Task<ResearchSearchResult> SearchAsync(ResearchSearchRequest request, ResearchGenerationOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sources = new[]
        {
            new ResearchSourceCandidate("S1", "https://example.gov/energy", "https://example.gov/energy", "Energy ministry report", "example.gov", "Example Ministry", null, DateTime.UtcNow, "web", "Official energy evidence.", "Official energy evidence.", request.Plan.SearchQueries[0], 1, true, null),
            new ResearchSourceCandidate("S2", "https://example.org/market", "https://example.org/market", "Market outlook", "example.org", "Example Institute", null, DateTime.UtcNow, "web", "Independent market evidence.", "Independent market evidence.", request.Plan.SearchQueries[0], 2, true, null),
        };
        var evidence = sources.Select(source => new ResearchEvidenceCandidate(source.CitationId, "market evidence", source.ExtractedText!, null, null)).ToArray();
        var usage = new AiUsageMetadata("test", "research-search-test", 100, null, 50, 0m, 0m, 5, "completed", true, SafeMetadataJson: "{\"researchStage\":\"search\"}");
        return Task.FromResult(new ResearchSearchResult(sources, evidence, usage));
    }
}

public sealed class FakeResearchReportProvider : IResearchReportProvider
{
    public Task<ResearchReportProviderResult> GenerateAsync(ResearchReportPrompt prompt, ResearchGenerationOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var draft = new ResearchDraft
        {
            Title = "Solar energy research",
            Subtitle = "Cited overview",
            Language = "en",
            ExecutiveSummary = "The evidence indicates a developing opportunity with material execution risks.",
            KeyFindings = [new ResearchReportBlock { Type = ResearchBlockTypes.KeyFinding, Text = "Official evidence identifies the opportunity. [S1]", CitationIds = ["S1"] }],
            Sections = [new ResearchSection { Heading = "Evidence and risks", Blocks = [new ResearchReportBlock { Type = ResearchBlockTypes.Paragraph, Text = "Independent market evidence adds context. [S2]", CitationIds = ["S2"] }] }],
            Conclusion = "Review the source evidence before making a decision.",
            Sources = ["S1", "S2"],
        };
        var usage = new AiUsageMetadata("test", "research-report-test", 200, null, 150, 0m, 0m, 10, "completed", true, SafeMetadataJson: "{\"researchStage\":\"report\"}");
        return Task.FromResult(new ResearchReportProviderResult(draft, usage));
    }
}
