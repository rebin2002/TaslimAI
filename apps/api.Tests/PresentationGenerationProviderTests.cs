using System.Runtime.CompilerServices;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Presentations;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Deterministic, provider-free regression coverage for the Presentation Studio
/// cancellation boundary. Every completion below is synthetic: no live provider is
/// called and no private data is used.
/// </summary>
public sealed class PresentationGenerationProviderTests
{
    private const string ValidDraftJson = """
        {"title":"Launch plan","subtitle":"Quarterly review","language":"en","theme":"professional-navy","presentationType":"business","slides":[{"order":1,"type":"title","title":"Launch plan","subtitle":"Quarterly review","blocks":[],"notes":null,"visualSuggestion":null,"sourceRefs":[]}]}
        """;

    [Fact]
    public async Task Late_completion_after_cancellation_does_not_report_success()
    {
        using var cancellation = new CancellationTokenSource();
        var completion = new ScriptedCompletionService(ValidDraftJson, cancelCallerOnCall: cancellation);
        var provider = new AiPresentationGenerationProvider(completion);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GenerateAsync(Prompt(), Options(), cancellation.Token));

        Assert.Equal(1, completion.CallCount);
        Assert.True(cancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task Late_invalid_completion_after_cancellation_is_still_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var completion = new ScriptedCompletionService("{not json", cancelCallerOnCall: cancellation);
        var provider = new AiPresentationGenerationProvider(completion);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => provider.GenerateAsync(Prompt(), Options(), cancellation.Token));

        // A cancelled attempt must never be re-reported as a draft or provider
        // failure, otherwise the job is finalized as Failed instead of Cancelled.
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.IsNotType<PresentationGenerationStageException>(exception);
    }

    [Fact]
    public async Task Cancellation_is_observed_repeatedly_without_changing_outcome()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var completion = new ScriptedCompletionService(ValidDraftJson);
        var provider = new AiPresentationGenerationProvider(completion);

        for (var attempt = 0; attempt < 2; attempt += 1)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GenerateAsync(Prompt(), Options(), cancellation.Token));
        }

        Assert.Equal(2, completion.CallCount);
    }

    [Fact]
    public async Task Caller_cancellation_is_never_remapped_to_a_provider_timeout()
    {
        using var cancellation = new CancellationTokenSource();
        var completion = new ScriptedCompletionService(ValidDraftJson, cancelCallerOnCall: cancellation, throwCancellation: true);
        var provider = new AiPresentationGenerationProvider(completion);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => provider.GenerateAsync(Prompt(), Options(), cancellation.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.IsNotType<AiProviderTimeoutException>(exception);
    }

    [Fact]
    public async Task Timeout_without_caller_cancellation_still_reports_provider_timeout()
    {
        var completion = new ScriptedCompletionService(ValidDraftJson, throwCancellation: true);
        var provider = new AiPresentationGenerationProvider(completion);

        await Assert.ThrowsAsync<AiProviderTimeoutException>(() => provider.GenerateAsync(Prompt(), Options(), CancellationToken.None));
    }

    [Fact]
    public async Task Uncancelled_completion_still_produces_a_validated_draft()
    {
        var completion = new ScriptedCompletionService(ValidDraftJson);
        var provider = new AiPresentationGenerationProvider(completion);

        var result = await provider.GenerateAsync(Prompt(), Options(), CancellationToken.None);

        Assert.Equal("Launch plan", result.Draft.Title);
        Assert.Single(result.Draft.Slides);
    }

    [Fact]
    public async Task Cancelled_caller_does_not_stop_a_later_uncancelled_attempt_from_succeeding()
    {
        var firstCancellation = new CancellationTokenSource();
        firstCancellation.Cancel();
        var completion = new ScriptedCompletionService(ValidDraftJson);
        var provider = new AiPresentationGenerationProvider(completion);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GenerateAsync(Prompt(), Options(), firstCancellation.Token));
        using var secondCancellation = new CancellationTokenSource();
        firstCancellation.Dispose();

        var result = await provider.GenerateAsync(Prompt(), Options(), secondCancellation.Token);

        Assert.Equal("Launch plan", result.Draft.Title);
    }

    private static PresentationGenerationPrompt Prompt() => new("Return the canonical presentation draft.", "Create a launch plan deck.", "en");

    private static PresentationGenerationOptions Options() => new() { ProviderTimeoutSeconds = 30 };

    private static AiUsageMetadata Usage() => new("test", "test-presentation", 12, null, 24, null, null, 5, "completed", true);

    /// <summary>
    /// Returns (or throws) a canned completion and can cancel the caller token first,
    /// which reproduces the production ordering where a user cancellation is recorded
    /// while the provider response is already on its way back.
    /// </summary>
    private sealed class ScriptedCompletionService(string content, CancellationTokenSource? cancelCallerOnCall = null, bool throwCancellation = false) : IChatCompletionService
    {
        public int CallCount { get; private set; }

        public async Task<AiGenerationResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default)
        {
            CallCount += 1;
            if (cancelCallerOnCall is not null) cancelCallerOnCall.Cancel();
            if (throwCancellation) throw new OperationCanceledException(cancellationToken.IsCancellationRequested ? cancellationToken : default);
            await Task.Yield();
            return new AiGenerationResult(content, Usage());
        }

        public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield break;
        }
    }
}
