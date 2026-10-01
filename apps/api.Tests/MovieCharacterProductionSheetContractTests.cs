using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieCharacterProductionSheetContractTests
{
    [Fact]
    public void Rejects_duplicate_identity_reference_assets()
    {
        var asset = Guid.NewGuid();
        var exception = Assert.Throws<MovieCharacterProductionSheetValidationException>(() => MovieCharacterProductionSheetService.ValidateRequest(new MovieCharacterProductionSheetRequest
        {
            CanonicalIdentityFaceAssetId = asset,
            BodyReferenceAssetId = asset,
        }));

        Assert.Equal("DUPLICATE_REFERENCE_ASSET", exception.Code);
    }

    [Fact]
    public void Approval_requires_all_five_identity_slots()
    {
        var exception = Assert.Throws<MovieCharacterProductionSheetValidationException>(() => MovieCharacterProductionSheetService.ValidateRequest(new MovieCharacterProductionSheetRequest
        {
            CanonicalIdentityFaceAssetId = Guid.NewGuid(),
            BodyReferenceAssetId = Guid.NewGuid(),
            FrontReferenceAssetId = Guid.NewGuid(),
            SideReferenceAssetId = Guid.NewGuid(),
        }, requireComplete: true));

        Assert.Equal("INCOMPLETE_REFERENCE_SHEET", exception.Code);
    }

    [Fact]
    public void Look_keys_are_unique_within_a_version()
    {
        var exception = Assert.Throws<MovieCharacterProductionSheetValidationException>(() => MovieCharacterProductionSheetService.ValidateRequest(new MovieCharacterProductionSheetRequest
        {
            Looks =
            [
                new MovieCharacterProductionSheetLookRequest("hero", "Hero", Wardrobe: "Coat"),
                new MovieCharacterProductionSheetLookRequest("HERO", "Hero alternate", Wardrobe: "Jacket"),
            ],
        }));

        Assert.Equal("DUPLICATE_LOOK_KEY", exception.Code);
    }
}
