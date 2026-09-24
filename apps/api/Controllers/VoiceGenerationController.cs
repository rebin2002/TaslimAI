using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Infrastructure;
using Taslim.Api.Usage;
using Taslim.Api.Voice;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/voice-generation")]
public sealed class VoiceGenerationController(
    IGenerationJobService jobs,
    IOptions<VoiceGenerationOptions> options,
    IUsageCostControl costControl) : ControllerBase
{
    [HttpPost("jobs")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([FromBody] VoiceGenerationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var input = VoiceGenerationContractMapper.ToInput(request);
            VoiceGenerationRequestValidator.Validate(input, options.Value);
            var preflight = await costControl.CheckPreflightAsync(request.WorkspaceId, UsageFeature.Voice, 0m, cancellationToken);
            if (!preflight.Allowed)
                return ApiResults.Error(this, StatusCodes.Status429TooManyRequests, preflight.RejectionCode ?? "COST_GUARDRAIL_REJECTED", preflight.RejectionMessage ?? "This operation exceeds a configured safety limit.");

            var job = await jobs.CreateAsync(GetUserId(), new CreateGenerationJobRequest
            {
                WorkspaceId = request.WorkspaceId,
                ProjectId = request.ProjectId,
                JobType = GenerationJobTypes.VoiceGenerate,
                Title = request.Title,
                InputJson = VoiceGenerationContractMapper.SerializeInput(input),
                EstimatedProviderCostUsd = preflight.EstimatedProviderCostUsd,
            }, cancellationToken, Request.Headers["Idempotency-Key"].FirstOrDefault());
            return Accepted(new CreateVoiceGenerationResponse(GenerationJobContractMapper.ToDto(job)));
        }
        catch (VoiceRequestValidationException exception)
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
