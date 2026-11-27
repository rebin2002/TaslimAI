using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Music;
using Taslim.Api.Infrastructure;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Post-billing retrieval isolation for music. Once the provider has created and billed a
/// generation, a transport failure while fetching the finished result is a delivery problem, not a
/// provider outage. It must not read as "please try again", because RetryAsync replays the original
/// input as a fresh billable submission and would orphan the track that already exists. These tests
/// pin the whole chain - distinct exception type, own public error code, copy that does not invite a
/// retry, and a mechanical refusal at the retry endpoint - while proving that only persisted
/// pre-dispatch or explicit provider no-charge evidence keeps a retry available.
/// </summary>
public sealed class MusicPostBillingRetrievalIsolationTests : IClassFixture<MusicRetrievalFailureApiFactory>
{
    private readonly MusicRetrievalFailureApiFactory factory;

    public MusicPostBillingRetrievalIsolationTests(MusicRetrievalFailureApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Post_billing_retrieval_failure_gets_its_own_code_and_retry_is_refused()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"music-retrieval-{Guid.NewGuid():N}@example.com");
        var created = await SendWithCsrf(client, HttpMethod.Post, "/api/music-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            title = "Retrieval isolation test",
            description = "A calm instrumental bed used to pin post-billing retry isolation.",
            purpose = "Integration test for duplicate paid submission prevention.",
            genre = "cinematic",
            mood = "calm",
            durationSeconds = 60,
            vocalPreference = "instrumental",
            language = "auto",
        });
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var submitted = (await created.Content.ReadFromJsonAsync<CreateMusicGenerationResponse>(JsonOptions))!.Job;
        var failed = await WaitForTerminal(client, submitted.Id);

        Assert.Equal(GenerationJobStatus.Failed.ToString(), failed.Status);
        Assert.Equal(GenerationJobErrorCodes.MusicOutputRetrievalFailed, failed.ErrorCode);
        Assert.NotNull(failed.ErrorMessage);
        Assert.DoesNotContain("try again", failed.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(GenerationJobErrorMessages.For(GenerationJobErrorCodes.MusicOutputRetrievalFailed), failed.ErrorMessage);

        var billedRows = await UsageRowCount(submitted.Id);
        Assert.Equal(1, billedRows);

        var retry = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{submitted.Id}/retry", null, $"music-retrieval-retry-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.BadRequest, retry.StatusCode);
        using var errorBody = JsonDocument.Parse(await retry.Content.ReadAsStringAsync());
        Assert.Equal(GenerationJobErrorCodes.MusicRetrievalRetryBlocked, errorBody.RootElement.GetProperty("error").GetProperty("code").GetString());

        using var finalScope = factory.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(0, await finalDb.GenerationJobs.AsNoTracking().CountAsync(item => item.RetryOfJobId == submitted.Id));
        // The refused retry must not have added a second reservation for the same generation.
        Assert.Equal(billedRows, await UsageRowCount(submitted.Id));
    }

    [Fact]
    public async Task Repeated_retry_attempts_stay_refused_and_never_queue_a_second_generation()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"music-retrieval-repeat-{Guid.NewGuid():N}@example.com");
        var created = await SendWithCsrf(client, HttpMethod.Post, "/api/music-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            title = "Retrieval isolation repeat test",
            description = "A calm instrumental bed used to pin post-billing retry isolation.",
            purpose = "Integration test for repeated retry refusal.",
            genre = "cinematic",
            mood = "calm",
            durationSeconds = 60,
            vocalPreference = "instrumental",
            language = "auto",
        });
        var submitted = (await created.Content.ReadFromJsonAsync<CreateMusicGenerationResponse>(JsonOptions))!.Job;
        await WaitForTerminal(client, submitted.Id);

        var key = $"music-retrieval-repeat-{Guid.NewGuid():N}";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var retry = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{submitted.Id}/retry", null, key);
            Assert.Equal(HttpStatusCode.BadRequest, retry.StatusCode);
            using var errorBody = JsonDocument.Parse(await retry.Content.ReadAsStringAsync());
            Assert.Equal(GenerationJobErrorCodes.MusicRetrievalRetryBlocked, errorBody.RootElement.GetProperty("error").GetProperty("code").GetString());
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(0, await db.GenerationJobs.AsNoTracking().CountAsync(item => item.IdempotencyKey == key));
        Assert.Equal(0, await db.GenerationJobs.AsNoTracking().CountAsync(item => item.RetryOfJobId == submitted.Id));
    }

    private async Task<int> UsageRowCount(Guid jobId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return await db.UsageTransactions.AsNoTracking().CountAsync(item => item.RequestId == $"generation:{jobId:N}");
    }

    private static async Task<AuthResponse> Register(HttpClient client, string email)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName = "Retrieval Tester", email, password = "StrongPassword!123", preferredLanguage = "en" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<GenerationJobDto> WaitForTerminal(HttpClient client, Guid id)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{id}");
            Assert.NotNull(job);
            if (job.Status is "Succeeded" or "Failed" or "Cancelled") return job;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Music job {id} did not reach a terminal state.");
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

/// <summary>
/// Control lane: the same retry endpoint must keep working exactly as before for every other music
/// failure class, so the isolation stays narrow instead of disabling music retries altogether.
/// </summary>
public sealed class MusicProviderOutageRetryPreservationTests : IClassFixture<MusicOutageApiFactory>
{
    private readonly MusicOutageApiFactory factory;

