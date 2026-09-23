using System.Text;
using Taslim.Api.Contracts;

namespace Taslim.Api.Social;

public interface ISocialPromptBuilder
{
    SocialGenerationPrompt Build(SocialGenerationInput input, SocialProjectContext? project, IReadOnlyList<SocialSourceContext> sources, SocialGenerationOptions options);
}

public sealed class SocialPromptBuilder : ISocialPromptBuilder
{
    public SocialGenerationPrompt Build(SocialGenerationInput input, SocialProjectContext? project, IReadOnlyList<SocialSourceContext> sources, SocialGenerationOptions options)
    {
        var context = new StringBuilder();
        foreach (var source in sources)
        {
            context.AppendLine($"[Selected {source.Kind}] {source.Name}{(string.IsNullOrWhiteSpace(source.AssetType) ? string.Empty : $" | type={source.AssetType}")}{(string.IsNullOrWhiteSpace(source.ContentType) ? string.Empty : $" | contentType={source.ContentType}")}");
            if (!string.IsNullOrWhiteSpace(source.Content))
            {
                context.AppendLine("Content:");
                context.AppendLine(source.Content);
            }
            context.AppendLine();
        }
        if (project is not null)
        {
            context.AppendLine($"[Authorized project context] {project.Name}");
            if (!string.IsNullOrWhiteSpace(project.Instructions)) context.AppendLine($"Instructions: {project.Instructions}");
            if (!string.IsNullOrWhiteSpace(project.ContextNotes)) context.AppendLine($"Notes: {project.ContextNotes}");
        }
        if (context.Length > options.MaxContextCharacters) throw new SocialContextLimitException();

        var system = "You are Taslim Social Studio. Return only the required JSON social draft. Create useful, platform-aware social copy in the requested language. Do not invent factual claims, prices, dates, metrics, testimonials, guarantees, or product capabilities. If the selected context does not support a fact, write broadly or mark it for review. Keep the copy natural and ready for a human to review. Never return HTML, XML, markdown tables, or provider details. Asset references must use selected asset names only, never IDs.";
        var user = $"Create social content. Platform: {input.Platform}. Content type: {input.SocialType}. Tone: {input.Tone}. Requested language: {input.Language}. Audience: {input.Audience ?? "general audience"}. Brand voice: {input.BrandVoice ?? "clear and approachable"}. Call to action: {input.CallToAction ?? "use your judgment"}. Include hashtags: {input.IncludeHashtags}. Include emojis: {input.IncludeEmojis}. Generate variants: {input.GenerateVariants}. Brief: {input.Prompt}\n\n{context}\n\nReturn { (input.GenerateVariants ? "a small set of distinct post variants" : "one polished post") } with a strong hook, body, optional CTA, optional hashtags, alt text when useful, visual direction, and selected asset names when relevant. Preserve the requested language throughout.";
        return new SocialGenerationPrompt(input, project, sources, system, user);
    }
}

public sealed class SocialContextLimitException : Exception;
