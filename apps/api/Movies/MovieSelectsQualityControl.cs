using Microsoft.Extensions.Options;

namespace Taslim.Api.Movies;

/// <summary>
/// Evidence dimensions that a media inspection adapter may report for a movie segment.
/// The evaluator never infers a dimension from a prompt, label, provider payload, or creative
/// preference. Text/signage is opt-in because it is only relevant for some shots.
/// </summary>
public static class MovieSelectsQcIssueCategories
{
    public const string FaceIdentity = "face_identity";
    public const string Motion = "motion";
    public const string Artifact = "artifact";
    public const string Continuity = "continuity";
    public const string Framing = "framing";
    public const string TextSignage = "text_signage";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
    {
        FaceIdentity, Motion, Artifact, Continuity, Framing, TextSignage,
    };

    public static readonly IReadOnlyList<string> DefaultRelevant =
    [FaceIdentity, Motion, Artifact, Continuity, Framing];
}

public static class MovieSelectsQcCheckStatuses
{
    public const string Passed = "passed";
    public const string Issue = "issue";
    public const string Missing = "missing";
    public const string NotRelevant = "not_relevant";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
    {
        Passed, Issue, Missing, NotRelevant,
    };
}

public static class MovieSelectsQcSeverities
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Blocking = "blocking";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
    {
        Info, Warning, Blocking,
    };
}

public static class MovieSelectsQcActions
{
    public const string RecommendSelects = "recommend_selects";
    public const string RequireReview = "require_review";
    public const string NoUsableRange = "no_usable_range";
}

public static class MovieSelectsQcReasonCodes
{
    public const string Recommended = "MOVIE_SELECTS_QC_SELECTS_RECOMMENDED";
    public const string EvidenceMissing = "MOVIE_SELECTS_QC_EVIDENCE_MISSING";
    public const string EvidenceInvalid = "MOVIE_SELECTS_QC_EVIDENCE_INVALID";
    public const string IssueReported = "MOVIE_SELECTS_QC_ISSUE_REPORTED";
    public const string BlockingIssue = "MOVIE_SELECTS_QC_BLOCKING_ISSUE";
    public const string UsableRangeTooShort = "MOVIE_SELECTS_QC_USABLE_RANGE_TOO_SHORT";
    public const string NoUsableRange = "MOVIE_SELECTS_QC_NO_USABLE_RANGE";
    public const string NoSegments = "MOVIE_SELECTS_QC_NO_SEGMENTS";
    public const string LimitExceeded = "MOVIE_SELECTS_QC_EVIDENCE_LIMIT_EXCEEDED";
}

public sealed class MovieSelectsQualityControlOptions
{
    public int MaxSegments { get; set; } = 512;
    public int MaxIssues { get; set; } = 4_096;
    public int MaxFindings { get; set; } = 64;
    public int MaxIdentifierLength { get; set; } = 128;
    public int MaxEvidenceReferenceLength { get; set; } = 256;
    public decimal MinimumUsableRangeSeconds { get; set; } = 0.5m;
    public decimal CoalesceGapSeconds { get; set; } = 0m;
}

/// <summary>
/// Inclusive-start, exclusive-end source-media range. Values are seconds and may be fractional,
/// but must be finite, non-negative, and have a positive duration.
/// </summary>
public sealed record MovieSelectsQcRange(decimal StartSeconds, decimal EndSeconds)
{
    public decimal DurationSeconds => EndSeconds - StartSeconds;
}

/// <summary>
/// A bounded, provider-neutral check result supplied by a media inspection adapter. A passed
/// check means only that the adapter supplied positive evidence for this dimension; it is not an
/// artistic or subjective quality score.
/// </summary>
public sealed record MovieSelectsQcCheck(
    string Category,
    string Status,
    string? EvidenceReference = null);

/// <summary>
/// An observed issue with an exact affected range. The issue code and evidence reference are
/// opaque, bounded, provider-neutral values supplied by the inspection boundary.
/// </summary>
public sealed record MovieSelectsQcIssue(
    string Category,
    string Severity,
    string Code,
    MovieSelectsQcRange Range,
    string? EvidenceReference = null);

