using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Documents;
using Taslim.Api.Generation;
using Taslim.Api.Images;
using Taslim.Api.Infrastructure;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize]
public sealed class GenerationJobsController(
    IGenerationJobService jobs,
    IOptions<ImageGenerationOptions> imageOptions,
    IOptions<DocumentGenerationOptions> documentOptions) : ControllerBase
{
    [HttpPost("api/generation/jobs")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimiting.Generation)]
    public async Task<IActionResult> Create(CreateGenerationJobRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return ApiResults.Validation(this);
        if (string.Equals(request.JobType.Trim(), GenerationJobTypes.ImageGenerate, StringComparison.OrdinalIgnoreCase))
            return ApiResults.Error(this, StatusCodes.Status400BadRequest, GenerationJobErrorCodes.DedicatedRouteRequired, "Use the Image Studio endpoint for image generation.");
        // The dedicated Document Studio route honours the kill switch, but a
        // document.generate job can also be queued through this generic route.
        // Rejecting before CreateAsync keeps the switch from being bypassed and
        // means no job row and no usage reservation are ever written while the
        // studio is disabled.
        if (string.Equals(request.JobType.Trim(), GenerationJobTypes.DocumentGenerate, StringComparison.OrdinalIgnoreCase) && !documentOptions.Value.Enabled)
            return ApiResults.Error(this, StatusCodes.Status503ServiceUnavailable, DocumentStudioAvailability.UnavailableCode, DocumentStudioAvailability.UnavailableMessage);
        try
        {
            var job = await jobs.CreateAsync(GetUserId(), request, cancellationToken, Request.Headers["Idempotency-Key"].FirstOrDefault(), HttpContext.TraceIdentifier);
            return CreatedAtAction(nameof(Get), new { id = job.Id }, GenerationJobContractMapper.ToDto(job));
        }
        catch (GenerationJobForbiddenException) { return Forbid(); }
        catch (GenerationJobValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
    }

    [HttpGet("api/generation/jobs/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        // The published ResultJson snapshot stays exactly as stored; the live per-representation
        // download promise rides beside it, so a rendition whose file a retention prune has taken
        // stops being advertised as downloadable instead of dead-ending in
        // 409 ASSET_FILE_UNAVAILABLE (issue #1049). Authorization is unchanged: availability is
        // resolved only after this caller has been allowed to read the job, and a job whose
        // snapshot advertises no rendition is served byte-for-byte as before.
        var read = await jobs.GetPublishedAsync(GetUserId(), id, cancellationToken);
        return read is null
            ? ApiResults.Error(this, 404, GenerationJobErrorCodes.NotFound, "Job not found.")
            : Ok(GenerationJobContractMapper.ToDtoWithRepresentationAvailability(read.Job, read.RepresentationAvailability));
    }

    [HttpPost("api/generation/jobs/{id:guid}/retry")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimiting.Generation)]
    public async Task<IActionResult> Retry(Guid id, CancellationToken cancellationToken)
    {
        var source = await jobs.GetAsync(GetUserId(), id, cancellationToken);
        if (source is not null && !imageOptions.Value.Enabled && string.Equals(source.JobType, GenerationJobTypes.ImageGenerate, StringComparison.OrdinalIgnoreCase))
            return ApiResults.Error(this, StatusCodes.Status400BadRequest, GenerationJobErrorCodes.ImageStudioUnavailable, "Image generation is not available right now.");
        // A retry resumes provider work, so it is gated by the same kill switch as
        // the original request. The check runs before RetryAsync so the rejected
        // attempt leaves the caller's idempotency key unconsumed: the same key can
        // be replayed safely once Document Studio is re-enabled, instead of forcing
        // a new key that would risk a second draft, render, and Asset publication.
        if (source is not null && !documentOptions.Value.Enabled && string.Equals(source.JobType, GenerationJobTypes.DocumentGenerate, StringComparison.OrdinalIgnoreCase))
            return ApiResults.Error(this, StatusCodes.Status503ServiceUnavailable, DocumentStudioAvailability.UnavailableCode, DocumentStudioAvailability.UnavailableMessage);
        if (source is not null && string.Equals(source.JobType, GenerationJobTypes.ImageGenerate, StringComparison.OrdinalIgnoreCase))
            return ApiResults.Error(this, StatusCodes.Status400BadRequest, GenerationJobErrorCodes.DedicatedRouteRequired, "Use the Image Studio endpoint for image generation.");
        try
        {
            var job = await jobs.RetryAsync(GetUserId(), id, cancellationToken, Request.Headers["Idempotency-Key"].FirstOrDefault(), HttpContext.TraceIdentifier);
            return job is null
                ? ApiResults.Error(this, 404, GenerationJobErrorCodes.NotFound, "Job not found.")
                : Accepted(GenerationJobContractMapper.ToDto(job));
        }
        catch (GenerationJobForbiddenException) { return Forbid(); }
        catch (GenerationJobValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
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
        // Enum.TryParse also accepts the underlying numeric form, so "4" silently parsed as
        // Failed and "99" parsed as an undefined member that matches no stored row. Both were
        // served as a successful page instead of the intended validation error, which made an
        // invalid queue status filter indistinguishable from an empty queue. Accept defined
        // status names only, so filtering the generation-job queue by status stays truthful.
        GenerationJobStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            var requestedStatus = status.Trim();
            if (!Enum.TryParse<GenerationJobStatus>(requestedStatus, true, out var parsedStatus)
                || !Enum.IsDefined(parsedStatus)
                || !string.Equals(parsedStatus.ToString(), requestedStatus, StringComparison.OrdinalIgnoreCase))
                return ApiResults.Validation(this, "Choose a valid job status.");
            statusFilter = parsedStatus;
        }
        var result = await jobs.ListAsync(GetUserId(), new GenerationJobFilter(workspaceId, page, pageSize, statusFilter, projectId, jobType), cancellationToken);
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
