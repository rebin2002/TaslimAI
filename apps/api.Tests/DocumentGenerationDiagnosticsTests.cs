using Taslim.Api.Ai;
using Taslim.Api.Domain;
using Taslim.Api.Documents;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class DocumentGenerationDiagnosticsTests
{
    [Fact]
    public void Malformed_provider_response_is_classified_as_invalid_document_output()
    {
        var exception = new AiProviderException(
            failureCategory: AiProviderFailureCategories.MalformedResponse,
            modelKey: "synthetic-document-model",
            structuredOutputRequested: true);

        Assert.Equal(
            GenerationJobErrorCodes.DocumentOutputInvalid,
            DocumentGenerationFailureCodes.ForProvider(exception));
    }
}
