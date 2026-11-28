using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Music;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Second post-billing duplicate-submission lane: lease recovery. When a worker's claim expires, the
/// recovery sweep re-queues the job up to MaxAutomaticRetries times and the requeued attempt runs the
/// handler from the beginning. For music that means a fresh billable POST to the provider, because
/// neither Stable Audio nor Mubert accepts an idempotency key on creation and the provider operation
/// id is only known once the call returns. A worker that dies after submitting (crash loop, OOM kill,
/// a rollout mid-generation) would therefore pay for up to four generations for one customer request
/// while the first artifact is orphaned. Music is terminalized instead of re-dispatched, and the
/// customer keeps a deliberate single retry; every other job type keeps the existing recovery.
/// </summary>
public sealed class MusicLeaseRecoveryDuplicateSubmissionTests : IClassFixture<MusicLeaseRecoveryApiFactory>
{
    private readonly MusicLeaseRecoveryApiFactory factory;

    public MusicLeaseRecoveryDuplicateSubmissionTests(MusicLeaseRecoveryApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Expired_music_lease_terminates_without_re_dispatching_the_billable_provider_request()
    {
        var submissionsBefore = Provider().SubmittedGenerations;
        using var client = factory.CreateClient();
        var auth = await Register(client, $"music-lease-{Guid.NewGuid():N}@example.com");
        var created = await SendWithCsrf(client, HttpMethod.Post, "/api/music-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            title = "Lease recovery isolation",
            description = "A calm instrumental bed used to pin lease-recovery rebill prevention.",
            purpose = "Integration test for automatic duplicate paid submission prevention.",
            genre = "cinematic",
            mood = "calm",
            durationSeconds = 60,
            vocalPreference = "instrumental",
            language = "auto",
        });
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var job = (await created.Content.ReadFromJsonAsync<CreateMusicGenerationResponse>(JsonOptions))!.Job;

        // The application worker is disabled in this factory, so nothing has been submitted yet.
        Assert.Equal(submissionsBefore, Provider().SubmittedGenerations);

        var claimStart = DateTime.UtcNow;
        Guid staleToken;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var row = await db.GenerationJobs.FirstAsync(item => item.Id == job.Id);
            row.Status = GenerationJobStatus.Running;
            row.StartedAt = claimStart.AddMinutes(-5);
            row.ProgressPercent = 55;
            row.ClaimExpiresAt = claimStart.AddMinutes(-1); // expired lease: stuck Running
            staleToken = row.ConcurrencyToken;
            await db.SaveChangesAsync();
        }

        using var stopping = new CancellationTokenSource();
        var worker = CreateWorker(maxAutomaticRetries: 3);
        await ((IHostedService)worker).StartAsync(stopping.Token);

        var failed = await WaitForStatusAsync(client, job.Id, "Failed");
        Assert.Equal("Failed", failed.Status);
        Assert.Equal(GenerationJobErrorCodes.MusicProviderSubmissionUnconfirmed, failed.ErrorCode);
        Assert.NotNull(failed.ErrorMessage);
        Assert.Equal(GenerationJobErrorMessages.For(GenerationJobErrorCodes.MusicProviderSubmissionUnconfirmed), failed.ErrorMessage);
        // The dead attempt's progress is retained, exactly as the poison terminal does.
        Assert.Equal(55, failed.ProgressPercent);
        Assert.Equal(0, failed.RetryCount);

        stopping.Cancel();
        await ((IHostedService)worker).StopAsync(CancellationToken.None);

        // No re-dispatch means no second billable provider request, now or later.
        Assert.Equal(submissionsBefore, Provider().SubmittedGenerations);

        using var finalScope = factory.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var durable = await finalDb.GenerationJobs.AsNoTracking().FirstAsync(item => item.Id == job.Id);
        Assert.NotEqual(staleToken, durable.ConcurrencyToken);
        Assert.Null(durable.ClaimExpiresAt);
        Assert.Equal(0, durable.RetryCount);

        // No attempt/dispatch evidence exists, so recovery records a confirmed zero provider cost;
        // customer charge and reversal remain zero as on every failure.
        var usage = await finalDb.UsageTransactions.AsNoTracking()
            .SingleAsync(item => item.RequestId == $"generation:{job.Id:N}");
        Assert.Equal(UsageTransactionStatus.Failed, usage.Status);
        Assert.Equal(0m, usage.ProviderCostUsd);
        Assert.True(usage.ProviderCostKnown);
        Assert.Equal(UsageCostBasis.Actual, usage.CostBasis);
        Assert.Equal(0m, usage.ChargedAmount);
        Assert.Equal(0m, usage.ReversedAmount);

