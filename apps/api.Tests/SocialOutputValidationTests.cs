using Taslim.Api.Contracts;
using Taslim.Api.Social;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class SocialOutputValidationTests
{
    [Fact]
    public void Draft_validator_rejects_null_provider_collections_as_invalid_output()
    {
        var options = new SocialGenerationOptions();

        var missingPosts = new SocialDraft
        {
            Title = "Launch",
            Platform = "linkedin",
            SocialType = "announcement",
            Language = "en",
            Posts = null!,
        };
        Assert.Throws<SocialOutputValidationException>(() => SocialDraftValidator.Validate(missingPosts, options));

        var missingPostCollections = new SocialDraft
        {
            Title = "Launch",
            Platform = "linkedin",
            SocialType = "announcement",
            Language = "en",
            Posts = [new SocialPost { Order = 1, Hook = "Hook", Body = "Body", Hashtags = null!, AssetRefs = null! }],
        };
        Assert.Throws<SocialOutputValidationException>(() => SocialDraftValidator.Validate(missingPostCollections, options));
    }

    [Fact]
    public void Draft_validator_rejects_null_post_entries_as_invalid_output()
    {
        var draft = new SocialDraft
        {
            Title = "Launch",
            Platform = "linkedin",
            SocialType = "announcement",
            Language = "en",
            Posts = [null!],
        };

        Assert.Throws<SocialOutputValidationException>(() => SocialDraftValidator.Validate(draft, new SocialGenerationOptions()));
    }

    [Fact]
    public void Draft_validator_rejects_blank_references_and_markup_titles()
    {
        var options = new SocialGenerationOptions();

        var nullHashtag = new SocialDraft
        {
            Title = "Launch",
            Platform = "linkedin",
            SocialType = "announcement",
            Language = "en",
            Posts = [new SocialPost { Order = 1, Hook = "Hook", Body = "Body", Hashtags = [null!] }],
        };
        Assert.Throws<SocialOutputValidationException>(() => SocialDraftValidator.Validate(nullHashtag, options));

        var blankHashtag = new SocialDraft
        {
            Title = "Launch",
            Platform = "linkedin",
            SocialType = "announcement",
            Language = "en",
            Posts = [new SocialPost { Order = 1, Hook = "Hook", Body = "Body", Hashtags = ["   "] }],
        };
        Assert.Throws<SocialOutputValidationException>(() => SocialDraftValidator.Validate(blankHashtag, options));

        var nullAssetReference = new SocialDraft
        {
            Title = "Launch",
            Platform = "linkedin",
            SocialType = "announcement",
            Language = "en",
            Posts = [new SocialPost { Order = 1, Hook = "Hook", Body = "Body", AssetRefs = [null!] }],
        };
        Assert.Throws<SocialOutputValidationException>(() => SocialDraftValidator.Validate(nullAssetReference, options));

        var blankAssetReference = new SocialDraft
        {
            Title = "Launch",
            Platform = "linkedin",
            SocialType = "announcement",
            Language = "en",
            Posts = [new SocialPost { Order = 1, Hook = "Hook", Body = "Body", AssetRefs = ["   "] }],
        };
        Assert.Throws<SocialOutputValidationException>(() => SocialDraftValidator.Validate(blankAssetReference, options));

        var markupTitle = new SocialDraft
        {
            Title = "<b>Launch</b>",
            Platform = "linkedin",
            SocialType = "announcement",
            Language = "en",
            Posts = [new SocialPost { Order = 1, Hook = "Hook", Body = "Body" }],
        };
        Assert.Throws<SocialOutputValidationException>(() => SocialDraftValidator.Validate(markupTitle, options));
    }
}
