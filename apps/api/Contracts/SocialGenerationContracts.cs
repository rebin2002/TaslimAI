using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Taslim.Api.Domain;
using Taslim.Api.Social;

namespace Taslim.Api.Contracts;

public sealed class SocialGenerationRequest
{
    [Required] public Guid WorkspaceId { get; set; }
    public Guid? ProjectId { get; set; }
    [StringLength(6_000, MinimumLength = 3)] public string? Prompt { get; set; }
    [StringLength(40)] public string? SocialType { get; set; }
    [StringLength(40)] public string? Platform { get; set; }
    [StringLength(40)] public string? Tone { get; set; }
    [StringLength(20)] public string? Language { get; set; }
    [StringLength(400)] public string? Audience { get; set; }
    [StringLength(1_000)] public string? BrandVoice { get; set; }
    [StringLength(400)] public string? CallToAction { get; set; }
    public bool IncludeHashtags { get; set; } = true;
    public bool IncludeEmojis { get; set; }
    public bool GenerateVariants { get; set; }
    [MaxLength(8)] public IReadOnlyList<Guid> AssetIds { get; set; } = [];
    [MaxLength(5)] public IReadOnlyList<Guid> AttachmentIds { get; set; } = [];
}

public sealed record SocialGenerationInput(
    Guid WorkspaceId,
    Guid? ProjectId,
    string Prompt,
    string SocialType,
    string Platform,
    string Tone,
    string Language,
    string? Audience,
    string? BrandVoice,
    string? CallToAction,
    bool IncludeHashtags,
    bool IncludeEmojis,
    bool GenerateVariants,
    IReadOnlyList<Guid> AssetIds,
    IReadOnlyList<Guid> AttachmentIds);

public sealed class SocialDraft
{
    public string Title { get; set; } = string.Empty;
    public string Platform { get; set; } = "multi";
    public string SocialType { get; set; } = "general";
    public string Language { get; set; } = LanguageCodes.English;
    public List<SocialPost> Posts { get; set; } = [];
}

public sealed class SocialPost
{
    public int Order { get; set; }
    public string Hook { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? CallToAction { get; set; }
    public List<string> Hashtags { get; set; } = [];
    public string? AltText { get; set; }
    public string? VisualDirection { get; set; }
    public List<string> AssetRefs { get; set; } = [];
}

public sealed record SocialGenerationResult(Guid AssetId, string Title, string Platform, string Language, int PostCount, string SocialType);

public static class SocialGenerationContractMapper
{
    public static SocialGenerationInput ToInput(SocialGenerationRequest request) => new(
        request.WorkspaceId,
        request.ProjectId,
        NormalizeOptional(request.Prompt) ?? string.Empty,
        Normalize(request.SocialType, SocialGenerationDefaults.DefaultSocialType),
        Normalize(request.Platform, SocialGenerationDefaults.DefaultPlatform),
        Normalize(request.Tone, SocialGenerationDefaults.DefaultTone),
        Normalize(request.Language, SocialGenerationDefaults.DefaultLanguage),
        NormalizeOptional(request.Audience),
        NormalizeOptional(request.BrandVoice),
        NormalizeOptional(request.CallToAction),
        request.IncludeHashtags,
        request.IncludeEmojis,
        request.GenerateVariants,
        (request.AssetIds ?? []).Distinct().ToArray(),
        (request.AttachmentIds ?? []).Distinct().ToArray());

    public static string Normalize(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();
    public static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    public static string SerializeInput(SocialGenerationInput input) => JsonSerializer.Serialize(input);

    public static bool TryDeserializeInput(string json, out SocialGenerationInput? input)
    {
        try
        {
            input = JsonSerializer.Deserialize<SocialGenerationInput>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return input is not null;
        }
        catch (JsonException)
        {
            input = null;
            return false;
        }
    }
}

public static class SocialGenerationDefaults
{
    public const string DefaultLanguage = "auto";
    public const string DefaultTone = "professional";
    public const string DefaultSocialType = "general";
    public const string DefaultPlatform = "multi";
    public static readonly IReadOnlySet<string> Languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "auto", "en", "ar", "ku" };
    public static readonly IReadOnlySet<string> Tones = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "professional", "friendly", "persuasive", "educational", "playful", "concise", "thoughtful" };
    public static readonly IReadOnlySet<string> Platforms = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "instagram", "facebook", "linkedin", "x", "tiktok", "multi" };
    public static readonly IReadOnlySet<string> SocialTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "auto", "announcement", "product_launch", "promotion", "educational", "thought_leadership", "company_update", "event", "community", "general" };
    public static readonly IReadOnlySet<string> AttachmentExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pdf", ".docx", ".txt", ".md", ".csv", ".xlsx" };
}

