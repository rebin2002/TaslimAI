using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
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
        Assert.Equal(0, Provider().SubmittedGenerations);

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
        Assert.Equal(0, Provider().SubmittedGenerations);

        using var finalScope = factory.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var durable = await finalDb.GenerationJobs.AsNoTracking().FirstAsync(item => item.Id == job.Id);
        Assert.NotEqual(staleToken, durable.ConcurrencyToken);
        Assert.Null(durable.ClaimExpiresAt);
        Assert.Equal(0, durable.RetryCount);

        // The reservation is finalized as failed with zero customer charge, like every other failure.
        var usage = await finalDb.UsageTransactions.AsNoTracking()
            .SingleAsync(item => item.RequestId == $"generation:{job.Id:N}");
        Assert.Equal(UsageTransactionStatus.Failed, usage.Status);
        Assert.Equal(0m, usage.ChargedAmount);

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
    public async Task Non_music_lease_recovery_still_requeues_unchanged()
    {
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
        Assert.Equal(0, Provider().SubmittedGenerations);
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

    public int SubmittedGenerations => Volatile.Read(ref submittedGenerations);

    public Task<MusicProviderResult> GenerateAsync(MusicGenerationInput request, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref submittedGenerations);
        return Task.FromResult(new MusicProviderResult(
            Mp3,
            "audio/mpeg",
            "mp3",
            request.DurationSeconds,
            new MusicProviderUsage(100, 200, 0.004m, 0.004m, 12, CostBasis: UsageCostBasis.Actual)));
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
        Assert.Equal(0m, usage.ChargedAmount);
        Assert.Equal(0m, usage.ReversedAmount);

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
            Assert.Equal(0m, usage.ChargedAmount);
            Assert.Equal(0, await db.GenerationJobOutputs.AsNoTracking().CountAsync(item => item.GenerationJobId == job.Id));
        }

        liveProcess.Cancel();
        await ((IHostedService)worker).StopAsync(CancellationToken.None);
        Assert.Equal(submissionsBefore + 1, provider.SubmittedGenerations);
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

    public string Key => "test";

    public int SubmittedGenerations => Volatile.Read(ref submittedGenerations);

    public int ReportedAcceptances => Volatile.Read(ref reportedAcceptances);

    public AcceptedCancellationMode Mode { get; private set; } = AcceptedCancellationMode.ReportThenHang;

    public void Reset(AcceptedCancellationMode mode)
    {
        Mode = mode;
        Interlocked.Exchange(ref reportedAcceptances, 0);
    }

    public void Release() => released.TrySetResult();

    public Task<MusicProviderResult> GenerateAsync(MusicGenerationInput request, CancellationToken cancellationToken = default)
        => GenerateAsync(request, cancellationToken, onSubmissionAccepted: null);

    public async Task<MusicProviderResult> GenerateAsync(
        MusicGenerationInput request,
        CancellationToken cancellationToken,
        Func<string, Task>? onSubmissionAccepted,
        Func<Task>? onDispatchSubmitting = null)
    {
        Interlocked.Increment(ref submittedGenerations);
        if (Mode is AcceptedCancellationMode.ReportThenHang or AcceptedCancellationMode.ReportThenFinishOnRelease)
        {
            // Mirrors Mubert and Stable Audio: the create call was answered with the provider's own
            // operation id, and the evidence is handed to the sink before any polling happens.
            if (onSubmissionAccepted is not null) await onSubmissionAccepted(AcceptedOperationId);
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
