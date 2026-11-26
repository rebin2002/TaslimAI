using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;

namespace Taslim.Api.Projects;

/// <summary>
/// How a stamped write reacts when the row moved on after the caller read it.
/// </summary>
internal enum RevisionFence
{
    /// <summary>
    /// The client declared the revision its edit was built against (<c>If-Match</c> carrying a
    /// concrete token). A row that moved since then means the declared base is wrong, so the write
    /// is refused instead of re-targeted: re-aiming it would apply an edit the client never based
    /// on the content it is now landing on top of.
    /// </summary>
    Declared,

    /// <summary>
    /// The caller intends to apply over whatever the row holds: <c>If-Match: *</c>, or a legacy
    /// request with no <c>If-Match</c> at all. A row that moved since the caller's read is not a
    /// conflict for such a request, so the write is re-targeted at the live revision until it lands.
    /// </summary>
    LiveRow,
}

/// <summary>Outcome of <see cref="ProjectRevision.WriteStampedAsync"/>.</summary>
internal enum RevisionWriteOutcome
{
    /// <summary>The row was written and its revision advanced.</summary>
    Applied,

    /// <summary>The row is gone.</summary>
    Missing,

    /// <summary>The write was refused; the live revision is reported instead.</summary>
    Conflict,
}

/// <summary>
/// Result of a stamped write. <see cref="Revision"/> is the stamp the row now carries on
/// <see cref="RevisionWriteOutcome.Applied"/>, the stamp the row was found to carry on
/// <see cref="RevisionWriteOutcome.Conflict"/>, and <c>default</c> on
/// <see cref="RevisionWriteOutcome.Missing"/>.
/// </summary>
internal readonly record struct ProjectRevisionWriteResult(RevisionWriteOutcome Outcome, DateTime Revision);

/// <summary>
/// Revision-stamp helpers that make workspace-project edits conflict-safe.
/// The persisted <c>UpdatedAt</c> value of a project doubles as its opaque revision stamp:
/// it is surfaced through the <c>ETag</c> response header and re-declared on write with
/// <c>If-Match</c>. Two clock-adjacent writes could otherwise reuse the same stamp, so
/// <see cref="NextStamp"/> guarantees every accepted write strictly advances the
/// stamp by at least one microsecond — the finest resolution every supported store keeps —
/// which makes a stale base always detectable regardless of clock granularity.
///
/// Because that guarantee is what makes a published ETag expire forever, it has to hold for
/// <em>every</em> accepted write, including the unconditional ones: a stamp derived from a value
/// read before the write can already be behind whatever a concurrent commit put there. The
/// compare-and-set loop in <see cref="WriteStampedAsync"/> is therefore the only sanctioned way to
/// advance a project revision, for the fenced, wildcard, legacy, archive and restore paths alike.
/// </summary>
public static class ProjectRevision
{
    /// <summary>The RFC 9110 conditional token meaning "write over whatever is there now".</summary>
    public const string WildcardETag = "*";

    /// <summary>
    /// How many times <see cref="WriteStampedAsync"/> re-targets a write that a concurrent commit
    /// beat it to. Every attempt is a single atomic statement, so losing one only means the row
    /// moved again; the bound exists so pathological contention fails closed on a correct revision
    /// rather than spinning inside one request.
    /// </summary>
    internal const int MaxRevisionWriteAttempts = 16;

    /// <summary>Smallest advance applied to a revision stamp for any accepted write.</summary>
    /// <remarks>One microsecond is the finest resolution persisted by every supported provider.</remarks>
    internal static readonly TimeSpan MinimumStampAdvance = TimeSpan.FromMicroseconds(1);

