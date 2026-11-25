using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Taslim.Api.AcademicSources;
using Taslim.Api.Authorization;
using Taslim.Api.Controllers;
using Taslim.Api.Domain;
using Taslim.Api.Infrastructure;
using Taslim.Api.Persistence;
using Taslim.Api.Tests.AcademicSources;
using Xunit;

namespace Taslim.Api.Tests.AcademicSources;

/// <summary>
/// Issue #1267 slice 1 — contract of the workspace-scoped scholarly search endpoint.
/// These tests pin the limits the issue demands: membership before fan-out, bounded
/// pagination, provider-identity-free responses, metadata-vs-fulltext integrity,
/// validation, and cancellation propagation. HTTP-level authentication and cross
/// workspace isolation are covered by <see cref="AcademicSourcesApiTests"/>.
/// </summary>
public sealed class AcademicSourcesControllerTests
{
    private static readonly string[] InternalCatalogKeys = ["openalex", "crossref", "openlibrary", "googlebooks", ".googleapis.com", "api.openalex.org", "api.crossref.org"];

    [Fact]
    public async Task Non_member_is_forbidden_before_any_discovery_call()
    {
        await using var db = await CreateDbAsync();
        var fake = new CountingDiscovery();
        var controller = CreateController(db, fake, enabled: true);

        // A workspace id the caller has no membership row for — including one they could guess.
        var result = await controller.Search(Guid.NewGuid(), new AcademicSourceSearchRequest { Q = "pharmacy market access" }, CancellationToken.None);

        Assert.Equal(0, fake.CallCount);
        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task Missing_user_identifier_is_unauthorized_before_membership_check()
    {
        await using var db = await CreateDbAsync();
        var fake = new CountingDiscovery();
        var controller = CreateController(db, fake, enabled: true, nameIdentifier: "not-a-guid");

        var result = await controller.Search(Guid.NewGuid(), new AcademicSourceSearchRequest { Q = "anything" }, CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
        Assert.Equal(0, fake.CallCount);
    }

    [Fact]
    public async Task Disabled_discovery_returns_service_unavailable_without_calling_providers()
    {
        await using var db = await CreateDbAsync();
        var fake = new CountingDiscovery();
        var controller = CreateController(db, fake, enabled: false);
        var workspaceId = await MemberWorkspaceIdAsync(db);

        var result = await controller.Search(workspaceId, new AcademicSourceSearchRequest { Q = "pharmacy market access" }, CancellationToken.None);

        var error = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, error.StatusCode);
        Assert.Equal(0, fake.CallCount);
    }

    [Fact]
    public async Task Switch_on_with_nothing_behind_it_is_unavailable_not_an_empty_page()
    {
        await using var db = await CreateDbAsync();
        // The master switch is on, so this reaches discovery — and discovery answers that no catalog
        // behind the switch can serve the query at all (every row switched off, or the one enabled
        // row missing its key). Serving that as an empty page 200 would tell a student their topic
        // matched nothing. It is the same 503 contract as the switched-off path above.
        var fake = new RecordingDiscovery((_, _) => Task.FromResult(Batch([], noProvidersConfigured: true)));
        var controller = CreateController(db, fake, enabled: true);

        var result = await controller.Search(await MemberWorkspaceIdAsync(db), new AcademicSourceSearchRequest { Q = "pharmacy market access" }, CancellationToken.None);

        var error = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, error.StatusCode);
        Assert.Single(fake.Queries);
    }

