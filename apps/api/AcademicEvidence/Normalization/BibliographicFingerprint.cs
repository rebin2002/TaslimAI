using Taslim.Api.AcademicEvidence.Domain;

namespace Taslim.Api.AcademicEvidence.Normalization;

/// <summary>How strongly two records appear to describe the same work.</summary>
public enum DuplicateConfidence
{
    /// <summary>No shared identity signal.</summary>
    Distinct = 0,

    /// <summary>Shared title and author signal only; review before merging.</summary>
    Probable,

    /// <summary>Shared persistent identifier, or identifier plus matching core fields.</summary>
    Certain,
}

/// <summary>Why two records overlap, which decides whether merging is allowed.</summary>
public enum DuplicateRelation
{
    /// <summary>Nothing links them.</summary>
    Unrelated = 0,

    /// <summary>Same manifestation of the same work; safe to merge.</summary>
    SameWork,

    /// <summary>
    /// A preprint and its version of record: same scholarship, different
    /// bibliographic identity, so they must stay separate rows and be linked.
    /// </summary>
    PreprintOfVersionOfRecord,

    /// <summary>
    /// Different editions or printings of one title: same work, different
    /// pagination, so page numbers must never cross between them.
    /// </summary>
    DifferentEdition,

    /// <summary>
    /// A chapter and the book that contains it: the locator belongs to the
    /// chapter, the publisher to the volume.
    /// </summary>
    PartOfWhole,
}

/// <summary>A detected overlap between two records.</summary>
public sealed record DuplicateCandidate(
    Guid LeftSourceId,
    Guid RightSourceId,
    DuplicateConfidence Confidence,
    DuplicateRelation Relation,
    IReadOnlyList<string> EvidenceKeys);

/// <summary>
/// Deterministic dedupe keys for bibliographic records. Two provider hits for one
/// article must become one source row, otherwise the same study is counted twice
/// in an evidence matrix and a synthesis quietly gains fake corroboration.
/// </summary>
public static class BibliographicFingerprint
{
    /// <summary>
    /// Strongest identity available: DOI, then ISBN-13, then corpus id, then
    /// landing page. Null when the record has no persistent handle.
    /// </summary>
    public static string? PrimaryIdentifierKey(NormalizedBibliographicRecord record)
    {
        var doi = record.NormalizedIdentifier(IdentifierKind.Doi);
        if (doi is not null) return "doi:" + doi;

        var isbn = record.NormalizedIdentifier(IdentifierKind.Isbn);
        if (isbn is not null)
        {
            var isbn13 = BibliographicIdentifierValidator.ToIsbn13(
                BibliographicIdentifierValidator.ValidateIsbn(isbn));
            if (isbn13 is not null) return "isbn:" + isbn13;
        }

        if (!string.IsNullOrWhiteSpace(record.CorpusId)) return "corpus:" + record.CorpusId.Trim().ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(record.LandingPageUrl)
            && Uri.TryCreate(record.LandingPageUrl, UriKind.Absolute, out var uri))
        {
            return "url:" + CanonicalUrl(uri);
        }

