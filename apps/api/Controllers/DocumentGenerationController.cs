using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Documents;
using Taslim.Api.Generation;
using Taslim.Api.Infrastructure;
using Taslim.Api.Usage;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/document-generation")]
public sealed class DocumentGenerationController(
    IGenerationJobService jobs,
    IOptions<DocumentGenerationOptions> options,
    IUsageCostControl costControl) : ControllerBase
{
    [HttpPost("jobs")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([FromBody] DocumentGenerationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (!options.Value.Enabled)
                return ApiResults.Error(this, StatusCodes.Status503ServiceUnavailable, "DOCUMENT_STUDIO_UNAVAILABLE", "Document generation is not available right now.");
            var input = DocumentGenerationContractMapper.ToInput(request);
            DocumentGenerationRequestValidator.Validate(input, options.Value);
            var estimate = DocumentGenerationCostEstimator.Estimate(options.Value);
            var preflight = await costControl.CheckPreflightAsync(request.WorkspaceId, UsageFeature.Document, estimate, cancellationToken);
            if (!preflight.Allowed)
                return ApiResults.Error(this, StatusCodes.Status429TooManyRequests, preflight.RejectionCode ?? "COST_GUARDRAIL_REJECTED", preflight.RejectionMessage ?? "This operation exceeds a configured safety limit.");
            var job = await jobs.CreateAsync(GetUserId(), new CreateGenerationJobRequest
            {
                WorkspaceId = input.WorkspaceId,
                ProjectId = input.ProjectId,
                JobType = GenerationJobTypes.DocumentGenerate,
                Title = input.Title,
                InputJson = DocumentGenerationContractMapper.SerializeInput(input),
                EstimatedProviderCostUsd = preflight.EstimatedProviderCostUsd,
            }, cancellationToken, Request.Headers["Idempotency-Key"].FirstOrDefault());
            return Accepted(new CreateDocumentGenerationResponse(GenerationJobContractMapper.ToDto(job)));
        }
        catch (DocumentRequestValidationException exception)
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

public sealed record CreateDocumentGenerationResponse(GenerationJobDto Job);
