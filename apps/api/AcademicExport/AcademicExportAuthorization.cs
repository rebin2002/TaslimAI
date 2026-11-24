using Taslim.Api.AcademicTemplates;

namespace Taslim.Api.AcademicExport;

/// <summary>
/// Outcome of one export request: the frozen snapshot for the next round-trip, the decision the
/// policy produced, and whether the request is authorised to render at all. The export path is
/// render-only — when the decision does not authorise the free path the controller must refuse the
/// request outright; it never falls back to a generation job.
/// </summary>
public sealed record AcademicExportAuthorization(
    AcademicReExportDecision Decision,
    AcademicExportSnapshot Snapshot,
    bool Allowed,
    string? RefusalCode = null,
    string? RefusalMessage = null);

/// <summary>
/// The seam between the merged template library's pure policy (#1260/#1265) and the research lanes'
/// saved artifacts. Rules, in order:
///
/// 1. The artifact identity is the completed research job itself, and the content hash is recomputed
///    server-side from the immutable stored payload. A client can never claim "unchanged source" for
///    payload the server does not itself read from the database.
/// 2. First export of a saved report: the report already exists and was paid for when the research
///    job ran; re-rendering it into any template changes only formatting, so this is authorised as
///    <see cref="AcademicReExportPolicy.ModeTemplateReformat"/> with an explicit first-export reason.
/// 3. Subsequent exports compare against the previous snapshot (echoed by the client, validated
///    against the server's own artifact id and content hash before it is trusted for anything).
///    Same settings ⇒ free re-export; different template ⇒ free reformat.
/// 4. Any decision that would trigger a provider call is a refusal here. This controller cannot
///    charge or queue work even if the policy ever says otherwise — the refusal branch is the
///    fail-closed half of the credit-safety guarantee.
/// </summary>
public static class AcademicExportAuthorizer
{
    public const string ArtifactPrefix = "research-job:";
    public const string ReasonFirstAcademicExport = "FIRST_ACADEMIC_EXPORT";

    public static string ArtifactId(Guid jobId) => ArtifactPrefix + jobId.ToString("N");

    public static AcademicExportAuthorization Authorize(
        AcademicTemplateConfiguration target,
        Guid jobId,
        string currentSourceContentHash,
        string? previousSnapshotJson,
        IReadOnlyDictionary<string, string>? studentOverrides,
        DateTimeOffset capturedAtUtc)
    {
        var artifact = ArtifactId(jobId);

        var previous = ParseTrustedSnapshot(previousSnapshotJson, artifact);
        var baseline = previous
            ?? AcademicExportSnapshot.From(
                target,
                artifact,
                currentSourceContentHash,
                capturedAtUtc,
                studentOverrides,
                universityPresetId: null);

        var decision = AcademicReExportPolicy.Evaluate(baseline, target, currentSourceContentHash);

        // A previous snapshot only ever differs from the current hash if the stored payload changed,
        // which cannot happen for an immutable succeeded job; the SOURCE_CHANGED branch is therefore
        // defensive and must fail closed rather than degrade into a paid generation path.
        if (!decision.AllowedWithoutGenerationCharge || decision.TriggersProviderCall)
            return new AcademicExportAuthorization(
                decision,
                baseline,
                Allowed: false,
                decision.ReasonCode,
                "This export could not be authorised as a re-render of the saved report. No new research was started and nothing was charged.");

        var snapshot = AcademicExportSnapshot.From(target, artifact, currentSourceContentHash, capturedAtUtc, studentOverrides, previous?.UniversityPresetId);

        // The first export renders with the same settings that were just frozen into the baseline,
        // so the raw policy labels it "re-export-same-settings". That would claim a history that does
        // not exist; the response is corrected to a reformat of the saved payload with an explicit
        // first-export reason so the customer-facing wording stays truthful.
        var effectiveDecision = previous is null
            ? decision with { Mode = AcademicReExportPolicy.ModeTemplateReformat, ReasonCode = ReasonFirstAcademicExport, Detail = "Rendering uses the saved research payload; this is the first templated export of the report and no AI stage runs." }
            : decision;

        return new AcademicExportAuthorization(effectiveDecision, snapshot, Allowed: true);
    }

    /// <summary>
    /// A snapshot is trusted for the mode label only when it parses at the current schema version and
    /// names exactly the artifact the server is exporting. Everything else is treated as "no previous
    /// export", so a forged or stale echo cannot turn a first export into a claimed re-export with
    /// different content, and cannot affect charging (there is no charging on this path at all).
    /// </summary>
    private static AcademicExportSnapshot? ParseTrustedSnapshot(string? json, string artifact)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var snapshot = AcademicExportSnapshot.FromJson(json);
        if (snapshot is null) return null;
        if (!string.Equals(snapshot.ResearchArtifactId, artifact, StringComparison.Ordinal)) return null;
        if (string.IsNullOrWhiteSpace(snapshot.ConfigurationId)) return null;
        // The configuration must still exist in the versioned registry; a retired design id is
        // treated as unknown history, not as an attack, and never blocks a fresh free render.
        return AcademicTemplateRegistry.GetConfiguration(snapshot.ConfigurationId) is null ? null : snapshot;
    }
}
