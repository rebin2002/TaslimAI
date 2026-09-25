using System.Collections.Concurrent;
using System.Text;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Images;
using Taslim.Api.Movies;
using Taslim.Api.Music;
using Taslim.Api.Voice;

namespace Taslim.Api.Tests.ProviderTesting;

public enum FakeProviderKind
{
    Image,
    Voice,
    Music,
    Movie,
}

public enum FakeProviderScenario
{
    ImmediateSuccess,
    AsynchronousSuccess,
    QueuedRunningCompleted,
    TransientFailure,
    PermanentFailure,
    RateLimit,
    Timeout,
    MalformedOutput,
    Cancellation,
    DelayedCompletion,
    DuplicateCallbackOrResult,
    StaleWorker,
    FallbackSuccess,
    AllProvidersUnavailable,
}

public sealed class FakeProviderScenarioCatalog
{
    private readonly ConcurrentDictionary<FakeProviderKind, FakeProviderScenario> scenarios = new();

    public FakeProviderScenarioCatalog()
    {
        foreach (var kind in Enum.GetValues<FakeProviderKind>()) scenarios[kind] = FakeProviderScenario.ImmediateSuccess;
    }

    public FakeProviderScenario this[FakeProviderKind kind]
    {
        get => scenarios.TryGetValue(kind, out var scenario) ? scenario : FakeProviderScenario.ImmediateSuccess;
        set => scenarios[kind] = value;
    }
}

public sealed class FakeProviderCallLog
{
    private readonly ConcurrentDictionary<FakeProviderKind, int> calls = new();
    private readonly ConcurrentDictionary<FakeProviderKind, int> cancellations = new();
    private readonly ConcurrentDictionary<FakeProviderKind, int> duplicateResults = new();

    public int Calls(FakeProviderKind kind) => calls.TryGetValue(kind, out var value) ? value : 0;
    public int Cancellations(FakeProviderKind kind) => cancellations.TryGetValue(kind, out var value) ? value : 0;
    public int DuplicateResults(FakeProviderKind kind) => duplicateResults.TryGetValue(kind, out var value) ? value : 0;
    internal void RecordCall(FakeProviderKind kind) => calls.AddOrUpdate(kind, 1, (_, value) => value + 1);
    internal void RecordCancellation(FakeProviderKind kind) => cancellations.AddOrUpdate(kind, 1, (_, value) => value + 1);
    internal void RecordDuplicate(FakeProviderKind kind) => duplicateResults.AddOrUpdate(kind, 1, (_, value) => value + 1);
}

public sealed class FakeProviderConfigurationException(string message) : Exception(message);

public sealed class FakeProviderException(
    FakeProviderKind kind,
    FakeProviderScenario scenario,
    string code,
    bool transient = false,
    bool rateLimited = false) : Exception("Deterministic fake provider failure.")
{
    public FakeProviderKind Kind { get; } = kind;
    public FakeProviderScenario Scenario { get; } = scenario;
    public string Code { get; } = code;
    public bool IsTransient { get; } = transient;
    public bool IsRateLimited { get; } = rateLimited;
}

public sealed class FakeProviderConfiguration(string providerKey, string? secret)
{
    public string ProviderKey { get; } = providerKey;
    public string? Secret { get; } = secret;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ProviderKey)) throw new FakeProviderConfigurationException("Provider key is required.");
        if (string.Equals(ProviderKey, "unconfigured", StringComparison.OrdinalIgnoreCase))
            throw new FakeProviderConfigurationException("Provider key is not configured.");
    }

    public string SanitizedDescription() => $"provider={ProviderKey}; secretConfigured={!string.IsNullOrWhiteSpace(Secret)}";
}

public abstract class FakeProviderBase(FakeProviderKind kind, FakeProviderScenarioCatalog scenarios, FakeProviderCallLog calls)
{
    protected FakeProviderKind Kind { get; } = kind;
    protected FakeProviderScenarioCatalog Scenarios { get; } = scenarios;
    protected FakeProviderCallLog Calls { get; } = calls;