    [Fact]
    public async Task Search_without_any_query_condition_is_rejected()
    {
        await using var db = await CreateDbAsync();
        var fake = new CountingDiscovery();
        var controller = CreateController(db, fake, enabled: true);

        var result = await controller.Search(await MemberWorkspaceIdAsync(db), new AcademicSourceSearchRequest(), CancellationToken.None);

        AssertBadRequest(result);
        Assert.Equal(0, fake.CallCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task Page_below_one_is_rejected(int page)
    {
        await using var db = await CreateDbAsync();
        var controller = CreateController(db, new CountingDiscovery(), enabled: true);

        var result = await controller.Search(await MemberWorkspaceIdAsync(db), new AcademicSourceSearchRequest { Q = "x", Page = page }, CancellationToken.None);

        AssertBadRequest(result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(26)]
    public async Task PageSize_outside_bounds_is_rejected(int pageSize)
    {
        await using var db = await CreateDbAsync();
        var controller = CreateController(db, new CountingDiscovery(), enabled: true);

        var result = await controller.Search(await MemberWorkspaceIdAsync(db), new AcademicSourceSearchRequest { Q = "x", PageSize = pageSize }, CancellationToken.None);

        AssertBadRequest(result);
    }

    [Fact]
    public async Task Paging_beyond_the_bounded_depth_is_rejected()
    {
        await using var db = await CreateDbAsync();
        var controller = CreateController(db, new CountingDiscovery(), enabled: true);

        // 200 records deep exceeds MaxCombinedResults (150) at the default page size.
        var result = await controller.Search(await MemberWorkspaceIdAsync(db), new AcademicSourceSearchRequest { Q = "x", Page = 20, PageSize = 25 }, CancellationToken.None);

        AssertBadRequest(result);
    }

    [Fact]
    public async Task Overflow_shaped_page_number_is_rejected_rather_than_thrown()
    {
        await using var db = await CreateDbAsync();
        var fake = new CountingDiscovery();
        var controller = CreateController(db, fake, enabled: true);

        // int.MaxValue * 25 overflows a 32-bit product. The bound is arithmetic in the wider
        // type, so a crafted URL is a clean 400 and never reaches the provider pool at all.
        var result = await controller.Search(await MemberWorkspaceIdAsync(db), new AcademicSourceSearchRequest { Q = "x", Page = int.MaxValue, PageSize = 25 }, CancellationToken.None);

        AssertBadRequest(result);
        Assert.Equal(0, fake.CallCount);
    }

    [Fact]
    public async Task Oversized_page_without_a_page_size_is_rejected_too()
    {
        await using var db = await CreateDbAsync();
        var fake = new CountingDiscovery();
        var controller = CreateController(db, fake, enabled: true);

        // The exact shape that survived the first repair: `?q=test&page=2147483647` with the page
        // size left out. Depth was checked only when BOTH paging parameters were present, so this
        // request passed validation and the slicer computed (page - 1) * pageSize as a 32-bit
        // product — int.MaxValue - 1 times the default 10 — which wrapped to a negative offset.
        // The default size is now part of the check, in the wider type, so this URL is a clean 400.
        var result = await controller.Search(await MemberWorkspaceIdAsync(db), new AcademicSourceSearchRequest { Q = "test", Page = int.MaxValue }, CancellationToken.None);

        AssertBadRequest(result);
        Assert.Equal(0, fake.CallCount);
    }

    [Fact]
    public async Task Page_past_the_end_of_the_pool_is_an_empty_window()
    {
        await using var db = await CreateDbAsync();
        var fake = new RecordingDiscovery((_, _) => Task.FromResult(Batch(Records(50))));
        var controller = CreateController(db, fake, enabled: true);
        var workspaceId = await MemberWorkspaceIdAsync(db);

        // 6 * 25 is exactly at the browse bound, so this is a legal request; the pool simply has
        // fewer records than the window asks for. The offset is formed in long arithmetic and
        // compared against the pool before slicing, so a past-the-end page is empty rather than
        // wrapping around to records the caller already saw.
        var result = await controller.Search(workspaceId, new AcademicSourceSearchRequest { Q = "market access", Page = 6, PageSize = 25 }, CancellationToken.None);

        var body = Assert.IsType<AcademicSourceSearchResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Empty(body.Sources);
        Assert.Equal(50, body.TotalMatched);
    }

    [Fact]
    public async Task Invalid_identifier_is_rejected_before_network_fan_out()
    {
        await using var db = await CreateDbAsync();
        var fake = new CountingDiscovery();
        var controller = CreateController(db, fake, enabled: true);

        var badDoi = await controller.Search(await MemberWorkspaceIdAsync(db), new AcademicSourceSearchRequest { Doi = ["10.5555"] }, CancellationToken.None);
        // Digits and length pass, the check digit does not: it must not reach a provider.
        var badIsbn = await controller.Search(await MemberWorkspaceIdAsync(db), new AcademicSourceSearchRequest { Isbn = ["9780306406111"] }, CancellationToken.None);

        AssertBadRequest(badDoi);
        AssertBadRequest(badIsbn);
        Assert.Equal(0, fake.CallCount);
    }

    [Fact]
    public async Task Pagination_windows_page_results_and_report_has_more()
    {
        await using var db = await CreateDbAsync();
        var fake = new RecordingDiscovery((query, _) => Task.FromResult(Batch(Records(Math.Clamp(query.MaxResults, 1, 150)))));
        var controller = CreateController(db, fake, enabled: true);
        var workspaceId = await MemberWorkspaceIdAsync(db);

        var first = await controller.Search(workspaceId, new AcademicSourceSearchRequest { Q = "market access", Page = 1, PageSize = 10 }, CancellationToken.None);
        var second = await controller.Search(workspaceId, new AcademicSourceSearchRequest { Q = "market access", Page = 2, PageSize = 10 }, CancellationToken.None);

        var firstPage = Assert.IsType<OkObjectResult>(first);
        var firstBody = Assert.IsType<AcademicSourceSearchResponse>(firstPage.Value);
        Assert.Equal(10, firstBody.Sources.Count);
        Assert.Equal("Record 0", firstBody.Sources[0].Title);
        // The pool is fetched page-independently, so the universe reported is the same on every
        // page and is larger than one window: page 1 of a 50-record pool has 4 more pages, and a
        // client that derives its page count from TotalMatched can reach them.
        Assert.Equal(50, firstBody.TotalMatched);
        Assert.False(firstBody.HasMore);

        var secondBody = Assert.IsType<AcademicSourceSearchResponse>(Assert.IsType<OkObjectResult>(second).Value);
        Assert.Equal("Record 10", secondBody.Sources[0].Title);
        Assert.Equal(10, secondBody.Sources.Count);
        Assert.Equal(50, secondBody.TotalMatched);
        Assert.False(secondBody.HasMore);
        // Same pool both times: the requests differ only in which window was sliced, so paging
        // neither repeats a record nor drops one another page has already shown.
        Assert.Equal(2, fake.Queries.Count);
        Assert.Equal(fake.Queries[0].MaxResults, fake.Queries[1].MaxResults);
        var overlap = firstBody.Sources.Select(source => source.Title).Intersect(secondBody.Sources.Select(source => source.Title));
        Assert.Empty(overlap);
    }

    [Fact]
    public async Task Pool_truncated_at_the_browse_bound_reports_more_matches_exist()
    {
        await using var db = await CreateDbAsync();
        // A pool that filled MaxCombinedResults is the one case where "more exists" is provable:
        // the catalogues were cut off at the bound. It is a statement about the bound, not about
        // whether the current page has a successor.
        var fake = new RecordingDiscovery((_, _) => Task.FromResult(Batch(Records(150))));
        var controller = CreateController(db, fake, enabled: true);
        var workspaceId = await MemberWorkspaceIdAsync(db);

        var body = Assert.IsType<AcademicSourceSearchResponse>(Assert.IsType<OkObjectResult>(
            await controller.Search(workspaceId, new AcademicSourceSearchRequest { Q = "market access", Page = 1, PageSize = 10 }, CancellationToken.None)).Value);

        Assert.Equal(150, body.TotalMatched);
        Assert.True(body.HasMore);
        Assert.Equal(10, body.Sources.Count);
    }

    [Fact]
    public async Task Catalog_ceiling_reported_by_discovery_survives_as_has_more()
    {
        await using var db = await CreateDbAsync();
        // The combined bound is not the only ceiling a search stops at. Here the pool is 50 records
        // — one catalog's own per-provider ceiling, far below MaxCombinedResults — and discovery says
        // so. Inferring "more exists" from `totalMatched >= 150` alone would report this list as
        // complete, which is the claim the reviewer found false.
        var fake = new RecordingDiscovery((_, _) => Task.FromResult(Batch(Records(50), capped: true)));
        var controller = CreateController(db, fake, enabled: true);

        var body = Assert.IsType<AcademicSourceSearchResponse>(Assert.IsType<OkObjectResult>(
            await controller.Search(await MemberWorkspaceIdAsync(db), new AcademicSourceSearchRequest { Q = "market access", Page = 1, PageSize = 10 }, CancellationToken.None)).Value);

        Assert.Equal(50, body.TotalMatched);
        Assert.True(body.HasMore);
    }

    [Fact]
    public async Task Last_page_reports_no_more_results()
    {
        await using var db = await CreateDbAsync();
        var fake = new RecordingDiscovery((query, _) => Task.FromResult(Batch(Records(4))));
        var controller = CreateController(db, fake, enabled: true);

        var result = await controller.Search(await MemberWorkspaceIdAsync(db), new AcademicSourceSearchRequest { Q = "market access", Page = 1, PageSize = 10 }, CancellationToken.None);

        var body = Assert.IsType<AcademicSourceSearchResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(4, body.Sources.Count);
        Assert.False(body.HasMore);
    }

    [Fact]
    public async Task Request_is_bounded_by_configuration_not_by_page_depth()
    {
        await using var db = await CreateDbAsync();
        var fake = new RecordingDiscovery((_, _) => Task.FromResult(Batch([])));
        var controller = CreateController(db, fake, enabled: true);

        await controller.Search(await MemberWorkspaceIdAsync(db), new AcademicSourceSearchRequest { Q = "  vaccination uptake  ", Page = 3, PageSize = 10, YearFrom = 2015, YearTo = 2024, OpenAccessOnly = true }, CancellationToken.None);

        var query = Assert.Single(fake.Queries);
        // Depth is a slice decision made after the pool exists; what goes to the providers is the
        // configured per-provider ceiling, independent of which page was asked for.
        Assert.Equal(Options.Create(new AcademicSourceDiscoveryOptions { Enabled = true }.Normalize()).Value.MaxResultsPerProvider, query.MaxResults);
        Assert.Equal("vaccination uptake", query.SearchTerm);
        Assert.Equal(2015, query.YearFrom);
        Assert.Equal(2024, query.YearTo);
        Assert.True(query.OpenAccessOnly);
    }

    [Fact]
    public async Task Response_carries_no_internal_catalog_identity()
    {
        await using var db = await CreateDbAsync();
        var records = Records(3)
            .Select((record, index) => record with
            {
                ProviderKey = InternalCatalogKeys[index % InternalCatalogKeys.Length],
                ProviderWorkId = $"W2023{index:D3}",
                SafeMetadataJson = """{"mergedFrom":["openalex"],"n_references":42}""",
            })
            .ToArray();
        var batch = new AcademicDiscoveryBatch
        {
            Sources = records,
            ProviderOutcomes = records.Select((record, index) => new AcademicProviderOutcome
            {
                ProviderKey = InternalCatalogKeys[index],
                Status = index == 2 ? AcademicProviderOutcomeStatus.Failed : AcademicProviderOutcomeStatus.Ok,
                SourceCount = 1,
                FailureCategory = AcademicSourceFailureCategories.RateLimited,
            }).ToArray(),
        };
        var fake = new RecordingDiscovery((_, _) => Task.FromResult(batch));
        var controller = CreateController(db, fake, enabled: true);

        var result = await controller.Search(await MemberWorkspaceIdAsync(db), new AcademicSourceSearchRequest { Q = "market access" }, CancellationToken.None);

        var json = JsonSerializer.Serialize(Assert.IsType<AcademicSourceSearchResponse>(Assert.IsType<OkObjectResult>(result).Value));
        foreach (var key in InternalCatalogKeys.Concat(["gpt", "claude", "api-key", "apikey", "secret", "billing", "credits", "usd", "routing", "upstream", "model", "provider"]))
            Assert.False(json.Contains(key, StringComparison.OrdinalIgnoreCase), $"response leaked '{key}': {json}");
        // The failure collapsed into the honest, generic partial flag.
        var body = JsonSerializer.Deserialize<AcademicSourceSearchResponse>(json)!;
        Assert.True(body.Partial);
        Assert.NotNull(body.PartialNotice);
    }

    [Fact]
    public async Task Metadata_and_fulltext_indication_stay_distinct_from_read_content()
    {
        await using var db = await CreateDbAsync();
        var records = new[]
        {
            Record("metadata only book", access: AcademicAccessLevel.Metadata, providerKey: "openlibrary", providerWorkId: "OL1M"),
            Record("abstract article", access: AcademicAccessLevel.Abstract, providerKey: "openalex", providerWorkId: "W1", abstractText: "Catalog abstract text."),
            Record("indicated full text", access: AcademicAccessLevel.FullTextIndicated, providerKey: "openalex", providerWorkId: "W2", fullTextUrl: "https://example.org/w2.pdf"),
        };
        var fake = new RecordingDiscovery((_, _) => Task.FromResult(Batch(records)));
        var controller = CreateController(db, fake, enabled: true);

        var body = Assert.IsType<AcademicSourceSearchResponse>(Assert.IsType<OkObjectResult>(
            await controller.Search(await MemberWorkspaceIdAsync(db), new AcademicSourceSearchRequest { Q = "x" }, CancellationToken.None)).Value);

        Assert.Equal("metadata", body.Sources[0].AccessLevel);
        Assert.False(body.Sources[0].FullTextIndicated);
        Assert.Equal("abstract", body.Sources[1].AccessLevel);
        Assert.False(body.Sources[1].FullTextIndicated);
        // The indication level must survive as an indication, never as a claim of read content:
        // what this service shows is still metadata, and the DTO has no body field at all.
        Assert.Equal("metadata", body.Sources[2].AccessLevel);
        Assert.True(body.Sources[2].FullTextIndicated);
        Assert.Contains("indicated but not read", body.Sources[2].Provenance.AccessDescription);
        Assert.Equal(AcademicSourcePresentation.ProvenanceBasis, body.Sources[2].Provenance.Basis);
        var json = JsonSerializer.Serialize(body);
        Assert.DoesNotContain("full_text_content", json, StringComparison.OrdinalIgnoreCase);
        foreach (var property in typeof(AcademicSearchSourceDto).GetProperties())
            Assert.DoesNotContain("body", property.Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Doi_check_echoes_the_submitted_identifier_without_a_provider_identity()
    {
        await using var db = await CreateDbAsync();
        var records = new[] { Record("Registered work", doi: "10.5555/live.2024", providerKey: "crossref", providerWorkId: "10.5555/live.2024") };
        var batch = new AcademicDiscoveryBatch
        {
            Sources = records,
            ProviderOutcomes = [],
            DoiValidations =
            [
                new AcademicDoiValidationResult { SubmittedDoi = "10.5555/live.2024", ResolvedDoi = "10.5555/live.2024", IsRegistered = true },
                new AcademicDoiValidationResult { SubmittedDoi = "10.9999/gone.2024", IsRegistered = false, FailureCategory = AcademicSourceFailureCategories.NotFound },
            ],
        };
        var fake = new RecordingDiscovery((_, _) => Task.FromResult(batch));
        var controller = CreateController(db, fake, enabled: true);

        var body = Assert.IsType<AcademicSourceSearchResponse>(Assert.IsType<OkObjectResult>(
            await controller.Search(await MemberWorkspaceIdAsync(db), new AcademicSourceSearchRequest { Doi = ["10.5555/live.2024", "10.9999/gone.2024"] }, CancellationToken.None)).Value);

        Assert.Equal(2, body.DoiChecks.Count);
        Assert.Equal("10.5555/live.2024", body.DoiChecks[0].SubmittedDoi);
        Assert.True(body.DoiChecks[0].IsRegistered);
        Assert.Null(body.DoiChecks[0].FailureCode);
        Assert.Equal("not_registered", body.DoiChecks[1].FailureCode);
        Assert.DoesNotContain("crossref", JsonSerializer.Serialize(body), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Identifier_checks_survive_a_search_that_matched_no_record()
    {
        await using var db = await CreateDbAsync();
        // The other half of the same promise, from the server side: an unregistered DOI is an answer
        // the caller asked for, so the response must still carry it when the record pool came back
        // empty — a client that rendered nothing for an empty list used to lose the one fact this
        // search had established. Empty is also not truncation and not refusal, so neither flag may
        // be set here.
        var batch = new AcademicDiscoveryBatch
        {
            Sources = [],
            ProviderOutcomes = [],
            DoiValidations =
            [
                new AcademicDoiValidationResult { SubmittedDoi = "10.9999/gone.2024", IsRegistered = false, FailureCategory = AcademicSourceFailureCategories.NotFound },
            ],
        };
        var fake = new RecordingDiscovery((_, _) => Task.FromResult(batch));
        var controller = CreateController(db, fake, enabled: true);

        var body = Assert.IsType<AcademicSourceSearchResponse>(Assert.IsType<OkObjectResult>(
            await controller.Search(await MemberWorkspaceIdAsync(db), new AcademicSourceSearchRequest { Doi = ["10.9999/gone.2024"] }, CancellationToken.None)).Value);

        Assert.Empty(body.Sources);
        Assert.Equal(0, body.TotalMatched);
        var check = Assert.Single(body.DoiChecks);
        Assert.Equal("10.9999/gone.2024", check.SubmittedDoi);
        Assert.False(check.IsRegistered);
        Assert.Equal("not_registered", check.FailureCode);
        Assert.False(body.HasMore);
        Assert.False(body.Partial);
    }

    [Fact]
    public async Task Aborting_mid_search_cancels_the_in_flight_discovery_call()
    {
        // A student who changes page or leaves the tab must not leave provider work running
        // behind them: the caller's token has to reach the fan-out and abort it in flight
        // (a token that was already cancelled would only prove the fence, not the wiring).
        await using var db = await CreateDbAsync();
        using var gate = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new RecordingDiscovery(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            throw new InvalidOperationException("unreachable: the delay ends only by cancellation");
        });
        var controller = CreateController(db, fake, enabled: true);
        var workspaceId = await MemberWorkspaceIdAsync(db);

        var call = controller.Search(workspaceId, new AcademicSourceSearchRequest { Q = "market access" }, gate.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(fake.ObservedToken.IsCancellationRequested);
        await gate.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }

    [Fact]
    public void Endpoint_is_get_only_read_with_the_shared_search_rate_policy()
    {
        var method = typeof(AcademicSourcesController).GetMethod("Search")!;
        Assert.NotNull(method.GetCustomAttributes(typeof(HttpGetAttribute), false).SingleOrDefault());
        Assert.Null(method.GetCustomAttributes(typeof(HttpPostAttribute), false).SingleOrDefault());
        var policy = Assert.Single(method.GetCustomAttributes(typeof(EnableRateLimitingAttribute), false));
        Assert.Equal(RateLimiting.Search, ((EnableRateLimitingAttribute)policy).PolicyName);
        Assert.NotNull(typeof(AcademicSourcesController).GetCustomAttributes(typeof(AuthorizeAttribute), false).SingleOrDefault());
    }

    [Fact]
    public void Customer_dto_surface_has_no_provider_or_cost_shaped_fields()
    {
        foreach (var type in new[] { typeof(AcademicSearchSourceDto), typeof(AcademicSourceProvenanceDto), typeof(AcademicDoiCheckDto), typeof(AcademicSourceSearchResponse) })
        {
            foreach (var property in type.GetProperties())
            {
                var name = property.Name.ToLowerInvariant();
                Assert.DoesNotContain("provider", name);
                Assert.DoesNotContain("model", name);
                Assert.DoesNotContain("cost", name);
                Assert.DoesNotContain("credit", name);
                Assert.DoesNotContain("price", name);
                Assert.DoesNotContain("key", name);
                Assert.DoesNotContain("token", name);
            }
        }
    }

    [Fact]
    public async Task A_pre_cancelled_caller_short_circuits_before_any_provider_work()
    {
        // An already-abandoned request must not start new fan-out. The membership read itself
        // observes the token, so the guarantee is asserted as "no discovery call", not as a
        // particular exception site.
        await using var db = await CreateDbAsync();
        using var gate = new CancellationTokenSource();
        var fake = new RecordingDiscovery();
        var controller = CreateController(db, fake, enabled: true);
        var workspaceId = await MemberWorkspaceIdAsync(db);
        await gate.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            controller.Search(workspaceId, new AcademicSourceSearchRequest { Q = "market access" }, gate.Token));
        Assert.Empty(fake.Queries);
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static void AssertBadRequest(IActionResult result)
    {
        // ApiResults.Validation returns a StatusCode(400) ObjectResult, matching the envelope
        // every other validation path in this API produces.
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
    }


    private static AcademicSourceRecord Record(
        string title,
        AcademicAccessLevel access = AcademicAccessLevel.Metadata,
        string providerKey = "openalex",
        string providerWorkId = "W1",
        string? doi = null,
        string? abstractText = null,
        string? fullTextUrl = null) => new()
    {
        ProviderKey = providerKey,
        ProviderWorkId = providerWorkId,
        Title = title,
        Authors = ["Rebin Khoshnaw"],
        SourceType = AcademicSourceTypes.JournalArticle,
        PublicationYear = 2024,
        Doi = doi,
        Abstract = abstractText,
        LandingUrl = "https://example.org/landing",
        FullTextUrl = fullTextUrl,
        AccessLevel = access,
    };

    private static AcademicSourceRecord[] Records(int count) =>
        Enumerable.Range(0, count).Select(index => Record($"Record {index}", doi: $"10.5555/synth.{index:D4}")).ToArray();

    private static AcademicDiscoveryBatch Batch(IReadOnlyList<AcademicSourceRecord> sources, bool capped = false, bool noProvidersConfigured = false) => new()
    {
        Sources = sources,
        ResultCapped = capped,
        NoProvidersConfigured = noProvidersConfigured,
        ProviderOutcomes = sources.Count > 0
            ? [new AcademicProviderOutcome { ProviderKey = "openalex", Status = AcademicProviderOutcomeStatus.Ok, SourceCount = sources.Count }]
            : [],
    };

    private static async Task<TaslimDbContext> CreateDbAsync(bool memberOfOtherWorkspace = false)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var workspaceId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Users.Add(new Taslim.Api.Domain.ApplicationUser
        {
            Id = TestUserId,
            UserName = "student@example.test",
            Email = "student@example.test",
            DisplayName = "Student",
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Student Workspace", Slug = $"student-{workspaceId:N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = TestUserId, Role = WorkspaceRole.Owner, JoinedAt = now });
        if (memberOfOtherWorkspace)
        {
            // A second student's workspace: reachable by URL, never by membership.
            var otherUser = Guid.NewGuid();
            var otherWorkspace = Guid.NewGuid();
            db.Users.Add(new Taslim.Api.Domain.ApplicationUser { Id = otherUser, UserName = $"other-{otherUser:N}@example.test", Email = $"other-{otherUser:N}@example.test", DisplayName = "Other", CreatedAt = now, UpdatedAt = now });
            db.Workspaces.Add(new Workspace { Id = otherWorkspace, Name = "Other Workspace", Slug = $"other-{otherWorkspace:N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
            db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = otherWorkspace, UserId = otherUser, Role = WorkspaceRole.Owner, JoinedAt = now });
        }
        await db.SaveChangesAsync();
        return db;
    }

    private static readonly Guid TestUserId = Guid.Parse("9f1d2a3b-4c5d-6e7f-8a9b-0c1d2e3f4a5b");

    private static Task<Guid> MemberWorkspaceIdAsync(TaslimDbContext db) =>
        db.WorkspaceMembers.Where(member => member.UserId == TestUserId).Select(member => member.WorkspaceId).FirstAsync();

    private static AcademicSourcesController CreateController(
        TaslimDbContext db,
        IAcademicSourceDiscoveryService discovery,
        bool enabled,
        string nameIdentifier = "9f1d2a3b-4c5d-6e7f-8a9b-0c1d2e3f4a5b")
    {
        var options = Options.Create(new AcademicSourceDiscoveryOptions { Enabled = enabled }.Normalize());
        var controller = new AcademicSourcesController(discovery, new WorkspaceAccessService(db), options)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, nameIdentifier)], "synthetic")),
                },
            },
        };
        return controller;
    }
}

