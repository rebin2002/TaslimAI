using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Taslim.Api.Tests.ProviderTesting;
using Taslim.Api.Usage;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Issue #685 — the question no other lane answers: can the SAME number, 7,200, travel through a
/// synthetic API request/response as whole-film PROJECT metadata while the identical number is
/// refused as one VIDEO GENERATION CLIP before anything is reserved or queued?
///
/// Non-duplication evidence (checked 2026-11-19 against main <c>588c1bd7</c> and the open lanes):
/// - #1057 <c>MovieProjectDurationCeilingCompatibilityTests</c>: planning/validator constants and
///   the provider ADAPTER, normalizer, capability catalog and cost boundary. No HTTP admission
///   seam, no whole-film timeline total in milliseconds.
/// - <c>MovieIntercutPlannerTests</c> (merged, main): the 7,200 s timeline ceiling and the 3,601 s
///   per-clip ceiling as two unrelated cases — never the whole-film number itself at clip scale,
///   never next to a project response.
/// - <c>MovieBudgetDirectorLongFormPlanTests</c> (merged, main): the service-side planning grid
///   for &gt; 7,200 s briefs, in-process only, no response envelope.
/// - #781 shell/detail read model at 7,200 s; main's <c>MovieSceneReorderLongFormTimelineTests</c>
///   scene-reorder metadata at 7,200 s; #1078/#1083/#1088/#1101 the Director scene-plan validator
///   and apply seams; #1104/#1108 the client timecode. None touches clip admission.
/// - #269 <c>MovieProjectDurationBoundaryTests</c>/<c>MovieClipDurationAdmissionTests</c> prove
///   7,200 s create/read and over-cap clips, but on their own branch, against their own
///   <c>MovieEntities.cs</c> change, with 11 s and 120 s clips. The shapes below use the
///   whole-film number at both scales in one response and compile on current main untouched.
///
/// HOLD — RED findings recorded, not asserted as behaviour main does not have (this file is green
/// on main and stays green when the owners land):
/// 1. <c>MovieStudioService.QueueClipAsync</c> (apps/api/Movies/MovieStudioService.cs:1292, and the
///    identical clamp at :1056, :1110) coerces a stored shot of 7,200 s with
///    <c>Math.Clamp(..., 1, 3600)</c> and queues the truncated number instead of refusing it, so
///    two promised hours silently become one paid hour. Fail-closed rejection with
///    <c>MOVIE_CLIP_DURATION_UNSUPPORTED</c> is owned by PR #269; the same clamp family in
///    <c>MovieProductionPreflight.cs:123/:199</c> is shared with PR #348. Pinned here only as the
///    containment invariant that must hold in every merge order.
/// 2. <c>MovieDurationBudgetPolicy.MaxMovieDurationSeconds</c> is 86,400 s, so the
///    duration-budget read model accepts a 7,201 s target that the create request refuses; the
///    120-minute cap exists in exactly one place (<c>MovieStudioValidation.Validate</c>, main:
///    3,600 s today, and <c>MovieBudgetDirectorService.MaxProjectPlanningDurationSeconds</c> =
///    7,200 s). Reconciling the ceiling across those surfaces is the #269 lane.
///
/// Provider-free: file-backed SQLite test databases, the deterministic in-memory fake movie
/// adapter, the generation worker disabled, synthetic pricing at synthetic amounts. No provider is
/// contacted, nothing is charged or reserved, no production database, customer data, secret or
/// migration is touched, and nothing is merged or deployed.
/// </summary>
public sealed class MovieWholeFilmClipAdmissionBoundaryTests : IDisposable
{
    /// <summary>#685 whole-film ceiling: 120 minutes of project/timeline metadata.</summary>
    private const int ApprovedWholeFilmSeconds = 7_200;

    /// <summary>The repo's renderable-unit ceiling, deliberately a different number from the film cap.</summary>
    private const int RenderableClipCeilingSeconds = MovieDurationBudgetPolicy.MaxShotDurationSeconds;

    private const int WholeFilmMilliseconds = ApprovedWholeFilmSeconds * 1_000;
    private const int RenderableClipMilliseconds = RenderableClipCeilingSeconds * 1_000;

    /// <summary>A provider clip ceiling below the whole-film number, i.e. a short-clip provider.</summary>
    private const int ShortClipProviderCeilingSeconds = 600;

    private readonly MovieWholeFilmBoundaryApiFactory plainFactory = new();
    private readonly MovieWholeFilmGuardrailApiFactory guardrailFactory = new();

