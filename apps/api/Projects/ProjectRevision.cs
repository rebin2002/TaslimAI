using System.Globalization;

namespace Taslim.Api.Projects;

/// <summary>
/// Revision-stamp helpers that make workspace-project edits conflict-safe.
/// The persisted <c>UpdatedAt</c> value of a project doubles as its opaque revision stamp:
/// it is surfaced through the <c>ETag</c> response header and re-declared on write with
/// <c>If-Match</c>. Two clock-adjacent writes could otherwise reuse the same stamp, so
/// <see cref="NextStamp"/> guarantees every accepted guarded write strictly advances the
/// stamp by at least one microsecond — the finest resolution every supported store keeps —
/// which makes a stale base always detectable regardless of clock granularity.
/// </summary>
public static class ProjectRevision
{
    /// <summary>The RFC 9110 conditional token meaning "write over whatever is there now".</summary>
    public const string WildcardETag = "*";

    /// <summary>Smallest advance applied to a revision stamp for any accepted write.</summary>
    /// <remarks>One microsecond is the finest resolution persisted by every supported provider.</remarks>
    internal static readonly TimeSpan MinimumStampAdvance = TimeSpan.FromMicroseconds(1);

    /// <summary>Formats a persisted stamp as a strong ETag token.</summary>
    public static string FormatETag(DateTime revision) =>
        "\"" + revision.ToString("O", CultureInfo.InvariantCulture) + "Z\"";

    /// <summary>
    /// Parses an <c>If-Match</c> token into the revision stamp it carries. Accepts strong
    /// (<c>"…"</c>), weak (<c>W/"…"</c>) and bare ISO round-trip values; rejects everything
    /// else — including the wildcard and comma-separated lists — so callers own their
    /// wildcard policy explicitly. Results are normalised to <see cref="DateTimeKind.Unspecified"/>,
    /// the kind the database column stores, so compare-and-set parameters bind on identical terms.
    /// </summary>
    public static bool TryParseETag(string? headerValue, out DateTime revision)
    {
        revision = default;
        if (string.IsNullOrWhiteSpace(headerValue)) return false;
        var token = headerValue.Trim();
        if (token.IndexOf(',') >= 0) return false;
        if (token.StartsWith("W/", StringComparison.Ordinal)) token = token[2..];
        if (token.Length >= 2 && token[0] == '"' && token[^1] == '"') token = token[1..^1];
        if (!DateTime.TryParse(
                token,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.NoCurrentDateDefault,
                out var parsed))
        {
            return false;
        }
        // A token with a Z carries the same wall-clock digits the column stores; a token
        // with an explicit offset must first be brought back to UTC wall-clock time.
        var stamp = parsed.Kind == DateTimeKind.Local ? parsed.ToUniversalTime() : parsed;
        revision = DateTime.SpecifyKind(stamp, DateTimeKind.Unspecified);
        return true;
    }

    /// <summary>
    /// Computes the strictly greater stamp for a guarded write whose base was <paramref name="baseStamp"/>.
    /// When the clock cannot already be distinguished from the base — same tick, sub-microsecond
    /// advance, or backwards adjustment — the stamp still moves forward by one representable tick,
    /// so consecutive accepted writes always carry distinct revisions.
    /// </summary>
    public static DateTime NextStamp(DateTime baseStamp, DateTime now) =>
        now > baseStamp.Add(MinimumStampAdvance) ? now : baseStamp.Add(MinimumStampAdvance);
}
