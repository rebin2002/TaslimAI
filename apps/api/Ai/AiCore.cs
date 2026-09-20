using Microsoft.Extensions.Options;

namespace Taslim.Api.Ai;

public sealed record AiChatMessage(string Role, string Content);
public sealed record AiMemoryContext(string Category, string Title, string Content);
public sealed record AiFileContext(string FileName, string ContentType, string? ExtractedText, string? DataUrl);

public sealed record AiChatRequest(
    IReadOnlyList<AiChatMessage> Messages,
    string SystemInstruction,
    string RequestedTier,
    bool EnableStreaming = false,
    IReadOnlyList<AiFileContext>? Attachments = null);

public sealed record AiProviderSelection(
    string ProviderKey,
    string ModelKey,
    string ModelName,
    string Tier,
    bool IsTestProvider);

public sealed record AiUsageMetadata(
    string ProviderKey,
    string ModelKey,
    int? InputTokens,
    int? CachedInputTokens,
    int? OutputTokens,
    decimal? EstimatedCost,
    decimal? ActualCost,
    int LatencyMs,
    string FinishReason,
    bool IsTestResponse);

public sealed record AiGenerationResult(string Content, AiUsageMetadata Usage);

public abstract record AiStreamEvent;
public sealed record AiMessageDelta(string Delta) : AiStreamEvent;
public sealed record AiMessageCompleted(AiUsageMetadata Usage) : AiStreamEvent;

public interface IAiProvider
{
    string Key { get; }
    IAsyncEnumerable<AiStreamEvent> StreamAsync(
        AiChatRequest request,
        AiProviderSelection selection,
        CancellationToken cancellationToken = default);
}

public interface IAiModelRouter
{
    AiProviderSelection Select(AiChatRequest request);
}

public interface IChatCompletionService
{
    IAsyncEnumerable<AiStreamEvent> StreamAsync(
        AiChatRequest request,
        CancellationToken cancellationToken = default);

