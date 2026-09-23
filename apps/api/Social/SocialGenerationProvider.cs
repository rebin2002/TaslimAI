using System.Text.Json;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;

namespace Taslim.Api.Social;

public sealed record SocialProviderResult(SocialDraft Draft, AiUsageMetadata Usage);

public interface ISocialGenerationProvider
{
    Task<SocialProviderResult> GenerateAsync(SocialGenerationPrompt prompt, SocialGenerationOptions options, CancellationToken cancellationToken = default);
}

public sealed class AiSocialGenerationProvider(IChatCompletionService completion) : ISocialGenerationProvider
{
    public async Task<SocialProviderResult> GenerateAsync(SocialGenerationPrompt prompt, SocialGenerationOptions options, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.ProviderTimeoutSeconds)));
        AiGenerationResult result;
        try
        {
            result = await completion.CompleteAsync(new AiChatRequest(
                [new AiChatMessage("user", prompt.UserInstruction)],
                prompt.SystemInstruction,
                options.RequestedTier,
                EnableStreaming: false,
                MaxOutputTokens: options.MaxOutputTokens,
                StructuredOutput: SocialDraftStructuredOutput.Spec), timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiProviderTimeoutException();
        }
        if (string.IsNullOrWhiteSpace(result.Content)) throw new SocialGenerationStageException(SocialGenerationStages.DraftParse, GenerationJobErrorCodes.SocialOutputInvalid, "The social response was empty.", result.Usage);

        SocialDraft draft;
        try
        {
            draft = JsonSerializer.Deserialize<SocialDraft>(NormalizeStructuredJson(result.Content), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new JsonException("The social response was empty.");
        }
        catch (JsonException exception)
        {
            throw new SocialGenerationStageException(SocialGenerationStages.DraftParse, GenerationJobErrorCodes.SocialOutputInvalid, "The social response could not be parsed.", result.Usage, exception);
        }
        try
        {
            SocialDraftValidator.Validate(draft, options);
        }
        catch (SocialOutputValidationException exception)
        {
            throw new SocialGenerationStageException(SocialGenerationStages.DraftValidation, GenerationJobErrorCodes.SocialOutputInvalid, "The social response did not satisfy the required structure.", result.Usage, exception);
        }
        return new SocialProviderResult(draft, result.Usage);
    }

    private static string NormalizeStructuredJson(string content)
    {
        var value = content.Trim();
        if (!value.StartsWith("```", StringComparison.Ordinal) || !value.EndsWith("```", StringComparison.Ordinal)) return value;
        var firstLineEnd = value.IndexOf('\n');
        if (firstLineEnd < 0 || !value[..firstLineEnd].Trim().Equals("```json", StringComparison.OrdinalIgnoreCase)) throw new JsonException("The social response contained an unsupported wrapper.");
        return value[(firstLineEnd + 1)..^3].Trim();
    }
}

public static class SocialDraftStructuredOutput
{
    public static AiStructuredOutputSpec Spec { get; } = new(
        "taslim_social_draft",
        JsonDocument.Parse("""
        {
          "type":"object","additionalProperties":false,
          "properties":{
            "title":{"type":"string"},"platform":{"type":"string"},"socialType":{"type":"string"},"language":{"type":"string"},
            "posts":{"type":"array","items":{"type":"object","additionalProperties":false,"properties":{
              "order":{"type":"integer"},"hook":{"type":"string"},"body":{"type":"string"},"callToAction":{"type":["string","null"]},
              "hashtags":{"type":"array","items":{"type":"string"}},"altText":{"type":["string","null"]},"visualDirection":{"type":["string","null"]},"assetRefs":{"type":"array","items":{"type":"string"}}
            },"required":["order","hook","body","callToAction","hashtags","altText","visualDirection","assetRefs"]}}
          },
          "required":["title","platform","socialType","language","posts"]
        }
        """).RootElement.Clone(),
        "A bounded canonical Social Studio draft with ready-to-review posts.");
}
