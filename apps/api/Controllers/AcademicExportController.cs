using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.AcademicExport;
using Taslim.Api.AcademicTemplates;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Infrastructure;
using Taslim.Api.Persistence;

namespace Taslim.Api.Controllers;

// ── customer-facing DTOs ────────────────────────────────────────────────────────────────
// Shape rule inherited from the template catalog: design and saved-content data only. No provider,
// model, routing, cost, credit or backend identity field may be added to any record here; the same
// CI privacy guard token scan covers these surfaces (see AcademicExportIntegrationTests).

/// <summary>What the export lane can see about a saved research report before anything is rendered.
/// The access flags per source are the honest "read vs catalogue-only" distinction from the merged
/// evidence work; they are derived from persisted rows, never inferred from titles.</summary>
public sealed record AcademicExportSourceDto(
    string CitationId,
    string Title,
    string? Url,
    string? PublishedYear,
    string SourceType,
    bool ContentAccessed,
    /// <summary>"full_text" | "extracted_text" | "search_result_only" | "metadata_only". Kept apart
    /// from <see cref="ContentAccessed"/> on purpose: some content being held here is not evidence
    /// that anybody read the work, and the surface must be able to say which of the two it means.</summary>
    string AccessTier,
    bool FullTextAccessed,
    string AccessNote);

public sealed record AcademicExportReportSummaryDto(
    Guid JobId,
    string Title,
    string? Subtitle,
    string Language,
    DateTime SavedAtUtc,
    int SectionCount,
    int SourceCount,
    int AccessedSourceCount,
    int MetadataOnlySourceCount,
    /// <summary>Rows whose document itself is in the workspace — the only rows a "full text" claim
    /// may count, and the reason this is separate from <see cref="AccessedSourceCount"/>.</summary>
    int FullTextSourceCount,
    int ExtractedTextSourceCount,
    int SearchResultOnlySourceCount,
    string SourceContentHash,
    IReadOnlyList<AcademicExportSourceDto> Sources);

/// <summary>
/// Renders an existing saved report under a chosen template configuration. Nothing here creates a
/// generation job: the endpoint only reads immutable stored payload and a versioned design spec, so
/// it structurally cannot start provider work or bill a feature.
/// </summary>
public sealed record AcademicExportRequest(
    string ConfigurationId,
    string Format,
    string? PreviousSnapshotJson,
    Dictionary<string, string>? StudentOverrides,
    string? AiDisclosureText);

