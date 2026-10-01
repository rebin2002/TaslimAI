using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieSalvageDirectorTests
{
    private readonly MovieSalvageDirector director = new();

    [Fact]
    public void Duration_issue_prefers_trim_then_local_insert_then_audio_bridge_before_regeneration()
    {
        var plan = director.Plan(new MovieSalvageDirectorRequest
        {
            IssueType = MovieSalvageIssueTypes.Duration,
            Context = new MovieSalvageEditContext
            {
                HasUsableRange = true,
                HasCleanCutPoint = true,
                HasContinuitySafeInsert = true,
                HasSoundBridge = true,
                HasUsableAudio = true,
            },
        });

        Assert.Equal(MovieSalvageDirectorContract.Version, plan.ContractVersion);
        Assert.Equal(
            new[] { MovieSalvageActionTypes.Trim, MovieSalvageActionTypes.Insert, MovieSalvageActionTypes.SoundBridge, MovieSalvageActionTypes.Regenerate },
            plan.Options.Select(item => item.Action));
        Assert.True(plan.FullRegenerationDeferred);
        Assert.All(plan.Options.Take(3), option => Assert.True(option.UsesExistingFootage || option.RequiresGeneration));
    }

    [Fact]
    public void Continuity_issue_prefers_an_existing_continuity_safe_alternate()
    {
        var plan = director.Plan(new MovieSalvageDirectorRequest
        {
            IssueType = MovieSalvageIssueTypes.Continuity,
            Context = new MovieSalvageEditContext
            {
                HasContinuitySafeAlternate = true,
                HasContinuitySafeReaction = true,
                HasContinuitySafeInsert = true,
                HasContinuitySafeCoverAngle = true,
                ReferencesLocked = true,
            },
        });

        Assert.Equal(MovieSalvageActionTypes.AlternateSelect, plan.Options[0].Action);
        Assert.True(plan.Options[0].UsesExistingFootage);
        Assert.False(plan.Options[0].RequiresGeneration);
        Assert.Equal(MovieSalvageActionTypes.Regenerate, plan.Options[^1].Action);
    }

    [Fact]
    public void Composition_issue_uses_crop_only_when_safe_crop_evidence_exists()
    {
        var withCrop = director.Plan(new MovieSalvageDirectorRequest
        {
            IssueType = MovieSalvageIssueTypes.Composition,
            Context = new MovieSalvageEditContext { HasSafeCrop = true },
        });
        var withoutCrop = director.Plan(new MovieSalvageDirectorRequest
        {
            IssueType = MovieSalvageIssueTypes.Composition,
            Context = new MovieSalvageEditContext { HasSafeCrop = false },
        });

        Assert.Equal(MovieSalvageActionTypes.Crop, withCrop.Options[0].Action);
        Assert.DoesNotContain(withoutCrop.Options, item => item.Action == MovieSalvageActionTypes.Crop);
        Assert.Equal(MovieSalvageActionTypes.Regenerate, withoutCrop.Options.Single().Action);
    }

    [Fact]
    public void Qc_findings_infer_a_continuity_issue_without_exposing_internal_execution_details()
    {
        var plan = director.Plan(new MovieProductionQcDecision(
            MovieProductionQualityControlService.ContractVersion,
            MovieProductionQcActions.RequireReview,
            true,
            [new MovieProductionQcFinding(MovieProductionQcReasonCodes.HardContinuityConflict, "continuity", "error", "0", "1")],
            new MovieResolution(1920, 1080),
            new MovieResolution(1920, 1080),
            null,
            null), new MovieSalvageEditContext { HasContinuitySafeAlternate = true });

        Assert.Equal(MovieSalvageIssueTypes.Continuity, plan.IssueType);
        Assert.Contains(plan.Options, item => item.Action == MovieSalvageActionTypes.AlternateSelect);
        var json = System.Text.Json.JsonSerializer.Serialize(plan);
        Assert.DoesNotContain("provider", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Generated_repairs_require_a_locked_reference_package_when_references_are_not_locked()
    {
        var plan = director.Plan(new MovieSalvageDirectorRequest
        {
            IssueType = MovieSalvageIssueTypes.Generic,
            Context = new MovieSalvageEditContext
            {
                HasContinuitySafeInsert = true,
                ReferencesLocked = false,
            },
        });

        var insert = Assert.Single(plan.Options, item => item.Action == MovieSalvageActionTypes.Insert);
        Assert.True(plan.ReferenceLockRequired);
        Assert.True(insert.RequiresGeneration);
        Assert.True(insert.RequiresReferenceLock);
        Assert.Contains(insert.Preconditions, item => item.Contains("Locked reference package", StringComparison.Ordinal));
    }

    [Fact]
    public void Output_is_bounded_and_stably_ordered_even_when_many_repairs_are_available()
    {
        var request = new MovieSalvageDirectorRequest
        {
            IssueType = MovieSalvageIssueTypes.Selection,
            MaxOptions = 3,
            Context = new MovieSalvageEditContext
            {
                HasUsableRange = true,
                HasSafeCrop = true,
                HasContinuitySafeAlternate = true,
                HasContinuitySafeReaction = true,
                HasContinuitySafeInsert = true,
                HasContinuitySafeCoverAngle = true,
                HasSoundBridge = true,
                HasAdjacentTransition = true,
            },
        };

        var first = director.Plan(request);
        var second = director.Plan(request);

        Assert.Equal(3, first.Options.Count);
        Assert.Equal(MovieSalvageActionTypes.Regenerate, first.Options[^1].Action);
        Assert.Equal(new[] { 1, 2, 3 }, first.Options.Select(item => item.Order));
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(first),
            System.Text.Json.JsonSerializer.Serialize(second));
    }

    [Fact]
    public void No_evidence_returns_only_the_review_only_regeneration_fallback()
    {
        var plan = director.Plan(new MovieSalvageDirectorRequest
        {
            IssueType = MovieSalvageIssueTypes.Generic,
            Context = new MovieSalvageEditContext(),
        });

        var option = Assert.Single(plan.Options);
        Assert.Equal(MovieSalvageActionTypes.Regenerate, option.Action);
        Assert.True(option.RequiresUserApproval);
        Assert.True(option.RequiresGeneration);
        Assert.Contains("approval", string.Join(" ", option.Preconditions), StringComparison.OrdinalIgnoreCase);
    }
}
