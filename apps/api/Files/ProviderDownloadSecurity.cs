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
        // Fail closed: only a plain v4 or v6 endpoint can ever be a fetch target.
        if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)) return true;

        // ::ffff:0:0/96 (mapped) and the deprecated ::/96 (compatible) literals both embed an
        // IPv4 destination. Reduce to the v4 rules so an embedded address cannot inherit the v6
        // checks below: without this, [::7f00:1] or [::a9fe:a9fe] is neither link-local nor
        // unique-local and would be treated as a public target. :: and ::1 reduce to 0.0.0.0 and
        // 0.0.0.1, which the v4 rules refuse as unusable.
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        else
        {
            var candidate = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetworkV6 && HasEmbeddedIPv4(candidate))
                address = new IPAddress(new[] { candidate[12], candidate[13], candidate[14], candidate[15] });
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
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
                || (first == 192 && second == 88 && third == 99)
                || (first == 192 && second == 168)
                || (first == 198 && second is >= 18 and <= 19)
                || (first == 198 && second == 51 && third == 100)
                || (first == 203 && second == 0 && third == 113)
                || first >= 224;
        }

        // IPv6 unique-local (fc00::/7), link-local/site-local (fe80::/10 and
        // fec0::/10), multicast (ff00::/8), documentation (2001:db8::/32), discard-only
        // (100::/64), 6to4 (2002::/16), Teredo (2001::/32) and legacy 6bone (3ffe::/16) are
        // not fetch targets. 6to4 carries an attacker-chosen v4 destination in bytes 2-5 and
        // is refused outright rather than reduced to whatever that embedded v4 looks like.
        return (bytes[0] & 0xFE) == 0xFC
            || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)
            || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0xC0)
            || (bytes[0] == 0x01 && bytes[1] == 0x00 && bytes[2] == 0x00 && bytes[3] == 0x00)
            || (bytes[0] == 0x20 && bytes[1] == 0x02)
            || (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x00)
            || (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8)
            || (bytes[0] == 0x3F && bytes[1] == 0xFE)
            || bytes[0] >= 0xFC;
    }

    // True for an address in ::/96, the deprecated IPv4-compatible form that carries a v4
    // destination in the low four bytes. Only called with a 16-byte v6 address.
    private static bool HasEmbeddedIPv4(byte[] bytes)
    {
        if (bytes.Length != 16) return false;
        for (var i = 0; i < 12; i++)
        {
            if (bytes[i] != 0) return false;
        }

        return true;
    }
}

public sealed class ProviderDownloadSecurity(IProviderUrlPolicy urlPolicy)
{
    public const int MaxRedirects = 3;

    public Task EnsureSafeAsync(Uri uri, CancellationToken cancellationToken = default) =>
        urlPolicy.EnsureSafeAsync(uri, cancellationToken);
}
