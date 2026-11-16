using System.Text.Json;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;

namespace Taslim.Api.Presentations;

public sealed record PresentationProviderResult(PresentationDraft Draft, AiUsageMetadata Usage);

public interface IPresentationGenerationProvider
{
    Task<PresentationProviderResult> GenerateAsync(PresentationGenerationPrompt prompt, PresentationGenerationOptions options, CancellationToken cancellationToken = default);
}

public sealed class AiPresentationGenerationProvider(IChatCompletionService completion) : IPresentationGenerationProvider
{
    public async Task<PresentationProviderResult> GenerateAsync(PresentationGenerationPrompt prompt, PresentationGenerationOptions options, CancellationToken cancellationToken = default)
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
                StructuredOutput: PresentationDraftStructuredOutput.Spec), timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiProviderTimeoutException();
        }
        if (string.IsNullOrWhiteSpace(result.Content))
            throw new PresentationGenerationStageException(PresentationGenerationStages.DraftParse, GenerationJobErrorCodes.PresentationOutputInvalid, "The presentation response was empty.", result.Usage);

        PresentationDraft draft;
        try
        {
            draft = JsonSerializer.Deserialize<PresentationDraft>(NormalizeStructuredJson(result.Content), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new JsonException("The presentation response was empty.");
        }
        catch (JsonException exception)
        {
            throw new PresentationGenerationStageException(PresentationGenerationStages.DraftParse, GenerationJobErrorCodes.PresentationOutputInvalid, "The presentation response could not be parsed.", result.Usage, exception);
        }
        try
        {
            PresentationDraftValidator.Validate(draft, options);
        }
        catch (PresentationOutputValidationException exception)
        {
            throw new PresentationGenerationStageException(PresentationGenerationStages.DraftValidation, GenerationJobErrorCodes.PresentationOutputInvalid, "The presentation response did not satisfy the required structure.", result.Usage, exception);
        }
        return new PresentationProviderResult(draft, result.Usage);
    }

    private static string NormalizeStructuredJson(string content)
    {
        var value = content.Trim();
        if (!value.StartsWith("```", StringComparison.Ordinal) || !value.EndsWith("```", StringComparison.Ordinal)) return value;
        var firstLineEnd = value.IndexOf('\n');
        if (firstLineEnd < 0 || !value[..firstLineEnd].Trim().Equals("```json", StringComparison.OrdinalIgnoreCase)) throw new JsonException("The presentation response contained an unsupported wrapper.");
        return value[(firstLineEnd + 1)..^3].Trim();
    }
}

public static class PresentationDraftStructuredOutput
{
    public static AiStructuredOutputSpec Spec { get; } = new(
        "taslim_presentation_draft",
        JsonDocument.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "title": { "type": "string" },
            "subtitle": { "type": ["string", "null"] },
            "language": { "type": "string" },
            "theme": { "type": "string" },
            "presentationType": { "type": "string" },
            "slides": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "order": { "type": "integer" },
                  "type": { "type": "string", "enum": ["title", "agenda", "section", "content", "bullets", "two_column", "comparison", "metrics", "table", "timeline", "process", "quote", "summary", "next_steps", "closing"] },
                  "title": { "type": "string" },
                  "subtitle": { "type": ["string", "null"] },
                  "blocks": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "additionalProperties": false,
                      "properties": {
                        "type": { "type": "string", "enum": ["text", "bullets", "columns", "table", "metrics", "timeline", "process", "quote"] },
                        "text": { "type": ["string", "null"] },
                        "items": { "type": "array", "items": { "type": "string" } },
                        "columns": {
                          "type": "array",
                          "items": {
                            "type": "object",
                            "additionalProperties": false,
                            "properties": { "heading": { "type": "string" }, "items": { "type": "array", "items": { "type": "string" } } },
                            "required": ["heading", "items"]
                          }
                        },
                        "rows": {
                          "type": "array",
                          "items": {
                            "type": "object",
                            "additionalProperties": false,
                            "properties": { "cells": { "type": "array", "items": { "type": "string" } } },
                            "required": ["cells"]
                          }
                        },
                        "metrics": {
                          "type": "array",
                          "items": {
                            "type": "object",
                            "additionalProperties": false,
                            "properties": { "label": { "type": "string" }, "value": { "type": "string" }, "detail": { "type": ["string", "null"] } },
                            "required": ["label", "value", "detail"]
                          }
                        },
                        "label": { "type": ["string", "null"] },
                        "value": { "type": ["string", "null"] }
                      },
                      "required": ["type", "text", "items", "columns", "rows", "metrics", "label", "value"]
                    }
                  },
                  "notes": { "type": ["string", "null"] },
                  "visualSuggestion": { "type": ["string", "null"] },
                  "sourceRefs": { "type": "array", "items": { "type": "string" } }
                },
                "required": ["order", "type", "title", "subtitle", "blocks", "notes", "visualSuggestion", "sourceRefs"]
              }
            }
          },
          "required": ["title", "subtitle", "language", "theme", "presentationType", "slides"]
        }
        """).RootElement.Clone(),
        "A bounded canonical editable Presentation Studio draft.");
}
