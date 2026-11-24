using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Issue #685 — TEST-ONLY compatibility contract for the two different numbers the Movie
/// Studio long-form requirement mixes together:
///
///   * 7,200 seconds (120 minutes) is the <b>project/timeline plan</b> ceiling, and
///   * the per-generation ceiling is a completely separate, much smaller number that every
///     paid provider adapter enforces on its own clip.
///
/// Verified trace on <c>main</c> (read-only; nothing here edits a production file):
///
///   1. <see cref="MovieBudgetDirectorService.MaxProjectPlanningDurationSeconds"/> (120 * 60)
///      is the approved overall-plan runtime, and
///      <see cref="MovieBudgetDirectorService.MaxShotPlanningDurationSeconds"/> (60 * 60) is the
///      per-shot ceiling. <c>DeriveUnplannedProjectShotGrid</c> already refuses anything above
///      7,200 s with <c>project_duration_unsupported</c> and never models a two-hour brief as one
///      two-hour clip. That plan ceiling is <b>already merged</b> (#854).
///   2. The project <b>create request</b> is still capped by
///      <see cref="MovieStudioValidation.Validate"/> at the legacy one-hour value, so a
///      7,200-second project cannot be created yet. That seam is owned by open PR #269
///      (<c>MovieEntities.cs</c> / <c>MovieStudioService.cs</c>) and is deliberately NOT patched
///      here. Group B below is the red/green compatibility contract: it records the guard as
///      missing on today's baseline and starts enforcing #685's exact 7,200-second acceptance the
///      moment #269 lands, failing if that ceiling lands as anything other than 120 minutes.
///   3. Provider clip maxima are independent and far shorter than the plan: Runway accepts only
///      2-10 seconds and Manus only 1-30 seconds per generation; the provider-neutral
///      <see cref="DirectVideoRequestNormalizer"/> refuses any request outside its declared
///      capability range; the cost boundary
///      (<see cref="MovieGenerationCostEstimatorOptions.MaxDurationSeconds"/>) rejects a single
///      oversized generation with <c>duration_cap_exceeded</c> before it can be quoted; and no
///      provider definition in the capability catalog may even declare a clip above 3,600 seconds
///      while <c>ProviderCapabilityRegistry.Validate</c> refuses any request outside 1..3,600
///      seconds outright with <c>duration_invalid</c> before matching a definition at all.
///      Group C proves a 7,200-second single paid clip is blocked before any HTTP call, queue
///      entry, or spend, on today's baseline and after #269.
///
/// Scope and safety: this file is additive and provider-free. It performs no network I/O (every
/// adapter is driven by an in-memory stub handler that records requests so "zero calls" is
/// provable), no provider activation, no credentials, no purchases, no schema or data changes,
/// and no edits to any path owned by #269 or #348.
/// </summary>
public sealed class MovieProjectDurationCeilingCompatibilityTests
{
    /// <summary>#685: the approved overall project/timeline runtime (120 minutes).</summary>
    private const int ApprovedProjectCeilingSeconds = 7_200;

    /// <summary>The legacy create-request ceiling still on main while #269 is unmerged.</summary>
    private const int LegacyProjectCeilingSeconds = 3_600;

    /// <summary>Hard ceiling for ONE generation, used by the spend and capability seams.</summary>
    private const int PerGenerationCeilingSeconds = 3_600;

