using System.Text;
using Taslim.Api.Contracts;

namespace Taslim.Api.Images;

public interface IImagePromptBuilder
{
    ImagePromptBuildResult Build(ImageGenerationInput request);
}

public sealed class TaslimImagePromptBuilder : IImagePromptBuilder
{
    private static readonly IReadOnlyDictionary<string, string> StyleInstructions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [ImageGenerationValues.Auto] = "Choose a treatment that best serves the described subject and intended use.",
        [ImageGenerationValues.Photorealistic] = "Use a credible photorealistic treatment with natural materials, lighting, and detail.",
        [ImageGenerationValues.Product] = "Create a polished product-focused composition with clear form, believable materials, and professional presentation.",
        [ImageGenerationValues.Illustration] = "Use a refined illustration treatment with a clear silhouette, intentional shapes, and a coherent visual language.",
        [ImageGenerationValues.ThreeD] = "Use a polished 3D-rendered treatment with believable geometry, materials, lighting, and depth.",
        [ImageGenerationValues.Minimal] = "Use a restrained minimal composition with generous negative space and only necessary visual elements.",
        [ImageGenerationValues.Poster] = "Use a poster composition with strong hierarchy, a clear focal point, and deliberate space for requested text.",
        [ImageGenerationValues.SocialMedia] = "Use an attention-holding social-media composition that reads clearly at a glance and remains visually balanced.",
    };

    private static readonly IReadOnlyDictionary<string, string> AspectInstructions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [ImageGenerationValues.Square] = "Compose for a square 1:1 canvas.",
        [ImageGenerationValues.Portrait] = "Compose for a portrait 4:5 canvas with a comfortable vertical focal area.",
        [ImageGenerationValues.Landscape] = "Compose for a landscape 16:9 canvas with a comfortable horizontal focal area.",
    };

    private static readonly IReadOnlyDictionary<string, string> QualityInstructions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [ImageGenerationValues.Standard] = "Use a balanced level of detail suitable for a fast professional draft.",
        [ImageGenerationValues.High] = "Prioritize refined detail, legible requested text, coherent lighting, and professional finish.",
    };

    public ImagePromptBuildResult Build(ImageGenerationInput request)
    {
        var style = Normalize(request.Style, ImageGenerationValues.Styles, ImageGenerationValues.Auto, "style");
        var aspect = Normalize(request.AspectRatio, ImageGenerationValues.AspectRatios, ImageGenerationValues.Square, "aspect ratio");
        var quality = Normalize(request.Quality, ImageGenerationValues.Qualities, ImageGenerationValues.Standard, "quality");
        var description = Inline(request.Description);
        var lines = new List<string>
        {
            "Create one finished image for the user's stated purpose.",
            $"Subject and creative intent: {description}",
            StyleInstructions[style],
            AspectInstructions[aspect],
            QualityInstructions[quality],
            "Preserve named products, people, proper nouns, factual requirements, and exact creative constraints from the description.",
            "Do not add unrelated personal information, claims, logos, watermarks, or extra copy.",
        };
        if (!string.IsNullOrWhiteSpace(request.Mood)) lines.Add($"Mood: {Inline(request.Mood)}.");
        if (!string.IsNullOrWhiteSpace(request.Background)) lines.Add($"Background direction: {Inline(request.Background)}.");
        if (!string.IsNullOrWhiteSpace(request.TextInImage))
        {
            lines.Add($"Render exactly this requested text inside the image: \"{Inline(request.TextInImage)}\".");
            lines.Add("Use no additional readable text and keep the requested wording spelled exactly as provided.");
        }
        else
        {
            lines.Add("Do not render readable text inside the image unless it is essential to the described subject.");
        }
        return new ImagePromptBuildResult(string.Join("\n", lines), style, aspect, quality, request.Mood, request.Background, request.TextInImage);
    }

    // The prompt is a line-structured instruction block joined with newlines.
    // A line break carried inside user text would let a request append,
    // displace, or overwrite the fixed instruction lines, so every
    // line-break-capable character in user-derived fields is folded to a
    // single space here. Tabs are preserved so requested on-image text stays
    // spelled exactly as provided, and the stored job input is untouched.
    private static string Inline(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character == '\t' || (!char.IsControl(character) && character is not '\u2028' and not '\u2029'))
            {
                builder.Append(character);
                continue;
            }
            if (builder.Length == 0 || builder[^1] != ' ')
            {
                builder.Append(' ');
            }
        }
        return builder.ToString().Trim();
    }

    private static string Normalize(string value, IReadOnlySet<string> supported, string fallback, string field)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length == 0) return fallback;
        if (!supported.Contains(normalized)) throw new ImageRequestValidationException($"IMAGE_{field.Replace(" ", "_").ToUpperInvariant()}_UNSUPPORTED", $"The selected {field} is not supported.");
        return normalized;
    }
}

public sealed class ImageRequestValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