    public MusicProviderOutageRetryPreservationTests(MusicOutageApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Ordinary_provider_outage_still_maps_to_the_retryable_code_and_stays_retryable()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"music-outage-{Guid.NewGuid():N}@example.com");
        var created = await SendWithCsrf(client, HttpMethod.Post, "/api/music-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            title = "Outage control test",
            description = "A calm instrumental bed used as the retryable provider-outage control.",
            purpose = "Integration control for unchanged retry behaviour.",
            genre = "cinematic",
            mood = "calm",
            durationSeconds = 60,
            vocalPreference = "instrumental",
            language = "auto",
        });
        var submitted = (await created.Content.ReadFromJsonAsync<CreateMusicGenerationResponse>(JsonOptions))!.Job;
        var failed = await WaitForTerminal(client, submitted.Id);

        Assert.Equal(GenerationJobStatus.Failed.ToString(), failed.Status);
        Assert.Equal(GenerationJobErrorCodes.MusicProviderUnavailable, failed.ErrorCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var attempt = await db.GenerationProviderAttempts.AsNoTracking().SingleAsync(item => item.GenerationJobId == submitted.Id);
            Assert.Null(attempt.ProviderExecutionId);
            Assert.Contains("musicDispatchProtocol", attempt.SafeMetadataJson, StringComparison.Ordinal);
        }
        // Pre-existing copy is intentionally left untouched for the retryable class.
        Assert.Equal("Music generation is temporarily unavailable. Please try again later.", failed.ErrorMessage);

        var retry = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{submitted.Id}/retry", null, $"music-outage-retry-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        var retryJob = (await retry.Content.ReadFromJsonAsync<GenerationJobDto>(JsonOptions))!;
        Assert.Equal(submitted.Id, retryJob.RetryOfJobId);
    }

    private static async Task<AuthResponse> Register(HttpClient client, string email)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName = "Outage Tester", email, password = "StrongPassword!123", preferredLanguage = "en" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<GenerationJobDto> WaitForTerminal(HttpClient client, Guid id)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{id}");
            Assert.NotNull(job);
            if (job.Status is "Succeeded" or "Failed" or "Cancelled") return job;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Music job {id} did not reach a terminal state.");
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

/// <summary>
/// Boundary pins that need no server: the refusal copy must never invite a retry, the generic
/// music fallback must stay intact for every other code, and the new exception must remain a
/// <see cref="MusicProviderUnavailableException"/> so every pre-existing guard still catches it.
/// </summary>
public sealed class MusicPostBillingRetrievalContractTests
{
    [Fact]
    public void Retrieval_failure_copy_does_not_invite_the_paid_regeneration_the_fallback_would()
    {
        var retrieval = GenerationJobErrorMessages.For(GenerationJobErrorCodes.MusicOutputRetrievalFailed);
        var blocked = GenerationJobErrorMessages.For(GenerationJobErrorCodes.MusicRetrievalRetryBlocked);

        Assert.NotNull(retrieval);
        Assert.NotNull(blocked);
        Assert.DoesNotContain("try again", retrieval, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("try again", blocked, StringComparison.OrdinalIgnoreCase);
        // The exact-match arms must win over the "MUSIC_" prefix fallback, which says "try again".
        Assert.NotEqual("The music could not be generated. Please try again.", retrieval);
        Assert.NotEqual("The music could not be generated. Please try again.", blocked);
    }

    [Fact]
    public void Existing_music_copy_is_unchanged_for_retryable_and_fallback_classes()
    {
        Assert.Equal("Music generation is temporarily unavailable. Please try again later.",
            GenerationJobErrorMessages.For(GenerationJobErrorCodes.MusicProviderUnavailable));
        Assert.Equal("The music was generated but could not be saved. Please try again.",
            GenerationJobErrorMessages.For(GenerationJobErrorCodes.MusicOutputStorageFailed));
        Assert.Equal("The music could not be generated. Please try again.",
            GenerationJobErrorMessages.For("MUSIC_FUTURE_UNMAPPED_CODE"));
    }

    [Fact]
    public void Retrieval_failure_stays_a_provider_unavailable_exception_for_existing_guards()
    {
        var failure = new MusicOutputRetrievalFailedException();

        Assert.IsAssignableFrom<MusicProviderUnavailableException>(failure);
        Assert.DoesNotContain("http", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api.", failure.Message, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class MusicRetrievalFailureApiFactory : GenerationJobsApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("MusicGeneration:Enabled", "true");
        builder.UseSetting("MusicGeneration:ProviderKey", "test");
        builder.UseSetting("MusicGeneration:Model", "music-test");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMusicGenerationProvider>();
            services.AddSingleton<IMusicGenerationProvider, RetrievalFailedMusicProvider>();
        });
    }
}

public sealed class MusicOutageApiFactory : GenerationJobsApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("MusicGeneration:Enabled", "true");
        builder.UseSetting("MusicGeneration:ProviderKey", "test");
        builder.UseSetting("MusicGeneration:Model", "music-test");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMusicGenerationProvider>();
            services.AddSingleton<IMusicGenerationProvider, OutageMusicProvider>();
        });
    }
}

/// <summary>
/// Stands in for a provider whose generation completed and was billed, but whose finished result
/// could not be transported after the bounded GET re-fetches (the Stable Audio and Mubert
/// exhaustion paths, asserted separately in their own provider tests).
/// </summary>
internal sealed class RetrievalFailedMusicProvider : IMusicGenerationProvider
{
    public string Key => "test";

    public Task<MusicProviderResult> GenerateAsync(MusicGenerationInput request, CancellationToken cancellationToken = default)
        => throw new MusicOutputRetrievalFailedException();
}

internal sealed class OutageMusicProvider : IMusicGenerationProvider
{
    public string Key => "test";

    public Task<MusicProviderResult> GenerateAsync(MusicGenerationInput request, CancellationToken cancellationToken = default)
        => throw new MusicProviderUnavailableException();

    // Model an actual pre-dispatch outage, before the adapter could hand a paid request to transport.
    public Task<MusicProviderResult> GenerateAsync(
        MusicGenerationInput request, CancellationToken cancellationToken,
        Func<string, Task>? onSubmissionAccepted, Func<Task>? onDispatchSubmitting = null)
        => GenerateAsync(request, cancellationToken);

}

