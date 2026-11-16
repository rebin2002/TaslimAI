using System.Globalization;
using System.Security.Claims;
using System.Text;
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
        if (!IsSuccessfulResearchJob(job)) return NotFound();
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

    [HttpGet("jobs/{jobId:guid}/sources/export")]
    public async Task<IActionResult> ExportSources(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await jobs.GetAsync(GetUserId(), jobId, cancellationToken);
        if (!IsSuccessfulResearchJob(job)) return NotFound();
        var sources = await db.ResearchSources.AsNoTracking()
            .Where(source => source.GenerationJobId == jobId)
            .OrderBy(source => source.Rank)
            .ThenBy(source => source.CitationId)
            .Select(source => new ResearchSourceExportRow(
                source.CitationId,
                source.SourceType,
                source.Title,
                source.Domain,
                source.Publisher,
                source.Url,
                source.CanonicalUrl,
                source.PublishedAt,
                source.RetrievedAt,
                source.Rank,
                source.IsSelected,
                source.Evidence.Count))
            .ToArrayAsync(cancellationToken);
        var csv = new StringBuilder();
        csv.AppendLine("citationId,sourceType,title,domain,publisher,url,canonicalUrl,publishedAt,retrievedAt,rank,isSelected,evidenceCount");
        foreach (var source in sources)
        {
            csv.AppendLine(string.Join(',',
                CsvField(source.CitationId),
                CsvField(source.SourceType),
                CsvField(source.Title),
                CsvField(source.Domain),
                CsvField(source.Publisher),
                CsvField(source.Url),
                CsvField(source.CanonicalUrl),
                CsvField(source.PublishedAt?.ToString("O", CultureInfo.InvariantCulture)),
                CsvField(source.RetrievedAt.ToString("O", CultureInfo.InvariantCulture)),
                CsvField(source.Rank.ToString(CultureInfo.InvariantCulture)),
                CsvField(source.IsSelected ? "true" : "false"),
                CsvField(source.EvidenceCount.ToString(CultureInfo.InvariantCulture))));
        }
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(csv.ToString());
        return File(bytes, "text/csv; charset=utf-8", $"research-sources-{jobId:N}.csv");
    }

    private static string CsvField(string? value)
    {
        var text = value ?? string.Empty;
        var trimmed = text.TrimStart();
        if (trimmed.Length > 0 && trimmed[0] is '=' or '+' or '-' or '@') text = $"'{text}";
        return $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static bool IsSuccessfulResearchJob(GenerationJob? job) =>
        job is not null
        && job.Status == GenerationJobStatus.Succeeded
        && string.Equals(job.JobType, GenerationJobTypes.ResearchGenerate, StringComparison.OrdinalIgnoreCase);

    private Guid GetUserId() => Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? throw new InvalidOperationException("Authenticated user identifier is missing."));
}

public sealed record CreateResearchGenerationResponse(GenerationJobDto Job);
public sealed record ResearchSourceDetailDto(string CitationId, string? Url, string Title, string Domain, string? Publisher, DateTime? PublishedAt, DateTime RetrievedAt, string SourceType, string? Snippet, string? SearchQuery, int Rank, bool IsSelected, IReadOnlyList<ResearchEvidenceDto> Evidence);
public sealed record ResearchEvidenceDto(string Topic, string Excerpt, string? Context, DateTime? PublishedAt);
internal sealed record ResearchSourceExportRow(string CitationId, string SourceType, string Title, string Domain, string? Publisher, string? Url, string? CanonicalUrl, DateTime? PublishedAt, DateTime RetrievedAt, int Rank, bool IsSelected, int EvidenceCount);
