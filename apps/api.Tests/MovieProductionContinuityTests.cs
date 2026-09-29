using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieProductionContinuityTests
{
    [Fact]
    public void Shot_plan_reports_locked_character_world_guide_and_object_conflicts_with_grounded_evidence()
    {
        var projectId = Guid.NewGuid();
        var sceneId = Guid.NewGuid();
        var shotId = Guid.NewGuid();
        var maraId = Guid.NewGuid();
        var setId = Guid.NewGuid();
        var propId = Guid.NewGuid();
        var setLockId = Guid.NewGuid();
        var propStateLockId = Guid.NewGuid();
        var propPositionLockId = Guid.NewGuid();
        var maraLockId = Guid.NewGuid();
        var variationId = Guid.NewGuid();
        var shot = new MovieProductionContinuityShotContext(
            shotId, 1, "Mara wears a blue coat, is uninjured, and stands outside at night in a sunny set. The red box is closed on the right.",
            "Mara continuity close-up", "Mara", [maraId], "Courtyard", "Keep the red box in view.", null, null, null, null, null, null);
        var scene = new MovieProductionContinuitySceneContext(sceneId, 1, "Courtyard", "Mara enters the courtyard.", null, null, null, [shot]);
        var character = new MovieProductionContinuityCharacterContext(
            maraId, "Mara", "Courier", null, "red coat", null,
            [new MovieProductionContinuityCharacterStateContext(Guid.NewGuid(), "injured", "Injured", null, null, null, "injured", null, null, [])],
            [new MovieProductionContinuityLockContext(maraLockId, "character", maraId, "wardrobe", "red coat", "hard", "approved wardrobe"),
             new MovieProductionContinuityLockContext(Guid.NewGuid(), "character", maraId, "injuryOrCondition", "injured", "hard", "approved injury state")]);
        var set = new MovieWorldContinuitySetSnapshot(setId, null, "Courtyard", "Courtyard", "practical", null, "day", "rain", null, null,
            [new MovieWorldContinuityVariationSnapshot(variationId, setId, "Day", null, "day", "rain", null, null, null, true)]);
        var prop = new MovieWorldContinuityPropSnapshot(propId, "red box", "A red box", "prop", null, null, "open");
        var world = new MovieWorldContinuitySnapshotDto(
            projectId, sceneId, shotId, 1, DateTime.UtcNow, "hash", [], [set], [prop],
            [new MovieWorldContinuityFactSnapshot(Guid.NewGuid(), MovieWorldScopes.Prop, propId, "position", "left", null, DateTime.UtcNow)],
            [
                new MovieWorldContinuityLockSnapshot(setLockId, MovieWorldEntityTypes.Set, setId, "timeOfDay", "day", "hard", "daylight lock", DateTime.UtcNow),
                new MovieWorldContinuityLockSnapshot(Guid.NewGuid(), MovieWorldEntityTypes.Set, setId, "weather", "rain", "hard", "weather lock", DateTime.UtcNow),
                new MovieWorldContinuityLockSnapshot(propStateLockId, MovieWorldEntityTypes.Prop, propId, "state", "open", "hard", "object state", DateTime.UtcNow),
                new MovieWorldContinuityLockSnapshot(propPositionLockId, MovieWorldEntityTypes.Prop, propId, "position", "left", "hard", "object position", DateTime.UtcNow),
            ], []);
        var context = new MovieProductionContinuityContext(
            projectId, sceneId, shotId,
            new DirectorGuideContext("", "", "", "", "The courtyard must remain inside and daylight is authoritative.", 2, true, null,
                [new DirectorGuideSectionContext(MovieGuideSectionTypes.ContinuityBible, "{\"rule\":\"The courtyard must remain inside.\"}")]),
            [scene], [character], world,
            [new MovieWorldUsage { Id = Guid.NewGuid(), MovieProjectId = projectId, MovieSceneId = sceneId, MovieShotId = shotId, EntityType = MovieWorldEntityTypes.Set, EntityId = setId },
             new MovieWorldUsage { Id = Guid.NewGuid(), MovieProjectId = projectId, MovieSceneId = sceneId, MovieShotId = shotId, EntityType = MovieWorldEntityTypes.Prop, EntityId = propId }],
            [], [], DateTime.UtcNow);

        var findings = MovieProductionContinuityAnalyzer.Analyze(context);

        Assert.Contains(findings, item => item.FindingType == DirectorStoryFindingTypes.HardContinuityConflict && item.Category == DirectorStoryFindingCategories.Wardrobe);
        Assert.Contains(findings, item => item.FindingType == DirectorStoryFindingTypes.HardContinuityConflict && item.Category == DirectorStoryFindingCategories.InjuryState);
        Assert.Contains(findings, item => item.FindingType == DirectorStoryFindingTypes.HardContinuityConflict && item.Category == DirectorStoryFindingCategories.TimeOfDay);
        Assert.Contains(findings, item => item.FindingType == DirectorStoryFindingTypes.HardContinuityConflict && item.Category == DirectorStoryFindingCategories.Weather);
        Assert.Contains(findings, item => item.FindingType == DirectorStoryFindingTypes.HardContinuityConflict && item.Category == DirectorStoryFindingCategories.ObjectState);
        Assert.Contains(findings, item => item.FindingType == DirectorStoryFindingTypes.HardContinuityConflict && item.Category == DirectorStoryFindingCategories.ObjectPosition);
        Assert.Contains(findings, item => item.FindingType == DirectorStoryFindingTypes.HardContinuityConflict && item.Category == DirectorStoryFindingCategories.LockedMovieGuide);
        Assert.All(findings, item => Assert.NotEmpty(item.Evidence));
        Assert.All(findings, item => Assert.False(string.IsNullOrWhiteSpace(item.Explanation)));
        Assert.All(findings, item => Assert.False(string.IsNullOrWhiteSpace(item.SuggestedCorrection)));
    }

    [Fact]
    public void Planning_review_flags_ambiguous_order_and_explicit_character_presence_mismatch_without_mutating_plan()
    {
        var projectId = Guid.NewGuid();
        var sceneId = Guid.NewGuid();
        var maraId = Guid.NewGuid();
        var first = new MovieProductionContinuityShotContext(Guid.NewGuid(), 1, "Mara enters.", null, "Mara", [maraId], null, null, null, null, null, null, null, null);
        var duplicate = new MovieProductionContinuityShotContext(Guid.NewGuid(), 1, "Mara is referenced off screen.", null, "Mara", [], null, null, null, null, null, null, null, null);
        var scene = new MovieProductionContinuitySceneContext(sceneId, 1, "Entry", "Mara enters.", null, null, null, [first, duplicate]);
        var context = new MovieProductionContinuityContext(projectId, sceneId, null,
            new DirectorGuideContext("", "", "", "", "", null, false), [scene],
            [new MovieProductionContinuityCharacterContext(maraId, "Mara", "Courier", null, null, null, [], [])], null, [], [], [], DateTime.UtcNow);
        var before = (scene.Shots[1].Sequence, scene.Shots[1].Subjects, scene.Shots[1].SubjectCharacterIds.Count);

        var findings = MovieProductionContinuityAnalyzer.Analyze(context);

        Assert.Contains(findings, item => item.Category == DirectorStoryFindingCategories.Chronology && item.FindingType == DirectorStoryFindingTypes.HardContinuityConflict);
        Assert.Contains(findings, item => item.Category == DirectorStoryFindingCategories.CharacterPresence && item.FindingType == DirectorStoryFindingTypes.PossibleInconsistency);
        Assert.Equal(before, (scene.Shots[1].Sequence, scene.Shots[1].Subjects, scene.Shots[1].SubjectCharacterIds.Count));
    }

    [Fact]
    public void Relevant_canon_without_a_shot_reference_is_a_suggestion_not_a_conflict()
    {
        var projectId = Guid.NewGuid();
        var sceneId = Guid.NewGuid();
        var shotId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        var shot = new MovieProductionContinuityShotContext(shotId, 1, "Mara crosses the courtyard.", null, "Mara", [characterId], null, null, null, null, null, null, null, null);
        var scene = new MovieProductionContinuitySceneContext(sceneId, 1, "Courtyard", "Mara crosses.", null, null, null, [shot]);
        var character = new MovieProductionContinuityCharacterContext(characterId, "Mara", "Courier", null, "red coat", null, [], []);
        var context = new MovieProductionContinuityContext(projectId, sceneId, shotId, new DirectorGuideContext("", "", "", "", "", null, false), [scene], [character], null, [], [], [], DateTime.UtcNow);

        var findings = MovieProductionContinuityAnalyzer.Analyze(context);

        var suggestion = Assert.Single(findings, item => item.FindingType == DirectorStoryFindingTypes.CreativeSuggestion);
        Assert.Equal(DirectorStoryFindingCategories.ProductionContinuity, suggestion.Category);
        Assert.Equal(DirectorStoryFindingSeverities.Suggestion, suggestion.Severity);
        Assert.Equal(shotId, suggestion.AffectedTarget.TargetId);
    }
}