/// <summary>
/// Accepted-but-unconfirmed lane. Both music providers can lose contact with a generation they have
/// already accepted: the create call returns a track or generation id (so a paid submission exists) and
/// the polling path then times out, runs out of bounded attempts, or keeps failing transiently. That
/// state is neither "nothing was submitted" (the retryable outage and timeout codes instruct
/// GenerationJobExecution.RetryAsync to post a SECOND paid generation) nor "the music was generated"
/// (the retrieval-failure copy the previous lane uses asserts a finished track that was never observed).
/// The providers therefore report the code lease recovery already uses for the same unknown outcome,
/// MUSIC_PROVIDER_SUBMISSION_UNCONFIRMED, whose shipped copy states the outcome honestly and whose
/// automatic re-dispatch is suppressed (GenerationFailureClassifier pins it to retryable=false). The
/// customer's retry endpoint is refused for this code as well: an informed retry is still a second
/// paid submission whose sibling may already be running and billing, and the explicit product policy
/// is fair billing over immediate retry, so the job stays closed until the accepted outcome is
/// reconciled. A provider-declared terminal rejection and a cancellation the member asked for are
/// different states and keep their existing recovery path (see MusicRetryPostureIsolationTests).
/// </summary>
public sealed class MusicSubmissionUnconfirmedIsolationTests : IClassFixture<MusicUnconfirmedSubmissionApiFactory>
{
    private readonly MusicUnconfirmedSubmissionApiFactory factory;
    public MusicSubmissionUnconfirmedIsolationTests(MusicUnconfirmedSubmissionApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Accepted_but_unconfirmed_provider_fault_gets_the_unconfirmed_code_without_a_second_submission()
    {
        using var client = factory.CreateClient();
        var submissionsBefore = UnconfirmedProvider().Submissions;
        var auth = await Register(client, $"music-unconfirmed-{Guid.NewGuid():N}@example.com");
        var created = await SendWithCsrf(client, HttpMethod.Post, "/api/music-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            title = "Unconfirmed submission test",
            description = "A calm instrumental bed used to pin accepted-but-unconfirmed retry semantics.",
            purpose = "Integration test for duplicate paid submission prevention.",
            genre = "cinematic",
            mood = "calm",
            durationSeconds = 60,
            vocalPreference = "instrumental",
            language = "auto",
        });
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var submitted = (await created.Content.ReadFromJsonAsync<CreateMusicGenerationResponse>(UnconfirmedJsonOptions))!.Job;

        var failed = await WaitForTerminal(client, submitted.Id);
        Assert.Equal(GenerationJobStatus.Failed.ToString(), failed.Status);
        Assert.Equal(GenerationJobErrorCodes.MusicProviderSubmissionUnconfirmed, failed.ErrorCode);

        // The customer sees the shipped uncertainty copy, not a claim that a track exists.
        Assert.NotNull(failed.ErrorMessage);
        Assert.Equal(GenerationJobErrorMessages.For(GenerationJobErrorCodes.MusicProviderSubmissionUnconfirmed), failed.ErrorMessage);
        Assert.DoesNotContain("Your music was generated", failed.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not restarted automatically", failed.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        // Exactly one provider submission happened for THIS job: the failure terminal does not
        // re-dispatch it, so the accepted generation is not paid for twice. The fixture provider is
        // shared by every test in the class, hence the delta form.
        Assert.Equal(submissionsBefore + 1, UnconfirmedProvider().Submissions);
        submissionsBefore = UnconfirmedProvider().Submissions;
        var usage = await SingleUsageRow(submitted.Id);
        Assert.Equal(UsageTransactionStatus.Failed, usage.Status);
        Assert.Equal(0m, usage.ChargedAmount);

        // Fail-closed retry (explicit product policy): the customer's retry endpoint refuses this code
        // too, because a retry is a second billable submission while the first one is unreconciled.
        var retry = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{submitted.Id}/retry", null, $"music-unconfirmed-retry-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.BadRequest, retry.StatusCode);
        using (var refusal = JsonDocument.Parse(await retry.Content.ReadAsStringAsync()))
        {
            Assert.Equal(
                GenerationJobErrorCodes.MusicSubmissionRetryBlocked,
                refusal.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        // The refused retry queued nothing: no child job, no second provider submission, and the
        // usage row is still the single failed attempt at zero charged.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            Assert.Equal(0, await db.GenerationJobs.AsNoTracking().CountAsync(item => item.RetryOfJobId == submitted.Id));
        }
        Assert.Equal(submissionsBefore, UnconfirmedProvider().Submissions);
        var afterRetry = await SingleUsageRow(submitted.Id);
        Assert.Equal(UsageTransactionStatus.Failed, afterRetry.Status);
        Assert.Equal(0m, afterRetry.ChargedAmount);
    }
    [Fact]
    public async Task Repeated_customer_retries_of_an_unconfirmed_music_job_stay_refused_and_never_reach_the_provider()
    {
        // The refusal must not be a one-shot: the member can press retry again, and a different
        // idempotency key must not become a way to open a second paid submission for the same
        // accepted generation. This is the end-to-end form of the billing-fairness policy: the
        // submission counter of the fake provider is the ground truth for "was a second track paid for".
        using var client = factory.CreateClient();
        var auth = await Register(client, $"music-unconfirmed-repeat-{Guid.NewGuid():N}@example.com");
        var created = await SendWithCsrf(client, HttpMethod.Post, "/api/music-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            title = "Unconfirmed repeat retry test",
            description = "A calm instrumental bed used to pin repeated retry refusal.",
            purpose = "Integration test for fail-closed retry on accepted-but-unconfirmed outcomes.",
            genre = "cinematic",
            mood = "calm",
            durationSeconds = 60,
            vocalPreference = "instrumental",
            language = "auto",
        });
        var submitted = (await created.Content.ReadFromJsonAsync<CreateMusicGenerationResponse>(UnconfirmedJsonOptions))!.Job;
        var failed = await WaitForTerminal(client, submitted.Id);
        Assert.Equal(GenerationJobErrorCodes.MusicProviderSubmissionUnconfirmed, failed.ErrorCode);
        var before = UnconfirmedProvider().Submissions;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var retry = await SendWithCsrf(
                client, HttpMethod.Post, $"/api/generation/jobs/{submitted.Id}/retry", null,
                $"music-unconfirmed-repeat-{Guid.NewGuid():N}");
            Assert.Equal(HttpStatusCode.BadRequest, retry.StatusCode);
            using var refusal = JsonDocument.Parse(await retry.Content.ReadAsStringAsync());
            Assert.Equal(
                GenerationJobErrorCodes.MusicSubmissionRetryBlocked,
                refusal.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            Assert.Equal(0, await db.GenerationJobs.AsNoTracking().CountAsync(item => item.RetryOfJobId == submitted.Id));
        }
        Assert.Equal(before, UnconfirmedProvider().Submissions);
    }
    [Fact]
    public void Other_music_failure_states_keep_their_existing_recovery_and_are_not_refused_as_unconfirmed()
    {
        // Scope control for the fail-closed refusal: it must apply to the two states that are evidence
        // a submission exists (post-billing retrieval failure, accepted-but-unconfirmed) and not to a
        // provider-declared terminal rejection, which remains retryable only with an explicit no-charge proof.
        // The provider-declared arm is exercised here at the classification level; the job-level form,
        // including a member-asked cancellation, is MusicRetryPostureIsolationTests.
        Assert.Equal("provider", GenerationFailureClassifier.Classify(GenerationJobErrorCodes.MusicProviderSubmissionUnconfirmed).Category);
        Assert.False(GenerationFailureClassifier.Classify(GenerationJobErrorCodes.MusicProviderSubmissionUnconfirmed).Retryable);
        Assert.Equal("provider", GenerationFailureClassifier.Classify(GenerationJobErrorCodes.MusicSubmissionRetryBlocked).Category);
        Assert.False(GenerationFailureClassifier.Classify(GenerationJobErrorCodes.MusicSubmissionRetryBlocked).Retryable);
        // The public classification is not proof of no charge; RetryAsync separately requires durable evidence.
        var rejected = GenerationFailureClassifier.Classify(GenerationJobErrorCodes.MusicPromptRejected);
        Assert.Equal("validation", rejected.Category);
        // And the refusal never re-uses the unconfirmed code, so clients can tell the states apart.
        Assert.NotEqual(
            GenerationJobErrorCodes.MusicProviderSubmissionUnconfirmed,
            GenerationJobErrorCodes.MusicSubmissionRetryBlocked);
        Assert.NotEqual(
            GenerationJobErrorMessages.For(GenerationJobErrorCodes.MusicSubmissionRetryBlocked),
            GenerationJobErrorMessages.For(GenerationJobErrorCodes.MusicProviderSubmissionUnconfirmed));
    }