    /// <summary>Same seams, with the clip ceiling raised to the repo's renderable-unit ceiling.</summary>
    private readonly MovieWholeFilmGuardrailApiFactory renderableCapFactory = new(RenderableClipCeilingSeconds);

    // ------------------------------------------------ whole-film metadata versus one clip

    [Fact]
    public async Task WholeFilmTwoHourRuntimeIsRepresentableAsCanonicalTimelineTotalWhileTheSameNumberIsRefusedAsOneClip()
    {
        using var client = plainFactory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Two hour canonical total");
        await SetProjectRuntimeAsync(plainFactory, fixture.Project.Project.Id, ApprovedWholeFilmSeconds);

        // Whole-film PROJECT metadata at the canonical-timeline scale: twelve segments of the
        // renderable-unit ceiling build the revision total to exactly the 120-minute runtime.
        var segments = WholeFilmMilliseconds / RenderableClipMilliseconds;
        var revision = await MovieOperationalFixtures.PostAsync<MovieTimelineRevisionDto>(
            client,
            $"/api/movie-studio/projects/{fixture.Project.Project.Id}/timeline/revisions",
            new MovieTimelineRevisionRequest
            {
                Label = "Two hour assembly pass",
                ChangeSummary = "The 120-minute plan carried as whole-film timeline metadata.",
                Tracks =
                [
                    new MovieTimelineTrackRequest
                    {
                        Kind = MovieTimelineTrackKinds.Video,
                        Name = "Picture",
                        TrackNumber = 1,
                        Items = Enumerable.Range(0, segments).Select(index => new MovieTimelineItemRequest
                        {
                            Kind = MovieTimelineItemKinds.Gap,
                            TimelineInMilliseconds = index * RenderableClipMilliseconds,
                            TimelineOutMilliseconds = (index + 1) * RenderableClipMilliseconds,
                        }).ToArray(),
                    },
                ],
            });

        Assert.Equal(WholeFilmMilliseconds, revision.DurationMilliseconds);
        var reloaded = await MovieOperationalFixtures.GetAsync<MovieTimelineDto>(
            client, $"/api/movie-studio/projects/{fixture.Project.Project.Id}/timeline");
        Assert.Equal(WholeFilmMilliseconds, reloaded.CurrentRevision!.DurationMilliseconds);
        Assert.Equal(ApprovedWholeFilmSeconds, await ReadProjectRuntimeAsync(fixture.Project.Project.Id));
        Assert.Equal(0, plainFactory.Calls.Calls(FakeProviderKind.Movie));

        // The identical number at the clip scale, in the same synthetic request/response world:
        // one coverage candidate of exactly 7,200 s is refused against the per-clip ceiling and
        // no proposal is produced.
        var twoHoursAsOneClip = new MovieIntercutPlanRequest
        {
            BaseTimelineVersion = 1,
            Clips =
            [
                new MovieIntercutCoverageRequest
                {
                    ClipId = Guid.NewGuid(),
                    MovieShotId = fixture.Shot.Id,
                    MovieSceneId = fixture.Scene.Id,
                    SceneSequence = 1,
                    ShotSequence = 1,
                    DurationSeconds = ApprovedWholeFilmSeconds,
                    CoverageKind = MovieIntercutCoverageKinds.Primary,
                    Label = "Two hours in one paid clip",
                },
            ],
        };
        var refused = Assert.Throws<MovieIntercutPlanningException>(
            () => MovieIntercutPlanner.Build(fixture.Project.Project.Id, twoHoursAsOneClip));
        Assert.Equal(MovieIntercutPlanCodes.DurationInvalid, refused.Code);
        Assert.Contains($"{RenderableClipCeilingSeconds:N0}", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(7_201)]
    [InlineData(10_800)]
    public async Task ProjectScaleCarriesRuntimesPastTheFilmCeilingWhileTheClipScaleRefusesTheSameNumber(int durationSeconds)
    {
        using var client = plainFactory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: $"Long film scale probe {durationSeconds}");

        // Project scale: nothing at the 120-minute boundary is enforced on the scene entity, so
        // the number the create request will eventually permit travels through the API intact.
        var scene = await MovieOperationalFixtures.PostAsync<MovieSceneDto>(
            client, $"/api/movie-studio/projects/{fixture.Project.Project.Id}/scenes",
            new { title = "Two hour single scene", summary = "One scene spanning the approved runtime.", durationSeconds });
        Assert.Equal(durationSeconds, scene.DurationSeconds);
        await SetProjectRuntimeAsync(plainFactory, fixture.Project.Project.Id, durationSeconds);

        // The duration-budget read model reports the whole-film target verbatim. HOLD (#269):
        // it bounds the film at MovieDurationBudgetPolicy.MaxMovieDurationSeconds (86,400 s), so
        // it is not where the approved 120-minute ceiling is enforced.
        var budget = await MovieOperationalFixtures.GetAsync<MovieDurationBudgetResult>(
            client, $"/api/movie-studio/projects/{fixture.Project.Project.Id}/duration-budget");
        Assert.Equal(durationSeconds, budget.TargetDurationSeconds);

        // Clip scale: the same number on one shot — the unit a provider is paid for — is refused
        // at the request boundary against the independent per-clip ceiling.
        var overCapShot = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/shots",
            new { description = "One shot carrying the entire film runtime.", durationSeconds });
        Assert.Equal(HttpStatusCode.BadRequest, overCapShot.StatusCode);
        var shotRefusal = await overCapShot.Content.ReadAsStringAsync();
        Assert.Contains("MOVIE_SHOT_INVALID", shotRefusal, StringComparison.Ordinal);
        Assert.Contains($"{RenderableClipCeilingSeconds} seconds", shotRefusal, StringComparison.Ordinal);

