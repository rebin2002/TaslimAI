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
/// retry, and a mechanical refusal at the retry endpoint - while proving that ordinary music
/// provider outages keep their existing retry behaviour.
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
/// automatic re-dispatch is suppressed (GenerationFailureClassifier pins it to retryable=false), while
/// an informed customer may still choose one deliberate retry.
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

        // Exactly one provider submission happened: the failure terminal does not re-dispatch the job,
        // so the accepted generation is not paid for twice.
        Assert.Equal(1, UnconfirmedProvider().Submissions);
        var usage = await SingleUsageRow(submitted.Id);
        Assert.Equal(UsageTransactionStatus.Failed, usage.Status);
        Assert.Equal(0m, usage.ChargedAmount);

        // Deliberate, informed customer retry stays available for this code (the shipped product policy,
        // contrasted with the retrieval-failure lane where the retry endpoint refuses mechanically).
        var retry = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{submitted.Id}/retry", null, $"music-unconfirmed-retry-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        var child = (await retry.Content.ReadFromJsonAsync<GenerationJobDto>(UnconfirmedJsonOptions))!;
        Assert.Equal(submitted.Id, child.RetryOfJobId);
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