    // -----------------------------------------------------------------------------------------
    // Group A - the overall plan: 7,200 seconds is accepted, anything above it is refused.
    //           Provider-free, deterministic, green on main, and stays green after #269.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Overall_plan_ceiling_is_120_minutes_and_is_a_different_number_from_the_per_shot_ceiling()
    {
        // The whole point of #685: these two limits must never collapse into one constant.
        Assert.Equal(ApprovedProjectCeilingSeconds, MovieBudgetDirectorService.MaxProjectPlanningDurationSeconds);
        Assert.Equal(PerGenerationCeilingSeconds, MovieBudgetDirectorService.MaxShotPlanningDurationSeconds);
        Assert.True(
            MovieBudgetDirectorService.MaxProjectPlanningDurationSeconds > MovieBudgetDirectorService.MaxShotPlanningDurationSeconds,
            "The overall plan ceiling must stay strictly larger than the per-generation ceiling, otherwise a two-hour project would become a two-hour clip.");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(60)]
    [InlineData(3_600)]
    [InlineData(7_199)]
    [InlineData(7_200)]
    public void Project_plan_at_or_below_120_minutes_is_accepted_and_split_into_provider_compatible_shots(int projectDurationSeconds)
    {
        var (shotCount, durationSecondsPerShot) =
            MovieBudgetDirectorService.DeriveUnplannedProjectShotGrid(projectDurationSeconds);

        Assert.True(shotCount >= 1);
        Assert.InRange(durationSecondsPerShot, 1, PerGenerationCeilingSeconds);
        // Modelled footage is rounded up, never truncated.
        Assert.True(
            (long)shotCount * durationSecondsPerShot >= projectDurationSeconds,
            $"A {projectDurationSeconds}-second plan was modelled as {shotCount} x {durationSecondsPerShot}s, which is shorter than the approved runtime.");

        if (projectDurationSeconds > PerGenerationCeilingSeconds)
        {
            // The specific #685 hazard: a long-form plan must never be represented by one clip.
            Assert.True(shotCount > 1, $"A {projectDurationSeconds}-second plan was modelled as a single generation.");
            Assert.NotEqual(projectDurationSeconds, durationSecondsPerShot);
        }
    }