    private async Task<UsageTransaction> SingleUsageRow(Guid jobId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return await db.UsageTransactions.AsNoTracking()
            .SingleAsync(item => item.RequestId == $"generation:{jobId:N}");
    }

    private UnconfirmedSubmissionMusicProvider UnconfirmedProvider()
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<UnconfirmedSubmissionMusicProvider>();
    }

    private static async Task<AuthResponse> Register(HttpClient client, string email)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName = "Unconfirmed Tester", email, password = "StrongPassword!123", preferredLanguage = "en" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>(UnconfirmedJsonOptions))!;
    }

    private static async Task<GenerationJobDto> WaitForTerminal(HttpClient client, Guid id)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{id}", UnconfirmedJsonOptions);
            Assert.NotNull(job);
            if (job.Status is "Succeeded" or "Failed" or "Cancelled") return job;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Music job {id} did not reach a terminal state.");
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

    private static readonly JsonSerializerOptions UnconfirmedJsonOptions = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// Boundary pins that need no server: the new exception must stay inside the provider-unavailable
/// family so every pre-existing guard catches it, must carry no completion claim or provider detail,
/// must map to the honest copy rather than the retrieval text, and must stay suppressed for automatic
/// re-dispatch while the retrieval-failure retry block is left exactly as shipped.
/// </summary>
public sealed class MusicSubmissionUnconfirmedContractTests
{
    [Fact]
    public void Unconfirmed_exception_stays_unavailable_family_and_carries_no_claim_of_a_finished_track()
    {
        var failure = new MusicProviderSubmissionUnconfirmedException();
        Assert.IsAssignableFrom<MusicProviderUnavailableException>(failure);
        Assert.IsNotType<MusicOutputRetrievalFailedException>(failure);
        Assert.DoesNotContain("generated", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("completed", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api.", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unconfirmed_copy_states_the_unknown_outcome_instead_of_the_retrieval_claim()
    {
        var unconfirmed = GenerationJobErrorMessages.For(GenerationJobErrorCodes.MusicProviderSubmissionUnconfirmed);
        var retrieval = GenerationJobErrorMessages.For(GenerationJobErrorCodes.MusicOutputRetrievalFailed);
        Assert.NotNull(unconfirmed);
        Assert.NotEqual(retrieval, unconfirmed);
        Assert.Contains("may already have reached the provider", unconfirmed, StringComparison.OrdinalIgnoreCase);
        // The retrieval arm asserts existence; the unconfirmed arm must not borrow that wording.
        Assert.DoesNotContain("Your music was generated", unconfirmed, StringComparison.OrdinalIgnoreCase);
        // The retry endpoint refuses this job, so the copy must not tell the member to retry it.
        Assert.DoesNotContain("try again", unconfirmed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not restarted automatically", unconfirmed, StringComparison.OrdinalIgnoreCase);
        var blocked = GenerationJobErrorMessages.For(GenerationJobErrorCodes.MusicSubmissionRetryBlocked);
        Assert.NotNull(blocked);
        Assert.Contains("retry was blocked", blocked, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("try again", blocked, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Your music was generated", blocked, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cancellation_copy_distinguishes_accepted_from_pre_submission_and_known_from_unknown()
    {
        // F-36: three different truths must not collapse into one message. Cancelling before acceptance
        // means nothing is in flight; cancelling an accepted-but-unobserved generation means the first
        // attempt may still finish and still bill; cancelling after a completed output means the track
        // exists and only delivery was lost. Each gets its own wording, and only the ordinary one may
        // suggest trying again.
        var plain = GenerationJobErrorMessages.For(GenerationJobErrorCodes.MusicCancelled);
        var unconfirmed = GenerationJobErrorMessages.For(GenerationJobErrorCodes.MusicCancelledUnconfirmedSubmission);
        var undelivered = GenerationJobErrorMessages.For(GenerationJobErrorCodes.MusicCancelledUndeliveredOutput);
        Assert.NotNull(plain);
        Assert.NotNull(unconfirmed);
        Assert.NotNull(undelivered);
        Assert.NotEqual(plain, unconfirmed);
        Assert.NotEqual(plain, undelivered);
        Assert.NotEqual(unconfirmed, undelivered);
        Assert.Contains("already accepted", unconfirmed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("may still finish", unconfirmed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("try again", unconfirmed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Your music was generated", unconfirmed, StringComparison.OrdinalIgnoreCase);
        // The output WAS observed complete, so this one may say so - and must not offer a retry that
        // would generate the finished track a second time.
        Assert.Contains("Your music was generated", undelivered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("try again", undelivered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("may still finish", undelivered, StringComparison.OrdinalIgnoreCase);
        // The unconfirmed arm must not borrow the retrieval arm's existence claim either.
        Assert.NotEqual(GenerationJobErrorMessages.For(GenerationJobErrorCodes.MusicOutputRetrievalFailed), unconfirmed);
    }

    [Fact]
    public void Post_acceptance_cancellations_are_classified_as_non_retryable_for_automation()
    {
        // Both cancellation codes describe an accepted submission, so neither may be re-dispatched by an
        // operator automaton: the unknown-outcome one sits with the provider family and the finished-but-
        // undelivered one with the delivery family, matching the retry refusals in RetryAsync.
        var submission = GenerationFailureClassifier.Classify(GenerationJobErrorCodes.MusicCancelledUnconfirmedSubmission);
        Assert.Equal("provider", submission.Category);
        Assert.False(submission.Retryable);
        var delivery = GenerationFailureClassifier.Classify(GenerationJobErrorCodes.MusicCancelledUndeliveredOutput);
        Assert.Equal("delivery", delivery.Category);
        Assert.False(delivery.Retryable);
        // The ordinary cancellation keeps its existing, retryable-free posture.
        Assert.Equal("cancelled", GenerationFailureClassifier.Classify(GenerationJobErrorCodes.MusicCancelled).Category);
    }

    [Fact]
    public void Unconfirmed_code_stays_non_retryable_for_automation()
    {
        var result = GenerationFailureClassifier.Classify(GenerationJobErrorCodes.MusicProviderSubmissionUnconfirmed);
        Assert.Equal("provider", result.Category);
        Assert.False(result.Retryable);
    }
}

public sealed class MusicUnconfirmedSubmissionApiFactory : GenerationJobsApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("MusicGeneration:Enabled", "true");
        builder.UseSetting("MusicGeneration:ProviderKey", "test");
        builder.UseSetting("MusicGeneration:Model", "music-test");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMusicGenerationProvider>();
            services.AddSingleton<UnconfirmedSubmissionMusicProvider>();
            services.AddSingleton<IMusicGenerationProvider>(sp => sp.GetRequiredService<UnconfirmedSubmissionMusicProvider>());
        });
    }
}

/// <summary>
/// Stands in for a provider that accepted a billable generation (its create call returned an id) and
/// then lost contact: the polling path exhausted its bounded retries without ever observing completion.
/// The submission counter is the test-side measure of "a second billable generation was created".
/// </summary>
internal sealed class UnconfirmedSubmissionMusicProvider : IMusicGenerationProvider
{
    private int submissions;
    public string Key => "test";
    public int Submissions => Volatile.Read(ref submissions);
    public Task<MusicProviderResult> GenerateAsync(MusicGenerationInput request, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref submissions);
        throw new MusicProviderSubmissionUnconfirmedException();
    }
}
/// <summary>
/// The music postures that the fail-closed refusal must NOT capture, plus the one it must. A prompt
/// rejection with a provider-declared no-charge promise and a cancellation the member asked for BEFORE
/// the provider dispatch marker are both evidence that no paid submission is in flight, so their retry
/// path stays open (the delay outcome waits on the caller's token, which is how a
/// cancellation reaches a running music job). The third posture is F-36: the member cancels AFTER the
/// provider accepted the paid generation, which the real providers signal with
/// MusicProviderCancellationWithAcceptedSubmissionException and which the retry endpoint must refuse.
/// </summary>
internal sealed class PostureMusicProvider : IMusicGenerationProvider
{
    private int submissions;
    public string Key => "test";
    public int Submissions => Volatile.Read(ref submissions);
    public volatile bool DeclareRejected;
    public volatile bool DelayUntilCancelled;
    public volatile bool AcceptThenCancel;
    public volatile bool CancelWithDispatchMarkerOnly;
    public volatile bool AmbiguousCreateFault;
    public volatile bool AcceptThenRejectWithoutNoCharge;

    public Task<MusicProviderResult> GenerateAsync(MusicGenerationInput request, CancellationToken cancellationToken = default)
        => GenerateAsync(request, cancellationToken, null, null);

    public async Task<MusicProviderResult> GenerateAsync(
        MusicGenerationInput request,
        CancellationToken cancellationToken,
        Func<string, Task>? onSubmissionAccepted,
        Func<Task>? onDispatchSubmitting = null)
    {
        if (DelayUntilCancelled)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new MusicProviderUnavailableException();
        }
        if (onDispatchSubmitting is not null) await onDispatchSubmitting();
        if (AcceptThenCancel)
        {
            if (!CancelWithDispatchMarkerOnly && onSubmissionAccepted is not null) await onSubmissionAccepted("posture-accepted-operation");
            Interlocked.Increment(ref submissions);
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !CancelWithDispatchMarkerOnly)
            {
                throw new MusicProviderCancellationWithAcceptedSubmissionException("posture-accepted-operation", outputConfirmedComplete: false);
            }
            throw new MusicProviderUnavailableException();
        }
        Interlocked.Increment(ref submissions);
        if (AcceptThenRejectWithoutNoCharge)
        {
            if (onSubmissionAccepted is not null) await onSubmissionAccepted("posture-rejected-after-acceptance");
            throw new MusicProviderRejectedException();
        }
        if (DeclareRejected) throw new MusicProviderRejectedException(providerDeclaresNoCharge: true);
        if (AmbiguousCreateFault) throw new MusicProviderTimeoutException();
        throw new MusicProviderSubmissionUnconfirmedException();
    }
}
public sealed class MusicPostureApiFactory : GenerationJobsApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("MusicGeneration:Enabled", "true");
        builder.UseSetting("MusicGeneration:ProviderKey", "test");
        builder.UseSetting("MusicGeneration:Model", "music-test");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMusicGenerationProvider>();
            services.AddSingleton<PostureMusicProvider>();
            services.AddSingleton<IMusicGenerationProvider>(sp => sp.GetRequiredService<PostureMusicProvider>());
        });
    }
}
/// <summary>
/// Pins the SCOPE of the fail-closed music retry refusal. The billing-fairness policy blocks a second
/// paid submission only while an earlier submission's outcome is unknown; it must not strand the
/// states that prove nothing is in flight. A provider-declared no-charge prompt rejection and a
/// cancellation before dispatch remain retryable, while an ambiguous dispatch is blocked even with a
/// transient error code.
/// </summary>
public sealed class MusicRetryPostureIsolationTests : IClassFixture<MusicPostureApiFactory>
{
    private readonly MusicPostureApiFactory factory;
    public MusicRetryPostureIsolationTests(MusicPostureApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Ambiguous_create_timeout_is_refused_from_persisted_dispatch_evidence()
    {
        var provider = Provider();
        provider.AmbiguousCreateFault = true;
        var submissionsBefore = provider.Submissions;
        try
        {
            using var client = factory.CreateClient();
            var auth = await Register(client, $"music-ambiguous-{Guid.NewGuid():N}@example.com");
            var created = await SendWithCsrf(client, HttpMethod.Post, "/api/music-generation/jobs", MusicBody(auth));
            Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
            var submitted = (await created.Content.ReadFromJsonAsync<CreateMusicGenerationResponse>(PostureJson))!.Job;
            var failed = await WaitForTerminal(client, submitted.Id);
            Assert.Equal(GenerationJobErrorCodes.MusicProviderSubmissionUnconfirmed, failed.ErrorCode);
            Assert.Equal(submissionsBefore + 1, provider.Submissions);

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
                var attempt = await db.GenerationProviderAttempts.AsNoTracking().SingleAsync(item => item.GenerationJobId == submitted.Id);
                Assert.Equal("taslim:dispatch-pending", attempt.ProviderExecutionId);
                Assert.Contains("musicDispatchProtocol", attempt.SafeMetadataJson, StringComparison.Ordinal);
                var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.RequestId == $"generation:{submitted.Id:N}");
                Assert.Equal(UsageTransactionStatus.Failed, usage.Status);
                Assert.Equal(0m, usage.ChargedAmount);
            }

            var retry = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{submitted.Id}/retry", null, $"music-ambiguous-retry-{Guid.NewGuid():N}");
            Assert.Equal(HttpStatusCode.BadRequest, retry.StatusCode);
            using (var refusal = JsonDocument.Parse(await retry.Content.ReadAsStringAsync()))
                Assert.Equal(GenerationJobErrorCodes.MusicSubmissionRetryBlocked, refusal.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(submissionsBefore + 1, provider.Submissions);

            // A pre-marker attempt from an older worker is not proof of non-dispatch. Erasing the
            // current marker/protocol in this isolated SQLite database models that legacy record and
            // verifies that missing evidence fails closed rather than relying on the timeout code.
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
                var attempt = await db.GenerationProviderAttempts.SingleAsync(item => item.GenerationJobId == submitted.Id);
                attempt.ProviderExecutionId = null;
                attempt.SafeMetadataJson = null;
                var historicalJob = await db.GenerationJobs.SingleAsync(item => item.Id == submitted.Id);
                historicalJob.ErrorCode = GenerationJobErrorCodes.MusicProviderTimeout;
                await db.SaveChangesAsync();
            }
            var legacyRetry = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{submitted.Id}/retry", null, $"music-ambiguous-legacy-{Guid.NewGuid():N}");
            Assert.Equal(HttpStatusCode.BadRequest, legacyRetry.StatusCode);
            Assert.Equal(submissionsBefore + 1, provider.Submissions);
            using var finalScope = factory.Services.CreateScope();
            Assert.Equal(0, await finalScope.ServiceProvider.GetRequiredService<TaslimDbContext>().GenerationJobs.AsNoTracking().CountAsync(item => item.RetryOfJobId == submitted.Id));
        }
        finally
        {
            provider.AmbiguousCreateFault = false;
        }
    }

