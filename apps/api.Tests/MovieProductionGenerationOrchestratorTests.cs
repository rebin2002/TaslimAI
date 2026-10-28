using System.Text.Json;
using Taslim.Api.Movies;
using Taslim.Api.Domain;
using Taslim.Api.Tests.ProviderTesting;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieProductionGenerationOrchestratorTests
{
    [Fact]
    public void State_machine_allows_only_the_canonical_happy_path()
    {
        var path = new[]
        {
            MovieProductionOrchestrationState.Requested,
            MovieProductionOrchestrationState.Validated,
            MovieProductionOrchestrationState.Queued,
            MovieProductionOrchestrationState.Running,
            MovieProductionOrchestrationState.QualityControlPending,
            MovieProductionOrchestrationState.Succeeded,
        };

        for (var index = 1; index < path.Length; index++)
            Assert.True(MovieProductionOrchestrationStateMachine.CanTransition(path[index - 1], path[index]));

        Assert.False(MovieProductionOrchestrationStateMachine.CanTransition(
            MovieProductionOrchestrationState.Succeeded,
            MovieProductionOrchestrationState.Queued));
        Assert.False(MovieProductionOrchestrationStateMachine.CanTransition(
            MovieProductionOrchestrationState.Failed,
            MovieProductionOrchestrationState.Running));
    }

    [Fact]
    public void Cancellation_is_terminal_and_does_not_reopen_execution()
    {
        Assert.True(MovieProductionOrchestrationStateMachine.CanTransition(
            MovieProductionOrchestrationState.Running,
            MovieProductionOrchestrationState.CancellationRequested));
        Assert.True(MovieProductionOrchestrationStateMachine.CanTransition(
            MovieProductionOrchestrationState.CancellationRequested,
            MovieProductionOrchestrationState.Cancelled));
        Assert.False(MovieProductionOrchestrationStateMachine.CanTransition(
            MovieProductionOrchestrationState.Cancelled,
            MovieProductionOrchestrationState.Running));
    }

    [Theory]
    [InlineData(GenerationJobStatus.Pending, MovieProductionOrchestrationState.Requested)]
    [InlineData(GenerationJobStatus.Queued, MovieProductionOrchestrationState.Queued)]
    [InlineData(GenerationJobStatus.Running, MovieProductionOrchestrationState.Running)]
    [InlineData(GenerationJobStatus.Succeeded, MovieProductionOrchestrationState.Succeeded)]
    [InlineData(GenerationJobStatus.Failed, MovieProductionOrchestrationState.Failed)]
    [InlineData(GenerationJobStatus.Cancelled, MovieProductionOrchestrationState.Cancelled)]
    public void State_is_derived_from_the_canonical_generation_job_status(
        GenerationJobStatus status,
        MovieProductionOrchestrationState expected)
    {
        Assert.Equal(expected, MovieProductionOrchestrationStateMachine.From(status));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    public void Approved_production_admission_accepts_only_declared_single_clip_boundaries(int durationSeconds)
    {
        var calls = new FakeProviderCallLog();
        var provider = new FakeMovieVideoProvider(new FakeProviderScenarioCatalog(), calls)
        {
            SceneClipDurationRange = new MovieClipDurationRange(2, 10),
        };

        MovieProductionGenerationOrchestrator.ValidateProductionClipAdmission(
            provider, durationSeconds, "720p", "720p", DirectorQualityLevels.Standard,
            "native", "16:9");
        Assert.Equal(0, calls.Calls(FakeProviderKind.Movie));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(11)]
    [InlineData(60)]
    public void Approved_production_admission_rejects_out_of_range_clip_without_provider_submission(int durationSeconds)
    {
        var calls = new FakeProviderCallLog();
        var provider = new FakeMovieVideoProvider(new FakeProviderScenarioCatalog(), calls)
        {
            SceneClipDurationRange = new MovieClipDurationRange(2, 10),
        };

        var error = Assert.Throws<MovieProductionValidationException>(() =>
            MovieProductionGenerationOrchestrator.ValidateProductionClipAdmission(
                provider, durationSeconds, "720p", "720p", DirectorQualityLevels.Standard,
                "native", "16:9"));

        Assert.Equal("MOVIE_CLIP_DURATION_UNSUPPORTED", error.Code);
        Assert.Equal(0, calls.Calls(FakeProviderKind.Movie));
    }

    [Fact]
    public void Approved_production_admission_rejects_unavailable_provider()
    {
        var scenarios = new FakeProviderScenarioCatalog();
        scenarios[FakeProviderKind.Movie] = FakeProviderScenario.AllProvidersUnavailable;
        var calls = new FakeProviderCallLog();
        var provider = new FakeMovieVideoProvider(scenarios, calls);

        var error = Assert.Throws<MovieProductionValidationException>(() =>
            MovieProductionGenerationOrchestrator.ValidateProductionClipAdmission(
                provider, 5, "720p", "720p", DirectorQualityLevels.Standard, "native", "16:9"));

        Assert.Equal("MOVIE_PROVIDER_UNAVAILABLE", error.Code);
        Assert.Equal(0, calls.Calls(FakeProviderKind.Movie));
    }

    [Fact]
    public void Approved_production_admission_rejects_unpriced_or_unsupported_execution_intent()
    {
        var calls = new FakeProviderCallLog();
        var provider = new FakeMovieVideoProvider(new FakeProviderScenarioCatalog(), calls)
        {
            SceneClipDurationRange = new MovieClipDurationRange(2, 10),
            SceneClipCostIntentSupported = false,
        };

        var error = Assert.Throws<MovieProductionValidationException>(() =>
            MovieProductionGenerationOrchestrator.ValidateProductionClipAdmission(
                provider, 5, "1080p", "1080p", DirectorQualityLevels.Cinematic,
                "upscale", "16:9"));

        Assert.Equal("MOVIE_CLIP_EXECUTION_INTENT_UNSUPPORTED", error.Code);
        Assert.Equal(0, calls.Calls(FakeProviderKind.Movie));
    }

    [Fact]
    public void Approved_production_admission_rejects_adapters_with_no_declared_duration_contract()
    {
        var calls = new FakeProviderCallLog();
        var provider = new FakeMovieVideoProvider(new FakeProviderScenarioCatalog(), calls)
        {
            SceneClipDurationRange = null,
        };

        var error = Assert.Throws<MovieProductionValidationException>(() =>
            MovieProductionGenerationOrchestrator.ValidateProductionClipAdmission(
                provider, 5, "720p", "720p", DirectorQualityLevels.Standard,
                "native", "16:9"));

        Assert.Equal("MOVIE_CLIP_DURATION_UNSUPPORTED", error.Code);
        Assert.Equal(0, calls.Calls(FakeProviderKind.Movie));
    }

    [Fact]
    public void Production_request_has_no_provider_or_model_fields()
    {
        var request = new MovieProductionGenerationRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Take A",
            "Shot render",
            MovieResolutionTiers.P1080,
            DirectorQualityLevels.Cinematic,
            false,
            "production-001");

        var json = JsonSerializer.Serialize(request);
        Assert.DoesNotContain("provider", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Canonical_job_ownership_is_bound_to_the_persisted_clip_target()
    {
        var canonicalClipId = Guid.NewGuid();
        var job = new GenerationJob
        {
            InputJson = JsonSerializer.Serialize(new { MovieClipId = canonicalClipId }),
        };

        Assert.True(MovieProductionGenerationOrchestrator.JobTargetsClip(job, canonicalClipId));
        Assert.False(MovieProductionGenerationOrchestrator.JobTargetsClip(job, Guid.NewGuid()));
        Assert.False(MovieProductionGenerationOrchestrator.JobTargetsClip(new GenerationJob { InputJson = "not-json" }, canonicalClipId));
    }
}
