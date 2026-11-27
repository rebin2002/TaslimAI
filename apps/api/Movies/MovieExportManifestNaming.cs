namespace Taslim.Api.Movies;

/// <summary>
/// Composes the label that identifies one Movie project export manifest. The
/// manifest label is the title of the generation job that carries the export
/// request, so it must stay inside the persisted job-title column bound on every
/// project, exactly like the exporter's document name must stay inside its own
/// bound. The bound is a hard product rule rather than a display preference: an
/// over-long label is rejected by the database during queue admission, which
/// strands the assembly in the terminal queue-failure state and denies the
/// customer an export that was legitimately requested.
/// </summary>
public static class MovieExportManifestNaming
{
    /// <summary>
    /// The persisted bound of <c>GenerationJob.Title</c>. Mirrors the entity
    /// configuration; the contract test pins the two together.
    /// </summary>
    public const int JobTitleMaxLength = 160;

    /// <summary>
    /// The invariant delivery suffix that identifies a master export job. It is
    /// always preserved, so a truncated manifest label still reads as the export
    /// it belongs to instead of degrading into an ambiguous title.
    /// </summary>
    public const string ExportSuffix = " master export";

    /// <summary>
    /// The literal length of <see cref="ExportSuffix"/>. It is spelled out as a
    /// constant because the derived bound below must stay a compile-time
    /// constant; the contract test fails the build if the two ever drift.
    /// </summary>
    public const int ExportSuffixLength = 14;

    /// <summary>
    /// Longest project title that survives intact: the column bound minus the
    /// invariant suffix. Project titles are admitted up to the full column bound,
    /// so anything above this length must be cut, not rejected.
    /// </summary>
    public const int MaximumIntactTitleLength = JobTitleMaxLength - ExportSuffixLength;

    private const string FallbackTitle = "Movie";

    /// <summary>
    /// Builds the manifest export label for a project title. The result is
    /// deterministic (the same request always replays under the same label) and
    /// never longer than <see cref="JobTitleMaxLength"/>. A supplementary
    /// character is never cut in half, because a lone surrogate corrupts the
    /// job title once it is re-encoded for the API response.
    /// </summary>
    public static string ComposeExportLabel(string? projectTitle)
    {
        var title = (projectTitle ?? string.Empty).Trim();
        if (title.Length > MaximumIntactTitleLength)
        {
            title = TruncateOnCharacterBoundary(title, MaximumIntactTitleLength).TrimEnd();
        }

        if (title.Length == 0)
        {
            // Admission already rejects an empty title; this keeps the label
            // meaningful for legacy rows instead of emitting the bare suffix.
            title = FallbackTitle;
        }

        return string.Concat(title, ExportSuffix);
    }

    private static string TruncateOnCharacterBoundary(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        var length = maxLength > 0 && char.IsHighSurrogate(value[maxLength - 1]) ? maxLength - 1 : maxLength;
        return value[..Math.Max(0, length)];
    }
}
