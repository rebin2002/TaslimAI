using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Taslim.Api.Domain;
using Taslim.Api.Usage;

namespace Taslim.Api.Contracts;

public sealed class CreateGenerationJobRequest
{
    [Required]
    public Guid WorkspaceId { get; set; }

    public Guid? ProjectId { get; set; }

    [Required, StringLength(100)]
    public string JobType { get; set; } = string.Empty;

    [StringLength(160)]
    public string? Title { get; set; }

    [Required, StringLength(100_000)]
    public string InputJson { get; set; } = "{}";

    [JsonIgnore, BindNever]
    public decimal? EstimatedProviderCostUsd { get; set; }

    [JsonIgnore, BindNever]
    public GenerationCostEstimate? InternalCostEstimate { get; set; }

    [JsonIgnore, BindNever]
    public string? InternalCostEstimateJson { get; set; }
    [JsonIgnore]
    public bool ConfirmationAccepted { get; set; }
}

public static class GenerationCostEstimateStatuses
{
    public const string Known = "known";
    public const string Unknown = "unknown";
}

public sealed record GenerationCostWarningDto(string Code, string Severity, string Message);

public sealed record GenerationCostCapDto(
    string Scope,
    decimal? LimitUsd,
    decimal UsedUsd,
    bool UsageKnown,
    decimal? RemainingUsd,
    bool WouldExceed);

/// <summary>
/// User-safe generation economics. It deliberately contains no provider, model, prompt,
/// pricing-source, credential, or raw upstream fields.
/// </summary>
public sealed record GenerationCostPreviewDto(
    string EstimateStatus,
    decimal? EstimatedProviderCostUsd,
    bool EstimatedProviderCostKnown,
    string Currency,
    string? UnknownReason,
    bool Warning,
    bool ConfirmationRequired,
    bool CanProceed,
    GenerationCostCapDto? WorkspaceCap,
    GenerationCostCapDto? UserCap,
    GenerationCostCapDto? ProjectCap,
    IReadOnlyList<GenerationCostWarningDto> Warnings);

public static class GenerationCostPreviewMapper
{
    public static GenerationCostPreviewDto ToDto(GenerationCostPreflightResult result) => new(
        result.Estimate.IsKnown && result.Estimate.AmountUsd.HasValue ? GenerationCostEstimateStatuses.Known : GenerationCostEstimateStatuses.Unknown,
        result.Estimate.IsKnown ? result.Estimate.AmountUsd : null,
        result.Estimate.IsKnown && result.Estimate.AmountUsd.HasValue,
        string.IsNullOrWhiteSpace(result.Estimate.Currency) ? UsageCurrencies.Usd : result.Estimate.Currency,
        result.Estimate.UnknownReason,
        result.Warnings.Count > 0,
        result.ConfirmationRequired,
        result.CanProceed,
        ToCapDto(result.WorkspaceCap),
        ToCapDto(result.UserCap),
        ToCapDto(result.ProjectCap),
        result.Warnings.Select(item => new GenerationCostWarningDto(item.Code, item.Severity, item.Message)).ToArray());

    private static GenerationCostCapDto? ToCapDto(GenerationCostCapState? cap) => cap is null
        ? null
        : new(cap.Scope, cap.LimitUsd, cap.UsedUsd, cap.UsageKnown, cap.RemainingUsd, cap.WouldExceed);
}

public sealed record GenerationJobOutputDto(
    Guid Id,
    string OutputType,
    Guid? StoredFileId,
    string? MetadataJson,
    DateTime CreatedAt);

/// <summary>
/// One rendition advertised by a completed job's published <c>ResultJson</c> snapshot, paired with
/// the <em>live</em> fact the representation download route enforces for that same rendition.
///
/// The snapshot baked at completion by <c>GenerationJobExecution.AddPublishedAssetReference</c>
/// stores identity only (<c>id</c>, <c>type</c>, <c>fileName</c>, <c>contentType</c>) and is written
/// exactly once, so retention pruning a rendition's own <c>StoredFile</c> can never be reflected in
/// it; the studio read models that render their download buttons from that snapshot therefore keep
/// advertising a rendition whose route answers <c>409 ASSET_FILE_UNAVAILABLE</c> (issue #1049, the
/// sibling surface of the library lane in #1030/#1041). This companion list carries the availability
/// <em>next to</em> the snapshot instead of inside it, so historical <c>ResultJson</c> rows stay
/// byte-for-byte immutable while consumers can still withhold a dead-end button per rendition.
///
/// Availability is per representation, never per asset: a job whose PDF was pruned still advertises
/// its ready DOCX. Identity and metadata are retained for withheld entries - only the download
/// promise is withdrawn.
/// </summary>
public sealed record GenerationJobRepresentationDownloadDto(
    Guid Id,
    string? Type,
    string? FileName,
    string? ContentType,
    bool CanDownload);

