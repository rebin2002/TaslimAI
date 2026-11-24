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
/// - Deep-limited and rate-limited. The per-provider ask, page size, identifier counts and term
///   length are clamped server-side from <see cref="AcademicSourceDiscoveryOptions"/>, and the
///   shared "search" partition policy bounds requests per user (plus the global concurrency
///   limiter), so one caller cannot drive the provider pool. Paging walks a pool whose size is
///   fixed by the search itself, never by the page that was asked for; see
///   <see cref="Search"/> for what <c>totalMatched</c> and <c>hasMore</c> mean.
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
    /// <summary>Page size applied when the caller does not ask for one. Validation uses the same
    /// value, so the depth that is checked is exactly the depth that is served.</summary>
    private const int DefaultPageSize = 10;
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

        var pageSize = EffectivePageSize(request.PageSize);
        var page = Math.Max(1, request.Page ?? 1);
        // The bounded pool is fetched whole, once per request, and the page is a slice of it.
        // Asking the providers for exactly `page * pageSize` instead made the reported universe a
        // function of which page the caller happened to request: page 1 at size 10 could only ever
        // see 10 records, so it reported "10 matched" while `hasMore` said otherwise, and a client
        // that derived its page count from `totalMatched` had no reachable page 2. A page-
        // independent pool also keeps the windows of one search consistent with each other: moving
        // forward neither duplicates a record nor drops one that the previous page had shown. That
        // consistency is a property of ONE search, not of the catalogs — asking again later fetches
        // a fresh pool, and a catalog that had changed between the two requests can change the pool
        // with it. Paging state therefore belongs to the search that returned it.
        var batch = await discovery.DiscoverAsync(new AcademicDiscoveryQuery
        {
            SearchTerm = string.IsNullOrWhiteSpace(request.Q) ? null : request.Q.Trim(),
            Dois = (request.Doi ?? []).Where(doi => !string.IsNullOrWhiteSpace(doi)).ToArray(),
            Isbns = (request.Isbn ?? []).Where(isbn => !string.IsNullOrWhiteSpace(isbn)).ToArray(),
            YearFrom = request.YearFrom,
            YearTo = request.YearTo,
            Language = string.IsNullOrWhiteSpace(request.Language) ? null : request.Language.Trim(),
            OpenAccessOnly = request.OpenAccessOnly ?? false,
            // The per-provider ceiling, not a page depth: <see cref="AcademicSourceDiscoveryService"/>
            // clamps this to MaxResultsPerProvider and the merger caps the combined pool at
            // MaxCombinedResults, so the fan-out stays bounded by configuration rather than by the
            // page size a caller chose.
            MaxResults = Options.MaxResultsPerProvider,
        }, cancellationToken);

        // Enabled master switch, nothing behind it: every catalog switched off or incomplete (a key
        // missing is the usual case). This is not "no records matched", and it must not be served as
        // an empty page 200 — the studio would tell a student their topic found nothing when in fact
        // this deployment cannot look anything up. Same contract as the switched-off path above.
        if (batch.NoProvidersConfigured)
            return ApiResults.Error(this, StatusCodes.Status503ServiceUnavailable, "ACADEMIC_SEARCH_UNAVAILABLE", "Scholarly source search is not available right now.");

        // `totalMatched` is what the bounded search actually surfaced: the number of distinct
        // records in the pool, before the window was taken. Pages above the first therefore exist
        // exactly when the pool has records past the current window.
        var pool = batch.Sources;
        var totalMatched = pool.Count;
        // `hasMore` is a statement about the search, not about the current page, and it is the
        // search's own answer rather than something inferred here from one number: it is true when
        // this search stopped at one of its ceilings — the combined pool filled MaxCombinedResults,
        // a catalog came back with exactly its per-provider ceiling, or the whole-request budget ran
        // out. Any of those means more matched than this search was permitted to surface. A false
        // here means the bounded search completed and this really is everything it could find.
        // Reading it as "there is a next page" strands a caller on page 1; that is answered by
        // `totalMatched` against `pageSize`.
        // A pool that filled the combined bound is truncation the controller can see for itself, so
        // it is OR-ed in rather than trusted to the reporter: a discovery implementation that says
        // nothing about its ceilings must not be able to make this endpoint claim completeness.
        var hasMore = batch.ResultCapped || totalMatched >= Options.MaxCombinedResults;
        // Long arithmetic in every case, and validation has already proved the depth fits
        // MaxCombinedResults. The offset is still computed wider than the multiplication needs, so
        // a page past the end of the pool is an empty window rather than a wrapped-around one.
        var offset = (long)(page - 1) * pageSize;
        var window = offset >= pool.Count
            ? []
            : pool.Skip((int)offset).Take(pageSize)
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

    /// <summary>
    /// The page size this request will actually be served at: the caller's, if given and in range,
    /// otherwise the default, and never more than a single provider is asked for. Validation applies
    /// the same rule, which is the point — checking a depth that is not the depth served is how the
    /// oversized page survived.
    /// </summary>
    private int EffectivePageSize(int? requested) =>
        Math.Clamp(requested ?? DefaultPageSize, 1, Math.Min(MaxPageSize, Options.MaxResultsPerProvider));

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
        // Depth is checked against the page size the request will actually be served at, which is
        // the DEFAULT when the caller left it out. Requiring both parameters to be present before
        // checking meant `?q=x&page=2147483647` — no pageSize — arrived at the slicer untouched,
        // where `(page - 1) * pageSize` was computed as a 32-bit product, wrapped negative, and
        // made a page from the far side of the integer range look legal. The product is formed in
        // the wider type here, so no value of either parameter can reach that arithmetic.
        if (request.Page is { } page && page >= 1)
        {
            var effectivePageSize = EffectivePageSize(request.PageSize);
            if ((long)page * effectivePageSize > Options.MaxCombinedResults)
                failures["page"] = [$"Results can be browsed up to {Options.MaxCombinedResults / effectivePageSize} pages at this page size."];
        }

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