    protected async Task BeforeResultAsync(CancellationToken cancellationToken)
    {
        Calls.RecordCall(Kind);
        var scenario = Scenarios[Kind];
        switch (scenario)
        {
            case FakeProviderScenario.AsynchronousSuccess:
                await Task.Delay(15, cancellationToken);
                break;
            case FakeProviderScenario.QueuedRunningCompleted:
                await Task.Delay(15, cancellationToken);
                break;
            case FakeProviderScenario.DelayedCompletion:
                await Task.Delay(250, cancellationToken);
                break;
            case FakeProviderScenario.Cancellation:
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                break;
            case FakeProviderScenario.Timeout:
                throw new FakeProviderException(Kind, scenario, "PROVIDER_TIMEOUT");
            case FakeProviderScenario.TransientFailure:
                throw new FakeProviderException(Kind, scenario, "PROVIDER_TRANSIENT", transient: true);
            case FakeProviderScenario.PermanentFailure:
                throw new FakeProviderException(Kind, scenario, "PROVIDER_FAILED");
            case FakeProviderScenario.RateLimit:
                throw new FakeProviderException(Kind, scenario, "RATE_LIMITED", rateLimited: true);
            case FakeProviderScenario.AllProvidersUnavailable:
                throw new FakeProviderException(Kind, scenario, "PROVIDER_UNAVAILABLE");
            case FakeProviderScenario.StaleWorker:
                throw new FakeProviderException(Kind, scenario, "STALE_WORKER");
        }
    }

    protected void AfterResult()
    {
        if (Scenarios[Kind] == FakeProviderScenario.DuplicateCallbackOrResult)
            Calls.RecordDuplicate(Kind);
    }

    protected void RecordCancellation() => Calls.RecordCancellation(Kind);

    protected bool IsMalformed => Scenarios[Kind] == FakeProviderScenario.MalformedOutput;
}

public sealed class FakeImageGenerationProvider(
    FakeProviderScenarioCatalog scenarios,
    FakeProviderCallLog calls) : FakeProviderBase(FakeProviderKind.Image, scenarios, calls), IImageGenerationProvider
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    public string Key => "fake-image";

    public async Task<ImageProviderResult> GenerateAsync(ImageGenerationInput request, ImagePromptBuildResult prompt, CancellationToken cancellationToken = default)
    {
        try
        {
            await BeforeResultAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            RecordCancellation();
            throw;
        }

        AfterResult();
        if (IsMalformed) return new ImageProviderResult("not-an-image"u8.ToArray(), "text/plain", "txt", null, null, new ImageProviderUsage(null, null, null, null, 0m));
        return new ImageProviderResult(Png, "image/png", "png", 1, 1, new ImageProviderUsage(12, 12, null, 8, 0.01m, LatencyMs: 2, CostBasis: UsageCostBasis.Actual));
    }
}

public sealed class FakeVoiceGenerationProvider(
    FakeProviderScenarioCatalog scenarios,
    FakeProviderCallLog calls) : FakeProviderBase(FakeProviderKind.Voice, scenarios, calls), IVoiceGenerationProvider
{
    public string Key => "fake-voice";

    public async Task<VoiceProviderResult> GenerateAsync(VoiceGenerationInput request, CancellationToken cancellationToken = default)
    {
        try
        {
            await BeforeResultAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            RecordCancellation();
            throw;
        }

        AfterResult();
        if (IsMalformed) return new VoiceProviderResult("bad"u8.ToArray(), "text/plain", "txt", null, null, new VoiceProviderUsage("fake-voice-model", 1, 3, 0m, 1));
        var content = Encoding.UTF8.GetBytes("fake voice output");
        return new VoiceProviderResult(content, "audio/mpeg", "mp3", 1200, 24000, new VoiceProviderUsage("fake-voice-model", request.Text.Length, content.Length, 0.004m, 2, CostBasis: UsageCostBasis.Actual));
    }
}

public sealed class FakeMusicGenerationProvider(
    FakeProviderScenarioCatalog scenarios,
    FakeProviderCallLog calls) : FakeProviderBase(FakeProviderKind.Music, scenarios, calls), IMusicGenerationProvider
{
    public string Key => "fake-music";

    public async Task<MusicProviderResult> GenerateAsync(MusicGenerationInput request, CancellationToken cancellationToken = default)
    {
        try
        {
            await BeforeResultAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            RecordCancellation();
            throw;
        }

        AfterResult();
        if (IsMalformed) return new MusicProviderResult("bad"u8.ToArray(), "text/plain", "txt", request.DurationSeconds, new MusicProviderUsage(1, 1, 0m, 0m, 1));
        var content = Encoding.UTF8.GetBytes("fake music output");
        return new MusicProviderResult(content, "audio/mpeg", "mp3", request.DurationSeconds, new MusicProviderUsage(10, 20, 0.004m, 0.004m, 2, CostBasis: UsageCostBasis.Actual));
    }
}

