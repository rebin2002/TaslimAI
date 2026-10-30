using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Taslim.Api.Tests.ProviderTesting;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieLegacyQuoteReadinessAuditTests
{
    [Fact]
    public async Task Fresh_shot_quote_rejections_do_not_persist_reference_override_audit_or_generation_state()
    {
        using var factory = new MovieDeferredReadinessAuditApiFactory();
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client,
            title: "No audit before confirmed shot quote",
            sceneDurationSeconds: 5);
        var path = $"/api/movie-studio/shots/{fixture.Shot.Id}/generate";
        const string overrideReason = "Explicit provider-free regression fixture.";

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var clipsBefore = await db.MovieClips.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id);
        var jobsBefore = await db.GenerationJobs.CountAsync();
        var usageBefore = await db.UsageTransactions.CountAsync();
        var snapshotsBefore = await db.MovieCharacterContinuitySnapshots.CountAsync();
        var auditsBefore = await db.MovieReferenceReadinessOverrideAudits.CountAsync();

        using var missingQuote = await MovieOperationalFixtures.SendWithCsrfAsync(
            client,
            HttpMethod.Post,
            path,
            new
            {
                sourceResolution = "1080p",
                targetResolution = "1080p",
                qualityTier = "Standard",
                processingPath = "native",
                confirmationAccepted = true,
                allowReferenceReadinessOverride = true,
                referenceReadinessOverrideReason = overrideReason,
            },
            idempotencyKey: "shot-override-missing-quote");
        Assert.Equal(HttpStatusCode.BadRequest, missingQuote.StatusCode);
        Assert.Contains("MOVIE_SCENE_QUOTE_REQUIRED", await missingQuote.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var missingConfirmation = await MovieOperationalFixtures.SendWithCsrfAsync(
            client,
            HttpMethod.Post,
            path,
            new
            {
                sourceResolution = "1080p",
                targetResolution = "1080p",
                qualityTier = "Standard",
                processingPath = "native",
                confirmationAccepted = false,
                expectedProviderKey = "fake-movie",
                expectedProviderModelKey = "fake-model",
                expectedProviderCostUsd = 0m,
                allowReferenceReadinessOverride = true,
                referenceReadinessOverrideReason = overrideReason,
            },
            idempotencyKey: "shot-override-unconfirmed-quote");
        Assert.Equal(HttpStatusCode.BadRequest, missingConfirmation.StatusCode);
        Assert.Contains("MOVIE_SCENE_QUOTE_CONFIRMATION_REQUIRED", await missingConfirmation.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var staleQuote = await MovieOperationalFixtures.BuildConfirmedMovieGenerationRequestAsync(
            factory,
            path,
            new
            {
                sourceResolution = "1080p",
                targetResolution = "1080p",
                qualityTier = "Standard",
                processingPath = "native",
                allowReferenceReadinessOverride = true,
                referenceReadinessOverrideReason = overrideReason,
            });
        staleQuote["expectedProviderCostUsd"] = JsonSerializer.SerializeToElement(999m);
        using var staleMaximum = await MovieOperationalFixtures.SendWithCsrfAsync(
            client,
            HttpMethod.Post,
            path,
            staleQuote,
            idempotencyKey: "shot-override-stale-maximum");
        Assert.Equal(HttpStatusCode.BadRequest, staleMaximum.StatusCode);
        Assert.Contains("MOVIE_SCENE_QUOTE_STALE", await staleMaximum.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal(3, factory.ReferenceReadiness.ReadinessChecks);
        Assert.Equal(3, factory.ReferenceReadiness.ReadOnlyOverrideChecks);
        Assert.Equal(0, factory.ReferenceReadiness.PersistedOverrideChecks);
        Assert.Equal(auditsBefore, await db.MovieReferenceReadinessOverrideAudits.CountAsync());
        Assert.Equal(clipsBefore, await db.MovieClips.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.Equal(jobsBefore, await db.GenerationJobs.CountAsync());
        Assert.Equal(usageBefore, await db.UsageTransactions.CountAsync());
        Assert.Equal(snapshotsBefore, await db.MovieCharacterContinuitySnapshots.CountAsync());
        Assert.Equal(0, factory.Calls.Calls(FakeProviderKind.Movie));
    }

    [Fact]
    public async Task Canonical_shot_replay_precedes_changed_shot_and_reference_readiness_and_needs_no_new_quote()
    {
        using var factory = new MovieDeferredReadinessAuditApiFactory();
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client,
            title: "Replay remains canonical",
            sceneDurationSeconds: 5);
        var path = $"/api/movie-studio/shots/{fixture.Shot.Id}/generate";
        const string idempotencyKey = "shot-replay-after-readiness-change";

        var queued = await MovieOperationalFixtures.PostWithConfirmedMovieQuoteAsync<MovieStudioGenerationResponse>(
            factory,
            client,
            path,
            new { title = "Canonical shot replay" },
            idempotencyKey: idempotencyKey);
        Assert.Equal(1, factory.ReferenceReadiness.ReadinessChecks);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var clipsBefore = await db.MovieClips.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id);
        var jobsBefore = await db.GenerationJobs.CountAsync();
        var usageBefore = await db.UsageTransactions.CountAsync();
        var snapshotsBefore = await db.MovieCharacterContinuitySnapshots.CountAsync();
        await db.MovieShots.Where(item => item.Id == fixture.Shot.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, MovieShotStatuses.Archived));
        factory.ReferenceReadiness.BlockReadiness = true;

        using var replay = await MovieOperationalFixtures.SendWithCsrfAsync(
            client,
            HttpMethod.Post,
            path,
            new { title = "Canonical shot replay" },
            idempotencyKey: idempotencyKey);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        var replayed = await replay.Content.ReadFromJsonAsync<MovieStudioGenerationResponse>();
        Assert.NotNull(replayed);
        Assert.Equal(queued.Job.Id, replayed!.Job.Id);
        Assert.Equal(queued.ClipId, replayed.ClipId);

        Assert.Equal(1, factory.ReferenceReadiness.ReadinessChecks);
        Assert.Equal(MovieShotStatuses.Archived, await db.MovieShots.Where(item => item.Id == fixture.Shot.Id).Select(item => item.Status).SingleAsync());
        Assert.Equal(clipsBefore, await db.MovieClips.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.Equal(jobsBefore, await db.GenerationJobs.CountAsync());
        Assert.Equal(usageBefore, await db.UsageTransactions.CountAsync());
        Assert.Equal(snapshotsBefore, await db.MovieCharacterContinuitySnapshots.CountAsync());
        Assert.Equal(0, factory.Calls.Calls(FakeProviderKind.Movie));
    }
}

