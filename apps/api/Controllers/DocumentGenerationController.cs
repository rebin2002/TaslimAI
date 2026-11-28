using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Taslim.Api.Authorization;
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
    IUsageCostControl costControl,
    WorkspaceAccessService access) : ControllerBase
{
    [HttpPost("jobs")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimiting.ExpensiveAi)]
    public async Task<IActionResult> Create([FromBody] DocumentGenerationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var userId = GetUserId();
            if (!await access.IsMemberAsync(userId, request.WorkspaceId, cancellationToken))
                return Forbid();
            if (!options.Value.Enabled)
                return ApiResults.Error(this, StatusCodes.Status503ServiceUnavailable, "DOCUMENT_STUDIO_UNAVAILABLE", "Document generation is not available right now.");
            // Retry safety belongs to admission, not to execution. Without a key, a submission whose
            // response the caller never saw - a dropped connection, a client-level retry, a scripted
            // caller - queues a second full generation: a second provider call against the workspace
            // cost ceiling plus a second Asset for one document. docs/DOCUMENT_STUDIO_ARCHITECTURE.md
            // requires a document retry to be coupled to an idempotency key before it may safely
            // create another draft, render and Asset publication attempt, and the sibling expensive AI
            // creation routes already fail closed here (Presentation, Social). The shipped browser
            // client always sends a key (apps/web/src/lib/api.ts createDocumentGenerationJob uses
            // requestId()), so this rejects only callers that cannot prove a retry is safe.
            var idempotencyKey = Request.Headers["Idempotency-Key"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(idempotencyKey))
                return ApiResults.Error(this, StatusCodes.Status400BadRequest, "IDEMPOTENCY_KEY_REQUIRED", "A unique Idempotency-Key header is required for document generation.");
            var input = DocumentGenerationContractMapper.ToInput(request);
            DocumentGenerationRequestValidator.Validate(input, options.Value);
            var estimate = DocumentGenerationCostEstimator.Estimate(options.Value);
            var preflight = await costControl.CheckPreflightAsync(request.WorkspaceId, UsageFeature.Document, estimate, cancellationToken);
            if (!preflight.Allowed)
                return ApiResults.Error(this, StatusCodes.Status429TooManyRequests, preflight.RejectionCode ?? "COST_GUARDRAIL_REJECTED", preflight.RejectionMessage ?? "This operation exceeds a configured safety limit.");
            var job = await jobs.CreateAsync(userId, new CreateGenerationJobRequest
            {
                WorkspaceId = input.WorkspaceId,
                ProjectId = input.ProjectId,
                JobType = GenerationJobTypes.DocumentGenerate,
                Title = input.Title,
                InputJson = DocumentGenerationContractMapper.SerializeInput(input),
                EstimatedProviderCostUsd = preflight.EstimatedProviderCostUsd,
            }, cancellationToken, idempotencyKey);
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