/// <summary>
/// Discovery double that records every query and the exact cancellation token it was handed,
/// so "the controller forwards the caller's token" is an asserted fact, not a code-reading claim.
/// </summary>
internal sealed class RecordingDiscovery(Func<AcademicDiscoveryQuery, CancellationToken, Task<AcademicDiscoveryBatch>>? behavior = null) : IAcademicSourceDiscoveryService
{
    public List<AcademicDiscoveryQuery> Queries { get; } = [];
    public CancellationToken ObservedToken { get; private set; }

    public Task<AcademicDiscoveryBatch> DiscoverAsync(AcademicDiscoveryQuery query, CancellationToken cancellationToken = default)
    {
        Queries.Add(query);
        ObservedToken = cancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        return (behavior ?? Default)(query, cancellationToken);
    }

    private static readonly Func<AcademicDiscoveryQuery, CancellationToken, Task<AcademicDiscoveryBatch>> Default =
        (_, _) => Task.FromResult(new AcademicDiscoveryBatch { Sources = [], ProviderOutcomes = [] });
}

internal sealed class CountingDiscovery : IAcademicSourceDiscoveryService
{
    public int CallCount { get; private set; }

    public Task<AcademicDiscoveryBatch> DiscoverAsync(AcademicDiscoveryQuery query, CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Task.FromResult(new AcademicDiscoveryBatch { Sources = [], ProviderOutcomes = [] });
    }
}
