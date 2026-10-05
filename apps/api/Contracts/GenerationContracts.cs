using System.ComponentModel.DataAnnotations;
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
    int RetryCount = 0);

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
