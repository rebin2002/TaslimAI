using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Operations;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class AdminOperationsTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public AdminOperationsTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Operations_dashboard_is_admin_only_and_uses_real_aggregate_data()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Operations Administrator");
        await AddAdminRole(auth.User.Email);
        var now = DateTime.UtcNow;
        var failedJobId = Guid.NewGuid();
        var runningJobId = Guid.NewGuid();
        var completedJobId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var file = new StoredFile
            {
                Id = Guid.NewGuid(), WorkspaceId = auth.PersonalWorkspace.Id, UserId = auth.User.Id,
                OriginalFileName = "operations.json", StoredFileName = "operations.json", ContentType = "application/json",
                Extension = ".json", SizeBytes = 2_048, StorageProvider = FileStorageProviders.Local, StorageKey = $"operations/{Guid.NewGuid():N}",
                Status = StoredFileStatus.Ready, TextExtractionStatus = FileExtractionStatus.NotApplicable, CreatedAt = now,
            };
            var failedJob = new GenerationJob
            {
                Id = failedJobId, WorkspaceId = auth.PersonalWorkspace.Id, CreatedByUserId = auth.User.Id,
                JobType = GenerationJobTypes.ImageGenerate, Status = GenerationJobStatus.Failed, Title = "Failed image",
                InputJson = "{}", ErrorCode = GenerationJobErrorCodes.ImageGenerationFailed, ErrorMessage = "Internal provider detail must not be returned.",
                CreatedAt = now.AddMinutes(-5), FailedAt = now.AddMinutes(-4), ConcurrencyToken = Guid.NewGuid(),
            };
            var runningJob = new GenerationJob
            {
                Id = runningJobId, WorkspaceId = auth.PersonalWorkspace.Id, CreatedByUserId = auth.User.Id,
                JobType = GenerationJobTypes.VoiceGenerate, Status = GenerationJobStatus.Running, Title = "Running voice",
                InputJson = "{}", ProgressPercent = 55, CreatedAt = now.AddMinutes(-3), QueuedAt = now.AddMinutes(-3), StartedAt = now.AddMinutes(-2), ConcurrencyToken = Guid.NewGuid(),
            };
            var completedJob = new GenerationJob
            {
                Id = completedJobId, WorkspaceId = auth.PersonalWorkspace.Id, CreatedByUserId = auth.User.Id,
                JobType = GenerationJobTypes.DocumentGenerate, Status = GenerationJobStatus.Succeeded, Title = "Completed document",
                InputJson = "{}", ProgressPercent = 100, CreatedAt = now.AddMinutes(-8), CompletedAt = now.AddMinutes(-1), ConcurrencyToken = Guid.NewGuid(),
            };
            var rateLimitedJob = new GenerationJob
            {
                Id = Guid.NewGuid(), WorkspaceId = auth.PersonalWorkspace.Id, CreatedByUserId = auth.User.Id,
                JobType = GenerationJobTypes.DocumentGenerate, Status = GenerationJobStatus.Failed, InputJson = "{\"prompt\":\"private prompt\"}",
                ErrorCode = GenerationJobErrorCodes.DocumentProviderRateLimited, ErrorMessage = "raw provider payload must not be returned",
                RetryCount = 1, CreatedAt = now.AddMinutes(-7), FailedAt = now.AddMinutes(-6), ConcurrencyToken = Guid.NewGuid(),
            };
            var qcFailedJob = new GenerationJob
            {
                Id = Guid.NewGuid(), WorkspaceId = auth.PersonalWorkspace.Id, CreatedByUserId = auth.User.Id,
                JobType = GenerationJobTypes.ImageGenerate, Status = GenerationJobStatus.Failed, InputJson = "{\"prompt\":\"another private prompt\"}",
                ErrorCode = GenerationJobErrorCodes.ImageOutputInvalid, ErrorMessage = "another raw provider payload must not be returned",
                CreatedAt = now.AddMinutes(-9), FailedAt = now.AddMinutes(-8), ConcurrencyToken = Guid.NewGuid(),
            };
            db.StoredFiles.Add(file);
            db.Assets.Add(new Asset
            {
                Id = Guid.NewGuid(), WorkspaceId = auth.PersonalWorkspace.Id, CreatedByUserId = auth.User.Id, StoredFileId = file.Id,
                SourceGenerationJobId = completedJob.Id, Name = "Operations report", AssetType = AssetTypes.Document,
                MimeType = "application/json", Status = AssetStatus.Active, CreatedAt = now, UpdatedAt = now,
            });
            db.GenerationJobs.AddRange(failedJob, runningJob, completedJob, rateLimitedJob, qcFailedJob);
            db.UsageTransactions.Add(new UsageTransaction
            {
                Id = Guid.NewGuid(), WorkspaceId = auth.PersonalWorkspace.Id, UserId = auth.User.Id, GenerationJobId = completedJob.Id,
                RequestId = $"admin-operations-{Guid.NewGuid():N}", Feature = UsageFeature.Document, Provider = "test-provider", Model = "test-model",
                Status = UsageTransactionStatus.Completed, InputTokens = 300, CachedInputTokens = 20, OutputTokens = 500,
                ImageInputTokens = 7, ImageOutputTokens = 11, ProviderCostUsd = 1.25m, ChargedAmount = 4.50m,
                LatencyMs = 840, EstimatedProviderCostUsd = 1.50m, Currency = "USD", CostBasis = UsageCostBasis.Actual, CreatedAt = now, CompletedAt = now,
            });
            db.UsageTransactions.Add(new UsageTransaction
            {
                Id = Guid.NewGuid(), WorkspaceId = auth.PersonalWorkspace.Id, UserId = auth.User.Id,
                RequestId = $"admin-operations-chat-{Guid.NewGuid():N}", Feature = UsageFeature.Chat, Provider = "mock", Model = "test-chat",
                Status = UsageTransactionStatus.Completed, LatencyMs = 120, ProviderCostUsd = 0m, ChargedAmount = 0m,
                Currency = "USD", CostBasis = UsageCostBasis.Estimated, CreatedAt = now, CompletedAt = now,
            });
            await db.SaveChangesAsync();
        }

        var from = Uri.EscapeDataString(now.AddDays(-1).ToString("O"));
        var to = Uri.EscapeDataString(now.AddDays(1).ToString("O"));
        var response = await client.GetAsync($"/api/admin/operations/dashboard?fromUtc={from}&toUtc={to}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var dashboard = JsonSerializer.Deserialize<AdminOperationsDashboardDto>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(dashboard);
        Assert.Contains(dashboard!.Generation.ByStatus, item => item.Key == "Running" && item.Count >= 1);
        Assert.Contains(dashboard.Generation.ByStudio, item => item.Key == GenerationJobTypes.ImageGenerate && item.Count >= 1);
        Assert.Contains(dashboard.Generation.RunningJobs, item => item.JobId == runningJobId && item.ProgressPercent == 55);
        Assert.Contains(dashboard.Generation.RecentFailures, item => item.JobId == failedJobId && item.ErrorCode == GenerationJobErrorCodes.ImageGenerationFailed);
        Assert.True(dashboard.Usage.RequestCount >= 1);
        Assert.True(dashboard.Usage.ProviderCostUsd >= 1.25m);
        Assert.True(dashboard.Usage.CustomerChargesUsd >= 4.50m);
        Assert.Contains(dashboard.Usage.ByFeature, item => item.Feature == "Document" && item.InputTokens >= 300 && item.OutputTokens >= 500);
        Assert.True(dashboard.AssetsAndStorage.TotalStoredFiles >= 1);
        Assert.Contains(dashboard.AssetsAndStorage.AssetsByType, item => item.Key == AssetTypes.Document && item.Count >= 1);
        Assert.False(dashboard.Billing.CustomerChargingEnabled);
        Assert.Equal("unconfigured", dashboard.Billing.ConfiguredProvider);
        Assert.False(dashboard.Billing.PaymentProviderConfigured);
        Assert.True(dashboard.Signals.RunningJobCount >= 1);
        Assert.NotNull(dashboard.Signals.LastCompletedGenerationAt);

        var imageProvider = Assert.Single(dashboard.Providers, item => item.Key == "openai-image");
        Assert.False(imageProvider.Enabled);
        Assert.False(imageProvider.Configured);
        Assert.Equal("disabled", imageProvider.Status);
        Assert.True(imageProvider.RecentFailureCount >= 2);
        Assert.True(imageProvider.QualityControlFailureCount >= 1);
        Assert.True(imageProvider.RetryCount >= 0);
        Assert.Contains(imageProvider.RecentFailures, item => item.ErrorCode == GenerationJobErrorCodes.ImageGenerationFailed);
        var documentProvider = Assert.Single(dashboard.Providers, item => item.Key == "openai-document");
        Assert.True(documentProvider.RecentSuccessCount >= 1);
        Assert.True(documentProvider.RateLimitEventCount >= 1);
        Assert.Equal(840, documentProvider.AverageLatencyMs);
        Assert.True(documentProvider.EstimatedProviderCostUsd >= 1.50m);
        Assert.True(documentProvider.ActualProviderCostUsd >= 1.25m);
        Assert.False(documentProvider.FallbackTelemetryRecorded);
        Assert.Equal("disabled", Assert.Single(dashboard.Providers, item => item.Key == "mubert").Status);
        var chatProvider = Assert.Single(dashboard.Providers, item => item.Key == "chat");
        Assert.True(chatProvider.Enabled);
        Assert.True(chatProvider.Configured);
        Assert.Equal("operational", chatProvider.Status);
        Assert.Equal(120, chatProvider.AverageLatencyMs);

        Assert.DoesNotContain("Internal provider detail", json);
        Assert.DoesNotContain("raw provider payload", json);
        Assert.DoesNotContain("private prompt", json);
        Assert.DoesNotContain("InputJson", json);
        Assert.DoesNotContain("ErrorMessage", json);
        Assert.DoesNotContain("ApiKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storageKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("email", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("providerPaymentReference", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Normal_anonymous_and_disabled_administrators_cannot_read_operations_dashboard()
    {
        using var normalUser = factory.CreateClient();
        var normal = await Register(normalUser, "Normal Operations User");
        Assert.Equal(HttpStatusCode.Forbidden, (await normalUser.GetAsync("/api/admin/operations/dashboard")).StatusCode);

        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/operations/dashboard")).StatusCode);

        using var disabledAdmin = factory.CreateClient();
        var disabled = await Register(disabledAdmin, "Disabled Operations Administrator");
        await AddAdminRole(disabled.User.Email);
        using (var scope = factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByEmailAsync(disabled.User.Email);
            Assert.NotNull(user);
            user!.IsActive = false;
            Assert.True((await userManager.UpdateAsync(user)).Succeeded);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await disabledAdmin.GetAsync("/api/admin/operations/dashboard")).StatusCode);
    }

    [Fact]
    public async Task Auth_session_exposes_only_a_safe_admin_capability_flag_for_page_gating()
    {
        using var client = factory.CreateClient();
        var normal = await Register(client, "Page Gate Customer");
        Assert.False(normal.User.IsAdmin);
        await AddAdminRole(normal.User.Email);

        var updated = await client.GetFromJsonAsync<AuthResponse>("/api/auth/me");
        Assert.NotNull(updated);
        Assert.True(updated!.User.IsAdmin);
    }

    [Fact]
    public async Task Movie_operations_report_queue_age_retries_qc_ingestion_accounting_and_workers_without_private_payloads()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Movie Operations Administrator");
        await AddAdminRole(auth.User.Email);
        var now = DateTime.UtcNow;
        var queuedJobId = Guid.NewGuid();
        var failedJobId = Guid.NewGuid();
        var completedJobId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.GenerationJobs.AddRange(
                new GenerationJob
                {
                    Id = queuedJobId, WorkspaceId = auth.PersonalWorkspace.Id, CreatedByUserId = auth.User.Id,
                    JobType = GenerationJobTypes.MovieClipGenerate, Status = GenerationJobStatus.Queued,
                    InputJson = "{\"prompt\":\"private movie prompt\"}", CreatedAt = now.AddMinutes(-8), QueuedAt = now.AddMinutes(-7), RetryCount = 2,
                    ConcurrencyToken = Guid.NewGuid(),
                },
                new GenerationJob
                {
                    Id = failedJobId, WorkspaceId = auth.PersonalWorkspace.Id, CreatedByUserId = auth.User.Id,
                    JobType = GenerationJobTypes.MovieClipGenerate, Status = GenerationJobStatus.Failed,
                    InputJson = "{\"prompt\":\"another private prompt\"}", ErrorCode = GenerationJobErrorCodes.MovieProviderUnavailable,
                    ErrorMessage = "provider payload must never be returned", CreatedAt = now.AddMinutes(-6), FailedAt = now.AddMinutes(-5), RetryCount = 1,
                    ConcurrencyToken = Guid.NewGuid(),
                },
                new GenerationJob
                {
                    Id = completedJobId, WorkspaceId = auth.PersonalWorkspace.Id, CreatedByUserId = auth.User.Id,
                    JobType = GenerationJobTypes.MovieQuickGenerate, Status = GenerationJobStatus.Succeeded,
                    InputJson = "{}", CreatedAt = now.AddMinutes(-4), CompletedAt = now.AddMinutes(-3),
                    ConcurrencyToken = Guid.NewGuid(),
                });
            db.UsageTransactions.Add(new UsageTransaction
            {
                Id = Guid.NewGuid(), WorkspaceId = auth.PersonalWorkspace.Id, UserId = auth.User.Id, GenerationJobId = completedJobId,
                RequestId = "movie-ops-accounting", Feature = UsageFeature.Movie, Provider = "internal", Model = "internal",
                Status = UsageTransactionStatus.Completed, EstimatedProviderCostUsd = 2.25m, ProviderCostUsd = 1.75m,
                ChargedAmount = 0m, Currency = "USD", CostBasis = UsageCostBasis.Actual, CreatedAt = now.AddMinutes(-3), CompletedAt = now.AddMinutes(-3),
            });
            db.GenerationWorkerHeartbeats.Add(new GenerationWorkerHeartbeat
            {
                Id = Guid.NewGuid(), WorkerId = "test-worker-0", InstanceId = "test-instance", WorkerIndex = 0, WorkerConcurrency = 1,
                Status = GenerationWorkerStatuses.Running, StartedAt = now.AddHours(-1), LastSeenAt = now, ActiveJobId = queuedJobId,
            });
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync($"/api/admin/operations/dashboard?fromUtc={Uri.EscapeDataString(now.AddDays(-1).ToString("O"))}&toUtc={Uri.EscapeDataString(now.AddDays(1).ToString("O"))}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var dashboard = JsonSerializer.Deserialize<AdminOperationsDashboardDto>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(dashboard);
        Assert.True(dashboard!.Movie.MovieJobCountInRange >= 3);
        Assert.True(dashboard.Movie.OldestQueueAgeSeconds >= 7 * 60);
        Assert.True(dashboard.Movie.TotalRetryCount >= 3);
        Assert.True(dashboard.Movie.ProviderDisabledFailureCount >= 1);
        Assert.True(dashboard.Movie.MissingAccountingEvidenceCount >= 1);
        Assert.True(dashboard.Movie.CompletedJobsWithoutAssetCount >= 1);
        Assert.Contains(dashboard.Workers.Workers, worker => worker.WorkerId == "test-worker-0" && !worker.IsStale);
        Assert.Equal("disabled", dashboard.Movie.ProviderStatus);
        Assert.DoesNotContain("private movie prompt", json);
        Assert.DoesNotContain("provider payload must never be returned", json);
        Assert.DoesNotContain("InputJson", json);
    }

    [Fact]
    public async Task Admin_can_recover_only_an_expired_movie_lease_and_the_action_is_audited()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Movie Recovery Administrator");
        await AddAdminRole(auth.User.Email);
        var jobId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.GenerationJobs.Add(new GenerationJob
            {
                Id = jobId, WorkspaceId = auth.PersonalWorkspace.Id, CreatedByUserId = auth.User.Id,
                JobType = GenerationJobTypes.MovieClipGenerate, Status = GenerationJobStatus.Running, InputJson = "{}",
                CreatedAt = now.AddMinutes(-30), StartedAt = now.AddMinutes(-20), ClaimExpiresAt = now.AddMinutes(-5), RetryCount = 3,
                ConcurrencyToken = Guid.NewGuid(),
            });
            await db.SaveChangesAsync();
        }

        var response = await SendWithCsrf(client, HttpMethod.Post, $"/api/admin/operations/jobs/{jobId}/recover", new { reason = "Worker lease expired during the disabled-provider acceptance test." });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var recovery = await response.Content.ReadFromJsonAsync<AdminJobRecoveryResult>();
        Assert.NotNull(recovery);
        Assert.Equal(jobId, recovery!.JobId);
        Assert.Equal(GenerationJobStatus.Queued, recovery.Status);
        Assert.Equal(4, recovery.RetryCount);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var job = await verifyDb.GenerationJobs.SingleAsync(item => item.Id == jobId);
        Assert.Equal(GenerationJobStatus.Queued, job.Status);
        Assert.Null(job.ClaimExpiresAt);
        var audit = await verifyDb.AdminOperationAuditEvents.SingleAsync(item => item.TargetId == jobId);
        Assert.Equal(AdminOperationActions.RecoverExpiredGenerationJob, audit.Action);
        Assert.Equal(AdminOperationOutcomes.Succeeded, audit.Outcome);
        Assert.Equal("status=Running;retryCount=3", audit.BeforeState);
        Assert.Equal("status=Queued;retryCount=4", audit.AfterState);
        Assert.DoesNotContain("prompt", audit.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Replayed_admin_recovery_request_returns_the_original_result_once()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Movie Recovery Replay Administrator");
        await AddAdminRole(auth.User.Email);
        var jobId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        const string requestId = "admin-recovery-replay-20261005-0001";

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.GenerationJobs.Add(new GenerationJob
            {
                Id = jobId, WorkspaceId = auth.PersonalWorkspace.Id, CreatedByUserId = auth.User.Id,
                JobType = GenerationJobTypes.MovieClipGenerate, Status = GenerationJobStatus.Running, InputJson = "{}",
                CreatedAt = now.AddMinutes(-30), StartedAt = now.AddMinutes(-20), ClaimExpiresAt = now.AddMinutes(-5), RetryCount = 1,
                ConcurrencyToken = Guid.NewGuid(),
            });
            await db.SaveChangesAsync();
        }

        var firstResponse = await SendWithCsrf(
            client,
            HttpMethod.Post,
            $"/api/admin/operations/jobs/{jobId}/recover",
            new { reason = "Recover the expired movie worker lease after a response timeout." },
            requestId);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        var first = await firstResponse.Content.ReadFromJsonAsync<AdminJobRecoveryResult>();
        Assert.NotNull(first);

        var replayResponse = await SendWithCsrf(
            client,
            HttpMethod.Post,
            $"/api/admin/operations/jobs/{jobId}/recover",
            new { reason = "The client is retrying the same recovery command." },
            requestId);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        var replay = await replayResponse.Content.ReadFromJsonAsync<AdminJobRecoveryResult>();
        Assert.NotNull(replay);
        Assert.Equal(first!.JobId, replay!.JobId);
        Assert.Equal(first.Status, replay.Status);
        Assert.Equal(first.RetryCount, replay.RetryCount);
        Assert.Equal(first.QueuedAt, replay.QueuedAt);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var job = await verifyDb.GenerationJobs.SingleAsync(item => item.Id == jobId);
        Assert.Equal(GenerationJobStatus.Queued, job.Status);
        Assert.Equal(2, job.RetryCount);
        Assert.Equal(1, await verifyDb.AdminOperationAuditEvents.CountAsync(item => item.TargetId == jobId));
    }

    private async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"admin-operations-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private async Task AddAdminRole(string email)
    {
        using var scope = factory.Services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        if (!await roleManager.RoleExistsAsync(AdminPolicies.Role))
            Assert.True((await roleManager.CreateAsync(new IdentityRole<Guid>(AdminPolicies.Role))).Succeeded);
        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);
        Assert.True((await userManager.AddToRoleAsync(user!, AdminPolicies.Role)).Succeeded);
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload, string? requestId = null)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (!string.IsNullOrWhiteSpace(requestId)) request.Headers.Add("X-Request-ID", requestId);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}
