using System.ComponentModel.DataAnnotations;
using Taslim.Api.Domain;

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

    public decimal? EstimatedProviderCostUsd { get; set; }
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
    IReadOnlyList<GenerationJobOutputDto> Outputs);

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
        job.ErrorMessage,
        job.CancellationRequested,
        job.CreatedAt,
        job.QueuedAt,
        job.StartedAt,
        job.CompletedAt,
        job.FailedAt,
        job.CancelledAt,
        job.Outputs.OrderBy(output => output.CreatedAt)
            .Select(output => new GenerationJobOutputDto(output.Id, output.OutputType, output.StoredFileId, output.MetadataJson, output.CreatedAt))
            .ToArray());
}