        // Fail-closed policy (explicit product directive): an informed customer retry is still a
        // SECOND billable submission while the accepted outcome is unreconciled, so the retry
        // endpoint refuses this code as well. Earlier this lane asserted the opposite posture,
        // written when the code was only reachable from lease recovery; the no-resubmission
        // guarantee now holds against the member too, not just against the worker.
        var retry = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{job.Id}/retry", null, $"music-lease-retry-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.BadRequest, retry.StatusCode);
        using var refusal = JsonDocument.Parse(await retry.Content.ReadAsStringAsync());
        Assert.Equal(
            GenerationJobErrorCodes.MusicSubmissionRetryBlocked,
            refusal.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, await finalDb.GenerationJobs.AsNoTracking().CountAsync(item => item.RetryOfJobId == job.Id));
    }

    [Fact]
    public async Task Successful_music_records_only_provider_confirmed_cost_and_keeps_customer_charge_zero()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"music-cost-success-{Guid.NewGuid():N}@example.com");
        var created = await SendWithCsrf(client, HttpMethod.Post, "/api/music-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            title = "Known Music cost",
            description = "Deterministic provider-cost accounting test.",
            purpose = "Verify confirmed Music provider spend without a customer charge.",
            genre = "cinematic",
            mood = "calm",
            durationSeconds = 60,
            vocalPreference = "instrumental",
            language = "auto",
        });
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var job = (await created.Content.ReadFromJsonAsync<CreateMusicGenerationResponse>(JsonOptions))!.Job;

        using var stopping = new CancellationTokenSource();
        var worker = CreateWorker(maxAutomaticRetries: 1);
        await ((IHostedService)worker).StartAsync(stopping.Token);
        try
        {
            var succeeded = await WaitForStatusAsync(client, job.Id, "Succeeded");
            Assert.Equal("Succeeded", succeeded.Status);
        }
        finally
        {
            stopping.Cancel();
            await ((IHostedService)worker).StopAsync(CancellationToken.None);
        }

        using var finalScope = factory.Services.CreateScope();
        var db = finalScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.GenerationJobId == job.Id);
        Assert.Equal(UsageTransactionStatus.Completed, usage.Status);
        Assert.True(usage.ProviderCostKnown);
        Assert.Equal(0.004m, usage.ProviderCostUsd);
        Assert.Equal(UsageCostBasis.Actual, usage.CostBasis);
        Assert.Equal(0m, usage.ChargedAmount);
        Assert.Equal(0m, usage.ReversedAmount);

        var attempt = await db.GenerationProviderAttempts.AsNoTracking().SingleAsync(item => item.GenerationJobId == job.Id);
        Assert.Equal(GenerationProviderAttemptStatus.Succeeded, attempt.Status);
        Assert.True(attempt.ActualProviderCostKnown);
        Assert.Equal(0.004m, attempt.ActualProviderCostUsd);
        using var metadata = JsonDocument.Parse(attempt.SafeMetadataJson!);
        var settlement = metadata.RootElement.GetProperty("musicProviderCostSettlement");
        Assert.Equal("settled", settlement.GetProperty("status").GetString());
        Assert.Equal("provider_response", settlement.GetProperty("source").GetString());
    }

    [Fact]
    public async Task Successful_music_with_estimate_but_no_provider_actual_remains_unknown_and_manual_required()
    {
        var provider = Provider();
        provider.SetCosts(estimatedCostUsd: 0.125m, actualCostUsd: null);
        try
        {
            using var client = factory.CreateClient();
            var auth = await Register(client, $"music-cost-unknown-{Guid.NewGuid():N}@example.com");
            var created = await SendWithCsrf(client, HttpMethod.Post, "/api/music-generation/jobs", new
            {
                workspaceId = auth.PersonalWorkspace.Id,
                title = "Unknown Music cost",
                description = "Deterministic provider-cost accounting test.",
                purpose = "An estimate must not be promoted to actual provider spend.",
                genre = "cinematic",
                mood = "calm",
                durationSeconds = 60,
                vocalPreference = "instrumental",
                language = "auto",
            });
            Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
            var job = (await created.Content.ReadFromJsonAsync<CreateMusicGenerationResponse>(JsonOptions))!.Job;

            using var stopping = new CancellationTokenSource();
            var worker = CreateWorker(maxAutomaticRetries: 1);
            await ((IHostedService)worker).StartAsync(stopping.Token);
            try
            {
                var succeeded = await WaitForStatusAsync(client, job.Id, "Succeeded");
                Assert.Equal("Succeeded", succeeded.Status);
            }
            finally
            {
                stopping.Cancel();
                await ((IHostedService)worker).StopAsync(CancellationToken.None);
            }

            using var finalScope = factory.Services.CreateScope();
            var db = finalScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.GenerationJobId == job.Id);
            Assert.Equal(UsageTransactionStatus.Completed, usage.Status);
            Assert.False(usage.ProviderCostKnown);
            Assert.Equal(0m, usage.ProviderCostUsd);
            Assert.Equal(UsageCostBasis.Unknown, usage.CostBasis);
            Assert.Equal(0m, usage.ChargedAmount);
            Assert.Equal(0m, usage.ReversedAmount);

            var attempt = await db.GenerationProviderAttempts.AsNoTracking().SingleAsync(item => item.GenerationJobId == job.Id);
            Assert.False(attempt.ActualProviderCostKnown);
            Assert.Null(attempt.ActualProviderCostUsd);
            using var metadata = JsonDocument.Parse(attempt.SafeMetadataJson!);
            Assert.Equal("manual_required", metadata.RootElement.GetProperty("musicProviderCostSettlement").GetProperty("status").GetString());
        }
        finally
        {
            provider.SetCosts(estimatedCostUsd: 0.004m, actualCostUsd: 0.004m);
        }
    }

    [Fact]
    public async Task Manual_settlement_is_admin_policy_guarded_audited_one_time_and_never_changes_customer_money()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"music-cost-manual-{Guid.NewGuid():N}@example.com");
        var created = await SendWithCsrf(client, HttpMethod.Post, "/api/music-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            title = "Manual Music cost",
            description = "Deterministic manual reconciliation test.",
            purpose = "Exercise the admin-only provider-cost settlement control.",
            genre = "cinematic",
            mood = "calm",
            durationSeconds = 60,
            vocalPreference = "instrumental",
            language = "auto",
        });
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var job = (await created.Content.ReadFromJsonAsync<CreateMusicGenerationResponse>(JsonOptions))!.Job;
        var actorId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();

        using (var seedScope = factory.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var jobRow = await db.GenerationJobs.SingleAsync(item => item.Id == job.Id);
            jobRow.Status = GenerationJobStatus.Failed;
            jobRow.FailedAt = DateTime.UtcNow;
            jobRow.ErrorCode = GenerationJobErrorCodes.MusicProviderSubmissionUnconfirmed;
            var usage = await db.UsageTransactions.SingleAsync(item => item.GenerationJobId == job.Id);
            usage.Status = UsageTransactionStatus.Failed;
            usage.Provider = "test";
            usage.Model = "music-test";
            usage.ProviderCostUsd = 0m;
            usage.ProviderCostKnown = false;
            usage.CostBasis = UsageCostBasis.Unknown;
            usage.ChargedAmount = 0m;
            usage.ReversedAmount = 0m;
            usage.IsBillable = false;
            usage.FailureCode = GenerationJobErrorCodes.MusicProviderSubmissionUnconfirmed;
            db.GenerationProviderAttempts.Add(new GenerationProviderAttempt
            {
                Id = attemptId,
                GenerationJobId = job.Id,
                JobConcurrencyToken = jobRow.ConcurrencyToken,
                IdempotencyKey = $"music-cost-manual:{job.Id:N}",
                Capability = GenerationJobTypes.MusicGenerate,
                AttemptNumber = 1,
                Provider = "test",
                Model = "music-test",
                ProviderExecutionId = AcceptedCancellationMusicProvider.AcceptedOperationId,
                Status = GenerationProviderAttemptStatus.Failed,
                ResultClassification = "Failed",
                FailureCode = GenerationJobErrorCodes.MusicProviderSubmissionUnconfirmed,
                SafeMetadataJson = "{\"musicProviderCostSettlement\":{\"status\":\"manual_required\"}}",
                FinalizationKey = $"music-cost-manual-final:{job.Id:N}",
                StartedAt = DateTime.UtcNow.AddMinutes(-1),
                CompletedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var authorize = Attribute.GetCustomAttribute(typeof(MusicProviderCostSettlementController), typeof(AuthorizeAttribute)) as AuthorizeAttribute;
        Assert.NotNull(authorize);
        Assert.Equal(Taslim.Api.Authorization.AdminPolicies.Usage, authorize!.Policy);

        using (var settleScope = factory.Services.CreateScope())
        {
            var db = settleScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var controller = new MusicProviderCostSettlementController(db)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actorId.ToString("D"))], "test")),
                    },
                },
            };
            var pendingAction = await controller.Pending(CancellationToken.None);
            var pendingResult = Assert.IsType<OkObjectResult>(pendingAction.Result);
            var pending = Assert.IsAssignableFrom<IReadOnlyList<MusicProviderCostSettlementCandidateDto>>(pendingResult.Value);
            Assert.Contains(pending, item => item.AttemptId == attemptId && !item.LedgerProviderCostKnown);

            var settleAction = await controller.Settle(attemptId, new SettleMusicProviderCostRequest(0.037m, "invoice:music-2026-17"), CancellationToken.None);
            var settledResult = Assert.IsType<OkObjectResult>(settleAction.Result);
            var response = Assert.IsType<SettleMusicProviderCostResponse>(settledResult.Value);
            Assert.Equal(0.037m, response.AttemptActualProviderCostUsd);
            Assert.True(response.LedgerProviderCostKnown);
            Assert.Equal(UsageCostBasis.Manual, response.CostBasis);
            Assert.Equal(0m, response.CustomerChargedAmount);
            Assert.Equal(0m, response.CustomerReversedAmount);
            Assert.Equal(actorId, response.SettledByUserId);
            Assert.Equal("invoice:music-2026-17", response.EvidenceReference);
        }

        using (var verifyScope = factory.Services.CreateScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var attempt = await db.GenerationProviderAttempts.AsNoTracking().SingleAsync(item => item.Id == attemptId);
            var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.GenerationJobId == job.Id);
            Assert.True(attempt.ActualProviderCostKnown);
            Assert.Equal(0.037m, attempt.ActualProviderCostUsd);
            Assert.True(usage.ProviderCostKnown);
            Assert.Equal(0.037m, usage.ProviderCostUsd);
            Assert.Equal(UsageCostBasis.Manual, usage.CostBasis);
            Assert.Equal(UsageTransactionStatus.Failed, usage.Status);
            Assert.Equal(0m, usage.ChargedAmount);
            Assert.Equal(0m, usage.ReversedAmount);
            Assert.False(usage.IsBillable);
            foreach (var json in new[] { attempt.SafeMetadataJson!, usage.SafeMetadataJson! })
            {
                using var metadata = JsonDocument.Parse(json);
                var audit = metadata.RootElement.GetProperty("manualProviderCostSettlements").GetProperty(attemptId.ToString("D"));
                Assert.Equal(0.037m, audit.GetProperty("amountUsd").GetDecimal());
                Assert.Equal("invoice:music-2026-17", audit.GetProperty("evidenceReference").GetString());
                Assert.Equal(actorId.ToString("D"), audit.GetProperty("settledByUserId").GetString());
                Assert.False(string.IsNullOrWhiteSpace(audit.GetProperty("settledAtUtc").GetString()));
            }
        }

        using (var retryScope = factory.Services.CreateScope())
        {
            var controller = new MusicProviderCostSettlementController(retryScope.ServiceProvider.GetRequiredService<TaslimDbContext>())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actorId.ToString("D"))], "test")),
                    },
                },
            };
            var second = await controller.Settle(attemptId, new SettleMusicProviderCostRequest(0.999m, "invoice:overwrite"), CancellationToken.None);
            Assert.IsType<ConflictObjectResult>(second.Result);
        }
    }

    [Fact]
    public async Task Non_music_lease_recovery_still_requeues_unchanged()
    {
        var submissionsBefore = Provider().SubmittedGenerations;
        using var client = factory.CreateClient();
        var auth = await Register(client, $"generic-lease-{Guid.NewGuid():N}@example.com");
        var stalled = await SendWithCsrf<GenerationJobDto>(client, HttpMethod.Post, "/api/generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            jobType = "system.test",
            inputJson = "{}",
            title = "Generic recovery control",
        });

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var row = await db.GenerationJobs.FirstAsync(item => item.Id == stalled.Id);
            row.Status = GenerationJobStatus.Running;
            row.StartedAt = DateTime.UtcNow.AddMinutes(-5);
            row.ClaimExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        using var stopping = new CancellationTokenSource();
        var worker = CreateWorker(maxAutomaticRetries: 3);
        await ((IHostedService)worker).StartAsync(stopping.Token);

        GenerationJobDto? resumed = null;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            resumed = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{stalled.Id}");
            if (resumed is not null && resumed.RetryCount >= 1) break;
            await Task.Delay(50);
        }

        stopping.Cancel();
        await ((IHostedService)worker).StopAsync(CancellationToken.None);

        Assert.NotNull(resumed);
        // Recovery re-dispatched it once, which is the pre-existing behaviour for every non-music lane.
        Assert.True(resumed!.RetryCount >= 1, $"expected the generic job to be requeued, status={resumed.Status} retryCount={resumed.RetryCount}");
        Assert.NotEqual(GenerationJobErrorCodes.MusicProviderSubmissionUnconfirmed, resumed.ErrorCode);
        Assert.Equal(submissionsBefore, Provider().SubmittedGenerations);
    }

    [Fact]
    public void Unconfirmed_submission_copy_states_the_unknown_outcome_and_leaks_nothing()
    {
        var message = GenerationJobErrorMessages.For(GenerationJobErrorCodes.MusicProviderSubmissionUnconfirmed);

        Assert.NotNull(message);
        Assert.DoesNotContain("http", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider key", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", message, StringComparison.OrdinalIgnoreCase);
        // Both music lanes now refuse a retry, so neither copy may invite one. The accepted-but-
        // unconfirmed lane used to invite a deliberate customer retry; the billing-fairness policy
        // closed that, because an informed retry is still a second paid submission whose sibling may
        // already be running. What the copy must keep is the honest uncertainty, not a completion claim.
        Assert.DoesNotContain("try again", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("try again", GenerationJobErrorMessages.For(GenerationJobErrorCodes.MusicOutputRetrievalFailed)!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("may already have reached the provider", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("was generated", message, StringComparison.OrdinalIgnoreCase);
    }

    private CountingMusicProvider Provider() => factory.Services.GetRequiredService<CountingMusicProvider>();

    private GenerationJobWorker CreateWorker(int maxAutomaticRetries)
        => new(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new GenerationJobOptions
            {
                WorkerEnabled = true,
                WorkerConcurrency = 1,
                PollIntervalMilliseconds = 60_000,
                CancellationPollMilliseconds = 10,
                ClaimRecoveryIntervalMilliseconds = 60_000,
                ClaimLeaseMinutes = 2,
                MaxAutomaticRetries = maxAutomaticRetries,
            }),
            NullLogger<GenerationJobWorker>.Instance);

    private static async Task<GenerationJobDto> WaitForStatusAsync(HttpClient client, Guid jobId, string status)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{jobId}");
            Assert.NotNull(job);
            if (job!.Status == status) return job;
            await Task.Delay(50);
        }

        throw new TimeoutException($"Job {jobId} never reached {status}.");
    }

    private static async Task<AuthResponse> Register(HttpClient client, string email)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName = "Lease Isolation Tester", email, password = "StrongPassword!123", preferredLanguage = "en" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object payload)
    {
        var response = await SendWithCsrf(client, method, path, payload);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions))!;
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

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

public sealed class MusicLeaseRecoveryApiFactory : GenerationJobsApiFactory
{
    protected override bool WorkerEnabled => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("MusicGeneration:Enabled", "true");
        builder.UseSetting("MusicGeneration:ProviderKey", "test");
        builder.UseSetting("MusicGeneration:Model", "music-test");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMusicGenerationProvider>();
            services.AddSingleton<CountingMusicProvider>();
            services.AddSingleton<IMusicGenerationProvider>(sp => sp.GetRequiredService<CountingMusicProvider>());
        });
    }
}

/// <summary>
/// Counts how many times a provider request was actually submitted, which is the test-side stand-in
/// for "a second billable generation was created". It produces the same minimal MP3 the deterministic
/// provider uses so that a deliberate customer retry behaves like a normal successful generation.
/// </summary>
internal sealed class CountingMusicProvider : IMusicGenerationProvider
{
    private static readonly byte[] Mp3 = CreateMinimalMp3();

