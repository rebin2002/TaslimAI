using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Infrastructure;
using Taslim.Api.Presentations;
using Taslim.Api.Usage;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/presentation-generation")]
public sealed class PresentationGenerationController(
    IGenerationJobService jobs,
    IOptions<PresentationGenerationOptions> options,
    IUsageCostControl costControl) : ControllerBase
{
    [HttpPost("jobs")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([FromBody] PresentationGenerationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (!options.Value.Enabled)
                return ApiResults.Error(this, StatusCodes.Status503ServiceUnavailable, "PRESENTATION_STUDIO_UNAVAILABLE", "Presentation generation is not available right now.");
            var input = PresentationGenerationContractMapper.ToInput(request);
            PresentationGenerationRequestValidator.Validate(input, options.Value);
            var estimate = PresentationGenerationCostEstimator.Estimate(options.Value);
            var preflight = await costControl.CheckPreflightAsync(input.WorkspaceId, UsageFeature.Presentation, estimate, cancellationToken);
            if (!preflight.Allowed)
                return ApiResults.Error(this, StatusCodes.Status429TooManyRequests, preflight.RejectionCode ?? "COST_GUARDRAIL_REJECTED", preflight.RejectionMessage ?? "This operation exceeds a configured safety limit.");
            var job = await jobs.CreateAsync(GetUserId(), new CreateGenerationJobRequest
            {
                WorkspaceId = input.WorkspaceId,
                ProjectId = input.ProjectId,
                JobType = GenerationJobTypes.PresentationGenerate,
                Title = input.Title,
                InputJson = PresentationGenerationContractMapper.SerializeInput(input),
                EstimatedProviderCostUsd = preflight.EstimatedProviderCostUsd,
            }, cancellationToken, Request.Headers["Idempotency-Key"].FirstOrDefault());
            return Accepted(new CreatePresentationGenerationResponse(GenerationJobContractMapper.ToDto(job)));
        }
        catch (PresentationRequestValidationException exception)
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
        catch (UsageGuardrailRejectedException exception)
        {
            return ApiResults.Error(this, StatusCodes.Status429TooManyRequests, exception.Code, exception.Message);
        }
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? throw new InvalidOperationException("Authenticated user identifier is missing."));
}

public sealed record CreatePresentationGenerationResponse(GenerationJobDto Job);
