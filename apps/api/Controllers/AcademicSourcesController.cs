using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Taslim.Api.AcademicSources;
using Taslim.Api.Authorization;
using Taslim.Api.Infrastructure;
namespace Taslim.Api.Controllers;
/// <summary>
/// Issue #1267 integration slice 1: scholarly source discovery wired behind the existing
/// authenticated, workspace-scoped route pattern, and nothing else.
///
/// What this endpoint is:
/// - Read-only. It performs a bounded discovery search and returns normalized bibliographic
///   records. It creates no jobs, reserves no credits, touches no billing or usage table, and
///   stores nothing about the caller beyond what the framework logs.
/// - Workspace-scoped. The route carries a workspace id and membership is enforced with the same
///   <see cref="WorkspaceAccessService.IsMemberAsync"/> gate every other workspace route uses;
///   a non-member gets 403 before any provider fan-out happens, so a guessed workspace id in
///   another student's URL cannot be used to make the server search on their behalf.
/// - Deep-limited and rate-limited. The search depth, page size, identifier counts and term
///   length are clamped server-side from <see cref="AcademicSourceDiscoveryOptions"/>, and the
///   shared "search" partition policy bounds requests per user (plus the global concurrency
///   limiter), so one caller cannot drive the provider pool.
/// - Cancellable. The request cancellation token flows into discovery; a client that aborts
///   (page change, superseded search, closed tab) stops the in-flight provider work.
///
/// What this endpoint is NOT, yet (deliberate scope discipline — do not claim more):
/// - It does not run the academic workflow, save the evidence matrix, or export documents.
/// - It does not retrieve or read full text. Records carry provider-indicated access markers
///   only; a result marked as full-text-indicated means an address was advertised, never that
///   this service read the document.
///
/// Privacy shape rule (mirrors AcademicTemplatesController): the customer-facing DTOs below
/// have no field that can carry a provider, model, routing, cost or credential identifier.
/// `ProviderKey`, `ProviderWorkId` and `SafeMetadataJson` are dropped at mapping time, and per
/// provider outcomes are collapsed into a single boolean `partial` flag with a generic notice.
/// CI asserts the serialized response contains none of the internal catalog keys.
/// </summary>
[ApiController]
[Authorize]
public sealed class AcademicSourcesController(
    IAcademicSourceDiscoveryService discovery,
    WorkspaceAccessService access,
    IOptions<AcademicSourceDiscoveryOptions> optionsAccessor) : ControllerBase
{
    private const int MaxPageSize = 25;
    private const int MaxListParameters = 10;
    private const int MaxSingleIdentifierLength = 256;
    private AcademicSourceDiscoveryOptions Options { get; } = optionsAccessor.Value.Normalize();

    [HttpGet("api/workspaces/{workspaceId:guid}/academic-sources/search")]
    [EnableRateLimiting(RateLimiting.Search)]
    public async Task<IActionResult> Search(Guid workspaceId, [FromQuery] AcademicSourceSearchRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        if (!await access.IsMemberAsync(userId, workspaceId, cancellationToken)) return Forbid();

        var failures = Validate(request);
        if (failures.Count > 0) return ApiResults.Validation(this, "Please check the highlighted fields.", failures);

        if (!Options.Enabled)
            return ApiResults.Error(this, StatusCodes.Status503ServiceUnavailable, "ACADEMIC_SEARCH_UNAVAILABLE", "Scholarly source search is not available right now.");

        var pageSize = Math.Clamp(request.PageSize ?? 10, 1, Math.Min(MaxPageSize, Options.MaxResultsPerProvider));
        var page = Math.Max(1, request.Page ?? 1);
        // Long arithmetic: validation has already proved this product is within
        // MaxCombinedResults, so a crafted page number cannot reach an int overflow here.
        var depth = (int)Math.Min((long)page * pageSize, Options.MaxCombinedResults);

        var batch = await discovery.DiscoverAsync(new AcademicDiscoveryQuery
        {
            SearchTerm = string.IsNullOrWhiteSpace(request.Q) ? null : request.Q.Trim(),
            Dois = (request.Doi ?? []).Where(doi => !string.IsNullOrWhiteSpace(doi)).ToArray(),
            Isbns = (request.Isbn ?? []).Where(isbn => !string.IsNullOrWhiteSpace(isbn)).ToArray(),
            YearFrom = request.YearFrom,
            YearTo = request.YearTo,
            Language = string.IsNullOrWhiteSpace(request.Language) ? null : request.Language.Trim(),
            OpenAccessOnly = request.OpenAccessOnly ?? false,
            MaxResults = depth,
        }, cancellationToken);

        // The merged batch is capped at MaxResults = depth, so any record at or beyond the
        // requested window proves the result set was cut — regardless of which provider said so.
        var totalMatched = batch.Sources.Count;
        var hasMore = totalMatched >= depth;
        var window = batch.Sources.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(AcademicSourcePresentation.ToDto)
            .ToArray();

        return Ok(new AcademicSourceSearchResponse(
            workspaceId,
            page,
            pageSize,
            totalMatched,
            hasMore,
            batch.PartialResult,
            batch.PartialResult ? "Some scholarly catalogs did not answer, so this is not an exhaustive result." : null,
            window,
            batch.DoiValidations.Select(AcademicSourcePresentation.ToDoiDto).ToArray()));
    }

    private Dictionary<string, string[]> Validate(AcademicSourceSearchRequest request)
    {
        var failures = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var term = request.Q?.Trim();
        var dois = (request.Doi ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        var isbns = (request.Isbn ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        if ((term is null || term.Length == 0) && dois.Length == 0 && isbns.Length == 0)
            failures["q"] = ["Provide a search term, at least one DOI, or at least one ISBN."];
        if (term is not null && term.Length > Options.MaxSearchTermCharacters)
            failures["q"] = [$"The search term must be {Options.MaxSearchTermCharacters} characters or fewer."];

        if (dois.Length > MaxListParameters)
            failures["doi"] = [$"Check at most {MaxListParameters} DOIs in one request."];
        else if (dois.Any(doi => doi.Trim().Length > MaxSingleIdentifierLength || AcademicSourceIdentity.NormalizeDoi(doi) is null))
            failures["doi"] = ["One of the DOIs is not a valid DOI."];

        if (isbns.Length > MaxListParameters)
            failures["isbn"] = [$"Look up at most {MaxListParameters} ISBNs in one request."];
        else if (isbns.Any(isbn => isbn.Trim().Length > MaxSingleIdentifierLength || AcademicSourceIdentity.ToIsbn13(isbn) is null))
            failures["isbn"] = ["One of the ISBNs fails its check digit; retype it from the work."];

        if (request.Page is < 1) failures["page"] = ["Page must be 1 or greater."];
        if (request.PageSize is (< 1 or > MaxPageSize)) failures["pageSize"] = [$"Page size must be between 1 and {MaxPageSize}."];
        if (request.Page is { } page && request.PageSize is { } size && (long)page * size > Options.MaxCombinedResults)
            failures["page"] = [$"Results can be browsed up to {Options.MaxCombinedResults / Math.Max(1, size)} pages at this page size."];

        if (request.YearFrom is { } yearFrom && (yearFrom < 1000 || yearFrom > 9999))
            failures["yearFrom"] = ["Use a four-digit year."];
        if (request.YearTo is { } yearTo && (yearTo < 1000 || yearTo > 9999))
            failures["yearTo"] = ["Use a four-digit year."];
        if (request.YearFrom is { } from && request.YearTo is { } to && to < from)
            failures["yearTo"] = ["The end year cannot be earlier than the start year."];

        if (request.Language is { } language && language.Trim().Length > 16)
            failures["language"] = ["The language code is too long."];

        return failures;
    }

    private bool TryGetUserId(out Guid userId) => Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out userId);
}

/// <summary>
/// Query binding for the read-only discovery search. Every field is optional except the
/// requirement that at least one of <see cref="Q"/>, <see cref="Doi"/> or <see cref="Isbn"/>
/// is present; bounds are enforced by the controller, not here.
/// </summary>
public sealed class AcademicSourceSearchRequest
{
    public string? Q { get; set; }
    public string[]? Doi { get; set; }
    public string[]? Isbn { get; set; }
    public int? YearFrom { get; set; }
    public int? YearTo { get; set; }
    public string? Language { get; set; }
    public bool? OpenAccessOnly { get; set; }
    public int? Page { get; set; }
    public int? PageSize { get; set; }
}
