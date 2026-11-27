using Taslim.Api.Ai;
using Taslim.Api.Domain;
using Taslim.Api.Documents;
using Taslim.Api.Infrastructure;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Document failure classification is provider-neutral: it is derived from the
/// provider-agnostic failure category, never from a vendor payload. These tests pin
/// the distinction between a provider that could not be reached and a provider that
/// answered with a body that is not a usable document, because the two need different
/// operator actions.
/// </summary>
public sealed class DocumentGenerationDiagnosticsTests
{
    [Fact]
    public void Malformed_provider_response_is_an_invalid_output_not_a_provider_outage()
    {
        var exception = new AiProviderException(
            httpStatusCode: 200,
            providerErrorCode: null,
            failureCategory: AiProviderFailureCategories.MalformedResponse,
            modelKey: "synthetic-document-model",
            structuredOutputRequested: true);

        var code = DocumentGenerationFailureCodes.ForProvider(exception);

        Assert.Equal(GenerationJobErrorCodes.DocumentOutputInvalid, code);
        Assert.Equal("output", GenerationFailureClassifier.Classify(code).Category);
        Assert.DoesNotContain("PROVIDER", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Transient_and_rate_limited_provider_failures_keep_their_distinct_codes()
    {
        Assert.Equal(
            GenerationJobErrorCodes.DocumentProviderTransientFailure,
            DocumentGenerationFailureCodes.ForProvider(new AiProviderException(failureCategory: AiProviderFailureCategories.Transient)));
        Assert.Equal(
            GenerationJobErrorCodes.DocumentProviderRateLimited,
            DocumentGenerationFailureCodes.ForProvider(new AiProviderException(failureCategory: AiProviderFailureCategories.RateLimited)));
        Assert.Equal(
            GenerationJobErrorCodes.DocumentProviderConfiguration,
            DocumentGenerationFailureCodes.ForProvider(new AiProviderException(failureCategory: AiProviderFailureCategories.Configuration)));
        Assert.Equal(
            GenerationJobErrorCodes.DocumentProviderUnsupportedRequest,
            DocumentGenerationFailureCodes.ForProvider(new AiProviderException(failureCategory: AiProviderFailureCategories.UnsupportedRequest)));
    }

    [Fact]
    public void Unknown_provider_failure_still_falls_back_to_unavailable()
    {
        var code = DocumentGenerationFailureCodes.ForProvider(new AiProviderException(failureCategory: AiProviderFailureCategories.Unknown));

        Assert.Equal(GenerationJobErrorCodes.DocumentProviderUnavailable, code);
        Assert.Equal("provider", GenerationFailureClassifier.Classify(code).Category);
    }

    [Fact]
    public void Malformed_output_and_provider_outage_remain_distinguishable_in_operational_reporting()
    {
        var malformed = GenerationFailureClassifier.Classify(DocumentGenerationFailureCodes.ForProvider(
            new AiProviderException(failureCategory: AiProviderFailureCategories.MalformedResponse)));
        var outage = GenerationFailureClassifier.Classify(DocumentGenerationFailureCodes.ForProvider(
            new AiProviderException(failureCategory: AiProviderFailureCategories.Unknown)));

        // The fix must not collapse the two signals: an output-contract violation is
        // reported where an operator will look at generated content, not at provider
        // health, and neither classification may leak provider detail.
        Assert.NotEqual(malformed.Category, outage.Category);
        Assert.Equal("output", malformed.Category);
        Assert.Equal("provider", outage.Category);
        Assert.Equal(GenerationJobErrorCodes.DocumentOutputInvalid, malformed.Code);
        Assert.Equal(GenerationJobErrorCodes.DocumentProviderUnavailable, outage.Code);
    }

    [Fact]
    public void Draft_stage_failures_and_the_kill_switch_use_distinct_document_codes()
    {
        Assert.Equal(GenerationJobErrorCodes.DocumentOutputInvalid, DocumentGenerationFailureCodes.ForStage(DocumentGenerationStages.DraftParse));
        Assert.Equal(GenerationJobErrorCodes.DocumentRenderFailed, DocumentGenerationFailureCodes.ForStage(DocumentGenerationStages.DocxRender));
        Assert.Equal(GenerationJobErrorCodes.DocumentStorageFailed, DocumentGenerationFailureCodes.ForStage(DocumentGenerationStages.AssetPublish));

        var unavailable = DocumentStudioAvailability.Unavailable();
        Assert.Equal(GenerationJobErrorCodes.DocumentStudioUnavailable, unavailable.Code);
        Assert.Equal(DocumentGenerationStages.Validation, unavailable.Stage);
        Assert.Equal("Document generation is not available right now.", unavailable.Message);
        // The availability signal is operational, not an output defect: it must not
        // be reported as provider health or as invalid generated content.
        Assert.Equal("internal", GenerationFailureClassifier.Classify(unavailable.Code).Category);
        Assert.False(GenerationFailureClassifier.Classify(unavailable.Code).Retryable);
    }

    [Fact]
    public void Kill_switch_message_carries_no_provider_or_model_detail()
    {
        Assert.DoesNotContain("provider", DocumentStudioAvailability.UnavailableMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model", DocumentStudioAvailability.UnavailableMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("key", DocumentStudioAvailability.UnavailableMessage, StringComparison.OrdinalIgnoreCase);
    }
}
