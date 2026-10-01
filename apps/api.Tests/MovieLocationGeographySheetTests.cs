using Xunit;
using Taslim.Api.Movies;

namespace Taslim.Api.Tests;

public sealed class MovieLocationGeographySheetTests
{
    [Fact]
    public void Validate_requires_complete_spatial_entries_and_bounds_groups()
    {
        var request = new MovieLocationGeographySheetRequest
        {
            EntrancesExits = [new MovieLocationGeographyOpening("", "door", "Entry")],
        };

        Assert.Equal("An opening label is required.", MovieLocationGeographySheetPolicy.Validate(request));

        request.EntrancesExits = Enumerable.Range(0, MovieLocationGeographyLimits.MaxEntriesPerGroup + 1)
            .Select(index => new MovieLocationGeographyOpening($"Door {index}", "door", "Entry")).ToList();
        Assert.Contains("cannot contain more than", MovieLocationGeographySheetPolicy.Validate(request));
    }

    [Fact]
    public void Serialization_is_bounded_and_round_trips_spatial_groups()
    {
        var entries = Enumerable.Range(0, MovieLocationGeographyLimits.MaxEntriesPerGroup + 3)
            .Select(index => new MovieLocationGeographyEntry($"Object {index}", "Continuity object")).ToArray();

        var json = MovieLocationGeographySheetSerialization.Entries(entries);
        var restored = MovieLocationGeographySheetSerialization.ReadEntries(json);

        Assert.Equal(MovieLocationGeographyLimits.MaxEntriesPerGroup, restored.Count);
        Assert.Equal("Object 0", restored[0].Label);
    }

    [Fact]
    public void Continuity_hash_changes_when_a_reference_or_anchor_changes()
    {
        var now = DateTime.UtcNow;
        var guide = new MovieContinuityGuide { Id = Guid.NewGuid(), CurrentRevisionNumber = 3, VisualLanguage = "Dusty realism", ContinuityRules = "North wall stays camera left." };
        var sheet = new MovieLocationGeographySheet { Id = Guid.NewGuid(), MovieLocationId = Guid.NewGuid(), CreatedAt = now, UpdatedAt = now, EstablishingReferenceAssetId = Guid.NewGuid(), EntrancesExitsJson = "[{\"label\":\"North door\",\"kind\":\"door\",\"description\":\"Entry\"}]" };

        var first = MovieLocationGeographyContinuity.Hash(sheet.MovieLocationId, sheet, guide);
        sheet.EntrancesExitsJson = "[{\"label\":\"South door\",\"kind\":\"door\",\"description\":\"Entry\"}]";

        Assert.NotEqual(first, MovieLocationGeographyContinuity.Hash(sheet.MovieLocationId, sheet, guide));
    }
}
