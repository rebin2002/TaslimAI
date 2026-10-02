using Microsoft.Extensions.Options;
namespace Taslim.Api.Movies;

/// <summary>Keyed provider selection for movie generation. Unknown keys never fall back to a paid provider.</summary>
public sealed class MovieVideoProviderRegistry(
    RunwayMovieVideoProvider runway,
    ManusMovieVideoProvider manus,
    IOptions<MovieVideoOptions> options) : IMovieVideoProvider
{
    private readonly IReadOnlyDictionary<string, IMovieVideoProvider> providers =
        new Dictionary<string, IMovieVideoProvider>(StringComparer.OrdinalIgnoreCase)
        {
            ["runway"] = runway,
            ["manus"] = manus,
        };
    private readonly MovieVideoOptions settings = options.Value;

    private IMovieVideoProvider Selected =>
        settings.Enabled && providers.TryGetValue(settings.ProviderKey?.Trim() ?? string.Empty, out var candidate)
            && candidate.IsAvailable
            ? candidate
            : new UnavailableMovieVideoProvider();

    public string Key => Selected.Key;
    public bool IsAvailable => Selected.IsAvailable;
    public IReadOnlyCollection<string> SupportedOperations => Selected.SupportedOperations;
    public Task<MovieVideoSubmission> SubmitAsync(MovieVideoGenerationRequest request, CancellationToken cancellationToken) => Selected.SubmitAsync(request, cancellationToken);
    public Task<MovieVideoProviderStatus> GetStatusAsync(string providerJobId, CancellationToken cancellationToken) => Selected.GetStatusAsync(providerJobId, cancellationToken);
    public Task<MovieVideoProviderOutput> RetrieveAsync(string providerJobId, MovieVideoProviderStatus status, CancellationToken cancellationToken) => Selected.RetrieveAsync(providerJobId, status, cancellationToken);
    public Task CancelAsync(string providerJobId, CancellationToken cancellationToken) => Selected.CancelAsync(providerJobId, cancellationToken);
}
