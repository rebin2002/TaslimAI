using Taslim.Api.Ai;
using Taslim.Api.Domain;
using Taslim.Api.Social;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class SocialGenerationDiagnosticsTests
{
    [Fact]
    public void Malformed_provider_output_is_classified_as_invalid_social_output()
    {
        var exception = new AiProviderException(failureCategory: AiProviderFailureCategories.MalformedResponse);

        var code = SocialGenerationFailureCodes.ForProvider(exception);

        Assert.Equal(GenerationJobErrorCodes.SocialOutputInvalid, code);
    }

    [Theory]
    [InlineData(AiProviderFailureCategories.Configuration, GenerationJobErrorCodes.SocialProviderConfiguration)]
    [InlineData(AiProviderFailureCategories.UnsupportedRequest, GenerationJobErrorCodes.SocialProviderUnsupportedRequest)]
    [InlineData(AiProviderFailureCategories.RateLimited, GenerationJobErrorCodes.SocialProviderRateLimited)]
    [InlineData(AiProviderFailureCategories.Transient, GenerationJobErrorCodes.SocialProviderTransientFailure)]
    [InlineData(AiProviderFailureCategories.Unknown, GenerationJobErrorCodes.SocialProviderUnavailable)]
    public void Provider_failure_categories_keep_their_stable_social_codes(string category, string expectedCode)
    {
        var exception = new AiProviderException(failureCategory: category);

        Assert.Equal(expectedCode, SocialGenerationFailureCodes.ForProvider(exception));
    }
}
