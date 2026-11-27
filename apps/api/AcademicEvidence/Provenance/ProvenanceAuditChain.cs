using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Taslim.Api.AcademicEvidence.Domain;

namespace Taslim.Api.AcademicEvidence.Provenance;

/// <summary>
/// Well-known event kinds written to the provenance audit trail. Codes are
/// constants so a report can group by them instead of matching on prose.
/// </summary>
public static class ProvenanceAuditKinds
{
    /// <summary>A provider payload was normalized into a record.</summary>
    public const string RecordNormalized = "record.normalized";

    /// <summary>A provenance verdict was computed.</summary>
    public const string VerdictComputed = "provenance.verified";

    /// <summary>A verdict changed, including a downgrade.</summary>
    public const string VerdictChanged = "provenance.verdict_changed";

    /// <summary>A publication-integrity notice was attached.</summary>
    public const string RetractionNoticed = "provenance.retraction_noticed";

    /// <summary>The source was screened in or out of the review.</summary>
    public const string ScreeningDecided = "screening.decided";

    /// <summary>An evidence item was recorded.</summary>
    public const string EvidenceRecorded = "evidence.recorded";

    /// <summary>An evidence item failed the integrity check.</summary>
    public const string EvidenceRejected = "evidence.rejected";

    /// <summary>A locator was rejected as unsourced.</summary>
    public const string LocatorRejected = "locator.rejected";

    /// <summary>Two records were merged as one work.</summary>
    public const string RecordsMerged = "record.merged";
}

/// <summary>
/// Append-only, tamper-evident provenance audit trail.
///
/// Each entry hashes its canonical payload together with the previous entry's
/// hash, so the stored sequence proves the order the decisions were made in. That
/// is what lets a later reviewer show whether a citation was marked verified
/// before or after a retraction was recorded, instead of the story being retold
/// afterwards.
/// </summary>
public sealed class ProvenanceAuditChain
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly List<ProvenanceAuditEvent> _events = [];

    /// <summary>Events in sequence order.</summary>
    public IReadOnlyList<ProvenanceAuditEvent> Events => _events;

    /// <summary>
    /// Append one event. The caller supplies the occurred time so a backfilled
    /// audit row stays distinguishable from a live one by its sequence position.
    /// </summary>
    public ProvenanceAuditEvent Append(
        Guid sourceId,
        Guid workspaceId,
        string eventKind,
        object payload,
        DateTimeOffset occurredAtUtc,
        AuditActorKind actorKind = AuditActorKind.System,
        Guid? actorId = null)
        => AppendCanonical(sourceId, workspaceId, eventKind,
            Canonicalize(payload), occurredAtUtc, actorKind, actorId);

    /// <summary>Append an event whose payload is already canonical JSON.</summary>
    public ProvenanceAuditEvent AppendCanonical(
        Guid sourceId,
        Guid workspaceId,
        string eventKind,
        string canonicalPayload,
        DateTimeOffset occurredAtUtc,
        AuditActorKind actorKind = AuditActorKind.System,
        Guid? actorId = null)
    {
        var previous = _events.Count == 0 ? null : _events[^1];
        var entry = new ProvenanceAuditEvent
        {
            Sequence = previous is null ? 1 : previous.Sequence + 1,
            SourceId = sourceId,
            WorkspaceId = workspaceId,
            EventKind = eventKind,
            ActorKind = actorKind,
            ActorId = actorId,
            PayloadCanonical = canonicalPayload,
            OccurredAtUtc = occurredAtUtc,
            PreviousHash = previous?.EntryHash ?? string.Empty,
        };

        var sealedEntry = entry with { EntryHash = ComputeHash(entry) };
        _events.Add(sealedEntry);
        return sealedEntry;
    }

    /// <summary>
    /// Verify the chain: sequence numbers are contiguous, every predecessor hash
    /// matches, and every stored entry hash still matches a recomputation.
    /// </summary>
    public bool IsValid(out string? failure) => Validate(_events, out failure);

    /// <summary>
    /// Verify an event list that was loaded back from storage, where the rows may
    /// have been edited, reordered or truncated outside this process.
    /// </summary>
    public static bool Validate(IReadOnlyList<ProvenanceAuditEvent> events, out string? failure)
    {
        var expectedSequence = 1L;
        var previousHash = string.Empty;

        foreach (var entry in events)
        {
            if (entry.Sequence != expectedSequence)
            {
                failure = $"sequence gap at {expectedSequence.ToString(CultureInfo.InvariantCulture)}";
                return false;
            }

            if (!string.Equals(entry.PreviousHash, previousHash, StringComparison.Ordinal))
            {
                failure = $"broken predecessor link at {entry.Sequence.ToString(CultureInfo.InvariantCulture)}";
                return false;
            }

            if (!string.Equals(ComputeHash(entry), entry.EntryHash, StringComparison.Ordinal))
            {
                failure = $"payload altered at {entry.Sequence.ToString(CultureInfo.InvariantCulture)}";
                return false;
            }

            previousHash = entry.EntryHash;
            expectedSequence++;
        }

        failure = null;
        return true;
    }

    /// <summary>
    /// Canonical JSON for an audit payload: object keys sorted and no insignificant
    /// whitespace, so two runs that built the same values in a different order
    /// produce the same hash.
    /// </summary>
    public static string Canonicalize(object payload)
    {
        var json = JsonSerializer.Serialize(payload, payload.GetType(), SerializerOptions);
        using var document = JsonDocument.Parse(json);
        var builder = new StringBuilder(256);
        WriteCanonical(document.RootElement, builder);
        return builder.ToString();
    }

    /// <summary>Recompute the hash of an entry from its fields alone.</summary>
    public static string ComputeHash(ProvenanceAuditEvent entry)
    {
        var material = string.Concat(
            entry.Sequence.ToString(CultureInfo.InvariantCulture), "|",
            entry.SourceId.ToString("D"), "|",
            entry.WorkspaceId.ToString("D"), "|",
            entry.EventKind, "|",
            ((int)entry.ActorKind).ToString(CultureInfo.InvariantCulture), "|",
            (entry.ActorId?.ToString("D") ?? string.Empty), "|",
            entry.OccurredAtUtc.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), "|",
            entry.PreviousHash, "|",
            entry.PayloadCanonical);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    private static void WriteCanonical(JsonElement element, StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var first = true;
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first) builder.Append(',');
                    first = false;
                    builder.Append('"').Append(property.Name).Append("\":");
                    WriteCanonical(property.Value, builder);
                }

                builder.Append('}');
                break;

            case JsonValueKind.Array:
                builder.Append('[');
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (index++ > 0) builder.Append(',');
                    WriteCanonical(item, builder);
                }

                builder.Append(']');
                break;

            case JsonValueKind.String:
                builder.Append(JsonSerializer.Serialize(element.GetString()));
                break;

            case JsonValueKind.True:
                builder.Append("true");
                break;

            case JsonValueKind.False:
                builder.Append("false");
                break;

            case JsonValueKind.Null:
                builder.Append("null");
                break;

            default:
                builder.Append(element.GetRawText());
                break;
        }
    }
}