public sealed class MovieDeferredReadinessAuditApiFactory : MovieOperationalApiFactory
{
    public RecordingMovieReferenceReadinessService ReferenceReadiness { get; } = new();

    protected override bool WorkerEnabled => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMovieReferenceReadinessService>();
            services.AddSingleton<IMovieReferenceReadinessService>(ReferenceReadiness);
        });
    }
}

public sealed class RecordingMovieReferenceReadinessService : IMovieReferenceReadinessService
{
    public bool BlockReadiness { get; set; }
    public int ReadinessChecks { get; private set; }
    public int ReadOnlyOverrideChecks { get; private set; }
    public int PersistedOverrideChecks { get; private set; }

    public Task<MovieReferenceReadinessDto?> GetAsync(Guid userId, Guid shotId, CancellationToken cancellationToken = default) =>
        Task.FromResult<MovieReferenceReadinessDto?>(null);

    public Task<MovieReferenceReadinessDto?> EvaluateAsync(Guid userId, Guid shotId, CancellationToken cancellationToken = default) =>
        Task.FromResult<MovieReferenceReadinessDto?>(null);

    public Task<MovieReferenceReadinessOverrideDto?> RecordOverrideAsync(
        Guid userId,
        Guid shotId,
        MovieReferenceReadinessOverrideRequest request,
        string source,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<MovieReferenceReadinessOverrideDto?>(null);

    public Task EnsureCanGenerateAsync(
        Guid userId,
        Guid shotId,
        bool allowOverride,
        string? overrideReason,
        string source,
        CancellationToken cancellationToken = default,
        bool persistOverride = true)
    {
        ReadinessChecks++;
        if (BlockReadiness && !allowOverride)
            throw new MovieReferenceReadinessException(
                "MOVIE_REFERENCE_READINESS_BLOCKED",
                "The test readiness state changed after the canonical request.");
        if (allowOverride && persistOverride)
            PersistedOverrideChecks++;
        else if (allowOverride)
            ReadOnlyOverrideChecks++;
        return Task.CompletedTask;
    }
}
