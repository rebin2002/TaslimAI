using System.Net;
using System.Net.Sockets;

namespace Taslim.Api.Files;

public interface IProviderUrlPolicy
{
    Task EnsureSafeAsync(Uri uri, CancellationToken cancellationToken = default);
}

public sealed class ProviderUrlPolicy : IProviderUrlPolicy
{
    public async Task EnsureSafeAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        if (!uri.IsAbsoluteUri || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrWhiteSpace(uri.Host))
            throw new InvalidDataException("Provider download URL is not safe.");

        if (IPAddress.TryParse(uri.DnsSafeHost, out var literal))
        {
            if (IsBlocked(literal)) throw new InvalidDataException("Provider download target is not public.");
            return;
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken);
        }
        catch (SocketException exception)
        {
            throw new InvalidDataException("Provider download host could not be resolved safely.", exception);
        }

        if (addresses.Length == 0 || addresses.Any(IsBlocked))
            throw new InvalidDataException("Provider download target is not public.");
    }

    private static bool IsBlocked(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var first = bytes[0];
            var second = bytes[1];
            return first == 0
                || first == 10
                || first == 127
                || (first == 169 && second == 254)
                || (first == 172 && second is >= 16 and <= 31)
                || (first == 192 && second == 168)
                || first >= 224;
        }

        // IPv6 unique-local (fc00::/7), link-local (fe80::/10), multicast,
        // documentation/unspecified ranges, and loopback are not fetch targets.
        return (bytes[0] & 0xFE) == 0xFC
            || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)
            || bytes[0] >= 0xFC;
    }
}

public sealed class ProviderDownloadSecurity(IProviderUrlPolicy urlPolicy)
{
    public const int MaxRedirects = 3;

    public Task EnsureSafeAsync(Uri uri, CancellationToken cancellationToken = default) =>
        urlPolicy.EnsureSafeAsync(uri, cancellationToken);
}
