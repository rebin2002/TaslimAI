using System.Text.Json;
using Taslim.Api.Movies;
using Taslim.Api.Movies.DialogueTiming;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieDialogueTimingContractsTests
{
    [Fact]
    public void Eligible_plan_is_deterministic_and_contains_synchronization_metadata()
    {
        var request = Request();

        var first = MovieLipSyncPlanner.Plan(request);
        var second = MovieLipSyncPlanner.Plan(request);

        Assert.Equal(MovieDialogueTimingContract.Version, first.ContractVersion);
        Assert.Equal(MovieLipSyncEligibilityStates.Eligible, first.EligibilityState);
        Assert.Equal(MovieLipSyncReviewStates.NotRequired, first.ReviewState);
        Assert.Equal(MovieLipSyncReasonCodes.Eligible, first.DecisionCode);
        Assert.Equal(2, first.Synchronization.CueCount);
        Assert.Equal(0, first.Synchronization.DurationDeltaMilliseconds);
        Assert.Equal(0, first.Synchronization.MaximumAbsoluteStartDriftMilliseconds);
        Assert.All(first.Cues, cue => Assert.Equal(MovieDialogueAlignmentStates.Aligned, cue.AlignmentState));
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
    }

    [Fact]
    public void A_stale_visual_selection_pointer_is_blocked_even_when_take_status_looks_selected()
    {
        var request = Request() with
        {
            VisualTake = Request().VisualTake with { ShotSelectedTakeId = Guid.NewGuid() },
        };

        var plan = MovieLipSyncPlanner.Plan(request);

        Assert.Equal(MovieLipSyncEligibilityStates.Blocked, plan.EligibilityState);
        Assert.Equal(MovieLipSyncReviewStates.Blocked, plan.ReviewState);
        Assert.Equal(MovieLipSyncReasonCodes.VisualTakeNotSelected, plan.DecisionCode);
    }

    [Fact]
    public void Unapproved_or_unpublished_voice_assets_are_blocked_without_execution_metadata()
    {
        var unapproved = MovieLipSyncPlanner.Plan(Request() with
        {
            VoiceAsset = Request().VoiceAsset with { ApprovalState = "Pending" },
        });
        var unpublished = MovieLipSyncPlanner.Plan(Request() with
        {
            VoiceAsset = Request().VoiceAsset with { HasPublishedAudio = false },
        });

        Assert.Equal(MovieLipSyncReasonCodes.VoiceAssetNotApproved, unapproved.DecisionCode);
        Assert.Equal(MovieLipSyncReasonCodes.VoiceMediaUnavailable, unpublished.DecisionCode);
        Assert.Empty(unapproved.Escalations);
        Assert.DoesNotContain("provider", MovieLipSyncPlanner.SerializeProductSafe(unpublished), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Timing_drift_produces_review_state_and_cue_level_escalation()
    {
        var request = Request() with
        {
            Cues =
            [
                new(Guid.NewGuid(), 1, "We have one hour.", 100, 600, 240, 760, "Mara"),
                new(Guid.NewGuid(), 2, "Move.", 700, 900, 760, 900, "Mara"),
            ],
        };

        var plan = MovieLipSyncPlanner.Plan(request);

        Assert.Equal(MovieLipSyncEligibilityStates.NeedsReview, plan.EligibilityState);
        Assert.Equal(MovieLipSyncReviewStates.Pending, plan.ReviewState);
        Assert.Equal(MovieLipSyncReasonCodes.TimingReviewRequired, plan.DecisionCode);
        var escalation = Assert.Single(plan.Escalations, item => item.Type == MovieLipSyncEscalationTypes.Timing);
        Assert.Single(escalation.CueIds);
        Assert.Equal(MovieDialogueAlignmentStates.NeedsReview, plan.Cues[0].AlignmentState);
        Assert.Equal(140, plan.Cues[0].StartDriftMilliseconds);
        Assert.Equal(160, plan.Cues[0].EndDriftMilliseconds);
    }

    [Fact]
    public void Duration_and_language_differences_are_explicit_review_escalations()
    {
        var request = Request() with
        {
            VisualLanguage = "ar",
            VoiceAsset = Request().VoiceAsset with { Language = "en", DurationMilliseconds = 1_500 },
        };

        var plan = MovieLipSyncPlanner.Plan(request);

        Assert.Equal(MovieLipSyncEligibilityStates.NeedsReview, plan.EligibilityState);
        Assert.Contains(plan.Escalations, item => item.Code == MovieLipSyncReasonCodes.DurationReviewRequired);
        Assert.Contains(plan.Escalations, item => item.Code == MovieLipSyncReasonCodes.LanguageReviewRequired);
        Assert.Equal(-500, plan.Synchronization.DurationDeltaMilliseconds);
    }

    [Fact]
    public void Invalid_windows_and_duplicate_cues_return_stable_validation_errors()
    {
        var cueId = Guid.NewGuid();
        var errors = MovieDialogueTimingRequestValidator.Validate(Request() with
        {
            Cues =
            [
                new(cueId, 1, "First", 0, 600, 0, 600),
                new(cueId, 1, "Overlap", 500, 700, 500, 700),
            ],
        });

        Assert.Contains(errors, error => error.Code == "duplicate" && error.Field.Contains("CueId", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Code == "duplicate" && error.Field.Contains("Sequence", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Code == "overlap" && error.Field.Contains("VoiceStartMilliseconds", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Code == "overlap" && error.Field.Contains("VisualStartMilliseconds", StringComparison.Ordinal));
    }

    [Fact]
    public void Invalid_request_throws_contract_exception_before_planning()
    {
        var request = Request() with
        {
            MaximumStartDriftMilliseconds = -1,
            VoiceAsset = Request().VoiceAsset with { AssetType = "video" },
        };

        var exception = Assert.Throws<MovieDialogueTimingValidationException>(() => MovieLipSyncPlanner.Plan(request));

        Assert.Contains(exception.Errors, error => error.Field == nameof(MovieDialogueTimingPlanningRequest.MaximumStartDriftMilliseconds));
        Assert.DoesNotContain(exception.Errors, error => error.Field.Contains("provider", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Product_safe_plan_does_not_expose_provider_model_or_prompt_fields()
    {
        var json = MovieLipSyncPlanner.SerializeProductSafe(MovieLipSyncPlanner.Plan(Request()));

        Assert.Contains("eligibilityState", json, StringComparison.Ordinal);
        Assert.Contains("synchronization", json, StringComparison.Ordinal);
        Assert.DoesNotContain("provider", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
    }

    private static MovieDialogueTimingPlanningRequest Request()
    {
        var shotId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var takeId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var visualAssetId = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var voiceAssetId = Guid.Parse("00000000-0000-0000-0000-000000000004");
        return new MovieDialogueTimingPlanningRequest(
            new MovieSelectedVisualTakeReference(shotId, takeId, takeId, MovieTakeStatuses.Selected, visualAssetId, "video", true, 1_000, true),
            new MovieApprovedVoiceAssetReference(voiceAssetId, "Approved", "audio", true, 1_000, "en"),
            [
                new(Guid.Parse("00000000-0000-0000-0000-000000000011"), 1, "We have one hour.", 100, 600, 100, 600, "Mara"),
                new(Guid.Parse("00000000-0000-0000-0000-000000000012"), 2, "Move.", 700, 900, 700, 900, "Mara"),
            ],
            "en");
    }
}