public sealed record MovieSelectsQcSegmentEvidence(
    string SegmentId,
    MovieSelectsQcRange SourceRange,
    IReadOnlyList<MovieSelectsQcCheck> Checks,
    IReadOnlyList<MovieSelectsQcIssue> Issues);

public sealed record MovieSelectsQcRequirements(
    IReadOnlyList<string>? RelevantCategories = null,
    decimal MinimumUsableRangeSeconds = 0.5m,
    bool RequireEvidenceForRelevantCategories = true);

public sealed record MovieSelectsQcRequest(
    IReadOnlyList<MovieSelectsQcSegmentEvidence> Segments,
    MovieSelectsQcRequirements? Requirements = null,
    Guid? MovieTakeId = null,
    Guid? MovieClipId = null);

public sealed record MovieSelectsQcSelect(
    string SegmentId,
    MovieSelectsQcRange Range);

public sealed record MovieSelectsQcSegmentDecision(
    string SegmentId,
    string Action,
    IReadOnlyList<MovieSelectsQcRange> UsableRanges,
    IReadOnlyList<MovieSelectsQcIssue> Issues,
    IReadOnlyList<string> ReasonCodes);

public sealed record MovieSelectsQcDecision(
    string ContractVersion,
    string Action,
    bool RequiresHumanReview,
    IReadOnlyList<MovieSelectsQcSelect> RecommendedSelects,
    IReadOnlyList<MovieSelectsQcSegmentDecision> Segments,
    IReadOnlyList<string> ReasonCodes,
    Guid? MovieTakeId = null,
    Guid? MovieClipId = null)
{
    public bool HasUsableRange => RecommendedSelects.Count > 0;
}

public interface IMovieSelectsQualityControl
{
    MovieSelectsQcDecision Evaluate(MovieSelectsQcRequest request);
}

