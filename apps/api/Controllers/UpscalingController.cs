using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Taslim.Api.Contracts;
using Taslim.Api.Infrastructure;
using Taslim.Api.Upscaling;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/upscaling/jobs")]
public sealed class UpscalingController(IUpscalingJobService jobs) : ControllerBase
{
    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimiting.ExpensiveAi)]
    public async Task<IActionResult> Create(CreateUpscalingJobRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return ApiResults.Validation(this);
        try
        {
            var job = await jobs.CreateAsync(
                GetUserId(),
                request,
                cancellationToken,
                Request.Headers["Idempotency-Key"].FirstOrDefault(),
                HttpContext.TraceIdentifier);
            return CreatedAtAction(nameof(Get), new { id = job.Id }, UpscalingContractMapper.ToDto(job));
        }
        catch (UpscalingForbiddenException) { return Forbid(); }
        catch (UpscalingValidationException exception) { return ApiResults.Error(this, StatusCodes.Status400BadRequest, exception.Code, exception.Message); }
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var job = await jobs.GetAsync(GetUserId(), id, cancellationToken);
        return job is null
            ? ApiResults.Error(this, StatusCodes.Status404NotFound, "UPSCALING_JOB_NOT_FOUND", "Upscaling job not found.")
            : Ok(UpscalingContractMapper.ToDto(job));
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] Guid workspaceId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        [FromQuery] string? targetResolution = null,
        CancellationToken cancellationToken = default)
    {
        UpscalingJobStatus? filterStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<UpscalingJobStatus>(status, true, out var parsedStatus))
                return ApiResults.Validation(this, "Choose a valid upscaling job status.");
            filterStatus = parsedStatus;
        }
        try
        {
            var result = await jobs.ListAsync(GetUserId(), new UpscalingJobFilter(workspaceId, page, pageSize, filterStatus, targetResolution), cancellationToken);
            return result is null ? Forbid() : Ok(result);
        }
        catch (UpscalingValidationException exception)
        {
            return ApiResults.Error(this, StatusCodes.Status400BadRequest, exception.Code, exception.Message);
        }
    }

    [HttpPost("{id:guid}/retry")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimiting.ExpensiveAi)]
    public async Task<IActionResult> Retry(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var job = await jobs.RetryAsync(GetUserId(), id, cancellationToken);
            return job is null
                ? ApiResults.Error(this, StatusCodes.Status404NotFound, "UPSCALING_JOB_NOT_FOUND", "Upscaling job not found.")
                : Ok(UpscalingContractMapper.ToDto(job));
        }
        catch (UpscalingValidationException exception)
        {
            return ApiResults.Error(this, StatusCodes.Status409Conflict, exception.Code, exception.Message);
        }
    }

    [HttpPost("{id:guid}/cancel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var job = await jobs.CancelAsync(GetUserId(), id, cancellationToken);
            return job is null
                ? ApiResults.Error(this, StatusCodes.Status404NotFound, "UPSCALING_JOB_NOT_FOUND", "Upscaling job not found.")
                : Ok(UpscalingContractMapper.ToDto(job));
        }
        catch (UpscalingValidationException exception)
        {
            return ApiResults.Error(this, StatusCodes.Status409Conflict, exception.Code, exception.Message);
        }
    }

    [HttpPost("{id:guid}/quality-review")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReviewQuality(Guid id, ReviewUpscalingQualityRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var job = await jobs.ReviewQualityAsync(GetUserId(), id, request, cancellationToken);
            return job is null
                ? ApiResults.Error(this, StatusCodes.Status404NotFound, "UPSCALING_JOB_NOT_FOUND", "Upscaling job not found.")
                : Ok(UpscalingContractMapper.ToDto(job));
        }
        catch (UpscalingValidationException exception)
        {
            return ApiResults.Error(this, StatusCodes.Status409Conflict, exception.Code, exception.Message);
        }
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Authenticated user identifier is missing."));
}
