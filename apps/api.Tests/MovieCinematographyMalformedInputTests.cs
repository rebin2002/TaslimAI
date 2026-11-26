using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieCinematographyMalformedInputTests
{
    [Fact]
    public void Null_capability_reference_is_rejected_without_throwing()
    {
        var selection = new CinematographyIntentSelection(
            CinematographyIntent.Natural,
            null,
            null,
            new CinematographyCapabilityReference[] { null! });

        Assert.NotNull(CinematographyIntentValidator.Validate(selection));
    }

    [Fact]
    public void Null_persisted_capability_reference_is_ignored_during_normalization()
    {
        var selection = new CinematographyIntentSelection(
            CinematographyIntent.Natural,
            null,
            null,
            new CinematographyCapabilityReference[] { null! });

        var normalized = CinematographyIntentValidator.Normalize(selection);

        Assert.NotNull(normalized);
        Assert.Empty(normalized!.CapabilityReferences!);
    }
}
