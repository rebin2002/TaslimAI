using System.Text.RegularExpressions;

namespace Taslim.Api.AcademicWorkflow;

/// <summary>
/// Syntactic validation of persistent identifiers. A source identity the engine cannot
/// resolve is never accepted, because unverifiable identifiers are the main vector for
/// fabricated citations.
/// </summary>
public static partial class AcademicIdentifierValidator
{
    public static bool IsValidDoi(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return DoiRegex().IsMatch(value.Trim());
    }

    public static bool IsValidIsbn(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var digits = value.Trim().Replace("-", string.Empty).Replace(" ", string.Empty);
        return digits.Length switch
        {
            10 => IsValidIsbn10(digits),
            13 => IsValidIsbn13(digits),
            _ => false,
        };
    }

    public static bool IsValidArXiv(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return ArXivRegex().IsMatch(value.Trim());
    }

    public static bool IsValidPmid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return PmidRegex().IsMatch(value.Trim());
    }

    /// <summary>Absolute http(s) URL with no embedded credentials, file schemes or scripts.</summary>
    public static bool IsValidLandingUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
        return uri.Host.Contains('.', StringComparison.Ordinal);
    }

    public static bool IsValid(AcademicIdentifierKind kind, string? value) => kind switch
    {
        AcademicIdentifierKind.Doi => IsValidDoi(value),
        AcademicIdentifierKind.Isbn => IsValidIsbn(value),
        AcademicIdentifierKind.ArXiv => IsValidArXiv(value),
        AcademicIdentifierKind.Pmid => IsValidPmid(value),
        AcademicIdentifierKind.Url => IsValidLandingUrl(value),
        _ => false,
    };

    private static bool IsValidIsbn10(string value)
    {
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            if (!char.IsAsciiDigit(value[i])) return false;
            sum += (value[i] - '0') * (10 - i);
        }

        var last = value[9] is 'X' or 'x' ? 10 : value[9] - '0';
        if (value[9] is not ('X' or 'x') && !char.IsAsciiDigit(value[9])) return false;
        return (sum + last) % 11 == 0;
    }

    private static bool IsValidIsbn13(string value)
    {
        if (!value.All(char.IsAsciiDigit)) return false;
        var sum = 0;
        for (var i = 0; i < 12; i++)
            sum += (value[i] - '0') * (i % 2 == 0 ? 1 : 3);
        var check = (10 - (sum % 10)) % 10;
        return value[12] - '0' == check;
    }

    [GeneratedRegex(@"^10\.\d{4,9}/[^\s/][^\s]*$", RegexOptions.CultureInvariant)]
    private static partial Regex DoiRegex();

    [GeneratedRegex(@"^(?:arXiv:)?(?:\d{4}\.\d{4,5}(?:\.\d{4})?(?:v\d+)?|[a-zA-Z\-]+(?:\.[A-Z]{2})?/\d{7}(?:v\d+)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex ArXivRegex();

    [GeneratedRegex(@"^[1-9]\d{0,7}$", RegexOptions.CultureInvariant)]
    private static partial Regex PmidRegex();
}

/// <summary>
/// The single place where "may this text be attributed to this source?" is answered.
/// Metadata-only sources carry bibliographic references but can never be quoted or
/// paraphrased as content; abstract-level access permits quoting only what the abstract
/// itself contains and never a locator; locators require full-text access. Every rule
/// here blocks fabrication structurally rather than by prompt instruction.
/// </summary>
public static class AcademicAccessRules
{
    /// <summary>Content evidence (verbatim or paraphrase) requires at least abstract access.</summary>
    public static bool MayCarryContent(AcademicAccessLevel access) => access >= AcademicAccessLevel.Abstract;

    /// <summary>A locator (page/section/paragraph) is only honest for full text we read.</summary>
    public static bool MayCarryLocator(AcademicAccessLevel access) => access >= AcademicAccessLevel.FullText;

    /// <summary>
    /// Verbatim attribution is allowed for full-text sources, and for abstract-level
    /// sources only when the quoted words are actually inside the stored abstract text.
    /// </summary>
    public static bool MayQuote(AcademicSourceRecord source, string quotedText) =>
        source.AccessLevel switch
        {
            AcademicAccessLevel.FullText => true,
            AcademicAccessLevel.Abstract => ContainsNormalized(source.AbstractText, quotedText),
            _ => false,
        };

    public static string NormalizeForContainment(string? value) =>
        string.IsNullOrEmpty(value)
            ? string.Empty
            : string.Join(' ', value.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));

    public static bool ContainsNormalized(string? haystack, string? needle)
    {
        var hay = NormalizeForContainment(haystack);
        var need = NormalizeForContainment(needle);
        if (need.Length == 0) return false;
        return hay.Contains(need, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Deterministic whole-snapshot validation. The engine runs this after every stage merge
/// and before committing the checkpoint: any blocking issue rolls the merge back, so no
/// fabricated identifier, unlicensed quotation, phantom page number, or unresolved
/// citation can ever reach durable state or an export.
/// </summary>
public static class AcademicIntegrityGuard
{
    public const int MaxSources = 400;
    public const int MaxEvidencePerTopicCharacters = 1_200;
    public const int MaxQuoteCharacters = 400;
    public const int MaxBlockCharacters = 4_000;

    /// <summary>Checks the Verify stage handler must declare; the engine requires all of them.</summary>
    public static readonly IReadOnlyList<string> RequiredVerificationChecks =
    [
        "identifier-validation",
        "screening-coverage",
        "citation-resolution",
        "quotation-access-policy",
        "locator-policy",
    ];

    public static IReadOnlyList<AcademicIntegrityIssue> Validate(AcademicWorkflowSnapshot snapshot)
    {
        var issues = new List<AcademicIntegrityIssue>();
        ValidatePlan(snapshot, issues);
        ValidateSources(snapshot, issues);
        ValidateScreening(snapshot, issues);
        ValidateEvidence(snapshot, issues);
        ValidateThemes(snapshot, issues);
        ValidateChapters(snapshot, issues);
        ValidateVerification(snapshot, issues);
        return issues;
    }

    public static bool HasBlockingIssues(AcademicWorkflowSnapshot snapshot) =>
        Validate(snapshot).Any(issue => issue.Severity == AcademicIssueSeverity.Blocking);

    public static IReadOnlyList<AcademicIntegrityIssue> BlockingIssues(AcademicWorkflowSnapshot snapshot) =>
        Validate(snapshot).Where(issue => issue.Severity == AcademicIssueSeverity.Blocking).ToList();

    /// <summary>
    /// Citation resolution: every citation id used by chapters or evidence must exist as a
    /// screened-in source. Public so the export/document lane can re-check before render.
    /// </summary>
    public static IReadOnlyList<AcademicIntegrityIssue> VerifyCitations(AcademicWorkflowSnapshot snapshot)
    {
        var issues = new List<AcademicIntegrityIssue>();
        var included = snapshot.IncludedSourceIds;
        var all = snapshot.Sources.Select(source => source.SourceId).ToHashSet(StringComparer.Ordinal);

        foreach (var item in snapshot.Evidence.Where(item => !all.Contains(item.SourceId)))
            issues.Add(Issue("EVT_UNKNOWN_SOURCE", $"Evidence {item.EvidenceId} references unknown source {item.SourceId}.", AcademicIssueSeverity.Blocking, AcademicStage.Extract, item.SourceId, item.EvidenceId));

        foreach (var chapter in snapshot.Chapters)
        foreach (var block in chapter.Blocks)
        {
            foreach (var citation in block.CitationSourceIds.Distinct(StringComparer.Ordinal))
            {
                if (!all.Contains(citation))
                    issues.Add(Issue("CHA_UNKNOWN_CITATION", $"Chapter {chapter.ChapterId} cites unknown source {citation}.", AcademicIssueSeverity.Blocking, AcademicStage.DraftChapter, citation, block.BlockId));
                else if (!included.Contains(citation))
                    issues.Add(Issue("CHA_CITATION_NOT_INCLUDED", $"Chapter {chapter.ChapterId} cites source {citation} that screening did not include.", AcademicIssueSeverity.Blocking, AcademicStage.DraftChapter, citation, block.BlockId));
            }

            foreach (var quote in block.Quotes)
                QuoteIssues(snapshot, quote.SourceId, quote.QuotedText, quote.Locator, AcademicStage.DraftChapter, block.BlockId, issues);
        }

        return issues;
    }

    private static void ValidatePlan(AcademicWorkflowSnapshot snapshot, List<AcademicIntegrityIssue> issues)
    {
        var plan = snapshot.Plan;
        if (plan is null)
        {
            if (snapshot.Stage(AcademicStage.Plan).State == AcademicStageState.Completed)
                issues.Add(Issue("PLN_MISSING", "The plan stage completed without a plan artifact.", AcademicIssueSeverity.Blocking, AcademicStage.Plan));
            return;
        }

        if (plan.Objectives.Count is 0 or > 12)
            issues.Add(Issue("PLN_OBJECTIVES", "A plan must hold between 1 and 12 objectives.", AcademicIssueSeverity.Blocking, AcademicStage.Plan));
        if (plan.InclusionCriteria.Count is 0 or > 30)
            issues.Add(Issue("PLN_INCLUSION", "A plan must hold between 1 and 30 inclusion criteria.", AcademicIssueSeverity.Blocking, AcademicStage.Plan));
        if (plan.ExclusionCriteria.Count > 30)
            issues.Add(Issue("PLN_EXCLUSION", "A plan may hold at most 30 exclusion criteria.", AcademicIssueSeverity.Blocking, AcademicStage.Plan));
        if (plan.SearchStrategy.Count is 0 or > 40)
            issues.Add(Issue("PLN_SEARCH", "A plan must declare between 1 and 40 bounded catalog queries.", AcademicIssueSeverity.Blocking, AcademicStage.Plan));
        if (plan.ChapterOutline.Count is 0 or > 24)
            issues.Add(Issue("PLN_OUTLINE", "A plan must declare between 1 and 24 chapters.", AcademicIssueSeverity.Blocking, AcademicStage.Plan));
        if (string.IsNullOrWhiteSpace(plan.ResearchQuestion) || plan.ResearchQuestion.Length > 8_000)
            issues.Add(Issue("PLN_QUESTION", "The planned research question must contain 1 to 8,000 characters.", AcademicIssueSeverity.Blocking, AcademicStage.Plan));
        foreach (var spec in plan.SearchStrategy.Where(spec => string.IsNullOrWhiteSpace(spec.Query) || spec.Query.Length > 500))
            issues.Add(Issue("PLN_QUERY", $"Search query '{Truncate(spec.Query)}' is empty or exceeds 500 characters.", AcademicIssueSeverity.Blocking, AcademicStage.Plan));
    }

    private static void ValidateSources(AcademicWorkflowSnapshot snapshot, List<AcademicIntegrityIssue> issues)
    {
        if (snapshot.Sources.Count > snapshot.Request.MaxDiscoverySources || snapshot.Sources.Count > MaxSources)
            issues.Add(Issue("SRC_OVER_LIMIT", $"Discovery produced more sources than the workflow bound allows ({snapshot.Request.MaxDiscoverySources}).", AcademicIssueSeverity.Blocking, AcademicStage.Discover));

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenDedup = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in snapshot.Sources)
        {
            if (!seenIds.Add(source.SourceId))
                issues.Add(Issue("SRC_DUPLICATE_ID", $"Duplicate source id {source.SourceId}.", AcademicIssueSeverity.Blocking, AcademicStage.Discover, source.SourceId));
            if (!seenDedup.Add(source.DedupKey))
                issues.Add(Issue("SRC_DUPLICATE_DEDUP", $"Two sources share dedup key {source.DedupKey}; discovery must normalize before returning.", AcademicIssueSeverity.Blocking, AcademicStage.Discover, source.SourceId));
            if (string.IsNullOrWhiteSpace(source.Title) || source.Title.Length > 600)
                issues.Add(Issue("SRC_TITLE", $"Source {source.SourceId} has a missing or oversized title.", AcademicIssueSeverity.Blocking, AcademicStage.Discover, source.SourceId));
            if (string.IsNullOrWhiteSpace(source.SourceType) || source.SourceType.Length > 64)
                issues.Add(Issue("SRC_TYPE", $"Source {source.SourceId} has a missing or oversized source type.", AcademicIssueSeverity.Blocking, AcademicStage.Discover, source.SourceId));
            if (source.PublicationYear is { } year && (year < 1000 || year > 2100))
                issues.Add(Issue("SRC_YEAR", $"Source {source.SourceId} carries an implausible publication year {year}.", AcademicIssueSeverity.Blocking, AcademicStage.Discover, source.SourceId));
            if (source.IdentifierKind is { } kind && !AcademicIdentifierValidator.IsValid(kind, source.IdentifierValue))
                issues.Add(Issue("SRC_IDENTIFIER_INVALID", $"Source {source.SourceId} declares {kind} identifier '{Truncate(source.IdentifierValue)}' that fails syntax validation.", AcademicIssueSeverity.Blocking, AcademicStage.Discover, source.SourceId));
            if (!string.IsNullOrEmpty(source.Url) && !AcademicIdentifierValidator.IsValidLandingUrl(source.Url))
                issues.Add(Issue("SRC_URL_INVALID", $"Source {source.SourceId} carries a landing URL that is not an absolute credential-free http(s) URL.", AcademicIssueSeverity.Blocking, AcademicStage.Discover, source.SourceId));
                if (source.AccessLevel == AcademicAccessLevel.MetadataOnly && !string.IsNullOrWhiteSpace(source.AbstractText))
                    issues.Add(Issue("SRC_ABSTRACT_WITHOUT_ACCESS", $"Source {source.SourceId} stores abstract text while claiming metadata-only access.", AcademicIssueSeverity.Blocking, AcademicStage.Discover, source.SourceId));
                if (source.AccessLevel == AcademicAccessLevel.Abstract && string.IsNullOrWhiteSpace(source.AbstractText))
                    issues.Add(Issue("SRC_ABSTRACT_MISSING", $"Source {source.SourceId} claims abstract access without abstract text.", AcademicIssueSeverity.Blocking, AcademicStage.Discover, source.SourceId));
            if (source.Authors.Count == 0)
                issues.Add(Issue("SRC_AUTHORS_MISSING", $"Source {source.SourceId} has no authors; keep it as metadata only and never invent them.", AcademicIssueSeverity.Warning, AcademicStage.Discover, source.SourceId));
        }
    }

    private static void ValidateScreening(AcademicWorkflowSnapshot snapshot, List<AcademicIntegrityIssue> issues)
    {
        if (snapshot.Screening.Count == 0)
        {
            if (snapshot.Stage(AcademicStage.Screen).State == AcademicStageState.Completed)
                issues.Add(Issue("SCR_MISSING", "The screening stage completed without decisions.", AcademicIssueSeverity.Blocking, AcademicStage.Screen));
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var decision in snapshot.Screening)
        {
            if (!seen.Add(decision.SourceId))
                issues.Add(Issue("SCR_DUPLICATE", $"Source {decision.SourceId} was screened more than once.", AcademicIssueSeverity.Blocking, AcademicStage.Screen, decision.SourceId));
            if (snapshot.FindSource(decision.SourceId) is null)
                issues.Add(Issue("SCR_UNKNOWN_SOURCE", $"Screening references unknown source {decision.SourceId}.", AcademicIssueSeverity.Blocking, AcademicStage.Screen, decision.SourceId));
            if (string.IsNullOrWhiteSpace(decision.Reason))
                issues.Add(Issue("SCR_REASON", $"Screening decision for {decision.SourceId} lacks a reason.", AcademicIssueSeverity.Blocking, AcademicStage.Screen, decision.SourceId));
            var source = snapshot.FindSource(decision.SourceId);
            if (source?.IsRetracted == true && decision.Decision == AcademicScreeningDecision.Included)
                issues.Add(Issue("SCR_INCLUDE_RETRACTED", $"Retracted source {decision.SourceId} cannot be included.", AcademicIssueSeverity.Blocking, AcademicStage.Screen, decision.SourceId));
        }

        foreach (var missing in snapshot.Sources.Where(source => !seen.Contains(source.SourceId)))
            issues.Add(Issue("SCR_COVERAGE", $"Source {missing.SourceId} has no screening decision.", AcademicIssueSeverity.Blocking, AcademicStage.Screen, missing.SourceId));
    }

    private static void ValidateEvidence(AcademicWorkflowSnapshot snapshot, List<AcademicIntegrityIssue> issues)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var perSource = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in snapshot.Evidence)
        {
            if (!seenIds.Add(item.EvidenceId))
                issues.Add(Issue("EVT_DUPLICATE_ID", $"Duplicate evidence id {item.EvidenceId}.", AcademicIssueSeverity.Blocking, AcademicStage.Extract, item.SourceId, item.EvidenceId));
            perSource.TryGetValue(item.SourceId, out var count);
            perSource[item.SourceId] = count + 1;

            if (string.IsNullOrWhiteSpace(item.Topic) || item.Topic.Length > 160)
                issues.Add(Issue("EVT_TOPIC", $"Evidence {item.EvidenceId} has a missing or oversized topic.", AcademicIssueSeverity.Blocking, AcademicStage.Extract, item.SourceId, item.EvidenceId));
            if (string.IsNullOrWhiteSpace(item.Content) || item.Content.Length > MaxEvidencePerTopicCharacters)
                issues.Add(Issue("EVT_CONTENT", $"Evidence {item.EvidenceId} has empty or oversized content.", AcademicIssueSeverity.Blocking, AcademicStage.Extract, item.SourceId, item.EvidenceId));

            var source = snapshot.FindSource(item.SourceId);
            if (source is not null && snapshot.IncludedSourceIds.Count > 0 && !snapshot.IncludedSourceIds.Contains(item.SourceId))
                issues.Add(Issue("EVT_SOURCE_NOT_INCLUDED", $"Evidence {item.EvidenceId} cites source {item.SourceId} that was not screened in.", AcademicIssueSeverity.Blocking, AcademicStage.Extract, item.SourceId, item.EvidenceId));
            if (source is not null)
            {
                if (!AcademicAccessRules.MayCarryContent(source.AccessLevel))
                    issues.Add(Issue("EVT_METADATA_ONLY", $"Evidence {item.EvidenceId} attributes content to metadata-only source {source.SourceId}.", AcademicIssueSeverity.Blocking, AcademicStage.Extract, source.SourceId, item.EvidenceId));
                else if (item.Verbatim && !AcademicAccessRules.MayQuote(source, item.Content))
                    issues.Add(Issue("EVT_QUOTE_UNVERIFIED", $"Verbatim evidence {item.EvidenceId} is not found in the accessible text of source {source.SourceId}.", AcademicIssueSeverity.Blocking, AcademicStage.Extract, source.SourceId, item.EvidenceId));
                if (item.Locator is not null && !AcademicAccessRules.MayCarryLocator(source.AccessLevel))
                    issues.Add(Issue("EVT_LOCATOR_REQUIRES_FULLTEXT", $"Evidence {item.EvidenceId} cites a locator for non full-text source {source.SourceId}; page numbers are never invented.", AcademicIssueSeverity.Blocking, AcademicStage.Extract, source.SourceId, item.EvidenceId));
            }
        }

        foreach (var pair in perSource.Where(pair => pair.Value > snapshot.Request.MaxEvidencePerSource))
            issues.Add(Issue("EVT_OVER_LIMIT", $"Source {pair.Key} contributed more evidence than the per-source bound ({snapshot.Request.MaxEvidencePerSource}).", AcademicIssueSeverity.Blocking, AcademicStage.Extract, pair.Key));
    }

    private static void ValidateThemes(AcademicWorkflowSnapshot snapshot, List<AcademicIntegrityIssue> issues)
    {
        if (snapshot.Themes.Count == 0)
        {
            if (snapshot.Stage(AcademicStage.Synthesize).State == AcademicStageState.Completed)
                issues.Add(Issue("THM_MISSING", "The synthesis stage completed without themes.", AcademicIssueSeverity.Blocking, AcademicStage.Synthesize));
            return;
        }

        var evidenceIds = snapshot.Evidence.Select(item => item.EvidenceId).ToHashSet(StringComparer.Ordinal);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var theme in snapshot.Themes)
        {
            if (!seenIds.Add(theme.ThemeId))
                issues.Add(Issue("THM_DUPLICATE_ID", $"Duplicate theme id {theme.ThemeId}.", AcademicIssueSeverity.Blocking, AcademicStage.Synthesize, ArtifactId: theme.ThemeId));
            if (string.IsNullOrWhiteSpace(theme.Statement) || theme.Statement.Length > 1_600)
                issues.Add(Issue("THM_STATEMENT", $"Theme {theme.ThemeId} has an empty or oversized statement.", AcademicIssueSeverity.Blocking, AcademicStage.Synthesize, ArtifactId: theme.ThemeId));
            foreach (var id in theme.SupportingEvidenceIds.Concat(theme.ContradictingEvidenceIds).Where(id => !evidenceIds.Contains(id)))
                issues.Add(Issue("THM_UNKNOWN_EVIDENCE", $"Theme {theme.ThemeId} references unknown evidence {id}.", AcademicIssueSeverity.Blocking, AcademicStage.Synthesize, ArtifactId: theme.ThemeId));
            if (theme.SupportingEvidenceIds.Count == 0 && theme.ResearchGaps.Count == 0)
                issues.Add(Issue("THM_UNSUPPORTED", $"Theme {theme.ThemeId} is asserted without supporting evidence or an explicit research gap.", AcademicIssueSeverity.Warning, AcademicStage.Synthesize, ArtifactId: theme.ThemeId));
        }
    }

    private static void ValidateChapters(AcademicWorkflowSnapshot snapshot, List<AcademicIntegrityIssue> issues)
    {
        var citationIssues = VerifyCitations(snapshot);
        issues.AddRange(citationIssues);
        if (snapshot.Chapters.Count == 0)
        {
            if (snapshot.Stage(AcademicStage.DraftChapter).State == AcademicStageState.Completed)
                issues.Add(Issue("CHA_MISSING", "The chapter stage completed without drafts.", AcademicIssueSeverity.Blocking, AcademicStage.DraftChapter));
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var seenBlock = new HashSet<string>(StringComparer.Ordinal);
        var outline = snapshot.Plan?.ChapterOutline ?? [];
        foreach (var chapter in snapshot.Chapters)
        {
            if (!seen.Add(chapter.ChapterId))
                issues.Add(Issue("CHA_DUPLICATE_ID", $"Duplicate chapter id {chapter.ChapterId}.", AcademicIssueSeverity.Blocking, AcademicStage.DraftChapter));
            if (string.IsNullOrWhiteSpace(chapter.Heading) || chapter.Heading.Length > 240)
                issues.Add(Issue("CHA_HEADING", $"Chapter {chapter.ChapterId} has a missing or oversized heading.", AcademicIssueSeverity.Blocking, AcademicStage.DraftChapter));
            else if (outline.Count > 0 && !outline.Contains(chapter.Heading, StringComparer.Ordinal))
                issues.Add(Issue("CHA_OUTLINE_MISMATCH", $"Chapter {chapter.ChapterId} is not part of the approved outline.", AcademicIssueSeverity.Blocking, AcademicStage.DraftChapter));
            if (chapter.Blocks.Count == 0)
                issues.Add(Issue("CHA_EMPTY", $"Chapter {chapter.ChapterId} has no blocks.", AcademicIssueSeverity.Blocking, AcademicStage.DraftChapter));

            foreach (var block in chapter.Blocks)
            {
                if (!seenBlock.Add(block.BlockId))
                    issues.Add(Issue("CHA_BLOCK_DUPLICATE", $"Duplicate block id {block.BlockId}.", AcademicIssueSeverity.Blocking, AcademicStage.DraftChapter, ArtifactId: block.BlockId));
                if (string.IsNullOrWhiteSpace(block.Text) || block.Text.Length > MaxBlockCharacters)
                    issues.Add(Issue("CHA_BLOCK_TEXT", $"Block {block.BlockId} has empty or oversized text.", AcademicIssueSeverity.Blocking, AcademicStage.DraftChapter, ArtifactId: block.BlockId));
                if (block.Kind == AcademicBlockKind.Paragraph && block.CitationSourceIds.Count == 0 && block.Quotes.Count == 0)
                    issues.Add(Issue("CHA_UNCITED_PARAGRAPH", $"Block {block.BlockId} states prose without citations; only framing prose may stay uncited.", AcademicIssueSeverity.Warning, AcademicStage.DraftChapter, ArtifactId: block.BlockId));
                foreach (var quote in block.Quotes.Where(quote => quote.QuotedText.Length > MaxQuoteCharacters))
                    issues.Add(Issue("CHA_QUOTE_TOO_LONG", $"Inline quote in block {block.BlockId} exceeds the {MaxQuoteCharacters}-character quotation bound.", AcademicIssueSeverity.Blocking, AcademicStage.DraftChapter, quote.SourceId, block.BlockId));
            }
        }
    }

    private static void ValidateVerification(AcademicWorkflowSnapshot snapshot, List<AcademicIntegrityIssue> issues)
    {
        var report = snapshot.Verification;
        if (report is null)
            return;
        var missing = RequiredVerificationChecks.Where(check => !report.ChecksRun.Contains(check, StringComparer.Ordinal)).ToList();
        if (missing.Count > 0)
            issues.Add(Issue("VFY_CHECKS_INCOMPLETE", $"Verification report omits required checks: {string.Join(", ", missing)}.", AcademicIssueSeverity.Blocking, AcademicStage.Verify));
    }

    private static void QuoteIssues(
        AcademicWorkflowSnapshot snapshot,
        string sourceId,
        string quotedText,
        string? locator,
        AcademicStage stage,
        string? artifactId,
        List<AcademicIntegrityIssue> issues)
    {
        var source = snapshot.FindSource(sourceId);
        if (source is null)
        {
            issues.Add(Issue("QUO_UNKNOWN_SOURCE", $"Quote references unknown source {sourceId}.", AcademicIssueSeverity.Blocking, stage, sourceId, artifactId));
            return;
        }

        if (!snapshot.IncludedSourceIds.Contains(sourceId))
            issues.Add(Issue("QUO_SOURCE_NOT_INCLUDED", $"Quote references source {sourceId} that was not screened in.", AcademicIssueSeverity.Blocking, stage, sourceId, artifactId));
        if (source.AccessLevel == AcademicAccessLevel.MetadataOnly)
            issues.Add(Issue("QUO_METADATA_ONLY", $"A verbatim quote cannot come from metadata-only source {sourceId}.", AcademicIssueSeverity.Blocking, stage, sourceId, artifactId));
        else if (source.AccessLevel == AcademicAccessLevel.Abstract && !AcademicAccessRules.MayQuote(source, quotedText))
            issues.Add(Issue("QUO_NOT_IN_ABSTRACT", $"Quoted words are not present in the abstract of source {sourceId}; the quote is unverifiable.", AcademicIssueSeverity.Blocking, stage, sourceId, artifactId));
        if (!string.IsNullOrWhiteSpace(locator) && !AcademicAccessRules.MayCarryLocator(source.AccessLevel))
            issues.Add(Issue("QUO_LOCATOR_REQUIRES_FULLTEXT", $"Quote cites a locator for non full-text source {sourceId}; page numbers are never invented.", AcademicIssueSeverity.Blocking, stage, sourceId, artifactId));
    }

    private static AcademicIntegrityIssue Issue(
        string code,
        string detail,
        AcademicIssueSeverity severity,
        AcademicStage stage,
        string? sourceId = null,
        string? ArtifactId = null) =>
        new() { Code = code, Detail = detail, Severity = severity, Stage = stage, SourceId = sourceId, ArtifactId = ArtifactId };

    private static string Truncate(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= 48 ? value : value[..45] + "...";
}
