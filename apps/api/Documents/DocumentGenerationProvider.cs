using System.Text.Json;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;

namespace Taslim.Api.Documents;

public sealed record DocumentProviderResult(DocumentDraft Draft, AiUsageMetadata Usage);

public interface IDocumentGenerationProvider
{
    Task<DocumentProviderResult> GenerateAsync(DocumentGenerationPrompt prompt, DocumentGenerationOptions options, CancellationToken cancellationToken = default);
}

public sealed class AiDocumentGenerationProvider(IChatCompletionService completion) : IDocumentGenerationProvider
{
    public async Task<DocumentProviderResult> GenerateAsync(DocumentGenerationPrompt prompt, DocumentGenerationOptions options, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.ProviderTimeoutSeconds)));
        AiGenerationResult result;
        try
        {
            result = await completion.CompleteAsync(
                new AiChatRequest(
                    [new AiChatMessage("user", prompt.UserInstruction)],
                    prompt.SystemInstruction,
                    options.RequestedTier,
                    EnableStreaming: false,
                    MaxOutputTokens: options.MaxOutputTokens,
                    StructuredOutput: DocumentDraftStructuredOutput.Spec),
                timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiProviderTimeoutException();
        }
        if (string.IsNullOrWhiteSpace(result.Content))
            throw new DocumentGenerationStageException(DocumentGenerationStages.DraftParse, GenerationJobErrorCodes.DocumentOutputInvalid, "The document response was empty.", result.Usage);

        DocumentDraft draft;
        try
        {
            draft = JsonSerializer.Deserialize<DocumentDraft>(NormalizeStructuredJson(result.Content), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new JsonException("The document response was empty.");
        }
        catch (JsonException exception)
        {
            throw new DocumentGenerationStageException(DocumentGenerationStages.DraftParse, GenerationJobErrorCodes.DocumentOutputInvalid, "The document response could not be parsed.", result.Usage, exception);
        }

        try
        {
            DocumentDraftValidator.Validate(draft, options);
        }
        catch (DocumentOutputValidationException exception)
        {
            throw new DocumentGenerationStageException(DocumentGenerationStages.DraftValidation, GenerationJobErrorCodes.DocumentOutputInvalid, "The document response did not satisfy the required structure.", result.Usage, exception);
        }

        return new DocumentProviderResult(draft, result.Usage);
    }

    private static string NormalizeStructuredJson(string content)
    {
        var value = content.Trim();
        if (!value.StartsWith("```", StringComparison.Ordinal) || !value.EndsWith("```", StringComparison.Ordinal)) return value;
        var firstLineEnd = value.IndexOf('\n');
        if (firstLineEnd < 0 || !value[..firstLineEnd].Trim().Equals("```json", StringComparison.OrdinalIgnoreCase))
            throw new JsonException("The document response contained an unsupported wrapper.");
        return value[(firstLineEnd + 1)..^3].Trim();
    }
}

public static class DocumentDraftStructuredOutput
{
    public static AiStructuredOutputSpec Spec { get; } = new(
        "taslim_document_draft",
        JsonDocument.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "title": { "type": "string" },
            "summary": { "type": "string" },
            "sections": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "heading": { "type": "string" },
                  "blocks": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "additionalProperties": false,
                      "properties": {
                        "type": { "type": "string", "enum": ["paragraph", "heading", "bullet_list", "numbered_list", "table"] },
                        "text": { "type": ["string", "null"] },
                        "items": { "type": ["array", "null"], "items": { "type": "string" } },
                        "rows": {
                          "type": ["array", "null"],
                          "items": {
                            "type": "object",
                            "additionalProperties": false,
                            "properties": {
                              "cells": { "type": "array", "items": { "type": "string" } }
                            },
                            "required": ["cells"]
                          }
                        }
                      },
                      "required": ["type", "text", "items", "rows"]
                    }
                  }
                },
                "required": ["heading", "blocks"]
              }
            }
          },
          "required": ["title", "summary", "sections"]
        }
        """).RootElement.Clone(),
        "A bounded canonical document draft for Taslim Document Studio.");
}
