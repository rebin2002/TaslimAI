using System.Text;
using Taslim.Api.Contracts;

namespace Taslim.Api.Presentations;

public sealed record PresentationSourceContext(string FileName, string Extension, string Text);
public sealed record PresentationProjectContext(string? Name, string? Instructions, string? ContextNotes);
public sealed record PresentationGenerationPrompt(string SystemInstruction, string UserInstruction, string Language);

public interface IPresentationPromptBuilder
{
    PresentationGenerationPrompt Build(PresentationGenerationInput input, PresentationProjectContext? project, IReadOnlyList<PresentationSourceContext> sources, PresentationGenerationOptions options);
}

public sealed class PresentationPromptBuilder : IPresentationPromptBuilder
{
    public PresentationGenerationPrompt Build(PresentationGenerationInput input, PresentationProjectContext? project, IReadOnlyList<PresentationSourceContext> sources, PresentationGenerationOptions options)
    {
        var language = input.Language == "auto" ? "the source language unless the user requests another language" : input.Language;
        var system = $"""
You are Taslim Presentation Studio. Create a concise, factual, professional slide deck from the supplied instructions and explicitly selected source material.
Return only valid JSON with no Markdown fences, commentary, provider names, model names, storage details, pricing, or internal metadata.
Use the exact canonical schema requested by the caller. Slides must be ordered starting at 1. Supported slide types are: {string.Join(", ", PresentationSlideTypes.Supported.OrderBy(value => value))}.
Supported content block types are: {string.Join(", ", PresentationBlockTypes.Supported.OrderBy(value => value))}.
Use editable-friendly text, bullets, columns, tables, metrics, timelines, processes, and quotations. Do not output HTML, SVG, arbitrary markup, image URLs, or invented numerical data.
Preserve source facts, identify uncertainty in notes when needed, and never invent citations, people, credentials, or confidential information.
""";
        system += $"\nWrite in {language}. Keep the deck within the requested length and make each slide understandable without hidden context.";

        var builder = new StringBuilder();
        builder.AppendLine($"Presentation title: {input.Title}");
        builder.AppendLine($"What to present: {input.Description}");
        builder.AppendLine($"Presentation type: {input.PresentationType}");
        builder.AppendLine($"Requested length: {input.Length}");
        builder.AppendLine($"Tone: {input.Tone}");
        builder.AppendLine($"Include agenda: {input.IncludeAgenda}");
        builder.AppendLine($"Include closing and next steps: {input.IncludeClosingNextSteps}");
        if (!string.IsNullOrWhiteSpace(input.Audience)) builder.AppendLine($"Audience: {input.Audience}");
        if (!string.IsNullOrWhiteSpace(input.BrandCompany)) builder.AppendLine($"Brand or company name: {input.BrandCompany}");
        if (!string.IsNullOrWhiteSpace(input.AdditionalInstructions)) builder.AppendLine($"Additional instructions: {input.AdditionalInstructions}");
        if (project is not null)
        {
            if (!string.IsNullOrWhiteSpace(project.Name)) builder.AppendLine($"Project name: {project.Name}");
            if (!string.IsNullOrWhiteSpace(project.Instructions)) builder.AppendLine($"Project instructions: {project.Instructions}");
            if (!string.IsNullOrWhiteSpace(project.ContextNotes)) builder.AppendLine($"Project context notes: {project.ContextNotes}");
        }
        builder.AppendLine("Explicitly selected source documents (use only these sources):");
        if (sources.Count == 0) builder.AppendLine("[No source documents selected. Do not imply that sources were reviewed.]");
        foreach (var source in sources)
        {
            builder.AppendLine($"--- {source.FileName} ({source.Extension}) ---");
            builder.AppendLine(source.Text);
            builder.AppendLine("--- end source ---");
        }
        var user = builder.ToString();
        if (user.Length > options.MaxContextCharacters) throw new PresentationContextLimitException();
        return new PresentationGenerationPrompt(system, user, language);
    }
}

public sealed class PresentationContextLimitException : Exception;
