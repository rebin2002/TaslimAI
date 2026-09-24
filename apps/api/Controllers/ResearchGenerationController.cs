using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Infrastructure;
using Taslim.Api.Persistence;
using Taslim.Api.Research;
using Taslim.Api.Usage;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/research-generation")]
public sealed class ResearchGenerationController(
    IGenerationJobService jobs,
    TaslimDbContext db,
    IOptions<ResearchGenerationOptions> options,
    IUsageCostControl costControl) : ControllerBase
{
    [HttpPost("jobs")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimiting.ExpensiveAi)]
    public async Task<IActionResult> Create([FromBody] ResearchGenerationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (!options.Value.Enabled)
                return ApiResults.Error(this, StatusCodes.Status503ServiceUnavailable, "RESEARCH_STUDIO_UNAVAILABLE", "Research Studio is not available right now.");
            var input = ResearchGenerationContractMapper.ToInput(request);
            ResearchGenerationRequestValidator.Validate(input, options.Value);
            var estimate = ResearchGenerationCostEstimator.Estimate(options.Value);
            var preflight = await costControl.CheckPreflightAsync(input.WorkspaceId, UsageFeature.Research, estimate, cancellationToken);
            if (!preflight.Allowed)
                return ApiResults.Error(this, StatusCodes.Status429TooManyRequests, preflight.RejectionCode ?? "COST_GUARDRAIL_REJECTED", preflight.RejectionMessage ?? "This operation exceeds a configured safety limit.");
            var job = await jobs.CreateAsync(GetUserId(), new CreateGenerationJobRequest
            {
                WorkspaceId = input.WorkspaceId,
                ProjectId = input.ProjectId,
                JobType = GenerationJobTypes.ResearchGenerate,
                Title = input.Title,
                InputJson = ResearchGenerationContractMapper.SerializeInput(input),
                EstimatedProviderCostUsd = preflight.EstimatedProviderCostUsd,
            }, cancellationToken, Request.Headers["Idempotency-Key"].FirstOrDefault());
            return Accepted(new CreateResearchGenerationResponse(GenerationJobContractMapper.ToDto(job)));
        }
        catch (ResearchRequestValidationException exception)
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

    [HttpGet("jobs/{jobId:guid}/sources")]
    public async Task<IActionResult> Sources(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await jobs.GetAsync(GetUserId(), jobId, cancellationToken);
        if (job is null) return NotFound();
        var sources = await db.ResearchSources.AsNoTracking()
            .Where(source => source.GenerationJobId == jobId)
            .OrderBy(source => source.Rank)
            .ThenBy(source => source.CitationId)
            .Select(source => new ResearchSourceDetailDto(
                source.CitationId,
                source.Url,
                source.Title,
                source.Domain,
                source.Publisher,
                source.PublishedAt,
                source.RetrievedAt,
                source.SourceType,
                source.Snippet,
                source.SearchQuery,
                source.Rank,
                source.IsSelected,
                source.Evidence.OrderBy(evidence => evidence.CreatedAt).Select(evidence => new ResearchEvidenceDto(evidence.Topic, evidence.Excerpt, evidence.Context, evidence.PublishedAt)).ToArray()))
            .ToArrayAsync(cancellationToken);
        return Ok(new { jobId, sources });
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? throw new InvalidOperationException("Authenticated user identifier is missing."));
}

public sealed record CreateResearchGenerationResponse(GenerationJobDto Job);
public sealed record ResearchSourceDetailDto(string CitationId, string? Url, string Title, string Domain, string? Publisher, DateTime? PublishedAt, DateTime RetrievedAt, string SourceType, string? Snippet, string? SearchQuery, int Rank, bool IsSelected, IReadOnlyList<ResearchEvidenceDto> Evidence);
public sealed record ResearchEvidenceDto(string Topic, string Excerpt, string? Context, DateTime? PublishedAt);