public sealed class FakeMovieVideoProvider(
    FakeProviderScenarioCatalog scenarios,
    FakeProviderCallLog calls) : FakeProviderBase(FakeProviderKind.Movie, scenarios, calls), IMovieVideoProvider
{
    private readonly ConcurrentDictionary<string, int> polls = new();
    public string Key => "fake-movie";
    public bool IsAvailable => Scenarios[FakeProviderKind.Movie] != FakeProviderScenario.AllProvidersUnavailable;
    public IReadOnlyCollection<string> SupportedOperations { get; } = [MovieStudioOperations.QuickMovie, MovieStudioOperations.SceneClip];

    public async Task<MovieVideoSubmission> SubmitAsync(MovieVideoGenerationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await BeforeResultAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            RecordCancellation();
            throw;
        }

        var providerJobId = $"fake-movie-{Guid.NewGuid():N}";
        polls[providerJobId] = 0;
        return new MovieVideoSubmission(providerJobId);
    }

    public Task<MovieVideoProviderStatus> GetStatusAsync(string providerJobId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var poll = polls.AddOrUpdate(providerJobId, 1, (_, value) => value + 1);
        var scenario = Scenarios[FakeProviderKind.Movie];
        if (scenario is FakeProviderScenario.PermanentFailure or FakeProviderScenario.RateLimit)
            return Task.FromResult(new MovieVideoProviderStatus(MovieVideoProviderJobStatus.Failed, 100));
        if (scenario == FakeProviderScenario.Timeout)
            return Task.FromResult(new MovieVideoProviderStatus(MovieVideoProviderJobStatus.Running, Math.Min(90, poll * 10)));
        if (scenario == FakeProviderScenario.Cancellation)
            return Task.FromResult(new MovieVideoProviderStatus(MovieVideoProviderJobStatus.Running, Math.Min(50, poll * 10)));
        if (scenario is FakeProviderScenario.QueuedRunningCompleted or FakeProviderScenario.DelayedCompletion)
            return Task.FromResult(poll < 2
                ? new MovieVideoProviderStatus(MovieVideoProviderJobStatus.Running, 50)
                : new MovieVideoProviderStatus(MovieVideoProviderJobStatus.Succeeded, 100, "video/mp4", "fake-movie.mp4", 18, 1));
        return Task.FromResult(new MovieVideoProviderStatus(MovieVideoProviderJobStatus.Succeeded, 100, "video/mp4", "fake-movie.mp4", 18, 1));
    }

    public Task<MovieVideoProviderOutput> RetrieveAsync(string providerJobId, MovieVideoProviderStatus status, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsMalformed)
            return Task.FromResult(new MovieVideoProviderOutput("text/plain", "fake.txt", 3, _ => Task.FromResult<Stream>(new MemoryStream("bad"u8.ToArray())), 1, null, 0m, 0m, null, UsageCostBasis.Actual, "{}"));
        var content = "fake movie output"u8.ToArray();
        return Task.FromResult(new MovieVideoProviderOutput("video/mp4", "fake-movie.mp4", content.Length, _ => Task.FromResult<Stream>(new MemoryStream(content)), 1, "{}", 0.01m, 0.01m, "USD", UsageCostBasis.Actual, "{\"deterministic\":true}"));
    }

    public Task CancelAsync(string providerJobId, CancellationToken cancellationToken)
    {
        RecordCancellation();
        return Task.CompletedTask;
    }
}

public static class FakeProviderConformance
{
    public static async Task<T> WithTransientRetriesAsync<T>(Func<CancellationToken, Task<T>> operation, int maxRetries, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await operation(cancellationToken); }
            catch (FakeProviderException exception) when (exception.IsTransient && attempt < maxRetries)
            {
                await Task.Delay(1, cancellationToken);
            }
        }
    }

    public static async Task<T> WithFallbackAsync<T>(Func<Task<T>> primary, Func<Task<T>> fallback)
    {
        try { return await primary(); }
        catch (FakeProviderException exception) when (exception.Scenario is FakeProviderScenario.AllProvidersUnavailable or FakeProviderScenario.RateLimit)
        {
            return await fallback();
        }
    }

    public static void AssertSecretSanitized(string value, string secret)
    {
        Xunit.Assert.DoesNotContain(secret, value, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("api_key", value, StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.DoesNotContain("authorization", value, StringComparison.OrdinalIgnoreCase);
    }
}
