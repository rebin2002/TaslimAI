using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Infrastructure;
using Taslim.Api.Social;
using Taslim.Api.Usage;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/social-generation")]
public sealed class SocialGenerationController(
    IGenerationJobService jobs,
    IOptions<SocialGenerationOptions> options,
    IUsageCostControl costControl) : ControllerBase
{
    [HttpPost("jobs")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimiting.ExpensiveAi)]
    public async Task<IActionResult> Create([FromBody] SocialGenerationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (!options.Value.Enabled) return ApiResults.Error(this, StatusCodes.Status503ServiceUnavailable, "SOCIAL_STUDIO_UNAVAILABLE", "Social content generation is not available right now.");
            var input = SocialGenerationContractMapper.ToInput(request);
            SocialGenerationRequestValidator.Validate(input, options.Value);
            var preflight = await costControl.CheckPreflightAsync(input.WorkspaceId, UsageFeature.Social, SocialGenerationCostEstimator.Estimate(options.Value), cancellationToken);
            if (!preflight.Allowed) return ApiResults.Error(this, StatusCodes.Status429TooManyRequests, preflight.RejectionCode ?? "COST_GUARDRAIL_REJECTED", preflight.RejectionMessage ?? "This operation exceeds a configured safety limit.");
            var job = await jobs.CreateAsync(GetUserId(), new CreateGenerationJobRequest
            {
                WorkspaceId = input.WorkspaceId,
                ProjectId = input.ProjectId,
                JobType = GenerationJobTypes.SocialGenerate,
                Title = input.Prompt.Length > 160 ? input.Prompt[..160] : input.Prompt,
                InputJson = SocialGenerationContractMapper.SerializeInput(input),
                EstimatedProviderCostUsd = preflight.EstimatedProviderCostUsd,
            }, cancellationToken, Request.Headers["Idempotency-Key"].FirstOrDefault());
            return Accepted(new CreateSocialGenerationResponse(GenerationJobContractMapper.ToDto(job)));
        }
        catch (SocialRequestValidationException exception) { return ApiResults.Error(this, StatusCodes.Status400BadRequest, exception.Code, exception.Message); }
        catch (GenerationJobValidationException exception) { return ApiResults.Error(this, StatusCodes.Status400BadRequest, exception.Code, exception.Message); }
        catch (GenerationJobForbiddenException) { return Forbid(); }
        catch (UsageGuardrailRejectedException exception) { return ApiResults.Error(this, StatusCodes.Status429TooManyRequests, exception.Code, exception.Message); }
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? throw new InvalidOperationException("Authenticated user identifier is missing."));
}

public sealed record CreateSocialGenerationResponse(GenerationJobDto Job);