/// <summary>
/// The bridge between the merged academic template library (#1260/#1265) and completed research
/// reports persisted by the research lanes (#1259 Phase A surface). Endpoints:
///
///   GET  api/academic-export/jobs/{jobId}/report — what is exportable, honestly summarised
///   POST api/academic-export/jobs/{jobId}/export — the saved report rendered as DOCX or PDF
///
/// Guarantees, each pinned by tests in AcademicExportIntegrationTests:
/// - tenant isolation: the job is resolved through the same membership-scoped job service the
///   research routes use; a non-member gets the identical 404 used for a missing job, never 403,
///   so job existence is not leaked across tenants;
/// - no generation charge: no GenerationJob, UsageTransaction or provider attempt is ever created
///   by these endpoints (they hold no cost-control or job-creation dependency at all);
/// - bounded render concurrency: the export POST is synchronous document production (DOCX package
///   build, PDF layout, then a full re-open and validation of the bytes). It carries the established
///   expensive-render policy `expensive-ai`, the same one that bounds the other render and mutation
///   surfaces in this API, so one student cannot pile unbounded renders onto the process. Without an
///   endpoint policy the global concurrency limiter's switch resolves this route to "no limiter",
///   which is exactly the combined-tree gap this closes;
/// - citations preserved verbatim, bibliography re-checked through the merged integrity gate;
/// - metadata-only rows are always labelled as not accessed, and the AI-use disclosure is rendered
///   even when the student has not completed it — this path can never hide the disclosure.
/// </summary>
[ApiController]
[Authorize]
[Route("api/academic-export")]
public sealed class AcademicExportController(
    IGenerationJobService jobs,
    TaslimDbContext db) : ControllerBase
{
    public const string ModeHeader = "X-Academic-Export-Mode";
    public const string ReasonHeader = "X-Academic-Export-Reason";
    public const string SnapshotHeader = "X-Academic-Export-Snapshot";
    private const int MaxStudentOverrides = 24;
    private const int MaxDisclosureCharacters = 6000;

    [HttpGet("jobs/{jobId:guid}/report")]
    public async Task<IActionResult> ReportSummary(Guid jobId, CancellationToken cancellationToken)
    {
        var gate = await LoadExportableJobAsync(jobId, cancellationToken);
        if (gate.Failure is not null) return gate.Failure;
        try
        {
            var report = AcademicSavedReportReader.Read(gate.Job!, gate.Sources);
            return Ok(ToSummary(report));
        }
        catch (AcademicSavedReportException exception)
        {
            return ApiResults.Error(this, StatusCodes.Status409Conflict, exception.Code, exception.Message);
        }
    }

    [HttpPost("jobs/{jobId:guid}/export")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimiting.ExpensiveAi)]
    public async Task<IActionResult> Export(Guid jobId, [FromBody] AcademicExportRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ConfigurationId) || request.ConfigurationId.Length > 200)
            return ApiResults.Error(this, StatusCodes.Status400BadRequest, "ACADEMIC_EXPORT_REQUEST_INVALID", "A template configuration id is required.");
        var format = (request.Format ?? string.Empty).Trim().ToLowerInvariant();
        if (format is not ("docx" or "pdf"))
            return ApiResults.Error(this, StatusCodes.Status400BadRequest, "ACADEMIC_EXPORT_REQUEST_INVALID", "Choose either a DOCX or a PDF export.");
        if (!string.IsNullOrWhiteSpace(request.AiDisclosureText) && request.AiDisclosureText.Length > MaxDisclosureCharacters)
            return ApiResults.Error(this, StatusCodes.Status400BadRequest, "ACADEMIC_EXPORT_REQUEST_INVALID", "The disclosure statement is too long.");
        if (request.StudentOverrides is { Count: > MaxStudentOverrides })
            return ApiResults.Error(this, StatusCodes.Status400BadRequest, "ACADEMIC_EXPORT_REQUEST_INVALID", "Too many formatting adjustments were requested.");

        var configuration = AcademicTemplateRegistry.GetConfiguration(request.ConfigurationId.Trim());
        if (configuration is null)
            return ApiResults.Error(this, StatusCodes.Status404NotFound, "ACADEMIC_TEMPLATE_NOT_FOUND", "Unknown template configuration.");

        var gate = await LoadExportableJobAsync(jobId, cancellationToken);
        if (gate.Failure is not null) return gate.Failure;

        AcademicSavedReport report;
        try
        {
            report = AcademicSavedReportReader.Read(gate.Job!, gate.Sources);
        }
        catch (AcademicSavedReportException exception)
        {
            return ApiResults.Error(this, StatusCodes.Status409Conflict, exception.Code, exception.Message);
        }

        // Quality gates run before any rendering work:
        var direction = AcademicRtlPolicy.IsRtlLanguage(report.Language) ? AcademicTextDirection.Rtl : AcademicTextDirection.Ltr;
        if (!string.Equals(configuration.Spec.Direction, direction, StringComparison.Ordinal))
            return ApiResults.Error(this, StatusCodes.Status409Conflict, "ACADEMIC_EXPORT_DIRECTION_MISMATCH",
                "This template is laid out for the other writing direction than the saved report uses. Pick a matching template instead.");

        var integrityFailure = ValidateBibliography(report);
        if (integrityFailure is not null) return integrityFailure;

        AcademicDesignSpec spec;
        try
        {
            spec = request.StudentOverrides is { Count: > 0 }
                ? configuration.Spec.WithOverrides(request.StudentOverrides)
                : configuration.Spec;
        }
        catch (Exception exception) when (exception is KeyNotFoundException or FormatException or ArgumentException)
        {
            return ApiResults.Error(this, StatusCodes.Status400BadRequest, "ACADEMIC_EXPORT_OVERRIDE_REJECTED",
                "One of the formatting adjustments is not available for this template.");
        }

        // The no-charge policy: the server's own recomputed hash is the only hash that counts.
        var authorization = AcademicExportAuthorizer.Authorize(
            configuration with { Spec = spec },
            jobId,
            report.SourceContentHash,
            request.PreviousSnapshotJson,
            request.StudentOverrides,
            DateTimeOffset.UtcNow);
        if (!authorization.Allowed)
            return ApiResults.Error(this, StatusCodes.Status409Conflict, authorization.RefusalCode ?? "ACADEMIC_EXPORT_REFUSED", authorization.RefusalMessage ?? "This export could not be authorised.");

        byte[] bytes;
        string contentType;
        string extension;
        try
        {
            if (format == "docx")
            {
                bytes = AcademicReportDocxRenderer.Render(spec, report, request.AiDisclosureText);
                contentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
                extension = "docx";
            }
            else
            {
                bytes = AcademicReportPdfRenderer.Render(spec, report, request.AiDisclosureText);
                contentType = "application/pdf";
                extension = "pdf";
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ApiResults.Error(this, StatusCodes.Status500InternalServerError, "ACADEMIC_EXPORT_RENDER_FAILED",
                "The document could not be produced from the saved report. Nothing was charged and no new research was started.");
        }

        var validation = format == "docx"
            ? AcademicExportFileValidator.ValidateDocx(bytes, report, spec)
            : AcademicExportFileValidator.ValidatePdf(bytes, report);
        if (!validation.Passed)
            return ApiResults.Error(this, StatusCodes.Status500InternalServerError, "ACADEMIC_EXPORT_VALIDATION_FAILED",
                $"The produced file failed integrity checks ({string.Join(", ", validation.Violations.Take(5))}). Nothing was charged and no new research was started.");

        var fileName = $"academic-report-{Sanitize(request.ConfigurationId.Trim())}.{extension}";
        Response.Headers[ModeHeader] = authorization.Decision.Mode;
        Response.Headers[ReasonHeader] = authorization.Decision.ReasonCode;
        Response.Headers[SnapshotHeader] = Uri.EscapeDataString(authorization.Snapshot.ToJson());
        return File(bytes, contentType, fileName);
    }

    private async Task<(GenerationJob? Job, IReadOnlyList<ResearchSource> Sources, IActionResult? Failure)> LoadExportableJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await jobs.GetAsync(GetUserId(), jobId, cancellationToken);
        if (job is null
            || job.Status != GenerationJobStatus.Succeeded
            || !string.Equals(job.JobType, GenerationJobTypes.ResearchGenerate, StringComparison.OrdinalIgnoreCase))
        {
            // Same answer for "not yours" and "not there": cross-tenant probes cannot map the space of job ids.
            return (null, Array.Empty<ResearchSource>(), ApiResults.Error(this, StatusCodes.Status404NotFound, "ACADEMIC_EXPORT_NOT_FOUND", "The saved report was not found or is not exportable."));
        }
        var sources = await db.ResearchSources.AsNoTracking()
            .Include(source => source.Evidence)
            .Where(source => source.GenerationJobId == jobId)
            .OrderBy(source => source.Rank)
            .ThenBy(source => source.CitationId)
            .ToListAsync(cancellationToken);
        return (job, sources, null);
    }

    /// <summary>
    /// Re-checks the bibliography through the merged integrity gate (#1260) before shipping a real
    /// export: public sources must carry a resolvable identifier, and nothing synthetic may ever enter
    /// a real document. Workspace-uploaded files are the exemption the gate itself acknowledges: the
    /// stored file in the student's own workspace is the verifiable identifier, and the file's text was
    /// genuinely read, so they are checked for presence of a title instead.
    /// </summary>
    private IActionResult? ValidateBibliography(AcademicSavedReport report)
    {
        var violations = new List<string>();
        var gateEntries = new List<AcademicBibliographyEntry>();
        foreach (var source in report.Sources)
        {
            var uploaded = string.Equals(source.SourceType, "uploaded", StringComparison.OrdinalIgnoreCase);
            if (uploaded)
            {
                if (string.IsNullOrWhiteSpace(source.Title))
                    violations.Add($"{source.CitationId}: uploaded source has no readable title");
                continue;
            }
            gateEntries.Add(new AcademicBibliographyEntry(
                AcademicReportDocxRenderer.BibliographyLine(source),
                Doi: null,
                Isbn: null,
                Url: source.Url,
                MarkedSyntheticPlaceholder: AcademicReportDocxRenderer.BibliographyLine(source).Contains("format sample", StringComparison.OrdinalIgnoreCase)));
        }
        if (gateEntries.Count > 0)
        {
            var report1 = AcademicBibliographyIntegrity.ValidateExportable(gateEntries);
            if (!report1.Passed) violations.AddRange(report1.Violations);
        }
        if (violations.Count == 0) return null;
        return ApiResults.Error(this, StatusCodes.Status409Conflict, "ACADEMIC_BIBLIOGRAPHY_INTEGRITY",
            $"Some sources cannot be cited verifiably in a submitted document ({string.Join("; ", violations.Take(5))}). Review the sources and regenerate the report if the record itself must change.");
    }

    private static AcademicExportReportSummaryDto ToSummary(AcademicSavedReport report) => new(
        report.JobId,
        report.Title,
        report.Subtitle,
        report.Language,
        report.GeneratedAtUtc,
        report.Sections.Count,
        report.Sources.Count,
        report.AccessedSourceCount,
        report.MetadataOnlySourceCount,
        report.FullTextSourceCount,
        report.ExtractedTextSourceCount,
        report.SearchResultOnlySourceCount,
        report.SourceContentHash,
        report.Sources.Select(source => new AcademicExportSourceDto(
            source.CitationId, source.Title, source.Url, source.PublishedYear, source.SourceType,
            source.ContentAccessed, source.AccessTier, source.FullTextAccessed, source.AccessNote)).ToList());

    private static string Sanitize(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '-');
        return builder.ToString().Trim('-');
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? throw new InvalidOperationException("Authenticated user identifier is missing."));
}