public static class SocialGenerationRequestValidator
{
    public static void Validate(SocialGenerationInput input, SocialGenerationOptions options)
    {
        if (input.WorkspaceId == Guid.Empty) Invalid("WORKSPACE_REQUIRED", "A workspace is required.");
        if (input.Prompt.Length < 3 || input.Prompt.Length > options.MaxPromptCharacters) Invalid(GenerationJobErrorCodes.SocialRequestInvalid, $"Social prompt must be between 3 and {options.MaxPromptCharacters} characters.");
        if (input.AssetIds.Count > options.MaxAssetSelections) Invalid(GenerationJobErrorCodes.SocialRequestInvalid, $"Choose no more than {options.MaxAssetSelections} assets.");
        if (input.AttachmentIds.Count > options.MaxAttachmentSelections) Invalid(GenerationJobErrorCodes.SocialRequestInvalid, $"Choose no more than {options.MaxAttachmentSelections} source files.");
        if (!SocialGenerationDefaults.Languages.Contains(input.Language) || !SocialGenerationDefaults.Tones.Contains(input.Tone) || !SocialGenerationDefaults.Platforms.Contains(input.Platform) || !SocialGenerationDefaults.SocialTypes.Contains(input.SocialType))
            Invalid(GenerationJobErrorCodes.SocialRequestInvalid, "The selected social settings are not supported.");
        if (input.Audience?.Length > options.MaxAudienceCharacters || input.BrandVoice?.Length > options.MaxBrandVoiceCharacters || input.CallToAction?.Length > options.MaxCallToActionCharacters)
            Invalid(GenerationJobErrorCodes.SocialRequestInvalid, "Optional social guidance is too long.");

        static void Invalid(string code, string message) => throw new SocialRequestValidationException(code, message);
    }
}

public sealed class SocialRequestValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class SocialGenerationCostEstimator
{
    public static decimal? Estimate(SocialGenerationOptions options) => options.EstimatedOutputUsdPer1KTokens.HasValue
        ? Math.Round(Math.Max(0m, options.MaxOutputTokens / 1_000m * options.EstimatedOutputUsdPer1KTokens.Value), 8)
        : null;
}

public static class SocialDraftValidator
{
    public static void Validate(SocialDraft draft, SocialGenerationOptions options)
    {
        if (string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Length > options.MaxTitleCharacters || draft.Posts.Count < 1 || draft.Posts.Count > options.MaxPosts) throw new SocialOutputValidationException();
        if (!SocialGenerationDefaults.Languages.Contains(draft.Language) && !string.Equals(draft.Language, "auto", StringComparison.OrdinalIgnoreCase)) throw new SocialOutputValidationException();
        if (!SocialGenerationDefaults.Platforms.Contains(draft.Platform) || !SocialGenerationDefaults.SocialTypes.Contains(draft.SocialType)) throw new SocialOutputValidationException();
        var expectedOrder = 1;
        foreach (var post in draft.Posts)
        {
            if (post.Order != expectedOrder++ || string.IsNullOrWhiteSpace(post.Hook) || post.Hook.Length > options.MaxHookCharacters || string.IsNullOrWhiteSpace(post.Body) || post.Body.Length > options.MaxPostCharacters || post.CallToAction?.Length > options.MaxCallToActionCharacters || post.AltText?.Length > options.MaxAltTextCharacters || post.VisualDirection?.Length > options.MaxVisualDirectionCharacters || post.Hashtags.Count > options.MaxHashtags || post.AssetRefs.Count > options.MaxAssetRefs) throw new SocialOutputValidationException();
            if (post.Hashtags.Any(tag => tag.Length > options.MaxHashtagCharacters || tag.Contains(' ') || !tag.StartsWith('#'))) throw new SocialOutputValidationException();
            if (post.AssetRefs.Any(reference => reference.Length > options.MaxAssetRefCharacters)) throw new SocialOutputValidationException();
            foreach (var text in AllText(post)) if (text.Contains('<') || text.Contains('>')) throw new SocialOutputValidationException();
        }

        static IEnumerable<string> AllText(SocialPost post)
        {
            yield return post.Hook;
            yield return post.Body;
            if (post.CallToAction is not null) yield return post.CallToAction;
            if (post.AltText is not null) yield return post.AltText;
            if (post.VisualDirection is not null) yield return post.VisualDirection;
            foreach (var hashtag in post.Hashtags) yield return hashtag;
            foreach (var reference in post.AssetRefs) yield return reference;
        }
    }
}

public sealed class SocialOutputValidationException : Exception;
public sealed record SocialSourceContext(string Name, string Kind, string? Content, string? AssetType = null, string? ContentType = null);
public sealed record SocialProjectContext(string Name, string? Instructions, string? ContextNotes);
public sealed record SocialGenerationPrompt(SocialGenerationInput Input, SocialProjectContext? Project, IReadOnlyList<SocialSourceContext> Sources, string SystemInstruction, string UserInstruction);