        return null;
    }

    /// <summary>
    /// Fallback key built from title, first author surname and year. Deliberately
    /// excludes the venue, because the same thesis is catalogued by a repository
    /// and by a library under different host names.
    /// </summary>
    public static string TitleAuthorYearKey(NormalizedBibliographicRecord record)
    {
        var title = BibliographicRecordNormalizer.FoldIdentity(record.Title);
        var author = AuthorNameNormalizer.FoldForMatching(record.FirstAuthorFamilyName ?? string.Empty);
        var year = record.PublicationYear ?? string.Empty;
        return $"{title}|{author}|{year}";
    }

    /// <summary>True when both records resolve to the same primary identifier.</summary>
    public static bool SharesIdentifier(
        NormalizedBibliographicRecord left,
        NormalizedBibliographicRecord right)
    {
        var a = PrimaryIdentifierKey(left);
        var b = PrimaryIdentifierKey(right);
        return a is not null && string.Equals(a, b, StringComparison.Ordinal);
    }

    /// <summary>
    /// Classify the overlap between two records.
    /// </summary>
    public static DuplicateCandidate Compare(
        NormalizedBibliographicRecord left,
        NormalizedBibliographicRecord right)
    {
        var evidence = new List<string>();
        var confidence = DuplicateConfidence.Distinct;
        var relation = DuplicateRelation.Unrelated;

        var leftId = PrimaryIdentifierKey(left);
        var rightId = PrimaryIdentifierKey(right);

        if (leftId is not null && string.Equals(leftId, rightId, StringComparison.Ordinal))
        {
            confidence = DuplicateConfidence.Certain;
            relation = DuplicateRelation.SameWork;
            evidence.Add(leftId);
        }

        // A DOI and an ISBN cannot name the same article, but two ISBNs of one
        // title are usually the hardback and paperback of one edition.
        if (leftId is not null && rightId is not null && !string.Equals(leftId, rightId, StringComparison.Ordinal))
        {
            var sameTitle = string.Equals(
                BibliographicRecordNormalizer.FoldIdentity(left.Title),
                BibliographicRecordNormalizer.FoldIdentity(right.Title),
                StringComparison.Ordinal);
            if (sameTitle && IsBookPair(left, right))
            {
                confidence = DuplicateConfidence.Probable;
                relation = DuplicateRelation.DifferentEdition;
                evidence.Add("title:" + leftId);
                evidence.Add("title:" + rightId);
            }
        }

        var sameTitleAuthors = RecordsShareTitleAndAuthors(left, right);
        if (sameTitleAuthors)
        {
            evidence.Add("title-author");
            if (confidence == DuplicateConfidence.Distinct)
            {
                confidence = DuplicateConfidence.Probable;
                relation = DuplicateRelation.SameWork;
            }
        }

        if (sameTitleAuthors && IsPreprintVersionPair(left, right))
        {
            relation = DuplicateRelation.PreprintOfVersionOfRecord;
            confidence = DuplicateConfidence.Certain;
            evidence.Add("preprint-of-version-of-record");
        }

        if (sameTitleAuthors && IsPartOfWholePair(left, right))
        {
            relation = DuplicateRelation.PartOfWhole;
            confidence = DuplicateConfidence.Probable;
            evidence.Add("chapter-of-book");
        }

        // Different years with an identifier mismatch are treated as distinct, so
        // two studies with a similar title are not collapsed into one.
        if (confidence != DuplicateConfidence.Distinct
            && left.PublicationYear is not null && right.PublicationYear is not null
            && !string.Equals(left.PublicationYear, right.PublicationYear, StringComparison.Ordinal)
            && confidence == DuplicateConfidence.Probable
            && relation == DuplicateRelation.SameWork)
        {
            confidence = DuplicateConfidence.Distinct;
            relation = DuplicateRelation.Unrelated;
            evidence.Add("year-mismatch");
        }

        return new DuplicateCandidate(left.SourceId, right.SourceId, confidence, relation, evidence);
    }

    /// <summary>
    /// Find every overlapping pair in a set. The scan is deterministic: pairs are
    /// returned ordered by source id, so a rebuild after reordering the input
    /// yields byte-identical output.
    /// </summary>
    public static IReadOnlyList<DuplicateCandidate> FindDuplicates(
        IEnumerable<NormalizedBibliographicRecord> records)
    {
        var list = records.OrderBy(r => r.SourceId).ToList();
        var findings = new List<DuplicateCandidate>();

        for (var i = 0; i < list.Count; i++)
        {
            for (var j = i + 1; j < list.Count; j++)
            {
                var candidate = Compare(list[i], list[j]);
                if (candidate.Confidence != DuplicateConfidence.Distinct) findings.Add(candidate);
            }
        }

        return findings;
    }

    /// <summary>
    /// Choose which record of a duplicate pair carries the merged identity: the
    /// one with the strongest provenance, and on a tie the one with the most
    /// recorded fields. This keeps a thin repository hit from overwriting a
    /// complete registry record.
    /// </summary>
    public static NormalizedBibliographicRecord PreferredSurvivor(
        NormalizedBibliographicRecord left,
        NormalizedBibliographicRecord right,
        Func<NormalizedBibliographicRecord, int> provenanceRank)
    {
        ArgumentNullException.ThrowIfNull(provenanceRank);

        var leftRank = (provenanceRank(left), FieldCount(left));
        var rightRank = (provenanceRank(right), FieldCount(right));

        return rightRank.CompareTo(leftRank) < 0 ? right : left;
    }

    private static int FieldCount(NormalizedBibliographicRecord record) =>
        (string.IsNullOrWhiteSpace(record.Title) ? 0 : 1)
        + (record.PublicationYear is null ? 0 : 1)
        + (string.IsNullOrWhiteSpace(record.Publisher) ? 0 : 1)
        + (string.IsNullOrWhiteSpace(record.Venue) ? 0 : 1)
        + record.Authors.Count
        + record.Identifiers.Count(i => i.IsWellFormed)
        + (record.PageRange is null ? 0 : 1)
        + (record.EditionNumber is null ? 0 : 1);

    private static bool RecordsShareTitleAndAuthors(
        NormalizedBibliographicRecord left,
        NormalizedBibliographicRecord right)
    {
        var leftTitle = BibliographicRecordNormalizer.FoldIdentity(left.Title);
        var rightTitle = BibliographicRecordNormalizer.FoldIdentity(right.Title);
        if (leftTitle.Length == 0 || rightTitle.Length == 0) return false;
        if (!string.Equals(leftTitle, rightTitle, StringComparison.Ordinal)) return false;

        var leftAuthor = AuthorNameNormalizer.FoldForMatching(left.FirstAuthorFamilyName ?? string.Empty);
        var rightAuthor = AuthorNameNormalizer.FoldForMatching(right.FirstAuthorFamilyName ?? string.Empty);
        if (leftAuthor.Length == 0 || rightAuthor.Length == 0) return false;

        return string.Equals(leftAuthor, rightAuthor, StringComparison.Ordinal);
    }

    private static bool IsPreprintVersionPair(
        NormalizedBibliographicRecord left,
        NormalizedBibliographicRecord right)
    {
        var leftIsPreprint = left.SourceKind == AcademicSourceKind.Preprint;
        var rightIsPreprint = right.SourceKind == AcademicSourceKind.Preprint;
        return leftIsPreprint != rightIsPreprint
            && (leftIsPreprint ? right.SourceKind : left.SourceKind)
                is AcademicSourceKind.JournalArticle
                or AcademicSourceKind.ReviewArticle
                or AcademicSourceKind.ConferencePaper;
    }

    private static bool IsBookPair(
        NormalizedBibliographicRecord left,
        NormalizedBibliographicRecord right) =>
        SourceKindMap.IsBookLevel(left.SourceKind) && SourceKindMap.IsBookLevel(right.SourceKind);

    private static bool IsPartOfWholePair(
        NormalizedBibliographicRecord left,
        NormalizedBibliographicRecord right)
    {
        var (part, whole) = left.SourceKind == AcademicSourceKind.BookChapter ? (left, right) : (right, left);
        if (part.SourceKind != AcademicSourceKind.BookChapter) return false;
        if (!SourceKindMap.IsBookLevel(whole.SourceKind)) return false;

        // The chapter names the book it sits in; match that against the volume.
        var host = BibliographicRecordNormalizer.FoldIdentity(part.Venue);
        return host.Length > 0 && host == BibliographicRecordNormalizer.FoldIdentity(whole.Title);
    }

    private static string CanonicalUrl(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];

        var path = uri.AbsolutePath.TrimEnd('/');
        var query = uri.Query.Length <= 1
            ? string.Empty
            : "?" + string.Join('&', uri.Query[1..]
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .OrderBy(part => part, StringComparer.Ordinal));

        return host + path + query;
    }
}
