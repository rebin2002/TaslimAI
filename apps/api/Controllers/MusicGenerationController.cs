using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Infrastructure;
using Taslim.Api.Music;
using Taslim.Api.Usage;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/music-generation")]
public sealed class MusicGenerationController(
    IGenerationJobService jobs,
    IOptions<MusicGenerationOptions> options,
    IUsageCostControl costControl) : ControllerBase
{
    [HttpPost("jobs")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([FromBody] MusicGenerationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var input = MusicGenerationContractMapper.ToInput(request);
            MusicGenerationRequestValidator.Validate(input, options.Value);
            var preflight = await costControl.CheckPreflightAsync(request.WorkspaceId, UsageFeature.Music, 0m, cancellationToken);
            if (!preflight.Allowed)
                return ApiResults.Error(this, StatusCodes.Status429TooManyRequests, preflight.RejectionCode ?? "COST_GUARDRAIL_REJECTED", preflight.RejectionMessage ?? "This operation exceeds a configured safety limit.");
            var job = await jobs.CreateAsync(GetUserId(), new CreateGenerationJobRequest
            {
                WorkspaceId = request.WorkspaceId,
                ProjectId = request.ProjectId,
                JobType = GenerationJobTypes.MusicGenerate,
                Title = request.Title,
                InputJson = System.Text.Json.JsonSerializer.Serialize(input),
                EstimatedProviderCostUsd = preflight.EstimatedProviderCostUsd,
            }, cancellationToken, Request.Headers["Idempotency-Key"].FirstOrDefault());
            return Accepted(new CreateMusicGenerationResponse(GenerationJobContractMapper.ToDto(job)));
        }
        catch (MusicRequestValidationException exception)
        {
            return ApiResults.Error(this, StatusCodes.Status400BadRequest, exception.Code, exception.Message);
        }
        catch (GenerationJobValidationException exception)
        {
            return ApiResults.Error(this, StatusCodes.Status400BadRequest, exception.Code, exception.Message);
        }
        catch (GenerationJobForbiddenException)
        {
            return Forbid();
        }
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? throw new InvalidOperationException("Authenticated user identifier is missing."));
}