    [Fact]
    public async Task Rejection_after_provider_acceptance_without_no_charge_proof_does_not_unlock_retry()
    {
        var provider = Provider();
        provider.AcceptThenRejectWithoutNoCharge = true;
        var submissionsBefore = provider.Submissions;
        try
        {
            using var client = factory.CreateClient();
            var auth = await Register(client, $"music-accepted-rejection-{Guid.NewGuid():N}@example.com");
            var created = await SendWithCsrf(client, HttpMethod.Post, "/api/music-generation/jobs", MusicBody(auth));
            Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
            var submitted = (await created.Content.ReadFromJsonAsync<CreateMusicGenerationResponse>(PostureJson))!.Job;
            var failed = await WaitForTerminal(client, submitted.Id);
            Assert.Equal(GenerationJobErrorCodes.MusicPromptRejected, failed.ErrorCode);
            Assert.Equal(submissionsBefore + 1, provider.Submissions);

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
                var attempt = await db.GenerationProviderAttempts.AsNoTracking().SingleAsync(item => item.GenerationJobId == submitted.Id);
                Assert.Equal("posture-rejected-after-acceptance", attempt.ProviderExecutionId);
                Assert.DoesNotContain("provider_declared_no_charge", attempt.SafeMetadataJson ?? string.Empty, StringComparison.Ordinal);
                var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.RequestId == $"generation:{submitted.Id:N}");
                Assert.Equal(UsageTransactionStatus.Failed, usage.Status);
                Assert.Equal(0m, usage.ChargedAmount);
            }

