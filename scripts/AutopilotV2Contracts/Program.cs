// Offline third-runtime verification of PR #275 golden vectors. Synthetic secret only.
// Does NOT execute the deployed Taslim receiver or Manus Bridge publisher.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class Program
{
    private static readonly Regex TimestampRule = new(@"\A[1-9][0-9]{0,11}\z", RegexOptions.CultureInvariant);
    private static readonly Regex MetadataRule = new(@"\A[A-Za-z0-9][A-Za-z0-9._:-]{0,199}\z", RegexOptions.CultureInvariant);
    private static readonly Regex SignatureRule = new(@"\Asha256=[a-f0-9]{64}\z", RegexOptions.CultureInvariant);

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1) throw new ArgumentException("usage: AutopilotV2Contracts <vectors.json>");
            using var document = JsonDocument.Parse(File.ReadAllText(args[0], Encoding.UTF8));
            var root = document.RootElement;
            Require(root.GetProperty("schema").GetString() == "taslim.autopilot.signed-completion-v2.vectors.v1", "unknown schema");
            byte[] secret = Encoding.UTF8.GetBytes(root.GetProperty("syntheticSecretUtf8").GetString()!);
            Require(secret.Length >= 32, "synthetic secret too short");
            var vectors = root.GetProperty("vectors");
            Require(vectors.ValueKind == JsonValueKind.Array && vectors.GetArrayLength() == 12, "expected 12 vectors");
            var names = new HashSet<string>(StringComparer.Ordinal);
            var frames = new HashSet<string>(StringComparer.Ordinal);
            var signatures = new HashSet<string>(StringComparer.Ordinal);

            foreach (var v in vectors.EnumerateArray())
            {
                string name = v.GetProperty("name").GetString()!;
                Require(names.Add(name), "duplicate vector name");
                string timestamp = v.GetProperty("timestamp").GetString()!;
                string source = v.GetProperty("source").GetString()!;
                string eventType = v.GetProperty("eventType").GetString()!;
                string externalId = v.GetProperty("externalEventId").GetString()!;
                byte[] body = ExactBase64(v.GetProperty("bodyBase64").GetString()!);
                byte[] expectedFrame = ExactBase64(v.GetProperty("signedFrameBase64").GetString()!);
                byte[] frame = Frame(timestamp, source, eventType, externalId, body);
                Require(frame.AsSpan().SequenceEqual(expectedFrame), name + ": exact signed frame mismatch");
                string actual = Mac(secret, frame);
                string expected = v.GetProperty("expectedSignature").GetString()!;
                Require(SignatureRule.IsMatch(expected), name + ": malformed expected signature");
                Require(CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expected)),
                    name + ": HMAC mismatch");
                Require(frames.Add(Convert.ToBase64String(frame)), name + ": duplicate signed frame");
                Require(signatures.Add(actual), name + ": duplicate HMAC");

                // A different signed identity or even semantically equivalent raw JSON cannot replay this HMAC.
                Require(Mac(secret, Frame(timestamp, source + "-tampered", eventType, externalId, body)) != actual,
                    name + ": header tampering did not change MAC");
                Require(Mac(secret, Frame(timestamp, source, eventType, externalId,
                    body.Concat(new byte[] { 0x20 }).ToArray())) != actual,
                    name + ": raw-body tampering did not change MAC");
            }

            Require(names.Contains("unchanged") && names.Contains("raw_unicode") && names.Contains("raw_unicode_escaped"),
                "missing required vectors");
            byte[] sample = new byte[] { (byte)'a' };
            foreach (var bad in new[] { "", "0", "01800000000", new string('9', 13), "1\r\n" })
                MustReject(() => Frame(bad, "manus-bridge", "wave.task.completed", "evt-1", sample));
            foreach (var bad in new[] { "", "bad metadata", new string('x', 201), "bad\nheader", "métadata" })
                MustReject(() => Frame("1800000000", bad, "wave.task.completed", "evt-1", sample));
            foreach (var bad in new[] { Array.Empty<byte>(), new byte[65537] })
                MustReject(() => Frame("1800000000", "manus-bridge", "wave.task.completed", "evt-1", bad));

            Console.WriteLine("PASS: .NET 8 independently reproduced 12 v2 HMAC vectors, raw-body integrity and negative boundaries (offline only)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex.Message);
            return 1;
        }
    }

    private static byte[] ExactBase64(string text)
    {
        byte[] decoded = Convert.FromBase64String(text);
        Require(Convert.ToBase64String(decoded) == text, "noncanonical base64");
        return decoded;
    }

    private static byte[] Frame(string timestamp, string source, string eventType, string externalId, byte[] body)
    {
        Require(TimestampRule.IsMatch(timestamp), "noncanonical timestamp");
        foreach (string field in new[] { source, eventType, externalId })
            Require(MetadataRule.IsMatch(field), "invalid signed metadata");
        Require(body.Length is >= 1 and <= 65536, "invalid raw-body size");
        string bodySha = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        string canonical = $"taslim-autopilot-v2\n{timestamp}\n{Encoding.UTF8.GetByteCount(source)}:{source}\n" +
            $"{Encoding.UTF8.GetByteCount(eventType)}:{eventType}\n{Encoding.UTF8.GetByteCount(externalId)}:{externalId}\n{bodySha}\n";
        return Encoding.UTF8.GetBytes(canonical);
    }

    private static string Mac(byte[] key, byte[] frame) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(key, frame)).ToLowerInvariant();

    private static void Require(bool ok, string reason)
    {
        if (!ok) throw new InvalidOperationException(reason);
    }

    private static void MustReject(Action operation)
    {
        try { operation(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("invalid frame was accepted");
    }
}
