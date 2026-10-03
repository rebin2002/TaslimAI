namespace Taslim.Api.Movies;

/// <summary>
/// Resolves Movie Studio video providers by their server-side key. Selection is
/// deliberately separate from provider construction so adding a provider does
/// not expand a conditional branch in application composition.
/// </summary>
public interface IMovieVideoProviderRegistry
{
    IReadOnlyCollection<string> Keys { get; }
    IMovieVideoProvider Resolve(string? providerKey);
}

public sealed class MovieVideoProviderRegistry : IMovieVideoProviderRegistry
{
    private readonly IReadOnlyDictionary<string, IMovieVideoProvider> providers;
    private readonly IMovieVideoProvider unavailable = new UnavailableMovieVideoProvider();

    public MovieVideoProviderRegistry(IEnumerable<IMovieVideoProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var indexed = new Dictionary<string, IMovieVideoProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            ArgumentNullException.ThrowIfNull(provider);
            if (string.IsNullOrWhiteSpace(provider.Key))
                throw new InvalidOperationException("Movie video providers must declare a non-empty key.");
            if (!indexed.TryAdd(provider.Key.Trim(), provider))
                throw new InvalidOperationException($"Movie video provider key '{provider.Key}' is registered more than once.");
        }

        this.providers = indexed;
        Keys = indexed.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyCollection<string> Keys { get; }

    public IMovieVideoProvider Resolve(string? providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey) || !providers.TryGetValue(providerKey.Trim(), out var provider))
            return unavailable;

        // A configured key is not enough. Every adapter owns its credential,
        // endpoint, and capability checks; unavailable adapters must remain the
        // result until all of those checks pass.
        return provider.IsAvailable ? provider : unavailable;
    }
}