            var retry = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{submitted.Id}/retry", null, $"music-accepted-rejection-retry-{Guid.NewGuid():N}");
            Assert.Equal(HttpStatusCode.BadRequest, retry.StatusCode);
            using (var refusal = JsonDocument.Parse(await retry.Content.ReadAsStringAsync()))
                Assert.Equal(GenerationJobErrorCodes.MusicSubmissionRetryBlocked, refusal.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(submissionsBefore + 1, provider.Submissions);
        }
        finally
        {
            provider.AcceptThenRejectWithoutNoCharge = false;
        }
    }

    [Fact]
    public async Task Provider_declared_rejection_is_still_retryable_after_the_unconfirmed_refusal_landed()
    {
        var provider = Provider();
        provider.DeclareRejected = true;
        try
        {
            using var client = factory.CreateClient();
            var auth = await Register(client, $"music-rejected-{Guid.NewGuid():N}@example.com");
            var created = await SendWithCsrf(client, HttpMethod.Post, "/api/music-generation/jobs", MusicBody(auth));
            Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
            var submitted = (await created.Content.ReadFromJsonAsync<CreateMusicGenerationResponse>(PostureJson))!.Job;
            var failed = await WaitForTerminal(client, submitted.Id);
            Assert.Equal(GenerationJobErrorCodes.MusicPromptRejected, failed.ErrorCode);
            var before = provider.Submissions;
            var retry = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{submitted.Id}/retry", null, $"music-rejected-retry-{Guid.NewGuid():N}");
            Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
            var child = (await retry.Content.ReadFromJsonAsync<GenerationJobDto>(PostureJson))!;
            Assert.Equal(submitted.Id, child.RetryOfJobId);
            // The retried job runs asynchronously; wait for it, then prove the provider was
            // re-approached - which is correct here because the provider's explicit no-charge outcome
            // proves that no paid generation is in flight.
            var retried = await WaitForTerminal(client, child.Id);
            Assert.Equal(GenerationJobErrorCodes.MusicPromptRejected, retried.ErrorCode);
            Assert.Equal(before + 1, provider.Submissions);
        }
        finally
        {
            provider.DeclareRejected = false;
        }
    }
    [Fact]
    public async Task Member_cancelled_music_job_is_still_retryable_and_is_never_reported_as_unconfirmed()
    {
        var provider = Provider();
        provider.DelayUntilCancelled = true;
        try
        {
            using var client = factory.CreateClient();
            var auth = await Register(client, $"music-cancelled-{Guid.NewGuid():N}@example.com");
            var created = await SendWithCsrf(client, HttpMethod.Post, "/api/music-generation/jobs", MusicBody(auth));
            var submitted = (await created.Content.ReadFromJsonAsync<CreateMusicGenerationResponse>(PostureJson))!.Job;
            GenerationJobDto? running = null;
            for (var attempt = 0; attempt < 200; attempt++)
            {
                running = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{submitted.Id}");
                if (running?.Status == GenerationJobStatus.Running.ToString()) break;
                await Task.Delay(25);
            }
            Assert.Equal(GenerationJobStatus.Running.ToString(), running?.Status);
            var cancel = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{submitted.Id}/cancel", null);
            Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
            var cancelled = await WaitForTerminal(client, submitted.Id);
            Assert.Equal(GenerationJobStatus.Cancelled.ToString(), cancelled.Status);
            Assert.NotEqual(GenerationJobErrorCodes.MusicProviderSubmissionUnconfirmed, cancelled.ErrorCode);
        // F-36 makes the code meaningful, so pin it: this cancellation happened before anything was
        // accepted (the posture provider never reports acceptance), which is exactly why a retry is safe.
        Assert.Equal(GenerationJobErrorCodes.MusicCancelled, cancelled.ErrorCode);
            var retry = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{submitted.Id}/retry", null, $"music-cancelled-retry-{Guid.NewGuid():N}");
            Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
            var child = (await retry.Content.ReadFromJsonAsync<GenerationJobDto>(PostureJson))!;
            Assert.Equal(submitted.Id, child.RetryOfJobId);
            // Let the retried job finish so the shared fixture does not carry a running job onward.
            await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{child.Id}/cancel", null);
            await WaitForTerminal(client, child.Id);
        }
        finally
        {
            provider.DelayUntilCancelled = false;
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_after_dispatch_remains_uncertain_and_never_posts_a_second_generation(bool markerOnly)
    {
        // The end-to-end regression the billing-fairness policy needs for F-36: the provider accepted the
        // paid generation, the member cancelled it, and no provider-side cancellation was ever issued -
        // so the accepted generation may still finish and still bill. The job must be recorded as a
        // cancellation that is NOT freely retryable, the retry endpoint must refuse it with the same code
        // as the polling-timeout family, the customer-facing copy must not claim a finished track or
        // invite another submission, and the provider's submission counter must not move. That counter is
        // the ground truth for "was a second track paid for"; the fixture provider is shared across the
        // class, so it is asserted as a delta.
        var provider = Provider();
        provider.CancelWithDispatchMarkerOnly = markerOnly;
        provider.AcceptThenCancel = true;
        var submissionsBeforeCreate = provider.Submissions;
        var client = factory.CreateClient();
        Guid? jobId = null;
        try
        {
            var auth = await Register(client, $"music-accepted-cancel-{Guid.NewGuid():N}@example.com");
            var created = await SendWithCsrf(client, HttpMethod.Post, "/api/music-generation/jobs", MusicBody(auth));
            Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
            var submitted = (await created.Content.ReadFromJsonAsync<CreateMusicGenerationResponse>(PostureJson))!.Job;
            jobId = submitted.Id;
            GenerationJobDto? running = null;
            for (var attempt = 0; attempt < 400; attempt++)
            {
                running = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{submitted.Id}");
                if (running?.Status == GenerationJobStatus.Running.ToString()) break;
                await Task.Delay(25);
            }
            Assert.Equal(GenerationJobStatus.Running.ToString(), running?.Status);
            // A job turns Running when the worker CLAIMS it, which happens before the provider is
            // approached, so wait for the accepted submission itself: that is the moment from which a
            // member cancellation is a post-acceptance cancellation. Exactly one submission for this job.
            for (var attempt = 0; attempt < 400 && provider.Submissions == submissionsBeforeCreate; attempt++) await Task.Delay(25);
            Assert.Equal(submissionsBeforeCreate + 1, provider.Submissions);

            var cancel = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{submitted.Id}/cancel", null);
            Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
            var cancelled = await WaitForTerminal(client, submitted.Id);
            Assert.Equal(GenerationJobStatus.Cancelled.ToString(), cancelled.Status);
            Assert.Equal(GenerationJobErrorCodes.MusicCancelledUnconfirmedSubmission, cancelled.ErrorCode);
            // Honest copy: the customer was billed once, the outcome is unknown, nothing is asserted as
            // complete, and the message does not tell them to try again.
            Assert.NotNull(cancelled.ErrorMessage);
            Assert.Equal(GenerationJobErrorMessages.For(GenerationJobErrorCodes.MusicCancelledUnconfirmedSubmission), cancelled.ErrorMessage);
            Assert.DoesNotContain("Your music was generated", cancelled.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("try again", cancelled.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("may still finish", cancelled.ErrorMessage, StringComparison.OrdinalIgnoreCase);

            var before = provider.Submissions;
            var retry = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{submitted.Id}/retry", null, $"music-accepted-cancel-retry-{Guid.NewGuid():N}");
            Assert.Equal(HttpStatusCode.BadRequest, retry.StatusCode);
            using (var refusal = JsonDocument.Parse(await retry.Content.ReadAsStringAsync()))
            {
                Assert.Equal(
                    GenerationJobErrorCodes.MusicSubmissionRetryBlocked,
                    refusal.RootElement.GetProperty("error").GetProperty("code").GetString());
            }
            // A second attempt with a fresh idempotency key must not be a way around the refusal either.
            var second = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{submitted.Id}/retry", null, $"music-accepted-cancel-retry-{Guid.NewGuid():N}");
            Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
                Assert.Equal(0, await db.GenerationJobs.AsNoTracking().CountAsync(item => item.RetryOfJobId == submitted.Id));
                var usage = await db.UsageTransactions.AsNoTracking()
                    .SingleAsync(item => item.RequestId == $"generation:{submitted.Id:N}");
                // The usage row is cancelled, not charged twice, and carries the unreconciled code.
                Assert.Equal(UsageTransactionStatus.Cancelled, usage.Status);
            }
            Assert.Equal(before, provider.Submissions);
        }
        finally
        {
            provider.AcceptThenCancel = false;
            provider.CancelWithDispatchMarkerOnly = false;
            // Never strand the shared fixture on an in-flight job: if the test failed before its own
            // cancellation ran, cancel and drain it so the neighbouring posture tests still get a worker.
            if (jobId is Guid orphan)
            {
                try
                {
                    await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{orphan}/cancel", null);
                    await WaitForTerminal(client, orphan);
                }
                catch
                {
                    // Best effort only; the assertions above carry the verdict.
                }
            }
            client.Dispose();
        }
    }

    private static object MusicBody(AuthResponse auth) => new
    {
        workspaceId = auth.PersonalWorkspace.Id,
        title = "Retry posture test",
        description = "A calm instrumental bed used to pin the scope of the music retry refusal.",
        purpose = "Integration test for retry posture separation.",
        genre = "cinematic",
        mood = "calm",
        durationSeconds = 60,
        vocalPreference = "instrumental",
        language = "auto",
    };
    private static readonly JsonSerializerOptions PostureJson = new(JsonSerializerDefaults.Web);
    private PostureMusicProvider Provider()
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<PostureMusicProvider>();
    }
    private async Task<GenerationJobDto> WaitForTerminal(HttpClient client, Guid jobId)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{jobId}");
            if (job is not null
                && job.Status != GenerationJobStatus.Queued.ToString()
                && job.Status != GenerationJobStatus.Running.ToString()) return job;
            await Task.Delay(25);
        }
        throw new TimeoutException($"music job {jobId} never reached a terminal state");
    }
    private static async Task<AuthResponse> Register(HttpClient client, string email)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Retry Posture Owner",
            email,
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>(PostureJson))!;
    }
    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? body, string? idempotencyKey = null)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var message = new HttpRequestMessage(method, path);
        message.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        if (idempotencyKey is not null) message.Headers.Add("Idempotency-Key", idempotencyKey);
        if (body is not null) message.Content = JsonContent.Create(body);
        return await client.SendAsync(message);
    }
}
