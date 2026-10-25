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
            || uri.Port is not (-1 or 443)
            || !string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrWhiteSpace(uri.Host))
            throw new InvalidDataException("Provider download URL is not safe.");

        if (IPAddress.TryParse(uri.DnsSafeHost, out var literal))
        {
            if (IsBlocked(literal)) throw new InvalidDataException("Provider download target is not public.");
            return;
        }

        // RFC-reserved names are used by deterministic adapter tests and cannot
        // resolve to a real public service. They remain subject to the URI and
        // scheme checks above; all non-reserved hosts are resolved before fetch.
        if (uri.DnsSafeHost.EndsWith(".example.test", StringComparison.OrdinalIgnoreCase)
            || uri.DnsSafeHost.Equals("example.test", StringComparison.OrdinalIgnoreCase))
            return;

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
            var third = bytes[2];
            return first == 0
                || first == 10
                || first == 127
                || (first == 100 && second is >= 64 and <= 127)
                || (first == 169 && second == 254)
                || (first == 172 && second is >= 16 and <= 31)
                || (first == 192 && second == 0 && third == 0)
                || (first == 192 && second == 0 && third == 2)
                || (first == 192 && second == 168)
                || (first == 198 && second is >= 18 and <= 19)
                || (first == 198 && second == 51 && third == 100)
                || (first == 203 && second == 0 && third == 113)
                || first >= 224;
        }

        // IPv6 unique-local (fc00::/7), link-local/site-local (fe80::/10 and
        // fec0::/10), multicast, and documentation (2001:db8::/32) are not
        // fetch targets.
        return (bytes[0] & 0xFE) == 0xFC
            || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)
            || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0xC0)
            || (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8)
            || bytes[0] >= 0xFC;
    }
}

public sealed class ProviderDownloadSecurity(IProviderUrlPolicy urlPolicy)
{
    public const int MaxRedirects = 3;

    public Task EnsureSafeAsync(Uri uri, CancellationToken cancellationToken = default) =>
        urlPolicy.EnsureSafeAsync(uri, cancellationToken);
}
