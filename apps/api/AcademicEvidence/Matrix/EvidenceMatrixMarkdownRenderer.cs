using System.Text;
using Taslim.Api.AcademicEvidence.Domain;

namespace Taslim.Api.AcademicEvidence.Matrix;

/// <summary>
/// Renders the evidence matrix as Markdown for the chapter workspace and for the
/// exported review.
///
/// The renderer never strengthens what the data says: an empty intersection prints
/// as "no evidence found", which is not the same claim as "contradicts", and a
/// source that is unverified is labelled in the table itself so a matrix pasted into
/// a draft carries its own caveats. Ordering is fixed, so two runs over the same
/// evidence produce byte-identical output and a changed table means changed
/// evidence.
/// </summary>
public static class EvidenceMatrixMarkdownRenderer
{
    /// <summary>Cell symbols, chosen to survive plain-text export.</summary>
    public static string Symbol(SourceStance stance) => stance switch
    {
        SourceStance.Supports => "+ supports",
        SourceStance.Contradicts => "- contradicts",
        SourceStance.Mixed => "~ mixed",
        SourceStance.Contextual => "o contextual",
        SourceStance.Indeterminate => "? indeterminate",
        SourceStance.NoEvidence => "no evidence found",
        _ => "-",
    };

    /// <summary>Short provenance label used in the source column.</summary>
    public static string ProvenanceLabel(ProvenanceVerdict verdict) => verdict switch
    {
        ProvenanceVerdict.Verified => "verified",
        ProvenanceVerdict.Unverified => "unverified",
        ProvenanceVerdict.Disputed => "disputed",
        ProvenanceVerdict.Withdrawn => "retracted",
        ProvenanceVerdict.Invalid => "invalid",
        _ => "unchecked",
    };

    /// <summary>Render the whole matrix section, including relationships and gaps.</summary>
    public static string Render(Domain.EvidenceMatrix matrix, Func<Guid, string?> sourceLabel)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        ArgumentNullException.ThrowIfNull(sourceLabel);

        var builder = new StringBuilder(1024);

        builder.Append("## Evidence matrix\n\n");

        if (matrix.Questions.Count == 0 || matrix.SourceIds.Count == 0)
        {
            builder.Append("No sources have been screened into this review yet, so no matrix can be shown.\n\n");
            return builder.ToString();
        }

        builder.Append("| Source | Provenance | Access | ");
        for (var i = 0; i < matrix.Questions.Count; i++)
        {
            builder.Append(EscapeCell(matrix.Questions[i].Text)).Append(" | ");
        }

        builder.Length -= 1;
        builder.Append('\n');

        builder.Append("| --- | --- | --- |");
        for (var i = 0; i < matrix.Questions.Count; i++) builder.Append(" --- |");
        builder.Append('\n');

        foreach (var sourceId in matrix.SourceIds)
        {
            var firstCell = matrix.Cells.FirstOrDefault(c => c.SourceId == sourceId);
            builder.Append("| ").Append(EscapeCell(sourceLabel(sourceId) ?? sourceId.ToString("D")))
                .Append(" | ").Append(ProvenanceLabel(firstCell?.Provenance ?? ProvenanceVerdict.Unassessed))
                .Append(" | ").Append(AccessLabel(firstCell?.AccessLevel ?? SourceAccessLevel.Unknown))
                .Append(" |");

            foreach (var question in matrix.Questions)
            {
                var cell = matrix.Find(question.Key, sourceId);
                var text = cell is null ? "-" : Symbol(cell.Stance);
                if (cell is not null && cell.Flags.Any(f => f.Severity == FindingSeverity.Blocking))
                {
                    text += " [blocked]";
                }

                builder.Append(' ').Append(text).Append(" |");
            }

            builder.Append('\n');
        }

        builder.Append('\n');

        var contradictions = matrix.Relationships
            .Where(r => r.Relation == ClaimRelation.Contradiction)
            .OrderBy(r => r.QuestionKey, StringComparer.Ordinal)
            .ThenBy(r => r.LeftSourceId)
            .ThenBy(r => r.RightSourceId)
            .ToList();

        if (contradictions.Count > 0)
        {
            builder.Append("### Contradictions\n\n");
            foreach (var relationship in contradictions)
            {
                builder.Append("- ").Append(EscapeCell(relationship.QuestionKey)).Append(": ")
                    .Append(sourceLabel(relationship.LeftSourceId) ?? relationship.LeftSourceId.ToString("D"))
                    .Append(" against ")
                    .Append(sourceLabel(relationship.RightSourceId) ?? relationship.RightSourceId.ToString("D"))
                    .Append(" - ").Append(relationship.Explanation ?? "opposing findings.").Append('\n');
            }

            builder.Append('\n');
        }

        var agreements = matrix.Relationships
            .Where(r => r.Relation == ClaimRelation.Agreement)
            .OrderBy(r => r.QuestionKey, StringComparer.Ordinal)
            .ToList();

        if (agreements.Count > 0)
        {
            builder.Append("### Convergence\n\n");
            foreach (var relationship in agreements)
            {
                builder.Append("- ").Append(EscapeCell(relationship.QuestionKey)).Append(": ")
                    .Append(sourceLabel(relationship.LeftSourceId) ?? relationship.LeftSourceId.ToString("D"))
                    .Append(" and ")
                    .Append(sourceLabel(relationship.RightSourceId) ?? relationship.RightSourceId.ToString("D"))
                    .Append(" agree - ").Append(relationship.Explanation ?? "same reported direction.").Append('\n');
            }

            builder.Append('\n');
        }

        if (matrix.Gaps.Count > 0)
        {
            builder.Append("### Research gaps\n\n");
            foreach (var gap in matrix.Gaps
                         .OrderBy(g => g.Severity == FindingSeverity.Blocking ? 0 : g.Severity == FindingSeverity.Warning ? 1 : 2)
                         .ThenBy(g => g.Kind)
                         .ThenBy(g => g.QuestionKey, StringComparer.Ordinal))
            {
                builder.Append("- **").Append(gap.Kind).Append("**");
                if (gap.QuestionKey is { Length: > 0 } key) builder.Append(" (").Append(EscapeCell(key)).Append(')');
                builder.Append(": ").Append(gap.Description);
                if (gap.SuggestedStep is { Length: > 0 } step) builder.Append(" Next: ").Append(step);
                builder.Append('\n');
            }

            builder.Append('\n');
        }

        builder.Append("Reading the table: *no evidence found* means this review retrieved nothing that ")
            .Append("addresses the question, which is not the same as a source disagreeing with it. ")
            .Append("*unverified* means the bibliographic identity has not been confirmed by two ")
            .Append("independent origins.\n");

        return builder.ToString();
    }

    private static string AccessLabel(SourceAccessLevel level) => level switch
    {
        SourceAccessLevel.MetadataOnly => "metadata only",
        SourceAccessLevel.Abstract => "abstract",
        SourceAccessLevel.FullText => "full text",
        SourceAccessLevel.PrivateUpload => "private upload",
        _ => "unknown",
    };

    private static string EscapeCell(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        return value.Replace("|", "\\|").Replace('\n', ' ').Replace('\r', ' ');
    }
}