    public string Key => "test";

    private int submittedGenerations;
    private decimal? estimatedCostUsd = 0.004m;
    private decimal? actualCostUsd = 0.004m;

    public int SubmittedGenerations => Volatile.Read(ref submittedGenerations);

    public void SetCosts(decimal? estimatedCostUsd, decimal? actualCostUsd)
    {
        this.estimatedCostUsd = estimatedCostUsd;
        this.actualCostUsd = actualCostUsd;
    }

    public Task<MusicProviderResult> GenerateAsync(MusicGenerationInput request, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref submittedGenerations);
        return Task.FromResult(new MusicProviderResult(
            Mp3,
            "audio/mpeg",
            "mp3",
            request.DurationSeconds,
            new MusicProviderUsage(
                100,
                200,
                estimatedCostUsd,
                actualCostUsd,
                12,
                CostBasis: actualCostUsd.HasValue ? UsageCostBasis.Actual : estimatedCostUsd.HasValue ? UsageCostBasis.Estimated : UsageCostBasis.Unknown)));
    }

    private static byte[] CreateMinimalMp3()
    {
        // Byte-identical to the payload the existing music HTTP pipeline accepts, so the only thing
        // this provider contributes is the submission counter.
        var bytes = new byte[417];
        bytes[0] = 0xFF;
        bytes[1] = 0xFB;
        bytes[2] = 0x90;
        bytes[3] = 0x64;
        return bytes;
    }
}

/// <summary>
/// F-36.1: the out-of-process half of the accepted-cancellation gap. The seventh carrier head closed the
/// case where the worker that made the submission is still alive to see the cancellation; this lane covers
/// the case where it is not. When the process is lost after the provider answered the billable create with
/// an operation id and while a member cancellation is pending, lease recovery is the only code left that can
/// terminalise the job, and it knows nothing but what is stored. So the acceptance is recorded durably on the
/// provider-attempt row at the moment it happens (the same row the worker already commits before calling the
/// handler), recovery consults that record, and a cancellation with paid work behind it terminalises on the
/// unconfirmed code that <c>RetryAsync</c> refuses - fail closed rather than a second paid submission. A
/// cancellation with no evidence behind it keeps the ordinary code and its retry, which is what every
/// provider double predating F-36.1 still gets through the interface default.
/// </summary>
public sealed class MusicAcceptedCancellationLeaseRecoveryTests : IClassFixture<AcceptedCancellationMusicApiFactory>
{
    private readonly AcceptedCancellationMusicApiFactory factory;