        // And the shot bound is not quietly widened by the long project: a compliant short shot
        // in the same project is accepted unchanged.
        var compliantShot = await MovieOperationalFixtures.PostAsync<MovieShotDto>(
            client, $"/api/movie-studio/scenes/{scene.Id}/shots",
            new { description = "A provider-sized shot inside a two hour film.", durationSeconds = 10 });
        Assert.Equal(10, compliantShot.DurationSeconds);
    }

    [Theory]
    [InlineData(7_201)]
    [InlineData(10_800)]
    [InlineData(100_000)]
    [InlineData(int.MaxValue)]
    public void ProjectRuntimeAboveTheApproved120MinuteCeilingFailsClosedInEveryPlanningEnvelope(int projectDurationSeconds)
    {
        // The whole-film number is bounded at exactly the approved cap and never truncated into
        // a shorter approved plan.
        var oversizedBrief = Assert.Throws<MovieBudgetDirectorValidationException>(
            () => MovieBudgetDirectorService.DeriveUnplannedProjectShotGrid(projectDurationSeconds));
        Assert.Equal(MovieBudgetDirectorReasons.ProjectDurationUnsupported, oversizedBrief.Code);
        Assert.Contains("120 minutes", oversizedBrief.Message, StringComparison.Ordinal);

        // The same number smuggled in as one clip-sized shot is refused before any estimate.
        Assert.Throws<MovieBudgetDirectorValidationException>(
            () => MovieBudgetDirectorService.DeriveUnplannedProjectShotGrid(ApprovedWholeFilmSeconds * 2));
    }

    // ------------------------------------------------ clip admission before reservation/queue

    [Fact]
    public async Task ClipAboveTheProviderCeilingIsRefusedForALegalWholeFilmProjectBeforeAnyClipJobOrReservation()
    {
        using var client = guardrailFactory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Whole film with an over-cap clip");
        await SetProjectRuntimeAsync(guardrailFactory, fixture.Project.Project.Id, ApprovedWholeFilmSeconds);

        // The generation-ready shot re-planned inside the repo's renderable-unit ceiling but
        // above the selected provider's clip ceiling, inside a legal 120-minute project.
        var clipSizedShotId = fixture.Shot.Id;
        await SetShotDurationAsync(guardrailFactory, clipSizedShotId, ShortClipProviderCeilingSeconds * 2);

        var clipsBefore = await CountAsync(guardrailFactory, db => db.MovieClips.LongCountAsync());
        var jobsBefore = await CountAsync(guardrailFactory, db => db.GenerationJobs.LongCountAsync());
        var usageBefore = await CountAsync(guardrailFactory, db => db.UsageTransactions.LongCountAsync());

        using var rejected = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, $"/api/movie-studio/shots/{clipSizedShotId}/generate",
            new { title = "Two-hour film, twenty-minute clip" },
            idempotencyKey: "685-whole-film-over-cap-clip");

        Assert.Equal(HttpStatusCode.PaymentRequired, rejected.StatusCode);
        Assert.Contains("COST_ESTIMATE_UNKNOWN", await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(clipsBefore, await CountAsync(guardrailFactory, db => db.MovieClips.LongCountAsync()));
        Assert.Equal(jobsBefore, await CountAsync(guardrailFactory, db => db.GenerationJobs.LongCountAsync()));
        Assert.Equal(usageBefore, await CountAsync(guardrailFactory, db => db.UsageTransactions.LongCountAsync()));
        Assert.Equal(0, guardrailFactory.Calls.Calls(FakeProviderKind.Movie));
        await NoJobAndNoClipForIdempotencyKeyAsync("685-whole-film-over-cap-clip");

        // Discrimination, so the refusal above is proven duration-driven rather than a blanket
        // admission failure: the same project admits a clip inside the provider's cap.
        await SetShotDurationAsync(guardrailFactory, clipSizedShotId, 10);
        var admitted = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(
            client, $"/api/movie-studio/shots/{clipSizedShotId}/generate",
            new { title = "Ten-second clip inside the provider cap" },
            idempotencyKey: "685-whole-film-compliant-clip");
        Assert.Equal(GenerationJobStatus.Queued.ToString(), admitted.Job.Status);

        // And the reason is named: the provider clip ceiling, independent of the film runtime.
        var estimator = guardrailFactory.Services.GetRequiredService<IMovieGenerationCostEstimator>();
        var overCap = await estimator.EstimateAsync(new MovieGenerationCostRequest(
            ShortClipProviderCeilingSeconds * 2, "1080p", "1080p", MovieQualityLevels.Standard, "native"), "fake-movie");
        Assert.Equal(MovieGenerationCostEstimateStates.Unknown, overCap.State);
        Assert.Equal("duration_cap_exceeded", overCap.Reason);
        var insideCap = await estimator.EstimateAsync(new MovieGenerationCostRequest(
            10, "1080p", "1080p", MovieQualityLevels.Standard, "native"), "fake-movie");
        Assert.Equal(MovieGenerationCostEstimateStates.Estimated, insideCap.State);
    }

    [Fact]
    public async Task WholeFilmSizedSingleClipNeverBecomesAQueuedProviderClip()
    {
        // HOLD (#269, with #348 for the preflight copy of the same clamp): on main this request is
        // admitted, and the two-hour number survives only as a truncated 3,600-second clip rather
        // than a refusal (MovieStudioService.cs:1292). The assertion below is the containment
        // invariant that must hold in every merge order — the whole-film runtime is never
        // submitted as one paid clip — so it stays green when the fail-closed guard lands.
        using var client = renderableCapFactory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Whole film sized clip request");
        await SetProjectRuntimeAsync(renderableCapFactory, fixture.Project.Project.Id, ApprovedWholeFilmSeconds);
        await SetShotDurationAsync(renderableCapFactory, fixture.Shot.Id, ApprovedWholeFilmSeconds);

        using var response = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, $"/api/movie-studio/shots/{fixture.Shot.Id}/generate",
            new { title = "Two hours promised as one clip" },
            idempotencyKey: "685-whole-film-clip-containment");
        var status = response.StatusCode;
        var body = await response.Content.ReadAsStringAsync();

        if (status is HttpStatusCode.OK or HttpStatusCode.Accepted)
        {
            // Observed on main today: admitted, but coerced down to the clip ceiling. That is the
            // silent truncation the owners must replace with a refusal; the numbers below are the
            // containment half that must hold either way.
            var (clipDuration, inputDuration) = await ReadQueuedClipAsync(fixture.Shot.Id);
            Assert.True(clipDuration <= RenderableClipCeilingSeconds,
                $"A {clipDuration}-second clip row was queued for one provider clip.");
            Assert.True(inputDuration <= RenderableClipCeilingSeconds,
                $"A {inputDuration}-second generation input reached the job queue.");
            // main yields exactly the renderable-unit ceiling here: two promised hours become
            // one paid hour, with no error surfaced to the caller.
            Assert.True(clipDuration < ApprovedWholeFilmSeconds,
                $"The clip ceiling was widened to the whole-film runtime ({clipDuration} s).");
        }
        else
        {
            Assert.Contains("MOVIE_CLIP_DURATION_UNSUPPORTED", body, StringComparison.Ordinal);
            await NoJobAndNoClipForIdempotencyKeyAsync("685-whole-film-clip-containment");
        }
    }

    public void Dispose()
    {
        plainFactory.Dispose();
        guardrailFactory.Dispose();
        renderableCapFactory.Dispose();
    }

    // --------------------------------------------------------------- fixtures

    /// <summary>Movie API with the deterministic in-memory adapter and no background worker.</summary>
    private sealed class MovieWholeFilmBoundaryApiFactory : MovieOperationalApiFactory
    {
        protected override bool WorkerEnabled => false;
    }

    /// <summary>
    /// Same adapter, plus synthetic pricing and a provider clip ceiling that stops well below the
    /// two-hour film number, so clip admission must be decided by the clip scale alone.
    /// </summary>
    private sealed class MovieWholeFilmGuardrailApiFactory : MovieOperationalApiFactory
    {
        private readonly int clipCeilingSeconds;

        protected override bool WorkerEnabled => false;

        public MovieWholeFilmGuardrailApiFactory(int clipCeilingSeconds = ShortClipProviderCeilingSeconds)
        {
            this.clipCeilingSeconds = clipCeilingSeconds;
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.Database.EnsureCreated();
            if (db.ProviderCapabilityPricings.Any(item => item.PricingVersion == "685-boundary-v1")) return;
            db.ProviderCapabilityPricings.Add(new ProviderCapabilityPricing
            {
                Id = Guid.NewGuid(),
                CapabilityKey = "movie.video",
                ProviderKey = "fake-movie",
                ModelKey = "685-boundary-model",
                SourceResolution = "1080p",
                TargetResolution = "1080p",
                QualityTier = MovieQualityLevels.Standard,
                ProcessingPath = "native",
                SupportsUpscaling = false,
                MaxDurationSeconds = RenderableClipCeilingSeconds,
                MaxRetryAttempts = 8,
                MaxUpscalePasses = 3,
                BasePriceUsdPerSecondMin = 0.0001m,
                BasePriceUsdPerSecondMax = 0.0002m,
                Currency = "USD",
                PricingVersion = "685-boundary-v1",
                EffectiveAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Source = "test-fixture",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // Enable the spend decision so an unquotable clip cannot reserve or queue anything.
            builder.UseSetting("GenerationBudget:Enabled", "true");
            builder.UseSetting("GenerationBudget:RejectUnknownEstimates", "true");
            // A provider clip ceiling far below the whole-film number: 7,200 seconds of project
            // metadata is never permission to submit a 7,200-second clip.
            builder.ConfigureServices(services => services
                .PostConfigure<MovieGenerationCostEstimatorOptions>(settings =>
                    settings.MaxDurationSeconds = clipCeilingSeconds));
        }
    }

    // --------------------------------------------------------------- helpers

    private static Task SetProjectRuntimeAsync(GenerationJobsApiFactory factory, Guid movieProjectId, int durationSeconds) =>
        MutateAsync(factory, db =>
        {
            var movie = db.MovieProjects.Single(item => item.Id == movieProjectId);
            movie.DurationSeconds = durationSeconds;
        });

    private static Task SetShotDurationAsync(GenerationJobsApiFactory factory, Guid shotId, int durationSeconds) =>
        MutateAsync(factory, db =>
        {
            var shot = db.MovieShots.Single(item => item.Id == shotId);
            shot.DurationSeconds = durationSeconds;
        });

    private static Task MutateAsync(GenerationJobsApiFactory factory, Action<TaslimDbContext> body)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        body(db);
        db.SaveChanges();
        return Task.CompletedTask;
    }

    private async Task<int> ReadProjectRuntimeAsync(Guid movieProjectId)
    {
        using var scope = plainFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return await db.MovieProjects.AsNoTracking()
            .Where(item => item.Id == movieProjectId)
            .Select(item => item.DurationSeconds)
            .SingleAsync();
    }

    private static Task<long> CountAsync(GenerationJobsApiFactory factory, Func<TaslimDbContext, Task<long>> body)
    {
        using var scope = factory.Services.CreateScope();
        return body(scope.ServiceProvider.GetRequiredService<TaslimDbContext>());
    }

    private async Task<(int DurationSeconds, int InputDurationSeconds)> ReadQueuedClipAsync(Guid shotId)
    {
        using var scope = renderableCapFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var clip = await db.MovieClips.AsNoTracking()
            .Where(item => item.MovieShotId == shotId && item.GenerationJob != null)
            .OrderByDescending(item => item.CreatedAt)
            .Select(item => new { item.DurationSeconds, item.GenerationJob!.InputJson })
            .FirstAsync();
        var input = System.Text.Json.JsonSerializer.Deserialize<MovieGenerationInput>(clip.InputJson)!;
        return (clip.DurationSeconds ?? 0, input.DurationSeconds);
    }

    private async Task NoJobAndNoClipForIdempotencyKeyAsync(string idempotencyKey)
    {
        using var scope = guardrailFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.GenerationJobs.AsNoTracking().AnyAsync(item => item.IdempotencyKey == idempotencyKey));
        Assert.Equal(0, await db.MovieClips.AsNoTracking()
            .Where(item => item.GenerationJob != null && item.GenerationJob.IdempotencyKey == idempotencyKey)
            .LongCountAsync());
    }
}
