using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Infrastructure;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize]
public sealed class GenerationJobsController(IGenerationJobService jobs) : ControllerBase
{
    [HttpPost("api/generation/jobs")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimiting.Generation)]
    public async Task<IActionResult> Create(CreateGenerationJobRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return ApiResults.Validation(this);
        try
        {
            var job = await jobs.CreateAsync(GetUserId(), request, cancellationToken, Request.Headers["Idempotency-Key"].FirstOrDefault());
            return CreatedAtAction(nameof(Get), new { id = job.Id }, GenerationJobContractMapper.ToDto(job));
        }
        catch (GenerationJobForbiddenException) { return Forbid(); }
        catch (GenerationJobValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
    }

    [HttpGet("api/generation/jobs/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var job = await jobs.GetAsync(GetUserId(), id, cancellationToken);
        return job is null ? ApiResults.Error(this, 404, GenerationJobErrorCodes.NotFound, "Job not found.") : Ok(GenerationJobContractMapper.ToDto(job));
    }

    [HttpGet("api/generation/jobs")]
    public async Task<IActionResult> List(
        [FromQuery] Guid workspaceId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        [FromQuery] Guid? projectId = null,
        [FromQuery] string? jobType = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<GenerationJobStatus>(status, true, out var parsedStatus) && !string.IsNullOrWhiteSpace(status))
            return ApiResults.Validation(this, "Choose a valid job status.");
        var result = await jobs.ListAsync(GetUserId(), new GenerationJobFilter(workspaceId, page, pageSize, string.IsNullOrWhiteSpace(status) ? null : parsedStatus, projectId, jobType), cancellationToken);
        return result is null ? Forbid() : Ok(result);
    }

    [HttpPost("api/generation/jobs/{id:guid}/cancel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken)
    {
        return await jobs.CancelAsync(GetUserId(), id, cancellationToken) switch
        {
            GenerationJobCancelResult.Cancelled => Ok(new { status = GenerationJobStatus.Cancelled.ToString() }),
            GenerationJobCancelResult.CancellationRequested => Accepted(new { status = GenerationJobStatus.Running.ToString(), cancellationRequested = true }),
            GenerationJobCancelResult.NotFound => ApiResults.Error(this, 404, GenerationJobErrorCodes.NotFound, "Job not found."),
            GenerationJobCancelResult.Forbidden => Forbid(),
            _ => ApiResults.Error(this, 409, GenerationJobErrorCodes.NotCancellable, "This job can no longer be cancelled."),
        };
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? throw new InvalidOperationException("Authenticated user identifier is missing."));
}