/// <summary>
/// Deterministic segment QC and select recommendation. It only combines explicit bounded
/// inspection evidence, subtracts explicitly reported blocking ranges, and returns a reviewable
/// recommendation. It does not select a provider/model, charge usage, queue regeneration, or
/// claim that a clip is creatively successful.
/// </summary>
public sealed class MovieSelectsQualityControlService(
    IOptions<MovieSelectsQualityControlOptions> options) : IMovieSelectsQualityControl
{
    private sealed record NormalizedRequirements(
        IReadOnlyList<string> RelevantCategories,
        decimal MinimumUsableRangeSeconds,
        bool RequireEvidenceForRelevantCategories);

    public const string ContractVersion = "movie-selects-qc.v1";
    private readonly MovieSelectsQualityControlOptions settings = options.Value;

    public MovieSelectsQcDecision Evaluate(MovieSelectsQcRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requirements = NormalizeRequirements(request.Requirements);
        var segments = request.Segments ?? [];
        var globalReasons = new List<string>();
        var segmentDecisions = new List<MovieSelectsQcSegmentDecision>();
        var recommended = new List<MovieSelectsQcSelect>();
        var reviewRequired = false;

        if (segments.Count == 0)
        {
            globalReasons.Add(MovieSelectsQcReasonCodes.NoSegments);
            return Decision(MovieSelectsQcActions.RequireReview, true, recommended, segmentDecisions, globalReasons, request);
        }

        if (segments.Count > Max(settings.MaxSegments, 1))
        {
            globalReasons.Add(MovieSelectsQcReasonCodes.LimitExceeded);
            return Decision(MovieSelectsQcActions.RequireReview, true, recommended, segmentDecisions, globalReasons, request);
        }

        var totalIssues = segments.Sum(item => item?.Issues?.Count ?? 0);
        if (totalIssues > Max(settings.MaxIssues, 1))
        {
            globalReasons.Add(MovieSelectsQcReasonCodes.LimitExceeded);
            return Decision(MovieSelectsQcActions.RequireReview, true, recommended, segmentDecisions, globalReasons, request);
        }

        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in segments)
        {
            if (segment is null)
            {
                segmentDecisions.Add(InvalidSegment("", MovieSelectsQcReasonCodes.EvidenceInvalid));
                reviewRequired = true;
                continue;
            }

            var segmentId = NormalizeIdentifier(segment.SegmentId);
            if (string.IsNullOrWhiteSpace(segmentId) || !identifiers.Add(segmentId))
            {
                var reason = string.IsNullOrWhiteSpace(segmentId)
                    ? MovieSelectsQcReasonCodes.EvidenceInvalid
                    : MovieSelectsQcReasonCodes.EvidenceInvalid;
                segmentDecisions.Add(InvalidSegment(segmentId, reason));
                reviewRequired = true;
                continue;
            }

            var errors = ValidateSegment(segment, requirements);
            if (errors.Count > 0)
            {
                segmentDecisions.Add(new(
                    segmentId,
                    MovieSelectsQcActions.RequireReview,
                    [],
                    NormalizeIssues(segment.Issues),
                    errors));
                globalReasons.AddRange(errors);
                reviewRequired = true;
                continue;
            }

            var checks = segment.Checks ?? [];
            var issues = NormalizeIssues(segment.Issues);
            var segmentReasons = new List<string>();
            var blockingRanges = issues
                .Where(item => item.Severity == MovieSelectsQcSeverities.Blocking)
                .Select(item => item.Range)
                .OrderBy(item => item.StartSeconds)
                .ThenBy(item => item.EndSeconds)
                .ToArray();

            if (issues.Count > 0)
            {
                segmentReasons.Add(MovieSelectsQcReasonCodes.IssueReported);
                globalReasons.Add(MovieSelectsQcReasonCodes.IssueReported);
            }
            if (blockingRanges.Length > 0)
            {
                segmentReasons.Add(MovieSelectsQcReasonCodes.BlockingIssue);
                globalReasons.Add(MovieSelectsQcReasonCodes.BlockingIssue);
            }
            if (issues.Any(item => item.Severity == MovieSelectsQcSeverities.Warning))
            {
                reviewRequired = true;
                segmentReasons.Add(MovieSelectsQcReasonCodes.IssueReported);
            }

            var missingRequiredChecks = requirements.RelevantCategories
                .Where(category => requirements.RequireEvidenceForRelevantCategories
                    && !checks.Any(check => check.Category == category && check.Status is MovieSelectsQcCheckStatuses.Passed or MovieSelectsQcCheckStatuses.Issue))
                .ToArray();
            if (missingRequiredChecks.Length > 0)
            {
                segmentReasons.Add(MovieSelectsQcReasonCodes.EvidenceMissing);
                globalReasons.Add(MovieSelectsQcReasonCodes.EvidenceMissing);
                reviewRequired = true;
            }

            var usableRanges = SubtractBlockingRanges(
                segment.SourceRange,
                blockingRanges,
                requirements.MinimumUsableRangeSeconds,
                segmentReasons);
            if (usableRanges.Count == 0)
            {
                segmentReasons.Add(MovieSelectsQcReasonCodes.NoUsableRange);
                globalReasons.Add(MovieSelectsQcReasonCodes.NoUsableRange);
                reviewRequired = true;
            }
            else if (missingRequiredChecks.Length == 0)
            {
                foreach (var range in usableRanges)
                    recommended.Add(new(segmentId, range));
            }

            var segmentAction = usableRanges.Count == 0
                ? MovieSelectsQcActions.NoUsableRange
                : missingRequiredChecks.Length > 0 || issues.Any(item => item.Severity == MovieSelectsQcSeverities.Warning)
                    ? MovieSelectsQcActions.RequireReview
                    : MovieSelectsQcActions.RecommendSelects;
            segmentDecisions.Add(new(segmentId, segmentAction, usableRanges, issues, Distinct(segmentReasons)));
        }

        if (recommended.Count > 0)
            globalReasons.Add(MovieSelectsQcReasonCodes.Recommended);
        var action = recommended.Count == 0
            ? segmentDecisions.Count > 0 && segmentDecisions.All(item => item.Action == MovieSelectsQcActions.NoUsableRange)
                ? MovieSelectsQcActions.NoUsableRange
                : MovieSelectsQcActions.RequireReview
            : reviewRequired
                ? MovieSelectsQcActions.RequireReview
                : MovieSelectsQcActions.RecommendSelects;
        if (action == MovieSelectsQcActions.NoUsableRange)
            reviewRequired = true;

        return Decision(action, reviewRequired, recommended, segmentDecisions, globalReasons, request);
    }

    private List<string> ValidateSegment(MovieSelectsQcSegmentEvidence segment, NormalizedRequirements requirements)
    {
        var errors = new List<string>();
        if (!IsValidRange(segment.SourceRange)) errors.Add(MovieSelectsQcReasonCodes.EvidenceInvalid);

        var checks = segment.Checks ?? [];
        var seenCategories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var check in checks)
        {
            var category = NormalizeCategory(check?.Category);
            var status = NormalizeStatus(check?.Status);
            if (!MovieSelectsQcIssueCategories.Supported.Contains(category)
                || !MovieSelectsQcCheckStatuses.Supported.Contains(status)
                || !seenCategories.Add(category)
                || !IsBounded(check?.EvidenceReference, settings.MaxEvidenceReferenceLength))
            {
                errors.Add(MovieSelectsQcReasonCodes.EvidenceInvalid);
                continue;
            }
            if (status == MovieSelectsQcCheckStatuses.NotRelevant && requirements.RelevantCategories.Contains(category))
                errors.Add(MovieSelectsQcReasonCodes.EvidenceMissing);
        }

        foreach (var issue in segment.Issues ?? [])
        {
            var category = NormalizeCategory(issue?.Category);
            var severity = NormalizeSeverity(issue?.Severity);
            var issueRange = issue?.Range;
            if (!MovieSelectsQcIssueCategories.Supported.Contains(category)
                || !MovieSelectsQcSeverities.Supported.Contains(severity)
                || !IsBoundedIdentifier(issue?.Code)
                || !IsBounded(issue?.EvidenceReference, settings.MaxEvidenceReferenceLength)
                || issueRange is null
                || !IsValidRange(issueRange)
                || !IsValidRange(segment.SourceRange)
                || issueRange.StartSeconds < segment.SourceRange.StartSeconds
                || issueRange.EndSeconds > segment.SourceRange.EndSeconds)
                errors.Add(MovieSelectsQcReasonCodes.EvidenceInvalid);

            var check = checks.FirstOrDefault(item => NormalizeCategory(item?.Category) == category);
            if (check is null || NormalizeStatus(check.Status) != MovieSelectsQcCheckStatuses.Issue)
                errors.Add(MovieSelectsQcReasonCodes.EvidenceInvalid);
        }

        foreach (var category in requirements.RelevantCategories)
        {
            var check = checks.FirstOrDefault(item => NormalizeCategory(item?.Category) == category);
            if (check is null && requirements.RequireEvidenceForRelevantCategories)
                errors.Add(MovieSelectsQcReasonCodes.EvidenceMissing);
            else if (check is not null && NormalizeStatus(check.Status) == MovieSelectsQcCheckStatuses.Issue
                && !(segment.Issues ?? []).Any(issue => NormalizeCategory(issue?.Category) == category))
                errors.Add(MovieSelectsQcReasonCodes.EvidenceInvalid);
        }

        return Distinct(errors).ToList();
    }

    private List<MovieSelectsQcRange> SubtractBlockingRanges(
        MovieSelectsQcRange source,
        IReadOnlyList<MovieSelectsQcRange> blockingRanges,
        decimal minimumDuration,
        List<string> reasons)
    {
        var remaining = new List<MovieSelectsQcRange> { source };
        foreach (var blocker in blockingRanges)
        {
            var next = new List<MovieSelectsQcRange>();
            foreach (var candidate in remaining)
            {
                if (blocker.EndSeconds <= candidate.StartSeconds || blocker.StartSeconds >= candidate.EndSeconds)
                {
                    next.Add(candidate);
                    continue;
                }
                if (blocker.StartSeconds > candidate.StartSeconds)
                    next.Add(new(candidate.StartSeconds, Math.Min(blocker.StartSeconds, candidate.EndSeconds)));
                if (blocker.EndSeconds < candidate.EndSeconds)
                    next.Add(new(Math.Max(blocker.EndSeconds, candidate.StartSeconds), candidate.EndSeconds));
            }
            remaining = next;
        }

        var coalesced = Coalesce(remaining, Math.Max(settings.CoalesceGapSeconds, 0m));
        var usable = new List<MovieSelectsQcRange>();
        foreach (var range in coalesced)
        {
            if (range.DurationSeconds >= minimumDuration)
                usable.Add(range);
            else
                reasons.Add(MovieSelectsQcReasonCodes.UsableRangeTooShort);
        }
        return usable;
    }

    private NormalizedRequirements NormalizeRequirements(MovieSelectsQcRequirements? requirements)
    {
        var relevant = (requirements?.RelevantCategories ?? MovieSelectsQcIssueCategories.DefaultRelevant)
            .Select(NormalizeCategory)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (relevant.Length == 0 || relevant.Any(category => !MovieSelectsQcIssueCategories.Supported.Contains(category)))
            throw new ArgumentException("Selects QC relevant categories must be supported and non-empty.", nameof(requirements));
        var minimum = requirements?.MinimumUsableRangeSeconds ?? settings.MinimumUsableRangeSeconds;
        if (minimum <= 0m) throw new ArgumentException("Selects QC minimum usable range must be positive.", nameof(requirements));
        return new(relevant, minimum, requirements?.RequireEvidenceForRelevantCategories ?? true);
    }

    private MovieSelectsQcDecision Decision(
        string action,
        bool reviewRequired,
        IReadOnlyList<MovieSelectsQcSelect> recommended,
        IReadOnlyList<MovieSelectsQcSegmentDecision> segments,
        IEnumerable<string> reasons,
        MovieSelectsQcRequest request) => new(
            ContractVersion,
            action,
            reviewRequired,
            recommended,
            segments,
            Distinct(reasons).Take(Max(settings.MaxFindings, 1)).ToArray(),
            request.MovieTakeId,
            request.MovieClipId);

    private MovieSelectsQcSegmentDecision InvalidSegment(string segmentId, string reason) =>
        new(segmentId, MovieSelectsQcActions.RequireReview, [], [], [reason]);

    private string NormalizeIdentifier(string? value) =>
        value?.Trim() is { Length: > 0 } normalized && normalized.Length <= Max(settings.MaxIdentifierLength, 1)
            ? normalized
            : string.Empty;

    private string NormalizeCategory(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;
    private string NormalizeStatus(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;
    private string NormalizeSeverity(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;

    private bool IsBounded(string? value, int maxLength) => value is null || value.Length <= Max(maxLength, 1);
    private bool IsBoundedIdentifier(string? value) => !string.IsNullOrWhiteSpace(value) && IsBounded(value.Trim(), settings.MaxIdentifierLength);

    private static bool IsValidRange(MovieSelectsQcRange? range) =>
        range is not null
        && range.StartSeconds >= 0m
        && range.EndSeconds > range.StartSeconds
        && range.StartSeconds <= decimal.MaxValue
        && range.EndSeconds <= decimal.MaxValue;

    private static IReadOnlyList<MovieSelectsQcIssue> NormalizeIssues(IReadOnlyList<MovieSelectsQcIssue>? issues) =>
        (issues ?? [])
            .Where(issue => issue is not null)
            .Select(issue => issue with
            {
                Category = issue.Category.Trim().ToLowerInvariant(),
                Severity = issue.Severity.Trim().ToLowerInvariant(),
                Code = issue.Code.Trim(),
                EvidenceReference = issue.EvidenceReference?.Trim(),
            })
            .ToArray();

    private static List<MovieSelectsQcRange> Coalesce(IEnumerable<MovieSelectsQcRange> ranges, decimal gap)
    {
        var ordered = ranges.OrderBy(item => item.StartSeconds).ThenBy(item => item.EndSeconds).ToArray();
        var result = new List<MovieSelectsQcRange>();
        foreach (var range in ordered)
        {
            if (result.Count == 0)
            {
                result.Add(range);
                continue;
            }
            var prior = result[^1];
            if (range.StartSeconds <= prior.EndSeconds + gap)
                result[^1] = new(prior.StartSeconds, Math.Max(prior.EndSeconds, range.EndSeconds));
            else
                result.Add(range);
        }
        return result;
    }

    private static IReadOnlyList<string> Distinct(IEnumerable<string> values) =>
        values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray();

    private static int Max(int value, int minimum) => Math.Max(value, minimum);
}
