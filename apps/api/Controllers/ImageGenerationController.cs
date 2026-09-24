using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Images;
using Taslim.Api.Infrastructure;
using Taslim.Api.Usage;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/image-generation")]
public sealed class ImageGenerationController(
    IGenerationJobService jobs,
    IOptions<ImageGenerationOptions> options,
    IImagePromptBuilder promptBuilder,
    IUsageCostControl costControl) : ControllerBase
{
    [HttpPost("jobs")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([FromBody] ImageGenerationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var input = ImageGenerationContractMapper.ToInput(request);
            ImageGenerationRequestValidator.Validate(input, options.Value);
            var prompt = promptBuilder.Build(input);
            var estimate = ImageGenerationCostEstimator.Estimate(prompt, options.Value.Pricing);
            var preflight = await costControl.CheckPreflightAsync(request.WorkspaceId, UsageFeature.Image, estimate, cancellationToken);
            if (!preflight.Allowed)
                return ApiResults.Error(this, StatusCodes.Status429TooManyRequests, preflight.RejectionCode ?? "COST_GUARDRAIL_REJECTED", preflight.RejectionMessage ?? "This operation exceeds a configured safety limit.");
            var job = await jobs.CreateAsync(GetUserId(), new CreateGenerationJobRequest
            {
                WorkspaceId = request.WorkspaceId,
                ProjectId = request.ProjectId,
                JobType = GenerationJobTypes.ImageGenerate,
                Title = request.Title,
                InputJson = System.Text.Json.JsonSerializer.Serialize(input),
                EstimatedProviderCostUsd = preflight.EstimatedProviderCostUsd,
            }, cancellationToken, Request.Headers["Idempotency-Key"].FirstOrDefault());
            return Accepted(new CreateImageGenerationResponse(GenerationJobContractMapper.ToDto(job)));
        }
        catch (ImageRequestValidationException exception)
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
