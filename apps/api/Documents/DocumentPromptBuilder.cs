using System.Text;
using Taslim.Api.Contracts;

namespace Taslim.Api.Documents;

public sealed record DocumentSourceContext(string FileName, string Extension, string Text);
public sealed record DocumentProjectContext(string? Name, string? Instructions, string? ContextNotes);
public sealed record DocumentGenerationPrompt(string SystemInstruction, string UserInstruction, string Language);

public interface IDocumentPromptBuilder
{
    DocumentGenerationPrompt Build(DocumentGenerationInput input, DocumentProjectContext? project, IReadOnlyList<DocumentSourceContext> sources, DocumentGenerationOptions options);
}

public sealed class DocumentPromptBuilder : IDocumentPromptBuilder
{
    public DocumentGenerationPrompt Build(DocumentGenerationInput input, DocumentProjectContext? project, IReadOnlyList<DocumentSourceContext> sources, DocumentGenerationOptions options)
    {
        var language = input.Language == "auto" ? "the source language unless the user requests another language" : input.Language;
        var system = """
You are Taslim Document Studio. Create a coherent document draft from the supplied source material and instructions.
Return only valid JSON; do not include Markdown fences, commentary, provider names, model names, or internal metadata.
The JSON schema is: {"title":"string","summary":"string","sections":[{"heading":"string","blocks":[{"type":"paragraph|heading|bullet_list|numbered_list|table","text":"string?","items":["string"],"rows":[{"cells":["string"]}]}]}]}.
""";
        system += $"\nUse {language}. Preserve important facts, qualify uncertainty, do not invent citations or personal details, and do not reproduce secrets or credentials.";
        var builder = new StringBuilder();
        builder.AppendLine($"Document title: {input.Title}");
        builder.AppendLine($"Document type: {input.DocumentType}");
        builder.AppendLine($"Requested length: {input.Length}");
        builder.AppendLine($"User description: {input.Description}");
        builder.AppendLine($"Tone: {input.Tone}");
        builder.AppendLine($"Include table of contents: {input.IncludeTableOfContents}");
        if (!string.IsNullOrWhiteSpace(input.Audience)) builder.AppendLine($"Audience: {input.Audience}");
        if (!string.IsNullOrWhiteSpace(input.AdditionalInstructions)) builder.AppendLine($"Additional instructions: {input.AdditionalInstructions}");
        if (project is not null)
        {
            if (!string.IsNullOrWhiteSpace(project.Name)) builder.AppendLine($"Project name: {project.Name}");
            if (!string.IsNullOrWhiteSpace(project.Instructions)) builder.AppendLine($"Project instructions: {project.Instructions}");
            if (!string.IsNullOrWhiteSpace(project.ContextNotes)) builder.AppendLine($"Project context notes: {project.ContextNotes}");
        }
        builder.AppendLine("Source documents:");
        foreach (var source in sources)
        {
            builder.AppendLine($"--- {source.FileName} ({source.Extension}) ---");
            builder.AppendLine(source.Text);
        }
        var user = builder.ToString();
        if (user.Length > options.MaxContextCharacters)
            throw new DocumentContextLimitException();
        return new DocumentGenerationPrompt(system, user, language);
    }
}

public sealed class DocumentContextLimitException : Exception;
