using Taslim.Api.Ai;
using Taslim.Api.Domain;
using Taslim.Api.Social;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class SocialGenerationDiagnosticsTests
{
    [Fact]
    public void Failure_code_mapping_classifies_malformed_provider_output_as_invalid_result()
    {
        var exception = new AiProviderException(failureCategory: AiProviderFailureCategories.MalformedResponse);

        var code = SocialGenerationFailureCodes.ForProvider(exception);

        Assert.Equal(GenerationJobErrorCodes.SocialOutputInvalid, code);
    }
}
