using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieDirectorStoryTargetIntegrityTests
{
    [Fact]
    public void Improve_dialogue_uses_the_selected_dialogue_element_in_review_and_replacement()
    {
        var movieProjectId = Guid.NewGuid();
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
        var context = new DirectorStoryBoundedContextDto(
            movieProjectId,
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
                [scene]),
            null,
            scene,
            [],
            [],
            [],
            DateTime.UnixEpoch,
            DurationSeconds: 120,
            Language: "en");

        var proposal = new DirectorStoryProposalPlanner().Build(
            new DirectorProposalRequest
            {
                StoryAction = DirectorStoryActionTypes.ImproveDialogue,
                TargetSceneId = sceneId,
                TargetElementId = selectedDialogueId,
            },
            context);

        var change = Assert.Single(proposal.Review.Changes);
        Assert.Equal(selectedDialogueId.ToString(), proposal.Payload.TargetElementId?.ToString());
        Assert.Equal("Selected line carries the turn.", change.ExistingContent);
        Assert.Contains("Selected line carries the turn.", change.ProposedContent, StringComparison.Ordinal);
        Assert.DoesNotContain("First line must remain separate.", change.ProposedContent, StringComparison.Ordinal);
    }
}