    [Theory]
    [InlineData(7_201)]
    [InlineData(8_640)]
    [InlineData(86_400)]
    [InlineData(int.MaxValue)]
    public void Project_plan_above_120_minutes_is_rejected_rather_than_silently_truncated(int projectDurationSeconds)
    {
        var exception = Assert.Throws<MovieBudgetDirectorValidationException>(() =>
            MovieBudgetDirectorService.DeriveUnplannedProjectShotGrid(projectDurationSeconds));

        Assert.Equal(MovieBudgetDirectorReasons.ProjectDurationUnsupported, exception.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Project_plan_with_no_runtime_is_rejected(int projectDurationSeconds)
    {
        Assert.Throws<MovieBudgetDirectorValidationException>(() =>
            MovieBudgetDirectorService.DeriveUnplannedProjectShotGrid(projectDurationSeconds));
    }

    // -----------------------------------------------------------------------------------------
    // Group B - the project create request (#685's missing guard, owned by PR #269).
    //           Compatibility contract: green on today's baseline while the guard is missing,
    //           and self-enforcing once the 120-minute ceiling lands.
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(7_201)]
    [InlineData(10_800)]
    [InlineData(86_400)]
    [InlineData(int.MaxValue)]
    public void Create_request_never_accepts_a_project_above_the_approved_120_minute_ceiling(int durationSeconds)
    {
        var error = MovieStudioValidation.Validate(CreateRequest(durationSeconds));
        Assert.NotNull(error);
        Assert.Contains("duration", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_request_ceiling_never_exceeds_the_planning_ceiling_at_any_merge_order()
    {
        var ceiling = EffectiveCreateRequestCeilingSeconds();

        // Whether #269 has landed or not, the create contract can never be looser than the
        // planner, so an accepted project is always a project the planner can budget.
        Assert.True(
            ceiling <= MovieBudgetDirectorService.MaxProjectPlanningDurationSeconds,
            $"The create validator accepts {ceiling}s while planning only supports {MovieBudgetDirectorService.MaxProjectPlanningDurationSeconds}s.");
    }

    [Fact]
    public void Create_request_boundary_is_monotonic_and_fails_closed_on_both_sides()
    {
        var ceiling = EffectiveCreateRequestCeilingSeconds();
        Assert.InRange(ceiling, 1, ApprovedProjectCeilingSeconds);

        foreach (var accepted in new[] { 1, 2, 30, 300, ceiling / 2, ceiling })
        {
            if (accepted < 1) continue;
            Assert.True(
                MovieStudioValidation.Validate(CreateRequest(accepted)) is null,
                $"Duration {accepted}s is below the effective ceiling {ceiling}s but was rejected.");
        }

        foreach (var rejected in new[] { ceiling + 1, ceiling + 600, ceiling * 2 })
        {
            Assert.True(
                MovieStudioValidation.Validate(CreateRequest(rejected)) is not null,
                $"Duration {rejected}s is above the effective ceiling {ceiling}s but was accepted.");
        }

        Assert.NotNull(MovieStudioValidation.Validate(CreateRequest(0)));
        Assert.NotNull(MovieStudioValidation.Validate(CreateRequest(-5)));
    }

    /// <summary>
    /// The red/green switch for #685.
    ///
    /// Today (main, #269 unmerged) the project create request still stops at one hour, so this
    /// test records that exact missing guard and pins the legacy value. Once PR #269 lifts the
    /// create cap to the approved 120 minutes, the same test flips to enforcing the acceptance
    /// criteria verbatim: 7,200 seconds is accepted and 7,201 seconds is refused. It also fails
    /// if the ceiling ever lands as an unapproved number, so the guard cannot be satisfied by an
    /// accidental value such as 3,601 or an unbounded 86,400.
    /// </summary>
    [Fact]
    public void Project_level_long_form_acceptance_matches_the_approved_ceiling_once_the_owner_cap_lands()
    {
        var ceiling = EffectiveCreateRequestCeilingSeconds();

        Assert.True(
            ceiling == LegacyProjectCeilingSeconds || ceiling == ApprovedProjectCeilingSeconds,
            $"Unexpected project duration ceiling {ceiling}s. Only the legacy one-hour cap (#{LegacyProjectCeilingSeconds / 60} min) or the approved #685 120-minute cap is sanctioned; widening the project cap needs its own review.");

        if (ceiling >= ApprovedProjectCeilingSeconds)
        {
            // #685 acceptance criteria, enforced from here on.
            Assert.Null(MovieStudioValidation.Validate(CreateRequest(ApprovedProjectCeilingSeconds)));
            var overCap = MovieStudioValidation.Validate(CreateRequest(ApprovedProjectCeilingSeconds + 1));
            Assert.NotNull(overCap);
            Assert.Contains("120", overCap, StringComparison.Ordinal);
        }
        else
        {
            // Missing guard, stated rather than hidden: the project metadata limit from #685 is
            // not implemented on this baseline, so a compliant two-hour brief is still refused.
            var error = MovieStudioValidation.Validate(CreateRequest(ApprovedProjectCeilingSeconds));
            Assert.NotNull(error);
            Assert.True(
                !error.Contains("120", StringComparison.Ordinal),
                "The validator advertises a 120-minute project ceiling but does not accept 7,200 seconds.");
        }
    }

    [Fact]
    public void Long_form_project_ceiling_and_plan_ceiling_use_the_same_approved_number()
    {
        // Cross-seam consistency: the create validator may lag the planner, but the planner must
        // already cover whatever the create validator accepts, and both must land on 7,200.
        var ceiling = EffectiveCreateRequestCeilingSeconds();
        Assert.True(ceiling <= MovieBudgetDirectorService.MaxProjectPlanningDurationSeconds);
        Assert.Equal(ApprovedProjectCeilingSeconds, MovieBudgetDirectorService.MaxProjectPlanningDurationSeconds);

        // Everything the create validator accepts is still plannable, including the boundary.
        _ = MovieBudgetDirectorService.DeriveUnplannedProjectShotGrid(ceiling);
    }

    // -----------------------------------------------------------------------------------------
    // Group C - provider clip maxima stay independent: one 7,200-second PAID clip is blocked
    //           before any HTTP call, queue entry, or spend, on both baselines.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Runway_refuses_a_two_hour_single_clip_without_calling_the_provider()
    {
        var handler = new RecordingHandler(_ => ThrowIfCalled());
        var provider = CreateRunway(handler);

        Assert.True(provider.IsAvailable);
        var exception = await Assert.ThrowsAsync<MovieVideoProviderException>(
            () => provider.SubmitAsync(ProviderRequest(ApprovedProjectCeilingSeconds), CancellationToken.None));

        Assert.Equal(GenerationJobErrorCodes.MovieProviderUnsupportedRequest, exception.Code);
        Assert.False(exception.IsTransient);
        // Blocked before submission: no provider request means no job and no spend.
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Manus_refuses_a_two_hour_single_clip_without_calling_the_provider()
    {
        var handler = new RecordingHandler(_ => ThrowIfCalled());
        var provider = CreateManus(handler);

        Assert.True(provider.IsAvailable);
        var exception = await Assert.ThrowsAsync<MovieVideoProviderException>(
            () => provider.SubmitAsync(ProviderRequest(ApprovedProjectCeilingSeconds), CancellationToken.None));

        Assert.Equal(GenerationJobErrorCodes.MovieProviderUnsupportedRequest, exception.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Each_adapter_still_accepts_a_short_clip_so_the_two_hour_refusal_is_duration_specific()
    {
        // Positive controls: the refusals above come from the clip ceiling, not from a provider
        // that is simply unavailable or a payload that always fails.
        var runwayHandler = new RecordingHandler(_ => JsonResponse("{\"id\":\"8d2d2e6e-88c7-4c47-a6ab-7c6ce6e1a6a4\",\"estimatedCost\":{\"credits\":60}}"));
        var submission = await CreateRunway(runwayHandler).SubmitAsync(ProviderRequest(10), CancellationToken.None);
        Assert.Equal("8d2d2e6e-88c7-4c47-a6ab-7c6ce6e1a6a4", submission.ProviderJobId);
        Assert.Single(runwayHandler.Requests);

        var manusHandler = new RecordingHandler(_ => JsonResponse("{\"ok\":true,\"task_id\":\"4c9a0f6e-4c2f-4f0a-9c8c-2f1b6a7d3e51\"}"));
        await CreateManus(manusHandler).SubmitAsync(ProviderRequest(30), CancellationToken.None);
        Assert.Single(manusHandler.Requests);

        // One second past each adapter's real per-clip maximum is still refused.
        await Assert.ThrowsAsync<MovieVideoProviderException>(
            () => CreateRunway(new RecordingHandler(_ => ThrowIfCalled())).SubmitAsync(ProviderRequest(11), CancellationToken.None));
        await Assert.ThrowsAsync<MovieVideoProviderException>(
            () => CreateManus(new RecordingHandler(_ => ThrowIfCalled())).SubmitAsync(ProviderRequest(31), CancellationToken.None));
    }

    [Fact]
    public void Normalizer_blocks_a_two_hour_request_at_the_declared_clip_ceiling()
    {
        // 600s is the kind of per-clip ceiling a long-clip adapter declares; the project plan is
        // twelve times larger and must never be forwarded as one generation.
        var declaration = new DirectVideoCapabilityDeclaration(
            new HashSet<string>(StringComparer.Ordinal) { MovieStudioOperations.SceneClip },
            new HashSet<string>(StringComparer.Ordinal) { DirectVideoResolutions.Auto },
            new HashSet<string>(StringComparer.Ordinal) { "16:9" },
            MinDurationSeconds: 2,
            MaxDurationSeconds: 600,
            SupportsReferenceImage: false,
            SupportsContinuation: false,
            SupportsUpscaling: false,
            SupportsNativeAudio: false);

        var normalized = DirectVideoRequestNormalizer.Normalize(ProviderRequest(600), declaration);
        Assert.Equal(600, normalized.DurationSeconds);

        var exception = Assert.Throws<DirectVideoRequestNormalizationException>(() =>
            DirectVideoRequestNormalizer.Normalize(ProviderRequest(ApprovedProjectCeilingSeconds), declaration));
        Assert.Equal(DirectVideoErrorCodes.CapabilityUnsupported, exception.Code);
    }

    [Fact]
    public void Capability_catalog_reports_a_two_hour_clip_as_duration_unsupported()
    {
        var registry = new ProviderCapabilityRegistry(Options.Create(LongClipCatalog(maximumDeclaredClipSeconds: 600)));

        Assert.True(registry.Validate(new ProviderCapabilityRequest(Capability, DurationSeconds: 600)).IsSupported);

        // Layer 1 - the absolute per-generation ceiling. The registry rejects any request outside
        // 1..3,600 seconds before it even matches a definition, so a 7,200-second single clip can
        // never resolve to a provider regardless of what a catalog declares.
        var oversize = registry.Validate(new ProviderCapabilityRequest(Capability, DurationSeconds: ApprovedProjectCeilingSeconds));
        Assert.False(oversize.IsSupported);
        Assert.Contains("duration_invalid", oversize.FailureCodes, StringComparer.OrdinalIgnoreCase);
        Assert.Empty(oversize.Matches);

        // Layer 2 - the per-definition ceiling. Inside the absolute bound, a clip longer than the
        // adapter actually supports is still refused rather than quietly downgraded.
        var aboveDeclared = registry.Validate(new ProviderCapabilityRequest(Capability, DurationSeconds: 1_800));
        Assert.False(aboveDeclared.IsSupported);
        Assert.Contains("duration_unsupported", aboveDeclared.FailureCodes, StringComparer.OrdinalIgnoreCase);
        Assert.Empty(aboveDeclared.Matches);
    }

    [Fact]
    public void Capability_catalog_cannot_even_declare_a_two_hour_clip()
    {
        // Structural guard behind #685's requirement: lifting the PROJECT ceiling cannot smuggle
        // a 7,200-second CLIP into existence, because no provider definition may declare one.
        var oversizeCatalog = LongClipCatalog(maximumDeclaredClipSeconds: ApprovedProjectCeilingSeconds);
        var failures = ProviderCapabilityRegistryOptionsValidator.ValidateOptions(oversizeCatalog);
        Assert.Contains(failures, failure => failure.Contains("invalid duration constraints", StringComparison.OrdinalIgnoreCase));

        var legalCatalog = LongClipCatalog(maximumDeclaredClipSeconds: PerGenerationCeilingSeconds);
        Assert.DoesNotContain(
            ProviderCapabilityRegistryOptionsValidator.ValidateOptions(legalCatalog),
            failure => failure.Contains("invalid duration constraints", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Cost_boundary_refuses_to_quote_a_two_hour_single_generation_before_any_spend()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new TaslimDbContext(
            new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        var estimator = new MovieGenerationCostEstimator(db, Options.Create(new MovieGenerationCostEstimatorOptions()));

        var oversize = await estimator.EstimateAsync(
            CostRequest(ApprovedProjectCeilingSeconds), null, null, CancellationToken.None);
        Assert.Equal(MovieGenerationCostEstimateStates.Unknown, oversize.State);
        Assert.Equal("duration_cap_exceeded", oversize.Reason);
        Assert.Null(oversize.MinimumAmountUsd);
        Assert.Null(oversize.MaximumAmountUsd);
        Assert.False(oversize.IsEstimated);

        // Control: an in-ceiling generation is not rejected by the cap, so the reason above is
        // the duration ceiling itself and not an artefact of this empty pricing table.
        var inCeiling = await estimator.EstimateAsync(
            CostRequest(PerGenerationCeilingSeconds), null, null, CancellationToken.None);
        Assert.NotEqual("duration_cap_exceeded", inCeiling.Reason);
    }

    // -----------------------------------------------------------------------------------------
    // Helpers (self-contained; no shared fixture and no production file is touched).
    // -----------------------------------------------------------------------------------------

    private const string Capability = ProviderCapabilityKinds.VideoGeneration;

    /// <summary>
    /// Behavioural probe for the project create-request ceiling. It deliberately does not
    /// reference a named constant, because the constant that #269 introduces
    /// (<c>MovieStudioValidation.MaxProjectDurationSeconds</c>) does not exist on this baseline;
    /// probing keeps this file compiling and meaningful on both.
    /// </summary>
    private static int EffectiveCreateRequestCeilingSeconds()
    {
        if (MovieStudioValidation.Validate(CreateRequest(ApprovedProjectCeilingSeconds)) is null)
        {
            return ApprovedProjectCeilingSeconds;
        }

        // The guard is missing: find the legacy ceiling instead of assuming it.
        Assert.NotNull(MovieStudioValidation.Validate(CreateRequest(LegacyProjectCeilingSeconds + 1)));
        Assert.Null(MovieStudioValidation.Validate(CreateRequest(LegacyProjectCeilingSeconds)));
        return LegacyProjectCeilingSeconds;
    }

    private static MovieStudioCreateRequest CreateRequest(int durationSeconds) => new()
    {
        WorkspaceId = Guid.NewGuid(),
        Mode = MovieProjectModes.Full,
        Title = "Two Hour Feature",
        Description = "A 120-minute project used to probe the approved #685 duration ceilings.",
        DurationSeconds = durationSeconds,
        AspectRatio = "16:9",
        Language = LanguageCodes.English,
    };

    private static MovieVideoGenerationRequest ProviderRequest(int durationSeconds) => new(
        MovieStudioOperations.SceneClip,
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        "A camera moves slowly through a sunlit forest.",
        durationSeconds,
        "16:9",
        "cinematic",
        LanguageCodes.English,
        "No text overlays.",
        "Visual language: natural light",
        "Scene: forest",
        "Shot: slow dolly",
        null,
        null);

    private static MovieGenerationCostRequest CostRequest(int durationSeconds) => new(
        durationSeconds,
        "1280x720",
        "1920x1080",
        "high",
        "native");

    private static RunwayMovieVideoProvider CreateRunway(RecordingHandler handler) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://api.dev.runwayml.com/v1/") },
        Options.Create(new MovieVideoOptions
        {
            Enabled = true,
            ProviderKey = "runway",
            ApiKey = "synthetic-token",
            ApiBaseUrl = "https://api.dev.runwayml.com/v1/",
            Model = "gen4.5",
            ProviderTimeoutSeconds = 15,
            MaxOutputBytes = 1024,
            MaxTransientRetries = 0,
        }),
        NullLogger<RunwayMovieVideoProvider>.Instance,
        new AllowAllProviderUrlPolicy());

    private static ManusMovieVideoProvider CreateManus(RecordingHandler handler) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://api.manus.ai/") },
        Options.Create(new MovieVideoOptions
        {
            Enabled = true,
            ProviderKey = "manus",
            ProviderTimeoutSeconds = 15,
            MaxOutputBytes = 1024,
            MaxTransientRetries = 0,
            Manus = new ManusMovieVideoOptions
            {
                Enabled = true,
                ApiKey = "synthetic-token",
                ApiBaseUrl = "https://api.manus.ai/",
                AgentProfile = "standard",
                CreditUsdPerCredit = 0.025m,
                MaxPromptCharacters = 12_000,
            },
        }),
        NullLogger<ManusMovieVideoProvider>.Instance);

    private static ProviderCapabilityRegistryOptions LongClipCatalog(int maximumDeclaredClipSeconds) => new()
    {
        SchemaVersion = 1,
        CatalogVersion = "duration-ceiling-compatibility-v1",
        EffectiveAtUtc = DateTime.UtcNow,
        Source = "test-only",
        Definitions =
        {
            new ProviderCapabilityDefinitionOptions
            {
                ProviderKey = "compatibility-fixture",
                ModelKey = "long-clip",
                DefinitionVersion = "1",
                Capability = Capability,
                IsAvailable = true,
                SupportedResolutions = { new ProviderResolutionOptions { Width = 1_280, Height = 720 } },
                SupportedAspectRatios = { "16:9" },
                SupportedDurationsSeconds = new List<int> { 2, 5, 10, 30, 600, 3_600 }
                    .Where(duration => duration <= maximumDeclaredClipSeconds)
                    .ToList(),
                Features = { "native_audio" },
                Constraints = new ProviderCapabilityConstraintsOptions
                {
                    MinimumDurationSeconds = 2,
                    MaximumDurationSeconds = maximumDeclaredClipSeconds,
                },
                Provenance = new ProviderCapabilityProvenanceOptions
                {
                    Source = "test-only fixture",
                    CapturedAtUtc = "2026-01-01T00:00:00Z",
                },
            },
        },
    };

    private static HttpResponseMessage ThrowIfCalled() =>
        throw new InvalidOperationException("A paid provider must never be called for an oversized single clip.");

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class AllowAllProviderUrlPolicy : IProviderUrlPolicy
    {
        public Task EnsureSafeAsync(Uri uri, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
