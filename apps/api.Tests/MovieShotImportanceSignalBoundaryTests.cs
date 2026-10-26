using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieShotImportanceSignalBoundaryTests
{
    [Fact]
    public void Ordinary_word_containing_hero_signal_does_not_make_a_shot_hero()
    {
        var result = new MovieShotImportanceClassifier().Classify(new MovieShotImportanceContext(
            "The analyst studies the map and opens the folder.",
            null,
            "analyst",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            new MovieShotImportanceSceneContext("Archive", "A quiet planning scene.", null, null, null),
            null,
            null));

        Assert.Equal(MovieShotImportanceLevels.Standard, result.Classification);
    }
}
