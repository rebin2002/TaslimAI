using System.Text.Json;
using Taslim.Api.Movies;
using Taslim.Api.Domain;
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