    Task<AiGenerationResult> CompleteAsync(
        AiChatRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class AiModelRouter(
    IOptions<AiOptions> options,
    AiModelCatalog catalog) : IAiModelRouter
{
    private readonly AiOptions settings = options.Value;

    public AiProviderSelection Select(AiChatRequest request)
    {
        var tier = NormalizeTier(request.RequestedTier, settings.DefaultChatTier);
        var providerKey = settings.OpenAI.Enabled ? "openai" : settings.AllowMockProvider ? "mock" : throw new AiProviderUnavailableException();
        var model = catalog.GetForTier(tier, providerKey)
            ?? throw new AiProviderUnavailableException();
        if (request.Attachments?.Any(attachment => !string.IsNullOrWhiteSpace(attachment.DataUrl)) == true && !model.SupportsVision)
        {
            model = catalog.All.FirstOrDefault(candidate => candidate.ProviderKey.Equals(providerKey, StringComparison.OrdinalIgnoreCase) && candidate.Enabled && candidate.SupportsVision)
                ?? throw new AiProviderUnavailableException();
        }

        return new AiProviderSelection(providerKey, model.ModelKey, model.DisplayName, model.CapabilityTier, providerKey == "mock");
    }

    private static string NormalizeTier(string? requested, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(requested) ? fallback : requested;
        return value.Trim().ToLowerInvariant() switch
        {
            "fast" => "Fast",
            "advanced" => "Advanced",
            _ => "Smart",
        };
    }
}

public sealed class ChatCompletionService(
    IAiModelRouter router,
    IEnumerable<IAiProvider> providers,
    IAiCostCalculator costCalculator,
    ILogger<ChatCompletionService> logger) : IChatCompletionService
{
    public async IAsyncEnumerable<AiStreamEvent> StreamAsync(
        AiChatRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var selection = router.Select(request);
        var provider = providers.FirstOrDefault(item => string.Equals(item.Key, selection.ProviderKey, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            logger.LogError("AI provider selection failed. ProviderKey={ProviderKey}; ModelKey={ModelKey}", selection.ProviderKey, selection.ModelKey);
            throw new AiProviderUnavailableException();
        }

        await foreach (var item in provider.StreamAsync(request, selection, cancellationToken))
        {
            if (item is AiMessageCompleted completed)
            {
                yield return completed with { Usage = EnrichUsage(completed.Usage, selection) };
            }
            else
            {
                yield return item;
            }
        }
    }

    public async Task<AiGenerationResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default)
    {
        var content = new System.Text.StringBuilder();
        AiUsageMetadata? usage = null;
        await foreach (var item in StreamAsync(request, cancellationToken))
        {
            switch (item)
            {
                case AiMessageDelta delta:
                    content.Append(delta.Delta);
                    break;
                case AiMessageCompleted completed:
                    usage = completed.Usage;
                    break;
            }
        }

        if (usage is null) throw new AiGenerationException("AI response did not complete.");
        return new AiGenerationResult(content.ToString(), usage);
    }

    private AiUsageMetadata EnrichUsage(AiUsageMetadata usage, AiProviderSelection selection)
    {
        var estimated = costCalculator.Calculate(usage with
        {
            ProviderKey = selection.ProviderKey,
            ModelKey = selection.ModelKey,
        });
        return usage with
        {
            ProviderKey = selection.ProviderKey,
            ModelKey = selection.ModelKey,
            EstimatedCost = estimated,
            ActualCost = estimated,
        };
    }
}

public sealed class AiContextBuilder(IOptions<AiOptions> options)
{
    private readonly AiOptions settings = options.Value;

    public AiChatRequest Build(IEnumerable<AiChatMessage> history)
        => Build(history, null, null, []);

    public AiChatRequest Build(
        IEnumerable<AiChatMessage> history,
        string? projectInstructions,
        string? projectContextNotes,
        IReadOnlyList<AiMemoryContext> personalMemories,
        IReadOnlyList<AiFileContext>? files = null)
    {
        var messages = history.ToList();
        var budget = Math.Max(256, settings.ContextBudgetTokens);
        var systemInstruction = BuildSystemInstruction(projectInstructions, projectContextNotes, personalMemories, files ?? []);
        var historyBudget = Math.Max(256, budget - EstimateTokens(systemInstruction) - Math.Max(0, settings.ContextOutputReserveTokens));
        var selected = new List<AiChatMessage>();
        var used = 0;

        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var message = messages[index];
            var tokens = EstimateTokens(message.Content);
            if (selected.Count > 0 && used + tokens > historyBudget) break;
            selected.Add(message);
            used += tokens;
        }

        selected.Reverse();
        return new AiChatRequest(selected, systemInstruction, settings.DefaultChatTier, EnableStreaming: true);
    }

    private string BuildSystemInstruction(string? projectInstructions, string? projectContextNotes, IReadOnlyList<AiMemoryContext> personalMemories, IReadOnlyList<AiFileContext> files)
    {
        var instruction = settings.SystemInstruction;
        var projectLines = new List<string>();
        if (!string.IsNullOrWhiteSpace(projectInstructions)) projectLines.Add($"Instructions: {projectInstructions.Trim()}");
        if (!string.IsNullOrWhiteSpace(projectContextNotes)) projectLines.Add($"Notes: {projectContextNotes.Trim()}");
        var projectSection = TrimToTokens(string.Join('\n', projectLines), Math.Max(0, settings.ProjectContextBudgetTokens));
        if (!string.IsNullOrWhiteSpace(projectSection)) instruction += $"\n\nProject-specific context (use only for this project):\n{projectSection}";

        var memoryLines = new List<string>();
        var memoryTokens = 0;
        foreach (var memory in personalMemories.Take(Math.Max(0, settings.MaxPersonalMemories)))
        {
            if (string.IsNullOrWhiteSpace(memory.Content)) continue;
            var line = $"- [{memory.Category}] {memory.Title}: {memory.Content.Trim()}";
            var lineTokens = EstimateTokens(line);
            if (memoryLines.Count > 0 && memoryTokens + lineTokens > settings.PersonalMemoryContextBudgetTokens) break;
            memoryLines.Add(TrimToTokens(line, Math.Max(1, settings.PersonalMemoryContextBudgetTokens - memoryTokens)));
            memoryTokens += EstimateTokens(memoryLines[^1]);
        }
        var memorySection = string.Join('\n', memoryLines);
        if (!string.IsNullOrWhiteSpace(memorySection)) instruction += $"\n\nPersonal memory (user-approved and reusable in this workspace):\n{memorySection}";
        var fileLines = new List<string>();
        var fileTokens = 0;
        foreach (var file in files.Where(file => !string.IsNullOrWhiteSpace(file.ExtractedText)))
        {
            var section = $"File: {file.FileName}\n{file.ExtractedText!.Trim()}";
            var remaining = Math.Max(1, settings.FileContextBudgetTokens - fileTokens);
            var bounded = TrimToTokens(section, remaining);
            if (string.IsNullOrWhiteSpace(bounded)) break;
            fileLines.Add(bounded);
            fileTokens += EstimateTokens(bounded);
            if (fileTokens >= settings.FileContextBudgetTokens) break;
        }
        if (fileLines.Count > 0) instruction += $"\n\nAttached file context (keep each file's boundary clear):\n{string.Join("\n\n", fileLines)}";
        return instruction;
    }

    private static string TrimToTokens(string value, int tokenBudget)
    {
        if (string.IsNullOrWhiteSpace(value) || tokenBudget <= 0) return string.Empty;
        var maxCharacters = Math.Max(4, tokenBudget * 4);
        return value.Length <= maxCharacters ? value : value[..(maxCharacters - 3)].TrimEnd() + "...";
    }

    private static int EstimateTokens(string content) => Math.Max(1, (content.Length + 3) / 4);
}

public sealed class AiProviderUnavailableException() : Exception("No configured AI provider is available.");
public sealed class AiGenerationException(string message) : Exception(message);
