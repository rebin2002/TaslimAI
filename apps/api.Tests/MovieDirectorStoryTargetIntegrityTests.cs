using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieDirectorStoryTargetIntegrityTests
{
    [Fact]
    public void Improve_dialogue_uses_the_selected_dialogue_element_in_review_and_replacement()
    {
        var sceneId = Guid.NewGuid();
        var firstDialogueId = Guid.NewGuid();
        var selectedDialogueId = Guid.NewGuid();
        var scene = new DirectorScreenplaySceneContext(
            sceneId,
            Guid.NewGuid(),
            "SCENE-1",
            1,
            1,
            "INT. WORKSHOP - DAY",
            "Two lines reveal different stakes.",
            [
                new(Guid.NewGuid(), MovieScreenplayElementTypes.Action, "The workshop falls silent.", null, null),
                new(firstDialogueId, MovieScreenplayElementTypes.Dialogue, "First line must remain separate.", "MARA", null),
                new(selectedDialogueId, MovieScreenplayElementTypes.Dialogue, "Selected line carries the turn.", "MARA", null),
            ]);

        var proposal = new DirectorStoryProposalPlanner().Build(
            new DirectorProposalRequest
            {
                StoryAction = DirectorStoryActionTypes.ImproveDialogue,
                TargetSceneId = sceneId,
                TargetElementId = selectedDialogueId,
            },
            CreateContext(scene));

        var change = Assert.Single(proposal.Review.Changes);
        Assert.Equal(selectedDialogueId, proposal.Payload.TargetElementId);
        Assert.Equal("Selected line carries the turn.", change.ExistingContent);
        Assert.Contains("Selected line carries the turn.", change.ProposedContent, StringComparison.Ordinal);
        Assert.DoesNotContain("First line must remain separate.", change.ProposedContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Improve_dialogue_preserves_the_selected_id_when_dialogue_text_is_duplicated()
    {
        var sceneId = Guid.NewGuid();
        var firstDialogueId = Guid.NewGuid();
        var selectedDialogueId = Guid.NewGuid();
        const string duplicateDialogue = "We stay until the lights turn red.";
        var scene = new DirectorScreenplaySceneContext(
            sceneId,
            null,
            "SCENE-1",
            1,
            1,
            "INT. WORKSHOP - DAY",
            "Two repeated lines have separate screenplay identities.",
            [
                new(firstDialogueId, MovieScreenplayElementTypes.Dialogue, duplicateDialogue, "MARA", null),
                new(selectedDialogueId, MovieScreenplayElementTypes.Dialogue, duplicateDialogue, "MARA", null),
            ]);

        var proposal = new DirectorStoryProposalPlanner().Build(
            new DirectorProposalRequest
            {
                StoryAction = DirectorStoryActionTypes.ImproveDialogue,
                TargetSceneId = sceneId,
                TargetElementId = selectedDialogueId,
            },
            CreateContext(scene));

        var change = Assert.Single(proposal.Review.Changes);
        Assert.Equal(sceneId, proposal.Payload.TargetSceneId);
        Assert.Equal(selectedDialogueId, proposal.Payload.TargetElementId);
        Assert.Equal(duplicateDialogue, change.ExistingContent);
        Assert.Contains(duplicateDialogue, change.ProposedContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_from_ai_draft_preserves_the_selected_dialogue_id_when_text_is_duplicated()
    {
        var sceneId = Guid.NewGuid();
        var firstDialogueId = Guid.NewGuid();
        var selectedDialogueId = Guid.NewGuid();
        const string duplicateDialogue = "We stay until the lights turn red.";
        var scene = new DirectorScreenplaySceneContext(
            sceneId,
            null,
            "SCENE-1",
            1,
            1,
            "INT. WORKSHOP - DAY",
            "Two repeated lines have separate screenplay identities.",
            [
                new(firstDialogueId, MovieScreenplayElementTypes.Dialogue, duplicateDialogue, "MARA", null),
                new(selectedDialogueId, MovieScreenplayElementTypes.Dialogue, duplicateDialogue, "MARA", null),
            ]);
        var request = new DirectorProposalRequest
        {
            StoryAction = DirectorStoryActionTypes.ImproveDialogue,
            TargetSceneId = sceneId,
            TargetElementId = selectedDialogueId,
        };
        var routing = new DirectorCreativeQualityRecommendation(
            DirectorStoryActionTypes.ImproveDialogue,
            DirectorQualityLevels.Fast,
            DirectorQualityLevels.Fast,
            "Fast",
            0.25m,
            0.25m,
            0.75m,
            null,
            [],
            false);
        var draft = new DirectorStoryAiDraft
        {
            Premise = "A grounded premise.",
            Logline = "A concise logline.",
            Synopsis = "A bounded synopsis.",
            Treatment = "A bounded treatment.",
            ReplacementContent = "The chosen line takes a different turn.",
        };

        var proposal = new DirectorStoryProposalPlanner().BuildFromAiDraft(request, CreateContext(scene), routing, draft);

        var change = Assert.Single(proposal.Review.Changes);
        Assert.Equal(sceneId, proposal.Payload.TargetSceneId);
        Assert.Equal(selectedDialogueId, proposal.Payload.TargetElementId);
        Assert.Equal(duplicateDialogue, change.ExistingContent);
        Assert.Equal(draft.ReplacementContent, change.ProposedContent);
    }

    [Fact]
    public void Improve_dialogue_rejects_a_dialogue_element_from_a_different_scene()
    {
        var selectedSceneId = Guid.NewGuid();
        var foreignSceneElementId = Guid.NewGuid();
        var selectedScene = new DirectorScreenplaySceneContext(
            selectedSceneId,
            null,
            "SCENE-1",
            1,
            1,
            "INT. WORKSHOP - DAY",
            "The requested scene has its own line.",
            [new(Guid.NewGuid(), MovieScreenplayElementTypes.Dialogue, "Stay here.", "MARA", null)]);
        var otherScene = new DirectorScreenplaySceneContext(
            Guid.NewGuid(),
            null,
            "SCENE-2",
            1,
            2,
            "EXT. STREET - NIGHT",
            "This line belongs to a different scene.",
            [new(foreignSceneElementId, MovieScreenplayElementTypes.Dialogue, "Meet me outside.", "MARA", null)]);

        var exception = Assert.Throws<DirectorValidationException>(() =>
            new DirectorStoryProposalPlanner().Build(
                new DirectorProposalRequest
                {
                    StoryAction = DirectorStoryActionTypes.ImproveDialogue,
                    TargetSceneId = selectedSceneId,
                    TargetElementId = foreignSceneElementId,
                },
                CreateContext(selectedScene, otherScene)));

        Assert.Contains("does not contain dialogue", exception.Message, StringComparison.Ordinal);
    }

    private static DirectorStoryBoundedContextDto CreateContext(params DirectorScreenplaySceneContext[] scenes)
    {
        var targetScene = scenes[0];
        return new DirectorStoryBoundedContextDto(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "A bounded workshop story.",
            new DirectorGuideContext("grounded", "observational", "natural light", "quiet", "Keep dialogue grounded.", 1, true),
            new DirectorStoryRevisionContext(
                Guid.NewGuid(),
                1,
                MovieStoryRevisionStatuses.Draft,
                MovieStoryAuthorship.Human,
                "A grounded premise.",
                "A concise logline.",
                "A bounded synopsis.",
                "A bounded treatment.",
                scenes),
            null,
            targetScene,
            [],
            [],
            [],
            DateTime.UnixEpoch,
            DurationSeconds: 120,
            Language: "en");
    }
}
