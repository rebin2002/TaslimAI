using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Taslim.Api.Upscaling;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class UpscalingTests : IClassFixture<GenerationJobsNoWorkerFactory>
{
    private readonly GenerationJobsNoWorkerFactory factory;

    public UpscalingTests(GenerationJobsNoWorkerFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Theory]
    [InlineData("1080p")]
    [InlineData("2K")]
    [InlineData("4k")]
    public async Task Create_accepts_supported_targets_and_replays_idempotently(string targetResolution)
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"upscale-{Guid.NewGuid():N}@example.com");
        var source = await CreateSourceAsset(auth);
        var request = new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            sourceAssetId = source.Id,
            targetResolution,
            title = "Master delivery",
        };

        var first = await SendWithCsrf<UpscalingJobDto>(client, HttpMethod.Post, "/api/upscaling/jobs", request, "upscale-request-001");
        var second = await SendWithCsrf<UpscalingJobDto>(client, HttpMethod.Post, "/api/upscaling/jobs", request, "upscale-request-001");

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(targetResolution.ToLowerInvariant() switch
        {
            "2k" => "2k",
            "4k" => "4k",
            _ => "1080p",
        }, first.TargetResolution);
        Assert.Equal("Pending", first.Status);
        Assert.Equal(source.Id, first.SourceAssetId);
        Assert.DoesNotContain("Provider", await client.GetStringAsync($"/api/upscaling/jobs/{first.Id}"));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(1, await db.UpscalingJobs.CountAsync(item => item.CreatedByUserId == auth.User.Id && item.IdempotencyKey == "upscale-request-001"));
        var persisted = await db.UpscalingJobs.AsNoTracking().SingleAsync(item => item.Id == first.Id);
        Assert.Contains(source.Id.ToString(), persisted.SourceProvenanceJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Invalid_target_and_cross_workspace_source_are_rejected_without_provider_details()
    {
        using var ownerClient = factory.CreateClient();
        var owner = await Register(ownerClient, $"upscale-owner-{Guid.NewGuid():N}@example.com");
        var source = await CreateSourceAsset(owner);
        var invalid = await SendWithCsrf(ownerClient, HttpMethod.Post, "/api/upscaling/jobs", new
        {
            workspaceId = owner.PersonalWorkspace.Id,
            sourceAssetId = source.Id,
            targetResolution = "8k",
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var invalidBody = await invalid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(UpscalingJobErrorCodes.TargetResolutionUnsupported, invalidBody.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("Provider", await invalid.Content.ReadAsStringAsync());

        using var otherClient = factory.CreateClient();
        var other = await Register(otherClient, $"upscale-other-{Guid.NewGuid():N}@example.com");
        var crossWorkspace = await SendWithCsrf(otherClient, HttpMethod.Post, "/api/upscaling/jobs", new
        {
            workspaceId = other.PersonalWorkspace.Id,
            sourceAssetId = source.Id,
            targetResolution = "1080p",
        });
        Assert.Equal(HttpStatusCode.BadRequest, crossWorkspace.StatusCode);
        var crossBody = await crossWorkspace.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(UpscalingJobErrorCodes.SourceAssetNotFound, crossBody.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Lifecycle_records_attempt_provenance_hands_off_to_qc_and_supports_rejected_retry()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"upscale-lifecycle-{Guid.NewGuid():N}@example.com");
        var source = await CreateSourceAsset(auth);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IUpscalingJobService>();
        var job = await service.CreateAsync(auth.User.Id, new CreateUpscalingJobRequest
        {
            WorkspaceId = auth.PersonalWorkspace.Id,
            SourceAssetId = source.Id,
            TargetResolution = "4K",
        }, idempotencyKey: $"lifecycle-{Guid.NewGuid():N}");

        var attempt = await service.BeginExecutionAsync(job.Id);
        Assert.NotNull(attempt);
        Assert.Equal(1, attempt!.AttemptNumber);
        var output = await CreateOutputAsset(db, auth, "Upscaled master");
        await service.CompleteAsync(job.Id, attempt.Id, output.Id, "{\"sourceAssetId\":\"source\",\"target\":\"4k\"}");
        var awaiting = await service.GetAsync(auth.User.Id, job.Id);
        Assert.Equal(UpscalingJobStatus.QualityControlPending, awaiting!.Status);
        Assert.Equal(output.Id, awaiting.OutputAssetId);
        Assert.Equal(UpscalingQualityStatus.Pending, Assert.Single(await db.UpscalingQualityHandoffs.Where(item => item.UpscalingJobId == job.Id).ToListAsync()).Status);

        var rejected = await service.ReviewQualityAsync(auth.User.Id, job.Id, new ReviewUpscalingQualityRequest { Approved = false, Note = "Needs another pass." });
        Assert.Equal(UpscalingJobStatus.QualityControlRejected, rejected!.Status);
        Assert.True(rejected.RetryCount < rejected.MaxRetryCount);
        var retried = await service.RetryAsync(auth.User.Id, job.Id);
        Assert.Equal(UpscalingJobStatus.Queued, retried!.Status);
        Assert.Equal(1, retried.RetryCount);
        var secondAttempt = await service.BeginExecutionAsync(job.Id);
        Assert.NotNull(secondAttempt);
        Assert.Equal(2, secondAttempt!.AttemptNumber);
        await service.FailAsync(job.Id, secondAttempt.Id, "OUTPUT_VALIDATION_FAILED", retryable: true);

        var failed = await service.GetAsync(auth.User.Id, job.Id);
        Assert.Equal(UpscalingJobStatus.Failed, failed!.Status);
        Assert.Equal("OUTPUT_VALIDATION_FAILED", failed.LastErrorCode);
        Assert.Equal(2, await db.UpscalingAttempts.CountAsync(item => item.UpscalingJobId == job.Id));
        Assert.Contains("assetId", failed.SourceProvenanceJson, StringComparison.Ordinal);
        Assert.DoesNotContain("provider", failed.SourceProvenanceJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Completed_quality_review_is_terminal_and_retry_is_not_allowed()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"upscale-approved-{Guid.NewGuid():N}@example.com");
        var source = await CreateSourceAsset(auth);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IUpscalingJobService>();
        var job = await service.CreateAsync(auth.User.Id, new CreateUpscalingJobRequest
        {
            WorkspaceId = auth.PersonalWorkspace.Id,
            SourceAssetId = source.Id,
            TargetResolution = "1080p",
        });
        var attempt = (await service.BeginExecutionAsync(job.Id))!;
        var output = await CreateOutputAsset(db, auth, "Approved master");
        await service.CompleteAsync(job.Id, attempt.Id, output.Id, "{\"qc\":true}");
        var completed = await service.ReviewQualityAsync(auth.User.Id, job.Id, new ReviewUpscalingQualityRequest { Approved = true });
        Assert.Equal(UpscalingJobStatus.Completed, completed!.Status);
        var retry = await Assert.ThrowsAsync<UpscalingValidationException>(() => service.RetryAsync(auth.User.Id, job.Id));
        Assert.Equal(UpscalingJobErrorCodes.InvalidLifecycleTransition, retry.Code);
    }

    [Fact]
    public async Task Default_provider_is_explicitly_unavailable_and_never_submits_work()
    {
        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IUpscalingProvider>();
        Assert.False(provider.IsAvailable);
        Assert.Empty(provider.SupportedResolutions);
        await Assert.ThrowsAsync<UpscalingProviderUnavailableException>(() => provider.SubmitAsync(new UpscalingProviderRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            UpscalingTargetResolutions.P1080,
            "video/mp4",
            1,
            "test-only",
            "{}")));
    }

    private async Task<AuthResponse> Register(HttpClient client, string email)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Upscaling Tester",
            email,
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private async Task<Asset> CreateSourceAsset(AuthResponse auth)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return await CreateAsset(db, auth, "Source video", AssetTypes.Video, "video/mp4");
    }

    private static async Task<Asset> CreateOutputAsset(TaslimDbContext db, AuthResponse auth, string name)
    {
        return await CreateAsset(db, auth, name, AssetTypes.Video, "video/mp4");
    }

    private static async Task<Asset> CreateAsset(TaslimDbContext db, AuthResponse auth, string name, string type, string contentType)
    {
        var file = new StoredFile
        {
            Id = Guid.NewGuid(),
            WorkspaceId = auth.PersonalWorkspace.Id,
            UserId = auth.User.Id,
            OriginalFileName = $"{name}.mp4",
            StoredFileName = $"{Guid.NewGuid():N}.mp4",
            ContentType = contentType,
            Extension = ".mp4",
            SizeBytes = 256,
            StorageProvider = FileStorageProviders.Local,
            StorageKey = $"test/{Guid.NewGuid():N}.mp4",
            Status = StoredFileStatus.Ready,
            CreatedAt = DateTime.UtcNow,
            ProcessedAt = DateTime.UtcNow,
        };
        var asset = new Asset
        {
            Id = Guid.NewGuid(),
            WorkspaceId = auth.PersonalWorkspace.Id,
            CreatedByUserId = auth.User.Id,
            StoredFileId = file.Id,
            Name = name,
            AssetType = type,
            MimeType = contentType,
            Status = AssetStatus.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.StoredFiles.Add(file);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        return asset;
    }

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object payload, string? idempotencyKey = null)
    {
        var response = await SendWithCsrf(client, method, path, payload, idempotencyKey);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload, string? idempotencyKey = null)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}
