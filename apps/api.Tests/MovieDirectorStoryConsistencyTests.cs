using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieDirectorStoryConsistencyTests
{
    [Fact]
    public void Duplicate_production_scene_links_return_findings_instead_of_throwing()
    {
        var movieSceneId = Guid.NewGuid();
        var current = Revision(
            Guid.NewGuid(),
            [
                Scene(Guid.NewGuid(), movieSceneId, "SCENE-1", 1, "INT. ROOM - DAY"),
                Scene(Guid.NewGuid(), movieSceneId, "SCENE-2", 2, "INT. HALL - DAY"),
            ]);
        var approved = Revision(Guid.NewGuid(), [Scene(Guid.NewGuid(), movieSceneId, "SCENE-1", 1, "INT. ROOM - DAY")]);
        var context = new DirectorStoryBoundedContextDto(
            Guid.NewGuid(), Guid.NewGuid(), "A bounded movie brief.",
            new DirectorGuideContext("visual", "camera", "light", "sound", "continuity"),
            current, approved, null, [], [], [], DateTime.UtcNow, 1, 0, "en", null, "16:9");

        var findings = DirectorStoryConsistencyAnalyzer.Analyze(context);

        Assert.Contains(findings, finding => finding.FindingType == DirectorStoryFindingTypes.HardContinuityConflict
            && finding.Explanation.Contains("multiple screenplay scenes", StringComparison.Ordinal));
    }

    private static DirectorStoryRevisionContext Revision(Guid id, IReadOnlyList<DirectorScreenplaySceneContext> scenes) =>
        new(id, 2, "Draft", "human", "premise", "logline", "synopsis", "treatment", scenes);

    private static DirectorScreenplaySceneContext Scene(Guid id, Guid movieSceneId, string identifier, int sequence, string slugline) =>
        new(id, movieSceneId, identifier, 1, sequence, slugline, "A bounded scene.", []);
}
