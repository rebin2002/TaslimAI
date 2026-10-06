using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
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
    IUsageCostControl costControl,
    IEnumerable<IImageGenerationProvider> providers) : ControllerBase
{
    [HttpPost("jobs")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimiting.ExpensiveAi)]
    public async Task<IActionResult> Create([FromBody] ImageGenerationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (!options.Value.Enabled)
                return ApiResults.Error(this, StatusCodes.Status503ServiceUnavailable, "IMAGE_STUDIO_UNAVAILABLE", "Image generation is not available right now.");
            var input = ImageGenerationContractMapper.ToInput(request);
            var provider = providers.FirstOrDefault(item => string.Equals(item.Key, options.Value.ProviderKey, StringComparison.OrdinalIgnoreCase));
            ImageGenerationRequestValidator.Validate(input, options.Value, (provider as IImageGenerationProviderCapabilities)?.Capabilities);
            if (provider is not IImageGenerationProviderReadiness { IsAvailable: true })
                return ApiResults.Error(this, StatusCodes.Status503ServiceUnavailable, GenerationJobErrorCodes.ImageProviderUnavailable, "Image generation is temporarily unavailable. Please try again later.");
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
            }, cancellationToken, Request.Headers["Idempotency-Key"].FirstOrDefault(), requestId: ImageGenerationPersistenceGuards.BuildImageStudioRequestId(HttpContext.TraceIdentifier));
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
        catch (DbUpdateException exception) when (ImageGenerationPersistenceGuards.IsActiveJobConflict(exception))
        {
            return ApiResults.Error(this, StatusCodes.Status409Conflict, ImageGenerationPersistenceGuards.ActiveJobConflictCode, ImageGenerationPersistenceGuards.ActiveJobConflictMessage);
        }
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? throw new InvalidOperationException("Authenticated user identifier is missing."));

}