    /// <summary>Formats a persisted stamp as a strong ETag token.</summary>
    /// <remarks>
    /// The round-trip ("O") format is deliberately not used here: it already appends a
    /// <c>Z</c> when the value carries <see cref="DateTimeKind.Utc"/>, which is exactly the
    /// kind Postgres hands back for a <c>timestamp with time zone</c> column. Appending the
    /// designator a second time produced an unparseable token, so every honest
    /// <c>If-Match</c> taken from this endpoint's own <c>ETag</c> was refused as
    /// <c>INVALID_IF_MATCH</c> on the production store while the in-memory test store,
    /// which returns <see cref="DateTimeKind.Unspecified"/>, stayed green. The digits are
    /// therefore rendered explicitly and the UTC designator is written exactly once, after
    /// any local-kind value has been brought back to UTC.
    /// </remarks>
    public static string FormatETag(DateTime revision)
    {
        var utc = revision.Kind == DateTimeKind.Local ? revision.ToUniversalTime() : revision;
        return "\"" + utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture) + "\"";
    }

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
    /// Computes the strictly greater stamp for a write whose base was <paramref name="baseStamp"/>.
    /// When the clock cannot already be distinguished from the base — same tick, sub-microsecond
    /// advance, or backwards adjustment — the stamp still moves forward by one representable tick,
    /// so consecutive accepted writes always carry distinct revisions.
    /// </summary>
    /// <remarks>
    /// Both terms are floored to the store's resolution first. .NET reads its clock in 100 ns
    /// ticks while every supported column keeps microseconds, so a stamp carrying sub-microsecond
    /// digits would be rounded by the store into a value that no longer matches the ETag handed to
    /// the client — and two such stamps could collapse onto the same persisted value. Flooring
    /// keeps every published token exactly equal to what the row reads back.
    /// </remarks>
    public static DateTime NextStamp(DateTime baseStamp, DateTime now)
    {
        var earliest = FloorToStoreResolution(baseStamp) + MinimumStampAdvance;
        var candidate = FloorToStoreResolution(now);
        // `earliest` is already strictly greater than `baseStamp`, so whichever term wins holds.
        return candidate > earliest ? candidate : earliest;
    }

    /// <summary>
    /// Drops the digits a supported store would not keep, preserving <paramref name="value"/>'s
    /// <see cref="DateTimeKind"/> so the value stays bindable against the production column.
    /// </summary>
    internal static DateTime FloorToStoreResolution(DateTime value)
    {
        var floored = value.Ticks - value.Ticks % MinimumStampAdvance.Ticks;
        return floored == value.Ticks ? value : new DateTime(floored, value.Kind);
    }

    /// <summary>
    /// Writes a project row and advances its revision stamp so that the new stamp is strictly
    /// greater than whatever the row actually held when the write landed.
    /// </summary>
    /// <remarks>
    /// The caller passes the revision it observed, which may already be stale: that is the whole
    /// point. Each attempt is one atomic <c>UPDATE … WHERE Id = … AND UpdatedAt = &lt;live&gt;</c>
    /// whose new stamp is derived from the same term the predicate matched, so a commit that lands
    /// after the caller's read cannot be walked over — the attempt simply matches no rows. What
    /// happens next is the caller's semantics, not the store's: <see cref="RevisionFence.Declared"/>
    /// reports a conflict (the client's base is wrong and re-targeting would silently merge into
    /// content it never saw), while <see cref="RevisionFence.LiveRow"/> re-reads the live revision
    /// and tries again, which is what an unconditional <c>If-Match: *</c> write and a legacy
    /// header-less write both promise. Neither can publish a stamp read from the pre-update value.
    /// </remarks>
    /// <param name="db">The context owning the projects set.</param>
    /// <param name="projectId">The row to write.</param>
    /// <param name="observedRevision">The revision the caller read (or the client declared).</param>
    /// <param name="fence">What to do when the row moved on since <paramref name="observedRevision"/>.</param>
    /// <param name="apply">
    /// Runs the actual column writes for one attempt, given the query already fenced on the live
    /// revision and the stamp to publish. Must set <c>UpdatedAt</c> to that stamp and return the
    /// number of rows affected.
    /// </param>
    /// <param name="cancellationToken">Request cancellation.</param>
    internal static async Task<ProjectRevisionWriteResult> WriteStampedAsync(
        TaslimDbContext db,
        Guid projectId,
        DateTime observedRevision,
        RevisionFence fence,
        Func<IQueryable<Project>, DateTime, Task<int>> apply,
        CancellationToken cancellationToken)
    {
        var liveRevision = observedRevision;
        for (var attempt = 0; ; attempt++)
        {
            var nextRevision = NextStamp(liveRevision, DateTime.UtcNow);
            var affected = await apply(
                db.Projects.Where(item => item.Id == projectId && item.UpdatedAt == liveRevision),
                nextRevision);
            if (affected == 1)
            {
                return new ProjectRevisionWriteResult(RevisionWriteOutcome.Applied, nextRevision);
            }

            // Nothing matched: either the row was deleted or somebody else committed first.
            var current = await db.Projects.AsNoTracking()
                .Where(item => item.Id == projectId)
                .Select(item => item.UpdatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (current == default)
            {
                return new ProjectRevisionWriteResult(RevisionWriteOutcome.Missing, default);
            }
            if (fence == RevisionFence.Declared || attempt >= MaxRevisionWriteAttempts)
            {
                return new ProjectRevisionWriteResult(RevisionWriteOutcome.Conflict, current);
            }
            liveRevision = current;
        }
    }
}
