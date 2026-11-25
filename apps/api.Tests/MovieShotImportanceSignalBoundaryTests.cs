using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieShotImportanceSignalBoundaryTests
{
    [Fact]
    public void Ordinary_word_containing_hero_signal_does_not_make_a_shot_hero()
    {
        var result = new MovieShotImportanceClassifier().Classify(Context(
            "The analyst studies the map and opens the folder."));

        Assert.Equal(MovieShotImportanceLevels.Standard, result.Classification);
        Assert.DoesNotContain(result.Evidence, item => item.Signal == "hero_narrative");
    }

    [Fact]
    public void Whole_phrase_signal_matches_across_punctuation_and_keeps_evidence_excerpt_on_match()
    {
        var description = $"A quiet archive {new string('x', 280)}. The FINAL—SHOWDOWN begins and Mara takes action.";

        var result = new MovieShotImportanceClassifier().Classify(Context(description));

        Assert.Equal(MovieShotImportanceLevels.Hero, result.Classification);
        var evidence = Assert.Single(result.Evidence, item => item.Source == "shot" && item.Signal == "hero_narrative");
        Assert.StartsWith("…", evidence.Excerpt);
        Assert.Contains("FINAL—SHOWDOWN", evidence.Excerpt);
    }

    private static MovieShotImportanceContext Context(string description) => new(
        description,
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
        null);
}
