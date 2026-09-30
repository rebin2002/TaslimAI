using System.Collections.Concurrent;
using Taslim.Api.Movies;

namespace Taslim.Api.Tests.ProviderTesting;

public enum MockDirectVideoScenario
{
    Success,
    QueuedThenSuccess,
    RateLimited,
    Failed,
    MalformedOutput,
    Cancelled,
}

/// <summary>
/// Test-only adapter. It is intentionally not registered by the production application.
/// </summary>
public sealed class MockDirectVideoProviderAdapter : IDirectVideoProviderAdapter
{
    private static readonly byte[] Mp4 = [
        0, 0, 0, 24, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D,
        0, 0, 0, 0, 0x69, 0x73, 0x6F, 0x6D, 0, 0, 0, 0,
    ];

    private readonly ConcurrentDictionary<string, int> polls = new(StringComparer.Ordinal);
    private readonly DirectVideoProviderOptions options;

    public MockDirectVideoProviderAdapter(MockDirectVideoScenario scenario = MockDirectVideoScenario.Success)
    {
        Scenario = scenario;
        options = new DirectVideoProviderOptions
        {
            Enabled = true,
            ProviderKey = "mock-direct-video",
            ModelKey = "mock-model",
            ApiBaseUrl = "https://mock.invalid/",
            ApiKey = "test-only-secret",
        };
        Health = new DirectVideoProviderHealthHook(options.ToConfiguration(), _ => Task.CompletedTask);
    }

    public MockDirectVideoScenario Scenario { get; set; }
    public int SubmitCount { get; private set; }
    public int StatusCount { get; private set; }
    public int RetrieveCount { get; private set; }
    public int CancelCount { get; private set; }
    public string Key => "mock-direct-video";
    public bool IsAvailable => options.ToConfiguration().IsConfigured && Scenario != MockDirectVideoScenario.Failed;
    public IDirectVideoProviderHealthHook Health { get; }

    public DirectVideoCapabilityDeclaration Capabilities { get; } = new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MovieStudioOperations.QuickMovie, MovieStudioOperations.SceneClip },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DirectVideoResolutions.Hd720, DirectVideoResolutions.FullHd },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "16:9", "9:16" },
        1,
        10,
        SupportsReferenceImage: true,
        SupportsContinuation: true,
        SupportsUpscaling: true,
        SupportsNativeAudio: false);

    public Task<DirectVideoSubmission> SubmitAsync(DirectVideoRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SubmitCount++;
        if (Scenario == MockDirectVideoScenario.RateLimited)
            throw new DirectVideoProviderException(DirectVideoErrorCategory.RateLimited, DirectVideoErrorCodes.ProviderRateLimited);
        if (Scenario == MockDirectVideoScenario.Failed)
            throw new DirectVideoProviderException(DirectVideoErrorCategory.PermanentFailure, DirectVideoErrorCodes.ProviderFailure);

        var id = $"mock-job-{request.MovieClipId:N}";
        polls[id] = 0;
        return Task.FromResult(new DirectVideoSubmission(id));
    }

    public Task<DirectVideoStatus> GetStatusAsync(string providerJobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StatusCount++;
        var poll = polls.AddOrUpdate(providerJobId, 1, (_, current) => current + 1);
        var response = Scenario switch
        {
            MockDirectVideoScenario.QueuedThenSuccess when poll == 1 => new DirectVideoProviderStatusResponse("QUEUED", 10),
            MockDirectVideoScenario.RateLimited => new DirectVideoProviderStatusResponse("FAILED", 100),
            MockDirectVideoScenario.Failed => new DirectVideoProviderStatusResponse("FAILED", 100),
            MockDirectVideoScenario.Cancelled => new DirectVideoProviderStatusResponse("CANCELLED", 100),
            _ => new DirectVideoProviderStatusResponse("SUCCEEDED", 100, "video/mp4", "mock-clip.mp4", Mp4.Length, 1, 0m, 0m, "USD", "Actual", "{\"deterministic\":true}"),
        };
        return Task.FromResult(DirectVideoResultNormalizer.NormalizeStatus(response));
    }

    public Task<DirectVideoProviderOutput> RetrieveAsync(string providerJobId, DirectVideoStatus status, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RetrieveCount++;
        if (Scenario == MockDirectVideoScenario.MalformedOutput)
        {
            var invalid = new byte[] { 1, 2, 3 };
            return Task.FromResult(DirectVideoResultNormalizer.NormalizeOutput(
                "text/plain", "mock.txt", invalid.Length, _ => Task.FromResult<Stream>(new MemoryStream(invalid)), 1, null, null, null, null, null, "mock-model", 1024));
        }

        return Task.FromResult(DirectVideoResultNormalizer.NormalizeOutput(
            "video/mp4", "mock-clip.mp4", Mp4.Length, _ => Task.FromResult<Stream>(new MemoryStream(Mp4, writable: false)), 1, 0m, 0m, "USD", "Actual", "{\"deterministic\":true}", "mock-model", 1024));
    }

    public Task CancelAsync(string providerJobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancelCount++;
        return Task.CompletedTask;
    }
}
