using System.Security.Cryptography;
using System.Text;

namespace Taslim.Api.Video;

/// <summary>
/// Provider-neutral authenticity seam for callback adapters. The adapter owns
/// secret lookup and header extraction; the core receives only normalized events.
/// </summary>
public static class VideoCallbackSecurity
{
    public static string ComputeHmacSha256(ReadOnlySpan<byte> body, ReadOnlySpan<byte> secret, string timestamp)
    {
        if (secret.Length == 0 || string.IsNullOrWhiteSpace(timestamp)) throw new ArgumentException("A callback secret and timestamp are required.");
        var prefix = Encoding.UTF8.GetBytes(timestamp.Trim() + ".");
        var payload = new byte[prefix.Length + body.Length];
        prefix.CopyTo(payload, 0);
        body.CopyTo(payload.AsSpan(prefix.Length));
        return "sha256=" + Convert.ToHexString(HMACSHA256.HashData(secret, payload)).ToLowerInvariant();
    }

    public static bool IsAuthentic(
        ReadOnlySpan<byte> body,
        ReadOnlySpan<byte> secret,
        string? timestamp,
        string? signature,
        DateTimeOffset receivedAt,
        TimeSpan maximumAge)
    {
        if (secret.Length == 0 || string.IsNullOrWhiteSpace(timestamp) || string.IsNullOrWhiteSpace(signature)
            || !long.TryParse(timestamp.Trim(), out var unixSeconds)) return false;
        DateTimeOffset signedAt;
        try { signedAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds); }
        catch (ArgumentOutOfRangeException) { return false; }
        if (maximumAge <= TimeSpan.Zero || (receivedAt - signedAt).Duration() > maximumAge) return false;
        var expected = ComputeHmacSha256(body, secret, timestamp);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature.Trim()));
    }
}