    public MusicAcceptedCancellationLeaseRecoveryTests(AcceptedCancellationMusicApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Durable_acceptance_survives_the_lost_worker_and_recovery_refuses_the_second_paid_submission()
    {
        var provider = Provider();
        var submissionsBefore = provider.SubmittedGenerations;
        provider.Reset(AcceptedCancellationMode.ReportThenHang);
        using var client = factory.CreateClient();
        var auth = await Register(client, $"f361-accepted-{Guid.NewGuid():N}@example.com");
        var created = await SendWithCsrf(client, HttpMethod.Post, "/api/music-generation/jobs", MusicBody(auth));
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var job = (await created.Content.ReadFromJsonAsync<CreateMusicGenerationResponse>(Json))!.Job;

        // Phase 1: the execution that is about to be lost. A real worker claims the job, so the accepted
        // operation id reaches the database through the production sink - the wiring under test - rather
        // than through a row this test wrote by hand.
        using var lostProcess = new CancellationTokenSource();
        var deadWorker = CreateWorker(cancellationPollMilliseconds: 60_000);
        await ((IHostedService)deadWorker).StartAsync(lostProcess.Token);

        Guid evidenceAttemptId = default;
        Guid deadClaimToken = default;
        for (var attempt = 0; attempt < 400 && evidenceAttemptId == default; attempt++)
        {
            await Task.Delay(25);
            using var probe = factory.Services.CreateScope();
            var stamped = await probe.ServiceProvider.GetRequiredService<TaslimDbContext>()
                .GenerationProviderAttempts.AsNoTracking()
                .FirstOrDefaultAsync(item => item.GenerationJobId == job.Id && item.ProviderExecutionId != null);
            if (stamped is not null)
            {
                evidenceAttemptId = stamped.Id;
                deadClaimToken = stamped.JobConcurrencyToken;
            }
        }

        Assert.NotEqual(default, evidenceAttemptId);
        Assert.Equal(submissionsBefore + 1, provider.SubmittedGenerations);
        Assert.Equal(AcceptedCancellationMusicProvider.AcceptedOperationId, stampedOperationId(evidenceAttemptId));
        Assert.Equal(1, provider.ReportedAcceptances);

        // Phase 2: the crash itself. The member's cancellation is on the row and its lease has expired, and
        // the worker that knows what it had submitted is never going to write again. The claim token is left
        // alone on purpose: the evidence belongs to the claim that died, which is exactly what recovery must
        // find. The abandoned worker is stopped only after the recovery assertions, and its own writes are
        // fenced out by the token the recovery sweep rotates.
        using (var stage = factory.Services.CreateScope())
        {
            await stage.ServiceProvider.GetRequiredService<TaslimDbContext>().GenerationJobs
                .Where(item => item.Id == job.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.CancellationRequested, true)
                    .SetProperty(item => item.ClaimExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        }

        using var recoveryProcess = new CancellationTokenSource();
        var recoveryWorker = CreateWorker(cancellationPollMilliseconds: 10);
        await ((IHostedService)recoveryWorker).StartAsync(recoveryProcess.Token);

        var cancelled = await WaitForStatusAsync(client, job.Id, "Cancelled");
        Assert.Equal("Cancelled", cancelled.Status);
        // Not the plain cancellation: the accepted paid generation is unreconciled, so the retry that used to
        // be permitted beside it is not.
        Assert.Equal(GenerationJobErrorCodes.MusicCancelledUnconfirmedSubmission, cancelled.ErrorCode);
        Assert.NotNull(cancelled.ErrorMessage);
        Assert.Equal(
            GenerationJobErrorMessages.For(GenerationJobErrorCodes.MusicCancelledUnconfirmedSubmission),
            cancelled.ErrorMessage);
        Assert.NotEqual("The job was cancelled.", cancelled.ErrorMessage);
        Assert.Equal(0, cancelled.RetryCount);
        Assert.Contains("may still finish", cancelled.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("try again", cancelled.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        // The money assertions. Recovery did not re-dispatch the handler, and the retry endpoint refuses to
        // start a second paid submission - twice, because a fresh idempotency key is not a way around it.
        Assert.Equal(submissionsBefore + 1, provider.SubmittedGenerations);
        var retry = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{job.Id}/retry", null, $"f361-accepted-retry-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.BadRequest, retry.StatusCode);
        using (var refusal = JsonDocument.Parse(await retry.Content.ReadAsStringAsync()))
        {
            Assert.Equal(
                GenerationJobErrorCodes.MusicSubmissionRetryBlocked,
                refusal.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        var secondRetry = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{job.Id}/retry", null, $"f361-accepted-retry-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.BadRequest, secondRetry.StatusCode);

        using var finalScope = factory.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(0, await finalDb.GenerationJobs.AsNoTracking().CountAsync(item => item.RetryOfJobId == job.Id));
        Assert.Equal(submissionsBefore + 1, provider.SubmittedGenerations);

        var usage = await finalDb.UsageTransactions.AsNoTracking()
            .SingleAsync(item => item.RequestId == $"generation:{job.Id:N}");
        // One reservation, cancelled: never charged a second time, and no refund is invented for a
        // generation whose provider outcome is still unknown.
        Assert.Equal(UsageTransactionStatus.Cancelled, usage.Status);
        Assert.Equal(0m, usage.ProviderCostUsd);
        Assert.False(usage.ProviderCostKnown);
        Assert.Equal(UsageCostBasis.Unknown, usage.CostBasis);
        Assert.Equal(0m, usage.ChargedAmount);
        Assert.Equal(0m, usage.ReversedAmount);

        var unknownAttempt = await finalDb.GenerationProviderAttempts.AsNoTracking()
            .SingleAsync(item => item.GenerationJobId == job.Id);
        Assert.False(unknownAttempt.ActualProviderCostKnown);
        Assert.Null(unknownAttempt.ActualProviderCostUsd);
        using (var attemptMetadata = JsonDocument.Parse(unknownAttempt.SafeMetadataJson!))
            Assert.Equal("manual_required", attemptMetadata.RootElement.GetProperty("musicProviderCostSettlement").GetProperty("status").GetString());

        // The accepted operation id outlives the terminalisation, from a context that never wrote it: that
        // is the handle a later provider-status reconciliation needs, and the reason recovery refused.
        Assert.Equal(AcceptedCancellationMusicProvider.AcceptedOperationId, stampedOperationId(evidenceAttemptId));
        Assert.NotEqual(deadClaimToken, default);

        // Phase 3: the zombie. Now that the row is terminal on a fresh concurrency token, everything the
        // abandoned worker still tries to write is fenced out, which is the same guarantee a dead process
        // gives - and it must not be able to downgrade the code recovery chose.
        lostProcess.Cancel();
        await ((IHostedService)deadWorker).StopAsync(CancellationToken.None);
        recoveryProcess.Cancel();
        await ((IHostedService)recoveryWorker).StopAsync(CancellationToken.None);

        using var afterStop = factory.Services.CreateScope();
        var settled = await afterStop.ServiceProvider.GetRequiredService<TaslimDbContext>()
            .GenerationJobs.AsNoTracking().FirstAsync(item => item.Id == job.Id);
        Assert.Equal(GenerationJobStatus.Cancelled, settled.Status);
        Assert.Equal(GenerationJobErrorCodes.MusicCancelledUnconfirmedSubmission, settled.ErrorCode);
        Assert.Equal(submissionsBefore + 1, provider.SubmittedGenerations);
    }

    [Fact]
    public async Task Recovery_without_durable_acceptance_keeps_the_plain_cancellation_and_its_retry()
    {
        // The carve-out, and the compatibility claim for every provider double that predates the sink: a
        // cancellation with no accepted submission behind it is still evidence that nothing billable is in
        // flight, so it keeps MUSIC_CANCELLED, the ordinary copy, and a retry that actually re-submits.
        var provider = Provider();
        var submissionsBefore = provider.SubmittedGenerations;
        provider.Reset(AcceptedCancellationMode.HangWithoutReporting);
        using var client = factory.CreateClient();
        var auth = await Register(client, $"f361-plain-{Guid.NewGuid():N}@example.com");
        var created = await SendWithCsrf(client, HttpMethod.Post, "/api/music-generation/jobs", MusicBody(auth));
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var job = (await created.Content.ReadFromJsonAsync<CreateMusicGenerationResponse>(Json))!.Job;

        using var lostProcess = new CancellationTokenSource();
        var deadWorker = CreateWorker(cancellationPollMilliseconds: 60_000);
        await ((IHostedService)deadWorker).StartAsync(lostProcess.Token);

        // Give the abandoned worker time to reach the provider before the crash is staged: the scenario is a
        // submission in flight, it is just one that was never accepted.
        for (var attempt = 0; attempt < 400 && provider.SubmittedGenerations == submissionsBefore; attempt++) await Task.Delay(25);
        Assert.Equal(submissionsBefore + 1, provider.SubmittedGenerations);
        Assert.Equal(0, provider.ReportedAcceptances);

        using (var stage = factory.Services.CreateScope())
        {
            await stage.ServiceProvider.GetRequiredService<TaslimDbContext>().GenerationJobs
                .Where(item => item.Id == job.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.CancellationRequested, true)
                    .SetProperty(item => item.ClaimExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        }

        using var recoveryProcess = new CancellationTokenSource();
        var recoveryWorker = CreateWorker(cancellationPollMilliseconds: 10);
        await ((IHostedService)recoveryWorker).StartAsync(recoveryProcess.Token);

        var cancelled = await WaitForStatusAsync(client, job.Id, "Cancelled");
        Assert.Equal(GenerationJobErrorCodes.MusicCancelled, cancelled.ErrorCode);
        // The served copy is derived from the code (GenerationContracts maps ErrorCode through the
        // message catalog), so the invariant is that this stays the ordinary cancellation copy: no
        // mention of an accepted provider submission, and not the unconfirmed twin's copy.
        Assert.Equal(GenerationJobErrorMessages.For(GenerationJobErrorCodes.MusicCancelled), cancelled.ErrorMessage);
        Assert.DoesNotContain("accepted", cancelled.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(GenerationJobErrorCodes.MusicCancelledUnconfirmedSubmission, cancelled.ErrorCode);

        var retry = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{job.Id}/retry", null, $"f361-plain-retry-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        var child = (await retry.Content.ReadFromJsonAsync<GenerationJobDto>(Json))!;
        Assert.Equal(job.Id, child.RetryOfJobId);
        // Here a second paid submission is the correct outcome - nothing was ever accepted - so the retry
        // really does reach the provider. The counter moving is the assertion, not a failure.
        for (var attempt = 0; attempt < 400 && provider.SubmittedGenerations < submissionsBefore + 2; attempt++) await Task.Delay(25);
        Assert.Equal(submissionsBefore + 2, provider.SubmittedGenerations);
        Assert.Equal(1, await CountChildrenAsync(job.Id));

        lostProcess.Cancel();
        await ((IHostedService)deadWorker).StopAsync(CancellationToken.None);
        recoveryProcess.Cancel();
        await ((IHostedService)recoveryWorker).StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Legacy_cancellation_attempt_without_dispatch_protocol_is_unknown_and_not_retryable()
    {
        var provider = Provider();
        var submissionsBefore = provider.SubmittedGenerations;
        provider.Reset(AcceptedCancellationMode.HangWithoutReporting);
        using var client = factory.CreateClient();
        var auth = await Register(client, $"f361-legacy-{Guid.NewGuid():N}@example.com");
        var created = await SendWithCsrf(client, HttpMethod.Post, "/api/music-generation/jobs", MusicBody(auth));
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var job = (await created.Content.ReadFromJsonAsync<CreateMusicGenerationResponse>(Json))!.Job;
        var claimToken = Guid.Empty;

        // Model a worker attempt written before the durable dispatch protocol existed. A blank provider
        // id in that record is not proof that the POST never left the process, so recovery must close it.
        using (var stage = factory.Services.CreateScope())
        {
            var db = stage.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var row = await db.GenerationJobs.SingleAsync(item => item.Id == job.Id);
            row.Status = GenerationJobStatus.Running;
            row.StartedAt = DateTime.UtcNow.AddMinutes(-5);
            row.ClaimExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            row.CancellationRequested = true;
            claimToken = row.ConcurrencyToken;
            db.GenerationProviderAttempts.Add(new GenerationProviderAttempt
            {
                Id = Guid.NewGuid(),
                GenerationJobId = job.Id,
                JobConcurrencyToken = claimToken,
                IdempotencyKey = $"f361-legacy-{Guid.NewGuid():N}",
                FinalizationKey = $"f361-legacy-finalization-{Guid.NewGuid():N}",
                Capability = GenerationJobTypes.MusicGenerate,
                Provider = "test",
                Status = GenerationProviderAttemptStatus.Started,
                SafeMetadataJson = null,
            });
            await db.SaveChangesAsync();
        }

        using var recoveryProcess = new CancellationTokenSource();
        var recoveryWorker = CreateWorker(cancellationPollMilliseconds: 10);
        await ((IHostedService)recoveryWorker).StartAsync(recoveryProcess.Token);
        var cancelled = await WaitForStatusAsync(client, job.Id, "Cancelled");
        recoveryProcess.Cancel();
        await ((IHostedService)recoveryWorker).StopAsync(CancellationToken.None);

        Assert.Equal(GenerationJobErrorCodes.MusicCancelledUnconfirmedSubmission, cancelled.ErrorCode);
        Assert.Equal(submissionsBefore, provider.SubmittedGenerations);
        await AssertRetryRefusedAsync(client, job.Id, GenerationJobErrorCodes.MusicSubmissionRetryBlocked);
        Assert.Equal(0, await CountChildrenAsync(job.Id));
        using var finalScope = factory.Services.CreateScope();
        var usage = await finalScope.ServiceProvider.GetRequiredService<TaslimDbContext>().UsageTransactions
            .AsNoTracking().SingleAsync(item => item.RequestId == $"generation:{job.Id:N}");
        Assert.Equal(UsageTransactionStatus.Cancelled, usage.Status);
        Assert.Equal(0m, usage.ChargedAmount);
    }

    [Fact]
    public async Task Acceptance_evidence_from_a_different_claim_does_not_hold_this_cancellation_closed()
    {
        // The consult is claim-scoped: the durable record answers "what did the process that owned this
        // lease reach?", not "has this job ever been accepted?". A stale row from a superseded attempt must
        // not strand an ordinary cancellation behind a refusal. (For music the accepted claim is always
        // terminalised rather than re-queued, so this is precision rather than a hole - the accepted attempt
        // is the one that died.)
        var provider = Provider();
        var submissionsBefore = provider.SubmittedGenerations;
        provider.Reset(AcceptedCancellationMode.HangWithoutReporting);
        using var client = factory.CreateClient();
        var auth = await Register(client, $"f361-superseded-{Guid.NewGuid():N}@example.com");
        var created = await SendWithCsrf(client, HttpMethod.Post, "/api/music-generation/jobs", MusicBody(auth));
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var job = (await created.Content.ReadFromJsonAsync<CreateMusicGenerationResponse>(Json))!.Job;

        Guid liveClaimToken;
        using (var stage = factory.Services.CreateScope())
        {
            var db = stage.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var row = await db.GenerationJobs.FirstAsync(item => item.Id == job.Id);
            row.Status = GenerationJobStatus.Running;
            row.StartedAt = DateTime.UtcNow.AddMinutes(-5);
            row.ClaimExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            row.CancellationRequested = true;
            liveClaimToken = row.ConcurrencyToken;
            // An accepted attempt belonging to an earlier, already-superseded claim of the same job.
            db.GenerationProviderAttempts.Add(new GenerationProviderAttempt
            {
                Id = Guid.NewGuid(),
                GenerationJobId = job.Id,
                JobConcurrencyToken = Guid.NewGuid(),
                IdempotencyKey = $"f361-superseded-{Guid.NewGuid():N}",
                Capability = GenerationJobTypes.MusicGenerate,
                Provider = "test",
                ProviderExecutionId = AcceptedCancellationMusicProvider.AcceptedOperationId,
                Status = GenerationProviderAttemptStatus.Started,
            });
            await db.SaveChangesAsync();
        }

        using var recoveryProcess = new CancellationTokenSource();
        var recoveryWorker = CreateWorker(cancellationPollMilliseconds: 10);
        await ((IHostedService)recoveryWorker).StartAsync(recoveryProcess.Token);

        var cancelled = await WaitForStatusAsync(client, job.Id, "Cancelled");
        Assert.Equal(GenerationJobErrorCodes.MusicCancelled, cancelled.ErrorCode);
        // The served copy is derived from the code (GenerationContracts maps ErrorCode through the
        // message catalog), so the invariant is that this stays the ordinary cancellation copy: no
        // mention of an accepted provider submission, and not the unconfirmed twin's copy.
        Assert.Equal(GenerationJobErrorMessages.For(GenerationJobErrorCodes.MusicCancelled), cancelled.ErrorMessage);
        Assert.DoesNotContain("accepted", cancelled.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        // The recovery sweep never runs the handler, so the shared provider must not have been
        // approached at all by this test (the counter is class-scoped and monotonic, hence a delta).
        Assert.Equal(submissionsBefore, provider.SubmittedGenerations);
        // The superseded evidence itself is left intact for whoever has to reconcile it later.
        using (var verify = factory.Services.CreateScope())
        {
            var kept = await verify.ServiceProvider.GetRequiredService<TaslimDbContext>()
                .GenerationProviderAttempts.AsNoTracking().Where(item => item.GenerationJobId == job.Id).ToListAsync();
            Assert.Equal(1, kept.Count(item => item.ProviderExecutionId == AcceptedCancellationMusicProvider.AcceptedOperationId));
            Assert.NotEqual(liveClaimToken, kept.Single().JobConcurrencyToken);
        }

        recoveryProcess.Cancel();
        await ((IHostedService)recoveryWorker).StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Accepted_generation_that_finished_after_a_cancellation_was_committed_is_cancelled_as_undelivered_output()
    {
        // The same money bug without any crash: the handler came back with a finished track while the
        // member's cancellation was already committed, so the publication fence terminalises the job. Before
        // F-36.1 that fence reported the type-only cancellation code and a retry was permitted next to a
        // generation that had already been paid for; the fence now routes music through the delivered-output
        // contract, which the retry endpoint refuses with the retrieval posture.
        var provider = Provider();
        var submissionsBefore = provider.SubmittedGenerations;
        provider.Reset(AcceptedCancellationMode.ReportThenFinishOnRelease);
        using var client = factory.CreateClient();
        var auth = await Register(client, $"f361-fence-{Guid.NewGuid():N}@example.com");
        var created = await SendWithCsrf(client, HttpMethod.Post, "/api/music-generation/jobs", MusicBody(auth));
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var job = (await created.Content.ReadFromJsonAsync<CreateMusicGenerationResponse>(Json))!.Job;

        // A long cancellation poll keeps the monitor asleep, so the cancellation reaches the worker only
        // through the row it reads at the publication fence - the late-finish interleaving itself.
        using var liveProcess = new CancellationTokenSource();
        var worker = CreateWorker(cancellationPollMilliseconds: 60_000);
        await ((IHostedService)worker).StartAsync(liveProcess.Token);

        for (var attempt = 0; attempt < 400 && provider.ReportedAcceptances == 0; attempt++) await Task.Delay(25);
        Assert.Equal(1, provider.ReportedAcceptances);

        using (var stage = factory.Services.CreateScope())
        {
            await stage.ServiceProvider.GetRequiredService<TaslimDbContext>().GenerationJobs
                .Where(item => item.Id == job.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CancellationRequested, true));
        }
        provider.Release();

        var cancelled = await WaitForStatusAsync(client, job.Id, "Cancelled");
        Assert.Equal(GenerationJobErrorCodes.MusicCancelledUndeliveredOutput, cancelled.ErrorCode);
        Assert.NotNull(cancelled.ErrorMessage);
        Assert.Equal(
            GenerationJobErrorMessages.For(GenerationJobErrorCodes.MusicCancelledUndeliveredOutput),
            cancelled.ErrorMessage);
        Assert.NotEqual("The job was cancelled.", cancelled.ErrorMessage);

        var retry = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{job.Id}/retry", null, $"f361-fence-retry-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.BadRequest, retry.StatusCode);
        using (var refusal = JsonDocument.Parse(await retry.Content.ReadAsStringAsync()))
        {
            Assert.Equal(
                GenerationJobErrorCodes.MusicRetrievalRetryBlocked,
                refusal.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        Assert.Equal(submissionsBefore + 1, provider.SubmittedGenerations);
        Assert.Equal(0, await CountChildrenAsync(job.Id));
        // Nothing was delivered, and nothing was billed a second time for it.
        using (var verify = factory.Services.CreateScope())
        {
            var db = verify.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var usage = await db.UsageTransactions.AsNoTracking()
                .SingleAsync(item => item.RequestId == $"generation:{job.Id:N}");
            Assert.Equal(UsageTransactionStatus.Cancelled, usage.Status);
            Assert.True(usage.ProviderCostKnown);
            Assert.Equal(0.004m, usage.ProviderCostUsd);
            Assert.Equal(UsageCostBasis.Actual, usage.CostBasis);
            Assert.Equal(0m, usage.ChargedAmount);
            Assert.Equal(0m, usage.ReversedAmount);
            var attempt = await db.GenerationProviderAttempts.AsNoTracking().SingleAsync(item => item.GenerationJobId == job.Id);
            Assert.True(attempt.ActualProviderCostKnown);
            Assert.Equal(0.004m, attempt.ActualProviderCostUsd);
            Assert.Equal(0, await db.GenerationJobOutputs.AsNoTracking().CountAsync(item => item.GenerationJobId == job.Id));
        }

        liveProcess.Cancel();
        await ((IHostedService)worker).StopAsync(CancellationToken.None);
        Assert.Equal(submissionsBefore + 1, provider.SubmittedGenerations);
    }

    [Theory]
    [InlineData("reclaimed", false)]
    [InlineData("expired", false)]
    [InlineData("cancelled", false)]
    [InlineData("terminal", false)]
    [InlineData("reclaimed", true)]
    [InlineData("expired", true)]
    [InlineData("cancelled", true)]
    [InlineData("terminal", true)]
    public async Task Music_callbacks_require_a_live_parent_even_when_the_attempt_token_still_matches(string change, bool afterDispatch)
    {
        var provider = Provider();
        provider.Reset(AcceptedCancellationMode.ReportThenHang);
        var submissionsBefore = provider.SubmittedGenerations;
        async Task ChangeParent(Guid jobId)
        {
            // Use a second connection at the fake transport boundary. The attempt row is left untouched.
            using var scope = factory.Services.CreateScope();
            var jobs = scope.ServiceProvider.GetRequiredService<TaslimDbContext>().GenerationJobs.Where(job => job.Id == jobId);
            var nextToken = Guid.NewGuid();
            var updated = change switch
            {
                "reclaimed" => await jobs.ExecuteUpdateAsync(s => s.SetProperty(job => job.ConcurrencyToken, nextToken)),
                "expired" => await jobs.ExecuteUpdateAsync(s => s.SetProperty(job => job.ClaimExpiresAt, DateTime.UtcNow.AddMinutes(-1))),
                "cancelled" => await jobs.ExecuteUpdateAsync(s => s.SetProperty(job => job.CancellationRequested, true)),
                "terminal" => await jobs.ExecuteUpdateAsync(s => s.SetProperty(job => job.Status, GenerationJobStatus.Failed)),
                _ => throw new ArgumentOutOfRangeException(nameof(change)),
            };
            Assert.Equal(1, updated);
        }

        using var claim = await StartClaimThatWillBeLostAsync(provider, "music-claim-" + change, waitAccepted: false,
            beforeDispatch: afterDispatch ? null : ChangeParent, beforeAcceptance: afterDispatch ? ChangeParent : null);
        for (var wait = 0; wait < 400 && provider.DispatchSinkFaults + provider.AcceptanceSinkFaults == 0; wait++)
            await Task.Delay(25);

        Assert.Equal(afterDispatch ? 0 : 1, provider.DispatchSinkFaults);
        Assert.Equal(afterDispatch ? 1 : 0, provider.AcceptanceSinkFaults);
        Assert.Equal(submissionsBefore + (afterDispatch ? 1 : 0), provider.SubmittedGenerations);
        Assert.Equal(0, provider.ReportedAcceptances);
        var (_, marker, _) = await AttemptEvidenceAsync(claim.JobId);
        Assert.Equal(afterDispatch ? "taslim:dispatch-pending" : null, marker);
    }

    [Fact]
    public async Task Marker_that_cannot_be_recorded_stops_the_paid_request_and_keeps_the_pre_dispatch_posture()
    {
        // F-36.1b finance defect, and the reason the marker is not allowed to be a log line: while the
        // pre-dispatch stamp was swallow-and-continue, a persistence failure reopened the exact window the
        // marker exists to close - a paid POST with no durable evidence, which recovery could only read as
        // "nothing was submitted". The guard now refuses to dispatch.
        var provider = Provider();
        var submissionsBefore = provider.SubmittedGenerations;
        provider.Reset(AcceptedCancellationMode.MarkerWriteFails);
        using var lost = await StartClaimThatWillBeLostAsync(
            provider, "f361b-marker", supersedeClaim: true, waitAccepted: false);
        var failed = await WaitForStatusAsync(lost.Client, lost.JobId, "Failed");

        // The proof the reviewer asked for: the create was never issued. This counter is the test-side
        // stand-in for "a generation was paid for", and a marker that cannot be written must not move it.
        Assert.Equal(submissionsBefore, provider.SubmittedGenerations);
        Assert.Equal(1, provider.DispatchSinkCalls);
        Assert.Equal(1, provider.DispatchSinkFaults);
        Assert.Equal(0, provider.ReportedAcceptances);

        // Nothing reached the transport, so the honest signal is the retryable outage class - not the
        // submission-unconfirmed twin, whose copy asserts the request may already have reached the vendor.
        Assert.Equal(GenerationJobErrorCodes.MusicProviderUnavailable, failed.ErrorCode);
        Assert.NotNull(failed.ErrorMessage);
        Assert.DoesNotContain("may already have reached the provider", failed.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(
            GenerationJobErrorMessages.For(GenerationJobErrorCodes.MusicProviderSubmissionUnconfirmed),
            failed.ErrorMessage);

        // Durable claim fencing: the sentinel never became a fact, so recovery is never later handed a
        // half-written marker to reason from either.
        var (markers, marker, _) = await AttemptEvidenceAsync(lost.JobId);
        Assert.Equal(0, markers);
        Assert.Null(marker);

        // The reservation keeps the pre-dispatch posture: released, with zero customer charge.
        using (var scope = factory.Services.CreateScope())
        {
            var usage = await scope.ServiceProvider.GetRequiredService<TaslimDbContext>().UsageTransactions
                .AsNoTracking().SingleAsync(item => item.RequestId == $"generation:{lost.JobId:N}");
            Assert.Equal(UsageTransactionStatus.Failed, usage.Status);
            Assert.Equal(0m, usage.ChargedAmount);
        }

        await lost.StopAsync();

        // And the customer is not stranded by the guard: no money moved, so the retry is accepted and the
        // provider is reached exactly once - as the FIRST paid submission for the request, not a second.
        provider.Reset(AcceptedCancellationMode.HangWithoutReporting);
        var retry = await SendWithCsrf(lost.Client, HttpMethod.Post, $"/api/generation/jobs/{lost.JobId}/retry", null, $"f361b-marker-retry-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        var child = (await retry.Content.ReadFromJsonAsync<GenerationJobDto>(Json))!;
        Assert.Equal(lost.JobId, child.RetryOfJobId);
        var childProcess = new CancellationTokenSource();
        var childWorker = CreateWorker(cancellationPollMilliseconds: 60_000);
        await ((IHostedService)childWorker).StartAsync(childProcess.Token);
        for (var attempt = 0; attempt < 400 && provider.SubmittedGenerations == submissionsBefore; attempt++) await Task.Delay(25);
        Assert.Equal(submissionsBefore + 1, provider.SubmittedGenerations);
        childProcess.Cancel();
        await ((IHostedService)childWorker).StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Crash_after_the_marker_and_before_the_provider_answer_closes_the_cancellation_without_asking_the_provider()
    {
        // The window the marker exists for: the create left the process and the answer never arrived.
        // Recovery holds a sentinel and no operation id, so it closes the outcome instead of releasing a
        // retry that could pay twice for a generation that may be running right now.
        var provider = Provider();
        var submissionsBefore = provider.SubmittedGenerations;
        provider.Reset(AcceptedCancellationMode.DispatchThenHang);
        using var lost = await StartClaimThatWillBeLostAsync(provider, "f361b-sentinel", waitAccepted: false);
        for (var attempt = 0; attempt < 400 && provider.SubmittedGenerations == submissionsBefore; attempt++) await Task.Delay(25);
        Assert.Equal(submissionsBefore + 1, provider.SubmittedGenerations);
        Assert.Equal(0, provider.ReportedAcceptances);
        Assert.Equal(1, provider.DispatchSinkCalls);
        Assert.Equal(0, provider.DispatchSinkFaults);

        var cancelled = await TerminaliseThroughRecoveryAsync(lost.Client, lost.JobId);
        Assert.Equal(GenerationJobErrorCodes.MusicCancelledUnconfirmedSubmission, cancelled.ErrorCode);
        Assert.Contains("may still finish", cancelled.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("try again", cancelled.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        // The sentinel is the only durable fact and it outlives the terminalisation. It is never mistaken
        // for an operation id: the lookup that would have to phrase a question about "dispatch pending" is
        // not attempted at all.
        var (markers, marker, _) = await AttemptEvidenceAsync(lost.JobId);
        Assert.Equal(1, markers);
        Assert.Equal("taslim:dispatch-pending", marker);
        Assert.Equal(0, provider.ReconciliationCalls);

        await AssertRetryRefusedAsync(lost.Client, lost.JobId, GenerationJobErrorCodes.MusicSubmissionRetryBlocked);
        await AssertReservationCancelledAsync(lost.JobId);
        await AssertUnknownProviderCostAsync(lost.JobId);
        Assert.Equal(submissionsBefore + 1, provider.SubmittedGenerations);
        await lost.StopAsync();
        Assert.Equal(submissionsBefore + 1, provider.SubmittedGenerations);
    }

    [Fact]
    public async Task Reconciled_completed_generation_cancels_as_undelivered_output_and_keeps_the_retrieval_posture()
    {
        // A provider status lookup that answers with the audio means the paid generation exists and only
        // delivery was lost, so the cancellation is the delivered-output twin: the retry it refuses is the
        // retrieval posture, and the answer is recorded durably next to the operation id it came from.
        var provider = Provider();
        var submissionsBefore = provider.SubmittedGenerations;
        provider.Reset(AcceptedCancellationMode.ReportThenHang);
        provider.ReconciliationResult = new MusicSubmissionReconciliation(MusicSubmissionOutcome.Completed, ProviderDeclaresNoCharge: false, ActualCostUsd: 0.009m);
        using var lost = await StartClaimThatWillBeLostAsync(provider, "f361b-completed");

        var cancelled = await TerminaliseThroughRecoveryAsync(lost.Client, lost.JobId);
        Assert.Equal(GenerationJobErrorCodes.MusicCancelledUndeliveredOutput, cancelled.ErrorCode);
        Assert.Equal(
            GenerationJobErrorMessages.For(GenerationJobErrorCodes.MusicCancelledUndeliveredOutput),
            cancelled.ErrorMessage);
        Assert.Equal(1, provider.ReconciliationCalls);
        Assert.Equal(AcceptedCancellationMusicProvider.AcceptedOperationId, provider.ReconciledOperationId);
        var (_, marker, metadata) = await AttemptEvidenceAsync(lost.JobId);
        Assert.Equal(AcceptedCancellationMusicProvider.AcceptedOperationId, marker);
        Assert.NotNull(metadata);
        Assert.Contains("musicSubmissionReconcile", metadata!, StringComparison.Ordinal);
        Assert.Contains("Completed", metadata!, StringComparison.Ordinal);

        await AssertRetryRefusedAsync(lost.Client, lost.JobId, GenerationJobErrorCodes.MusicRetrievalRetryBlocked);
        await AssertReservationCancelledAsync(lost.JobId);
        using (var costScope = factory.Services.CreateScope())
        {
            var db = costScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.GenerationJobId == lost.JobId);
            Assert.True(usage.ProviderCostKnown);
            Assert.Equal(0.009m, usage.ProviderCostUsd);
            Assert.Equal(UsageCostBasis.Actual, usage.CostBasis);
            Assert.Equal(0m, usage.ChargedAmount);
            Assert.Equal(0m, usage.ReversedAmount);
            var attempt = await db.GenerationProviderAttempts.AsNoTracking().SingleAsync(item => item.GenerationJobId == lost.JobId);
            Assert.True(attempt.ActualProviderCostKnown);
            Assert.Equal(0.009m, attempt.ActualProviderCostUsd);
            using var attemptMetadata = JsonDocument.Parse(attempt.SafeMetadataJson!);
            Assert.Equal("settled", attemptMetadata.RootElement.GetProperty("musicProviderCostSettlement").GetProperty("status").GetString());
        }
        Assert.Equal(submissionsBefore + 1, provider.SubmittedGenerations);
        await lost.StopAsync();
        Assert.Equal(submissionsBefore + 1, provider.SubmittedGenerations);
    }

    [Fact]
    public async Task Reconciled_failure_with_the_vendors_no_charge_promise_releases_the_ordinary_cancellation()
    {
        // The only supported release path, and it is contract-driven: Stability documents "you will not be
        // charged for failed generations", so a terminal declared failure from that vendor leaves nothing
        // behind to double-charge and the cancellation keeps the ordinary code and its retry. The no-charge
        // bit travels with the outcome; recovery never infers a refund from a failure alone.
        var provider = Provider();
        var submissionsBefore = provider.SubmittedGenerations;
        provider.Reset(AcceptedCancellationMode.ReportThenHang);
        provider.ReconciliationResult = new MusicSubmissionReconciliation(MusicSubmissionOutcome.ProviderDeclaredFailure, ProviderDeclaresNoCharge: true);
        using var lost = await StartClaimThatWillBeLostAsync(provider, "f361b-nocharge");

        var cancelled = await TerminaliseThroughRecoveryAsync(lost.Client, lost.JobId);
        Assert.Equal(GenerationJobErrorCodes.MusicCancelled, cancelled.ErrorCode);
        Assert.Equal(GenerationJobErrorMessages.For(GenerationJobErrorCodes.MusicCancelled), cancelled.ErrorMessage);
        Assert.DoesNotContain("accepted", cancelled.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, provider.ReconciliationCalls);
        var (_, _, metadata) = await AttemptEvidenceAsync(lost.JobId);
        Assert.NotNull(metadata);
        Assert.Contains("ProviderDeclaredFailure", metadata!, StringComparison.Ordinal);
        using (var noChargeScope = factory.Services.CreateScope())
        {
            var db = noChargeScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.GenerationJobId == lost.JobId);
            Assert.True(usage.ProviderCostKnown);
            Assert.Equal(0m, usage.ProviderCostUsd);
            Assert.Equal(UsageCostBasis.Actual, usage.CostBasis);
            Assert.Equal(0m, usage.ChargedAmount);
            Assert.Equal(0m, usage.ReversedAmount);
        }
        Assert.Equal(submissionsBefore + 1, provider.SubmittedGenerations);

        // Recovery did not re-dispatch anything itself; the release is the customer's decision, taken with
        // the vendor's no-charge answer in hand.
        await lost.StopAsync();
        var retry = await SendWithCsrf(lost.Client, HttpMethod.Post, $"/api/generation/jobs/{lost.JobId}/retry", null, $"f361b-nocharge-retry-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        var child = (await retry.Content.ReadFromJsonAsync<GenerationJobDto>(Json))!;
        Assert.Equal(lost.JobId, child.RetryOfJobId);
        var childProcess = new CancellationTokenSource();
        var childWorker = CreateWorker(cancellationPollMilliseconds: 60_000);
        await ((IHostedService)childWorker).StartAsync(childProcess.Token);
        for (var attempt = 0; attempt < 400 && provider.SubmittedGenerations < submissionsBefore + 2; attempt++) await Task.Delay(25);
        Assert.Equal(submissionsBefore + 2, provider.SubmittedGenerations);
        childProcess.Cancel();
        await ((IHostedService)childWorker).StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Reconciled_failure_without_a_no_charge_statement_stays_closed()
    {
        // Mubert is the case this arm exists for: its documentation confirms a quota-free track lookup but
        // says nothing about charging for a failed generation, so a declared failure there is not evidence
        // that no money was spent. Same outcome from the vendor, opposite posture, because the contract
        // differs - the ladder keys on the promise, never on the word "failed".
        var provider = Provider();
        var submissionsBefore = provider.SubmittedGenerations;
        provider.Reset(AcceptedCancellationMode.ReportThenHang);
        provider.ReconciliationResult = new MusicSubmissionReconciliation(MusicSubmissionOutcome.ProviderDeclaredFailure, ProviderDeclaresNoCharge: false);
        using var lost = await StartClaimThatWillBeLostAsync(provider, "f361b-declared");

        var cancelled = await TerminaliseThroughRecoveryAsync(lost.Client, lost.JobId);
        Assert.Equal(GenerationJobErrorCodes.MusicCancelledUnconfirmedSubmission, cancelled.ErrorCode);
        Assert.NotEqual(GenerationJobErrorCodes.MusicCancelled, cancelled.ErrorCode);
        Assert.Equal(1, provider.ReconciliationCalls);
        await AssertRetryRefusedAsync(lost.Client, lost.JobId, GenerationJobErrorCodes.MusicSubmissionRetryBlocked);
        await AssertReservationCancelledAsync(lost.JobId);
        await AssertUnknownProviderCostAsync(lost.JobId);
        Assert.Equal(submissionsBefore + 1, provider.SubmittedGenerations);
        await lost.StopAsync();
        Assert.Equal(submissionsBefore + 1, provider.SubmittedGenerations);
    }

    [Fact]
    public async Task Reconciled_generation_that_is_still_running_closes_the_cancellation_and_records_the_answer()
    {
        // "In progress" is the most dangerous answer of all: the paid generation is live while the customer
        // has already cancelled, so the outcome cannot be released and must not be re-dispatched either.
        var provider = Provider();
        var submissionsBefore = provider.SubmittedGenerations;
        provider.Reset(AcceptedCancellationMode.ReportThenHang);
        provider.ReconciliationResult = new MusicSubmissionReconciliation(MusicSubmissionOutcome.InProgress, ProviderDeclaresNoCharge: false);
        using var lost = await StartClaimThatWillBeLostAsync(provider, "f361b-running");

        var cancelled = await TerminaliseThroughRecoveryAsync(lost.Client, lost.JobId);
        Assert.Equal(GenerationJobErrorCodes.MusicCancelledUnconfirmedSubmission, cancelled.ErrorCode);
        var (_, _, metadata) = await AttemptEvidenceAsync(lost.JobId);
        Assert.NotNull(metadata);
        Assert.Contains("InProgress", metadata!, StringComparison.Ordinal);
        await AssertRetryRefusedAsync(lost.Client, lost.JobId, GenerationJobErrorCodes.MusicSubmissionRetryBlocked);
        await AssertUnknownProviderCostAsync(lost.JobId);
        Assert.Equal(1, provider.ReconciliationCalls);
        Assert.Equal(submissionsBefore + 1, provider.SubmittedGenerations);
        await lost.StopAsync();
        Assert.Equal(submissionsBefore + 1, provider.SubmittedGenerations);
    }

    [Fact]
    public async Task Unknown_provider_answer_keeps_the_cancellation_closed()
    {
        // 404, an unparsable body, a transport failure or an expired result all collapse into Unknown, and
        // Unknown is the fail-closed branch: the operation id outlives the terminalisation so a later
        // reconciliation can still answer the question the worker could not.
        var provider = Provider();
        var submissionsBefore = provider.SubmittedGenerations;
        provider.Reset(AcceptedCancellationMode.ReportThenHang);
        provider.ReconciliationResult = MusicSubmissionReconciliation.Unknown;
        using var lost = await StartClaimThatWillBeLostAsync(provider, "f361b-unknown");

        var cancelled = await TerminaliseThroughRecoveryAsync(lost.Client, lost.JobId);
        Assert.Equal(GenerationJobErrorCodes.MusicCancelledUnconfirmedSubmission, cancelled.ErrorCode);
        Assert.Equal(1, provider.ReconciliationCalls);
        Assert.Equal(AcceptedCancellationMusicProvider.AcceptedOperationId, provider.ReconciledOperationId);
        await AssertRetryRefusedAsync(lost.Client, lost.JobId, GenerationJobErrorCodes.MusicSubmissionRetryBlocked);
        await AssertReservationCancelledAsync(lost.JobId);
        await AssertUnknownProviderCostAsync(lost.JobId);
        var (_, marker, _) = await AttemptEvidenceAsync(lost.JobId);
        Assert.Equal(AcceptedCancellationMusicProvider.AcceptedOperationId, marker);
        Assert.Equal(submissionsBefore + 1, provider.SubmittedGenerations);
        await lost.StopAsync();
        Assert.Equal(submissionsBefore + 1, provider.SubmittedGenerations);
    }

    [Fact]
    public async Task Provider_lookup_that_throws_still_terminalises_the_cancellation_closed()
    {
        // A reconciliation that cannot complete may not strand the job in Running and may not fall back to
        // the release: the worker converts the fault into the unknown outcome and finishes the terminal
        // state, which is the only way the reservation and the member-facing answer stay consistent.
        var provider = Provider();
        var submissionsBefore = provider.SubmittedGenerations;
        provider.Reset(AcceptedCancellationMode.ReportThenHang);
        provider.ThrowOnReconcile = true;
        using var lost = await StartClaimThatWillBeLostAsync(provider, "f361b-throwing");

        var cancelled = await TerminaliseThroughRecoveryAsync(lost.Client, lost.JobId);
        Assert.Equal("Cancelled", cancelled.Status);
        Assert.Equal(GenerationJobErrorCodes.MusicCancelledUnconfirmedSubmission, cancelled.ErrorCode);
        Assert.Equal(1, provider.ReconciliationCalls);
        await AssertRetryRefusedAsync(lost.Client, lost.JobId, GenerationJobErrorCodes.MusicSubmissionRetryBlocked);
        await AssertReservationCancelledAsync(lost.JobId);
        await AssertUnknownProviderCostAsync(lost.JobId);
        Assert.Equal(submissionsBefore + 1, provider.SubmittedGenerations);
        await lost.StopAsync();
        Assert.Equal(submissionsBefore + 1, provider.SubmittedGenerations);
    }

    private sealed class LostClaim : IDisposable
    {
        public LostClaim(HttpClient client, Guid jobId, GenerationJobWorker worker, CancellationTokenSource process)
        {
            Client = client;
            JobId = jobId;
            Worker = worker;
            Process = process;
        }

        public HttpClient Client { get; }
        public Guid JobId { get; }
        public GenerationJobWorker Worker { get; }
        public CancellationTokenSource Process { get; }
        private bool Stopped { get; set; }

        public async Task StopAsync()
        {
            if (Stopped) return;
            Stopped = true;
            Process.Cancel();
            await ((IHostedService)Worker).StopAsync(CancellationToken.None);
            // The client stays usable: two tests post the customer retry after stopping the
            // abandoned worker, which is exactly what the release and refund legs assert.
        }

        public void Dispose()
        {
            // Defensive cleanup so a failed assertion cannot leave a worker polling this fixture's
            // database. Every test stops its own claim first, which makes this a no-op.
            if (!Stopped)
            {
                Stopped = true;
                Process.Cancel();
                try
                {
                    ((IHostedService)Worker).StopAsync(CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (Exception)
                {
                }
            }

            Client.Dispose();
        }
    }

    /// <summary>
    /// Creates a music job and lets a real worker claim it, then leaves that worker parked inside the
    /// provider: the process that knows what it submitted is about to become unavailable, which is what
    /// lease recovery is tested against. The claim token is deliberately left alone so the durable evidence
    /// belongs to the claim that is lost.
    /// </summary>
    private async Task<LostClaim> StartClaimThatWillBeLostAsync(
        AcceptedCancellationMusicProvider provider,
        string label,
        bool supersedeClaim = false,
        bool waitAccepted = true,
        Func<Guid, Task>? beforeDispatch = null,
        Func<Guid, Task>? beforeAcceptance = null)
    {
        var client = factory.CreateClient();
        var auth = await Register(client, $"{label}-{Guid.NewGuid():N}@example.com");
        var created = await SendWithCsrf(client, HttpMethod.Post, "/api/music-generation/jobs", MusicBody(auth));
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var job = (await created.Content.ReadFromJsonAsync<CreateMusicGenerationResponse>(Json))!.Job;
        if (supersedeClaim) provider.BeforeMarkerStampAsync = SupersedeAttemptClaim(job.Id);
        if (beforeDispatch is not null) provider.BeforeMarkerStampAsync = () => beforeDispatch(job.Id);
        if (beforeAcceptance is not null) provider.BeforeAcceptanceStampAsync = () => beforeAcceptance(job.Id);
        var acceptances = provider.ReportedAcceptances;
        var process = new CancellationTokenSource();
        var worker = CreateWorker(cancellationPollMilliseconds: 60_000);
        await ((IHostedService)worker).StartAsync(process.Token);
        if (waitAccepted)
        {
            for (var attempt = 0; attempt < 400 && provider.ReportedAcceptances == acceptances; attempt++) await Task.Delay(25);
            Assert.True(provider.ReportedAcceptances > acceptances, $"{label}: the provider never reported an accepted submission.");
        }

        return new LostClaim(client, job.Id, worker, process);
    }

    private Func<Task> SupersedeAttemptClaim(Guid jobId) => async () =>
    {
        // Simulates the marker write failing at the durable layer: the attempt row stops matching this
        // claim's token, so the pre-dispatch UPDATE is a no-op. Only the attempt row is touched - the job
        // row keeps its token, so the worker's own terminal writes still land and the test observes the
        // guard's decision rather than a fencing artefact.
        var superseded = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<TaslimDbContext>().GenerationProviderAttempts
            .Where(item => item.GenerationJobId == jobId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.JobConcurrencyToken, superseded));
    };

    /// <summary>
    /// Stages the crash (pending member cancellation + expired lease) and runs the recovery sweep until the
    /// job is terminal, then stops the recovery worker. The abandoned worker is left parked on purpose: the
    /// durable evidence belongs to the claim that died, and its later writes must be fenced out.
    /// </summary>
    private async Task<GenerationJobDto> TerminaliseThroughRecoveryAsync(HttpClient client, Guid jobId)
    {
        using (var stage = factory.Services.CreateScope())
        {
            await stage.ServiceProvider.GetRequiredService<TaslimDbContext>().GenerationJobs
                .Where(item => item.Id == jobId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.CancellationRequested, true)
                    .SetProperty(item => item.ClaimExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        }

        using var recoveryProcess = new CancellationTokenSource();
        var recoveryWorker = CreateWorker(cancellationPollMilliseconds: 10);
        await ((IHostedService)recoveryWorker).StartAsync(recoveryProcess.Token);
        var cancelled = await WaitForStatusAsync(client, jobId, "Cancelled");
        recoveryProcess.Cancel();
        await ((IHostedService)recoveryWorker).StopAsync(CancellationToken.None);
        return cancelled;
    }

    private async Task<(int Markers, string? Marker, string? Metadata)> AttemptEvidenceAsync(Guid jobId)
    {
        using var scope = factory.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<TaslimDbContext>()
            .GenerationProviderAttempts.AsNoTracking()
            .Where(item => item.GenerationJobId == jobId)
            .ToListAsync();
        var stamped = rows.Where(item => !string.IsNullOrEmpty(item.ProviderExecutionId)).ToList();
        return (stamped.Count,
            stamped.Count == 1 ? stamped[0].ProviderExecutionId : null,
            stamped.Count == 1 ? stamped[0].SafeMetadataJson : null);
    }

    private static async Task AssertRetryRefusedAsync(HttpClient client, Guid jobId, string expectedCode)
    {
        var retry = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{jobId}/retry", null, $"f361b-retry-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.BadRequest, retry.StatusCode);
        using var refusal = JsonDocument.Parse(await retry.Content.ReadAsStringAsync());
        Assert.Equal(expectedCode, refusal.RootElement.GetProperty("error").GetProperty("code").GetString());
        // A fresh idempotency key is not a way around a closed outcome.
        var second = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{jobId}/retry", null, $"f361b-retry-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    private async Task AssertReservationCancelledAsync(Guid jobId)
    {
        using var scope = factory.Services.CreateScope();
        var usage = await scope.ServiceProvider.GetRequiredService<TaslimDbContext>().UsageTransactions
            .AsNoTracking().SingleAsync(item => item.RequestId == $"generation:{jobId:N}");
        // Never charged for the closed submission, and no refund invented for a generation whose provider
        // outcome is still unknown - the reservation is cancelled, not reversed.
        Assert.Equal(UsageTransactionStatus.Cancelled, usage.Status);
        Assert.Equal(0m, usage.ChargedAmount);
        Assert.Equal(0m, usage.ReversedAmount);
    }

    private async Task AssertUnknownProviderCostAsync(Guid jobId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.GenerationJobId == jobId);
        Assert.Equal(UsageTransactionStatus.Cancelled, usage.Status);
        Assert.Equal(0m, usage.ProviderCostUsd);
        Assert.False(usage.ProviderCostKnown);
        Assert.Equal(UsageCostBasis.Unknown, usage.CostBasis);
        Assert.Equal(0m, usage.ChargedAmount);
        Assert.Equal(0m, usage.ReversedAmount);

        var attempt = await db.GenerationProviderAttempts.AsNoTracking().SingleAsync(item => item.GenerationJobId == jobId);
        Assert.False(attempt.ActualProviderCostKnown);
        Assert.Null(attempt.ActualProviderCostUsd);
        using var metadata = JsonDocument.Parse(attempt.SafeMetadataJson!);
        Assert.Equal("manual_required", metadata.RootElement.GetProperty("musicProviderCostSettlement").GetProperty("status").GetString());
    }

    private string? stampedOperationId(Guid attemptId)
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<TaslimDbContext>()
            .GenerationProviderAttempts.AsNoTracking()
            .Where(item => item.Id == attemptId)
            .Select(item => item.ProviderExecutionId)
            .SingleOrDefault();
    }

    private async Task<int> CountChildrenAsync(Guid jobId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TaslimDbContext>()
            .GenerationJobs.AsNoTracking().CountAsync(item => item.RetryOfJobId == jobId);
    }

    private AcceptedCancellationMusicProvider Provider() => factory.Services.GetRequiredService<AcceptedCancellationMusicProvider>();

    private GenerationJobWorker CreateWorker(int cancellationPollMilliseconds)
        => new(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new GenerationJobOptions
            {
                WorkerEnabled = true,
                WorkerConcurrency = 1,
                PollIntervalMilliseconds = 25,
                CancellationPollMilliseconds = cancellationPollMilliseconds,
                ClaimRecoveryIntervalMilliseconds = 60_000,
                ClaimLeaseMinutes = 2,
                MaxAutomaticRetries = 3,
            }),
            NullLogger<GenerationJobWorker>.Instance);

    private static async Task<GenerationJobDto> WaitForStatusAsync(HttpClient client, Guid jobId, string status)
    {
        for (var attempt = 0; attempt < 800; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{jobId}");
            Assert.NotNull(job);
            if (job!.Status == status) return job;
            await Task.Delay(25);
        }

        throw new TimeoutException($"Job {jobId} never reached {status}.");
    }

    private static object MusicBody(AuthResponse auth) => new
    {
        workspaceId = auth.PersonalWorkspace.Id,
        title = "Accepted submission recovery",
        description = "A calm instrumental bed used to pin out-of-process cancellation recovery.",
        purpose = "Integration test for durable accepted-submission evidence during lease recovery.",
        genre = "cinematic",
        mood = "calm",
        durationSeconds = 60,
        vocalPreference = "instrumental",
        language = "auto",
    };

    private static async Task<AuthResponse> Register(HttpClient client, string email)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName = "Accepted Recovery Tester", email, password = "StrongPassword!123", preferredLanguage = "en" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
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

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

public sealed class AcceptedCancellationMusicApiFactory : GenerationJobsApiFactory
{
    protected override bool WorkerEnabled => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("MusicGeneration:Enabled", "true");
        builder.UseSetting("MusicGeneration:ProviderKey", "test");
        builder.UseSetting("MusicGeneration:Model", "music-test");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMusicGenerationProvider>();
            services.AddSingleton<AcceptedCancellationMusicProvider>();
            services.AddSingleton<IMusicGenerationProvider>(sp => sp.GetRequiredService<AcceptedCancellationMusicProvider>());
        });
    }
}

internal enum AcceptedCancellationMode
{
    /// The billable create was answered with an operation id, the worker's durable sink was told, and the
    /// provider then stays in flight: the process lost here leaves a paid submission nobody reconciled.
    ReportThenHang,
    /// No acceptance is ever reported, which is what every provider double written before F-36.1 does
    /// through the interface default - the pre-submission posture that must stay retryable.
    HangWithoutReporting,
    /// Acceptance is reported and the provider finishes late without observing the caller's token, so the
    /// worker reaches its publication fence with a cancellation already committed instead of an exception.
    ReportThenFinishOnRelease,
    /// F-36.1b: the durable pre-dispatch marker is the last thing the worker can record before money
    /// moves. This mode takes the attempt row's claim token away underneath the stamp, so the marker
    /// UPDATE matches no row - the persistence failure the pre-dispatch guard has to turn into "no paid
    /// request at all" rather than a log line beside a POST.
    MarkerWriteFails,
    /// The marker reached the database and the billable create was handed to the transport, but the
    /// provider never answered before the process was lost. Recovery then has a sentinel and no operation
    /// id: the narrowest crash window F-36.1b leaves, and the one it must still refuse to re-dispatch.
    DispatchThenHang,
}

/// <summary>
/// Stands in for the two real music providers at the F-36.1 seam: it reports the accepted operation id
/// through the sink the worker attaches, and its submission counter is the ground truth for "was a second
/// generation paid for". No real provider is ever approached.
/// </summary>
internal sealed class AcceptedCancellationMusicProvider : IMusicGenerationProvider
{
    public const string AcceptedOperationId = "op-accepted-before-the-crash";

    private static readonly byte[] Mp3 = CreateMinimalMp3();
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int submittedGenerations;
    private int reportedAcceptances;
    private int dispatchSinkCalls;
    private int dispatchSinkFaults;
    private int acceptanceSinkFaults;
    private int reconciliationCalls;
    private string? reconciledOperationId;

    public string Key => "test";

    public int SubmittedGenerations => Volatile.Read(ref submittedGenerations);

    public int ReportedAcceptances => Volatile.Read(ref reportedAcceptances);
    public int DispatchSinkCalls => Volatile.Read(ref dispatchSinkCalls);
    public int DispatchSinkFaults => Volatile.Read(ref dispatchSinkFaults);
    public int AcceptanceSinkFaults => Volatile.Read(ref acceptanceSinkFaults);
    public int ReconciliationCalls => Volatile.Read(ref reconciliationCalls);
    public string? ReconciledOperationId => Volatile.Read(ref reconciledOperationId);

    /// Runs immediately before the worker's pre-dispatch stamp, so a test can make that stamp fail for
    /// a reason the production code cannot see coming (a claim that was superseded underneath it).
    public Func<Task>? BeforeMarkerStampAsync { get; set; }
    public Func<Task>? BeforeAcceptanceStampAsync { get; set; }

    /// The answer recovery's provider-status lookup gets back, i.e. the test-side stand-in for what the
    /// vendor's own status endpoint says about the durable operation id.
    public MusicSubmissionReconciliation? ReconciliationResult { get; set; }
    public bool ThrowOnReconcile { get; set; }

    public AcceptedCancellationMode Mode { get; private set; } = AcceptedCancellationMode.ReportThenHang;

    public void Reset(AcceptedCancellationMode mode)
    {
        Mode = mode;
        BeforeMarkerStampAsync = null;
        BeforeAcceptanceStampAsync = null;
        Interlocked.Exchange(ref acceptanceSinkFaults, 0);
        ReconciliationResult = null;
        ThrowOnReconcile = false;
        Interlocked.Exchange(ref dispatchSinkCalls, 0);
        Interlocked.Exchange(ref dispatchSinkFaults, 0);
        Interlocked.Exchange(ref reconciliationCalls, 0);
        Interlocked.Exchange(ref reconciledOperationId, null);
        Interlocked.Exchange(ref reportedAcceptances, 0);
    }

    public void Release() => released.TrySetResult();

    public Task<MusicSubmissionReconciliation> ReconcileAcceptedSubmissionAsync(
        string providerOperationId,
        CancellationToken cancellationToken = default)
    {
        // Lease recovery's only view of the provider during the crash window, driven by the durable
        // operation id. The classification is supplied by the test and every call is counted, so a
        // marker-only cancellation can be proven never to have asked a question it could not phrase.
        Interlocked.Increment(ref reconciliationCalls);
        Interlocked.Exchange(ref reconciledOperationId, providerOperationId);
        if (ThrowOnReconcile)
        {
            // Transport failure, timeout, an unparsable answer: the worker has to treat "unanswerable"
            // exactly like "unknown" and may not fall back to a release.
            throw new MusicProviderUnavailableException();
        }

        return Task.FromResult(ReconciliationResult ?? MusicSubmissionReconciliation.Unknown);
    }
    public Task<MusicProviderResult> GenerateAsync(MusicGenerationInput request, CancellationToken cancellationToken = default)
        => GenerateAsync(request, cancellationToken, onSubmissionAccepted: null);

    public async Task<MusicProviderResult> GenerateAsync(
        MusicGenerationInput request,
        CancellationToken cancellationToken,
        Func<string, Task>? onSubmissionAccepted,
        Func<Task>? onDispatchSubmitting = null)
    {
        if (Mode != AcceptedCancellationMode.HangWithoutReporting)
        {
            // Both real providers stamp the durable marker before the billable create is issued, so this
            // is the seam the F-36.1b pre-dispatch guard is exercised through. HangWithoutReporting keeps
            // the interface default on purpose: it is what a provider double predating F-36.1 does, and
            // its pre-submission posture has to stay exactly as it was.
            if (BeforeMarkerStampAsync is not null) await BeforeMarkerStampAsync();
            if (onDispatchSubmitting is null)
            {
                throw new InvalidOperationException("The music handler must attach a pre-dispatch sink for an evidence-aware provider.");
            }

            Interlocked.Increment(ref dispatchSinkCalls);
            try
            {
                await onDispatchSubmitting();
            }
            catch (OperationCanceledException)
            {
                // A deliberate caller cancellation is still a cancellation, not a marker fault.
                throw;
            }
            catch (Exception)
            {
                // Fail closed above: the counter that stands in for "a generation was paid for" is only
                // reached when the marker was durable, so a failed write leaves it exactly where it was.
                Interlocked.Increment(ref dispatchSinkFaults);
                throw new MusicProviderUnavailableException();
            }
        }
        Interlocked.Increment(ref submittedGenerations);
        if (Mode is AcceptedCancellationMode.ReportThenHang or AcceptedCancellationMode.ReportThenFinishOnRelease)
        {
            // Mirrors Mubert and Stable Audio: the create call was answered with the provider's own
            // operation id, and the evidence is handed to the sink before any polling happens.
            if (BeforeAcceptanceStampAsync is not null) await BeforeAcceptanceStampAsync();
            try
            {
                if (onSubmissionAccepted is not null) await onSubmissionAccepted(AcceptedOperationId);
            }
            catch (MusicProviderSubmissionUnconfirmedException)
            {
                Interlocked.Increment(ref acceptanceSinkFaults);
                throw;
            }
            Interlocked.Increment(ref reportedAcceptances);
        }

        if (Mode == AcceptedCancellationMode.ReportThenFinishOnRelease)
        {
            // Deliberately does not observe the caller's token: this is the generation that finished after
            // the member's cancellation had already been committed.
            await released.Task;
            return new MusicProviderResult(
                Mp3,
                "audio/mpeg",
                "mp3",
                request.DurationSeconds,
                new MusicProviderUsage(100, 200, 0.004m, 0.004m, 12, CostBasis: UsageCostBasis.Actual));
        }

        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new MusicProviderUnavailableException();
    }

    private static byte[] CreateMinimalMp3()
    {
        var bytes = new byte[417];
        bytes[0] = 0xFF;
        bytes[1] = 0xFB;
        bytes[2] = 0x90;
        bytes[3] = 0x64;
        return bytes;
    }
}
