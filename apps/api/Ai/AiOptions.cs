namespace Taslim.Api.Ai;

public sealed class AiOptions
{
    public string DefaultChatTier { get; set; } = "Smart";
    public int ContextBudgetTokens { get; set; } = 12_000;
    public int ContextOutputReserveTokens { get; set; } = 2_048;
    public int ProjectContextBudgetTokens { get; set; } = 1_200;
    public int PersonalMemoryContextBudgetTokens { get; set; } = 1_200;
    public int MaxPersonalMemories { get; set; } = 50;
    public int ProviderTimeoutSeconds { get; set; } = 90;
    public bool AllowMockProvider { get; set; } = true;
    public string SystemInstruction { get; set; } = "You are Taslim, a helpful multilingual AI assistant. Provide clear, accurate and useful answers. Respond naturally in the user's language unless they request another language.";
    public OpenAiOptions OpenAI { get; set; } = new();
    public Dictionary<string, AiModelDefinitionOptions> Models { get; set; } = new();
}

public sealed class OpenAiOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
}

public sealed class AiModelDefinitionOptions
{
    public string ProviderKey { get; set; } = "openai";
    public string DisplayName { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool SupportsStreaming { get; set; } = true;
    public bool SupportsVision { get; set; }
    public bool SupportsTools { get; set; }
    public int ContextWindow { get; set; } = 128_000;
    public decimal InputPricePerMillion { get; set; }
    public decimal CachedInputPricePerMillion { get; set; }
    public decimal OutputPricePerMillion { get; set; }
    public string CostTier { get; set; } = "standard";
    public string CapabilityTier { get; set; } = "Smart";
}

public sealed record AiModelDefinition(
    string ProviderKey,
    string ModelKey,
    string DisplayName,
    bool Enabled,
    bool SupportsStreaming,
    bool SupportsVision,
    bool SupportsTools,
    int ContextWindow,
    decimal InputPricePerMillion,
    decimal CachedInputPricePerMillion,
    decimal OutputPricePerMillion,
    string CostTier,
    string CapabilityTier);

public sealed class AiModelCatalog(IConfiguration configuration)
{
    private static readonly IReadOnlyDictionary<string, AiModelDefinition> Defaults = new Dictionary<string, AiModelDefinition>(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-5.6-luna"] = new("openai", "gpt-5.6-luna", "Taslim Fast", true, true, false, false, 128_000, 0.20m, 0.02m, 1.20m, "low", "Fast"),
        ["gpt-5.6-terra"] = new("openai", "gpt-5.6-terra", "Taslim Smart", true, true, false, false, 128_000, 2.00m, 0.20m, 12.00m, "standard", "Smart"),
        ["gpt-5.6-sol"] = new("openai", "gpt-5.6-sol", "Taslim Advanced", true, true, true, true, 128_000, 4.00m, 0.40m, 20.00m, "high", "Advanced"),
        ["mock"] = new("mock", "taslim-mock-chat", "Taslim Development", true, true, false, false, 64_000, 0m, 0m, 0m, "test", "Smart"),
    };

    private readonly IReadOnlyList<AiModelDefinition> models = Load(configuration);

    public IReadOnlyList<AiModelDefinition> All => models;

    public AiModelDefinition? Find(string modelKey) => models.FirstOrDefault(model => string.Equals(model.ModelKey, modelKey, StringComparison.OrdinalIgnoreCase));

    public AiModelDefinition? GetForTier(string tier, string providerKey) => models.FirstOrDefault(model =>
        model.Enabled && string.Equals(model.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase) && string.Equals(model.CapabilityTier, tier, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<AiModelDefinition> Load(IConfiguration configuration)
    {
        var configured = configuration.GetSection("Ai:Models").Get<Dictionary<string, AiModelDefinitionOptions>>() ?? new();
        var result = new Dictionary<string, AiModelDefinition>(Defaults, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in configured)
        {
            var options = pair.Value;
            result[pair.Key] = new(
                options.ProviderKey,
                pair.Key,
                string.IsNullOrWhiteSpace(options.DisplayName) ? pair.Key : options.DisplayName,
                options.Enabled,
                options.SupportsStreaming,
                options.SupportsVision,
                options.SupportsTools,
                options.ContextWindow,
                options.InputPricePerMillion,
                options.CachedInputPricePerMillion,
                options.OutputPricePerMillion,
                options.CostTier,
                options.CapabilityTier);
        }
        return result.Values.ToList();
    }
}
