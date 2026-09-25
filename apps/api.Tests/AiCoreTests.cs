using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class AiCoreTests
{
    [Theory]
    [InlineData("Fast", "gpt-5.6-luna")]
    [InlineData("Smart", "gpt-5.6-terra")]
    [InlineData("Advanced", "gpt-5.6-sol")]
    public void Router_selects_configured_internal_model_for_each_tier(string tier, string modelKey)
    {
        var options = Options.Create(new AiOptions { DefaultChatTier = "Smart", AllowMockProvider = false, OpenAI = new OpenAiOptions { Enabled = true, ApiKey = "test-only" } });
        var router = new AiModelRouter(options, ConfiguredCatalog());
        var selection = router.Select(new AiChatRequest([], "system", tier));
        Assert.Equal("openai", selection.ProviderKey);
        Assert.Equal(modelKey, selection.ModelKey);
        Assert.Equal(tier, selection.Tier);
    }

    [Fact]
    public void Router_rejects_when_production_has_no_real_provider()
    {
        var options = Options.Create(new AiOptions { AllowMockProvider = false, OpenAI = new OpenAiOptions { Enabled = false } });
        var router = new AiModelRouter(options, ConfiguredCatalog());
        Assert.Throws<AiProviderUnavailableException>(() => router.Select(new AiChatRequest([], "system", "Smart")));
    }

    [Fact]
    public void Router_rejects_structured_request_when_selected_tier_lacks_capability()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:Models:gpt-5.6-terra:ProviderKey"] = "openai",
            ["Ai:Models:gpt-5.6-terra:CapabilityTier"] = "Smart",
            ["Ai:Models:gpt-5.6-terra:SupportsStructuredOutput"] = "false",
        }).Build();
        var options = Options.Create(new AiOptions { DefaultChatTier = "Smart", AllowMockProvider = false, OpenAI = new OpenAiOptions { Enabled = true, ApiKey = "test-only" } });
        var router = new AiModelRouter(options, new AiModelCatalog(configuration));
        var schema = System.Text.Json.JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone();

        Assert.Throws<AiProviderUnavailableException>(() => router.Select(new AiChatRequest([], "system", "Smart", StructuredOutput: new AiStructuredOutputSpec("test", schema))));
    }

    [Fact]
    public async Task Completion_service_calculates_catalog_cost_and_preserves_usage()
    {
        var options = Options.Create(new AiOptions { AllowMockProvider = false, OpenAI = new OpenAiOptions { Enabled = true, ApiKey = "test-only" } });
        var catalog = ConfiguredCatalog();
        var router = new AiModelRouter(options, catalog);
        var completion = new ChatCompletionService(router, [new FakeProvider()], new AiCostCalculator(catalog), NullLogger<ChatCompletionService>.Instance);
        var result = await completion.CompleteAsync(new AiChatRequest([new("user", "hello")], "system", "Smart"));
        Assert.Equal("hello world", result.Content);
        Assert.Equal(1000, result.Usage.InputTokens);
        Assert.Equal(200, result.Usage.CachedInputTokens);
        Assert.Equal(1000, result.Usage.OutputTokens);
        Assert.Equal(0.01364m, result.Usage.EstimatedCost);
    }

    [Fact]
    public void Cost_calculator_caps_cached_tokens_at_input_tokens()
    {
        var catalog = ConfiguredCatalog();
        var calculator = new AiCostCalculator(catalog);
        var cost = calculator.Calculate(new AiUsageMetadata("openai", "gpt-5.6-terra", 1000, 1500, 1000, null, null, 0, "completed", false));
        Assert.Equal(0.0122m, cost);
    }

    [Fact]
    public void Context_builder_prioritizes_latest_turns_within_budget()
    {
        var builder = new AiContextBuilder(Options.Create(new AiOptions { ContextBudgetTokens = 256, DefaultChatTier = "Smart", SystemInstruction = "Taslim instruction" }));
        var history = Enumerable.Range(0, 8).Select(index => new AiChatMessage(index % 2 == 0 ? "user" : "assistant", $"message-{index}-" + new string('x', 140)));
        var context = builder.Build(history);
        Assert.Equal("Taslim instruction", context.SystemInstruction);
        Assert.Equal("message-7-" + new string('x', 140), context.Messages[^1].Content);
        Assert.DoesNotContain(context.Messages, item => item.Content.StartsWith("message-0-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Completion_service_exposes_provider_independent_stream_events()
    {
        var options = Options.Create(new AiOptions { AllowMockProvider = false, OpenAI = new OpenAiOptions { Enabled = true, ApiKey = "test-only" } });
        var catalog = new AiModelCatalog(new ConfigurationBuilder().Build());
        var completion = new ChatCompletionService(new AiModelRouter(options, catalog), [new FakeProvider()], new AiCostCalculator(catalog), NullLogger<ChatCompletionService>.Instance);
        var events = new List<AiStreamEvent>();
        await foreach (var item in completion.StreamAsync(new AiChatRequest([new("user", "hello")], "system", "Smart"))) events.Add(item);
        Assert.Collection(events,
            item => Assert.Equal("hello ", ((AiMessageDelta)item).Delta),
            item => Assert.Equal("world", ((AiMessageDelta)item).Delta),
            item => Assert.Equal("completed", ((AiMessageCompleted)item).Usage.FinishReason));
    }

    private sealed class FakeProvider : IAiProvider
    {
        public string Key => "openai";

        public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiChatRequest request, AiProviderSelection selection, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new AiMessageDelta("hello ");
            yield return new AiMessageDelta("world");
            yield return new AiMessageCompleted(new AiUsageMetadata(selection.ProviderKey, selection.ModelKey, 1000, 200, 1000, null, null, 4, "completed", false));
        }
    }

    private static AiModelCatalog ConfiguredCatalog() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Ai:Models:gpt-5.6-luna:InputPricePerMillion"] = "0.2",
        ["Ai:Models:gpt-5.6-luna:CachedInputPricePerMillion"] = "0.02",
        ["Ai:Models:gpt-5.6-luna:OutputPricePerMillion"] = "1.2",
        ["Ai:Models:gpt-5.6-terra:InputPricePerMillion"] = "2",
        ["Ai:Models:gpt-5.6-terra:CachedInputPricePerMillion"] = "0.2",
        ["Ai:Models:gpt-5.6-terra:OutputPricePerMillion"] = "12",
        ["Ai:Models:gpt-5.6-sol:InputPricePerMillion"] = "4",
        ["Ai:Models:gpt-5.6-sol:CachedInputPricePerMillion"] = "0.4",
        ["Ai:Models:gpt-5.6-sol:OutputPricePerMillion"] = "20",
    }).Build());
}
