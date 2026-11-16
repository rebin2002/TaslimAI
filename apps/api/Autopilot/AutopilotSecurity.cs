using System.Security.Cryptography;
using System.Text;

namespace Taslim.Api.Autopilot;

public sealed record AutopilotSignatureResult(bool Valid, string? FailureReason)
{
    public static readonly AutopilotSignatureResult ValidResult = new(true, null);

    public static AutopilotSignatureResult Invalid(string reason) => new(false, reason);
}

/// <summary>
/// Verifies the HMAC signature of an intake event and rejects replays.
/// The signing secret is read from a server-side environment variable only and
/// is never persisted, logged, or exposed through any contract.
/// </summary>
public interface IAutopilotEventAuthenticator
{
    AutopilotSignatureResult Verify(string? signatureHeader, string? timestampHeader, string payload, DateTime nowUtc);
    string ComputePayloadHash(string payload);
}

public sealed class AutopilotEventAuthenticator(AutopilotOptions options) : IAutopilotEventAuthenticator
{
    private readonly AutopilotOptions settings = options;

    public AutopilotSignatureResult Verify(string? signatureHeader, string? timestampHeader, string payload, DateTime nowUtc)
    {
        if (!settings.RequireSignedEvents)
            return AutopilotSignatureResult.ValidResult;

        if (string.IsNullOrWhiteSpace(signatureHeader))
            return AutopilotSignatureResult.Invalid("signature_missing");

        if (!long.TryParse(timestampHeader, out var unixSeconds))
            return AutopilotSignatureResult.Invalid("timestamp_missing");

        var signedAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
        var skew = (nowUtc - signedAt).Duration();
        if (skew > TimeSpan.FromSeconds(Math.Max(30, settings.SignatureToleranceSeconds)))
            return AutopilotSignatureResult.Invalid("timestamp_out_of_tolerance");

        var secret = ResolveSecret();
        if (string.IsNullOrWhiteSpace(secret))
            return AutopilotSignatureResult.Invalid("signing_secret_unavailable");

        var expected = ComputeSignature(secret, timestampHeader!, payload);
        var provided = NormalizeSignature(signatureHeader!);
        if (provided.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected)))
            return AutopilotSignatureResult.Invalid("signature_mismatch");

        return AutopilotSignatureResult.ValidResult;
    }

    public string ComputePayloadHash(string payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload ?? string.Empty))).ToLowerInvariant();

    private static string NormalizeSignature(string value)
    {
        var trimmed = value.Trim();
        var separator = trimmed.IndexOf('=');
        var raw = separator >= 0 ? trimmed[(separator + 1)..] : trimmed;
        return raw.Trim().ToLowerInvariant();
    }

    private static string ComputeSignature(string secret, string timestamp, string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var material = $"{timestamp}.{payload}";
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    private string ResolveSecret() =>
        Environment.GetEnvironmentVariable(settings.SigningSecretEnvironmentVariable) ?? string.Empty;
}