public sealed record GenerationJobDto(
    Guid Id,
    Guid WorkspaceId,
    Guid? ProjectId,
    string JobType,
    string Status,
    string? Title,
    int ProgressPercent,
    string? ResultJson,
    string? ErrorCode,
    string? ErrorMessage,
    bool CancellationRequested,
    DateTime CreatedAt,
    DateTime? QueuedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    DateTime? FailedAt,
    DateTime? CancelledAt,
    IReadOnlyList<GenerationJobOutputDto> Outputs,
    Guid? RetryOfJobId = null,
    int RetryCount = 0,
    // Omitted from the payload entirely unless the read model was built with live availability
    // data, so every existing consumer sees the byte-identical contract it sees today.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<GenerationJobRepresentationDownloadDto>? RepresentationDownloads = null);

public sealed record GenerationJobListDto(
    IReadOnlyList<GenerationJobDto> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);

public sealed record GenerationJobFilter(
    Guid WorkspaceId,
    int Page = 1,
    int PageSize = 20,
    GenerationJobStatus? Status = null,
    Guid? ProjectId = null,
    string? JobType = null);

public static class GenerationJobContractMapper
{
    public static GenerationJobDto ToDto(GenerationJob job) => new(
        job.Id,
        job.WorkspaceId,
        job.ProjectId,
        job.JobType,
        job.Status.ToString(),
        job.Title,
        job.ProgressPercent,
        job.ResultJson,
        job.ErrorCode,
        GenerationJobErrorMessages.For(job.ErrorCode),
        job.CancellationRequested,
        job.CreatedAt,
        job.QueuedAt,
        job.StartedAt,
        job.CompletedAt,
        job.FailedAt,
        job.CancelledAt,
        job.Outputs.OrderBy(output => output.CreatedAt)
            .Select(output => new GenerationJobOutputDto(output.Id, output.OutputType, output.StoredFileId, output.MetadataJson, output.CreatedAt))
            .ToArray(),
        job.RetryOfJobId,
        job.RetryCount);

    /// <summary>
    /// <see cref="ToDto"/> plus the live per-representation download promise of the published
    /// snapshot (issue #1049).
    ///
    /// <paramref name="publishedRepresentations"/> is the rendition graph the caller deliberately
    /// loaded for this job (typically <c>job.Assets.SelectMany(asset =&gt; asset.Representations)</c>
    /// with <c>ThenInclude(representation =&gt; representation.StoredFile)</c>). Passing <c>null</c>
    /// means the caller did not load it, and the payload then stays byte-for-byte identical to the
    /// <see cref="ToDto"/> contract - no availability is guessed from an unloaded navigation, so a
    /// read model that forgot the include cannot fail-close a live download. Passing a collection
    /// (including an empty one) commits the caller to the facts it holds: every entry advertised by
    /// the snapshot is answered from those rows, and any snapshot entry with no matching loaded
    /// rendition is reported unavailable, because the route would not serve it either.
    ///
    /// <c>ResultJson</c> itself is never rewritten here in either direction: historical published
    /// snapshots stay immutable, and the availability fact rides beside them.
    /// </summary>
    public static GenerationJobDto ToDtoWithRepresentationDownloads(
        GenerationJob job,
        IEnumerable<AssetRepresentation>? publishedRepresentations)
    {
        var dto = ToDto(job);
        if (publishedRepresentations is null) return dto;
        return dto with
        {
            RepresentationDownloads = BuildRepresentationDownloads(
                job.ResultJson,
                BuildRepresentationDownloadability(publishedRepresentations)),
        };
    }

    /// <summary>
    /// Same companion list from an explicit <c>representationId -&gt; downloadable</c> lookup, for
    /// read models that resolve the renditions by the identities the snapshot advertises rather than
    /// through a loaded entity graph. Identical rules: <c>null</c> means "no availability data was
    /// supplied" and leaves the payload untouched; a supplied lookup answers every advertised entry
    /// and fails closed on any identity it cannot account for.
    /// </summary>
    public static GenerationJobDto ToDtoWithRepresentationAvailability(
        GenerationJob job,
        IReadOnlyDictionary<Guid, bool>? downloadableRepresentations)
    {
        var dto = ToDto(job);
        if (downloadableRepresentations is null) return dto;
        return dto with
        {
            RepresentationDownloads = BuildRepresentationDownloads(job.ResultJson, downloadableRepresentations),
        };
    }

    /// <summary>
    /// The rendition identities a published snapshot advertises, so a read model can resolve exactly
    /// those rows (and nothing else) before calling
    /// <see cref="ToDtoWithRepresentationAvailability"/>. Empty when the result is not a published
    /// asset snapshot.
    /// </summary>
    public static IReadOnlyList<Guid> AdvertisedRepresentationIds(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return Array.Empty<Guid>();
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !TryGetProperty(document.RootElement, "representations", out var representations)
                || representations.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<Guid>();
            }

            var ids = new List<Guid>();
            foreach (var representation in representations.EnumerateArray())
            {
                if (representation.ValueKind == JsonValueKind.Object
                    && TryGetGuid(representation, "id", out var id))
                {
                    ids.Add(id);
                }
            }

            return ids;
        }
        catch (JsonException)
        {
            return Array.Empty<Guid>();
        }
    }

    /// <summary>
    /// The one definition of "this rendition can actually be downloaded right now", read from the
    /// same fact <c>AssetService.GetDownloadAsync</c> enforces before it streams a representation:
    /// the rendition's own stored file has to be <see cref="StoredFileStatus.Ready"/>. A pruned
    /// (<c>Deleted</c>), still-processing (<c>Uploading</c>/<c>Processing</c>) or failed file is
    /// unavailable; the <c>AssetRepresentation</c> row itself survives retention, which is why the
    /// status - not the presence of a row - decides. Per representation, never per asset, matching
    /// the library lane's predicate in <c>AssetContractMapper.ToDto</c>.
    /// </summary>
    public static IReadOnlyDictionary<Guid, bool> BuildRepresentationDownloadability(
        IEnumerable<AssetRepresentation> representations)
    {
        var availability = new Dictionary<Guid, bool>();
        foreach (var representation in representations)
        {
            if (representation is null) continue;
            availability[representation.Id] = representation.StoredFile is { Status: StoredFileStatus.Ready };
        }

        return availability;
    }

    /// <summary>
    /// Pairs the renditions a published snapshot advertises with their live availability. Entries
    /// keep their identity and metadata even when withheld - the historical record is not edited,
    /// only the promise attached to it - and a snapshot that advertises nothing (or that cannot be
    /// read as JSON, exactly as the publisher itself tolerates) yields no companion list rather
    /// than an invented one.
    /// </summary>
    private static IReadOnlyList<GenerationJobRepresentationDownloadDto>? BuildRepresentationDownloads(
        string? resultJson,
        IReadOnlyDictionary<Guid, bool> availability)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!TryGetProperty(document.RootElement, "representations", out var representations)
                || representations.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var downloads = new List<GenerationJobRepresentationDownloadDto>();
            foreach (var representation in representations.EnumerateArray())
            {
                if (representation.ValueKind != JsonValueKind.Object) continue;
                if (!TryGetGuid(representation, "id", out var representationId)) continue;
                downloads.Add(new GenerationJobRepresentationDownloadDto(
                    representationId,
                    TryGetString(representation, "type", out var type) ? type : null,
                    TryGetString(representation, "fileName", out var fileName) ? fileName : null,
                    TryGetString(representation, "contentType", out var contentType) ? contentType : null,
                    // Fail closed on unknown state: a snapshot entry the read model cannot account
                    // for is a rendition whose file it has not been told is Ready.
                    availability.TryGetValue(representationId, out var canDownload) && canDownload));
            }

            return downloads;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The publisher writes camelCase; older fixtures and hand-authored payloads have used PascalCase.
    private static bool TryGetProperty(JsonElement value, string name, out JsonElement property)
    {
        if (value.TryGetProperty(name, out property)) return true;
        var pascal = char.ToUpperInvariant(name[0]) + name[1..];
        return value.TryGetProperty(pascal, out property);
    }

    private static bool TryGetString(JsonElement value, string name, out string? text)
    {
        text = null;
        if (!TryGetProperty(value, name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        text = property.GetString();
        return text is not null;
    }

    private static bool TryGetGuid(JsonElement value, string name, out Guid parsed)
    {
        parsed = Guid.Empty;
        if (!TryGetString(value, name, out var text) || !Guid.TryParse(text, out parsed))
        {
            parsed = Guid.Empty;
            return false;
        }

        return true;
    }

    public static GenerationJobDto ToMovieDto(GenerationJob job) => new(
        job.Id,
        job.WorkspaceId,
        job.ProjectId,
        job.JobType,
        job.Status.ToString(),
        job.Title,
        job.ProgressPercent,
        null,
        job.ErrorCode,
        SafeMovieErrorMessage(job.ErrorCode),
        job.CancellationRequested,
        job.CreatedAt,
        job.QueuedAt,
        job.StartedAt,
        job.CompletedAt,
        job.FailedAt,
        job.CancelledAt,
        job.Outputs.OrderBy(output => output.CreatedAt)
            .Select(output => new GenerationJobOutputDto(output.Id, output.OutputType, output.StoredFileId, null, output.CreatedAt))
            .ToArray(),
        job.RetryOfJobId,
        job.RetryCount);

    private static string? SafeMovieErrorMessage(string? errorCode) => string.IsNullOrWhiteSpace(errorCode)
        ? null
        : "The movie generation operation did not complete.";
}
