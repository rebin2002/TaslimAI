using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Taslim.Api.AcademicSources;
using Xunit;

namespace Taslim.Api.Tests.AcademicSources;

public sealed class AcademicSourceDiscoveryServiceTests
{
    private sealed class FakeWorkProvider(string key, bool configured, Func<AcademicDiscoveryQuery, Task<IReadOnlyList<AcademicSourceRecord>>>? behavior = null) : IAcademicWorkSearchProvider
    {
        public string ProviderKey => key;
        public bool IsConfigured { get; } = configured;
        public int CallCount { get; private set; }
        public Task<IReadOnlyList<AcademicSourceRecord>> SearchWorksAsync(AcademicDiscoveryQuery query, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return behavior is null ? Task.FromResult<IReadOnlyList<AcademicSourceRecord>>([]) : behavior(query);
        }
    }

    private sealed class FakeBookProvider(string key, bool configured, Func<IReadOnlyList<string>, Task<IReadOnlyList<AcademicSourceRecord>>>? isbnBehavior = null) : IAcademicBookCatalogProvider
    {
        public string ProviderKey => key;
        public bool IsConfigured { get; } = configured;
        public int IsbnCallCount { get; private set; }
        public int SearchCallCount { get; private set; }
        public IReadOnlyList<string>? LastIsbnBatch { get; private set; }
        public Task<IReadOnlyList<AcademicSourceRecord>> SearchBooksAsync(AcademicDiscoveryQuery query, CancellationToken cancellationToken = default)
        {
            SearchCallCount++;
            return Task.FromResult<IReadOnlyList<AcademicSourceRecord>>([]);
        }
        public Task<IReadOnlyList<AcademicSourceRecord>> LookupByIsbnAsync(IReadOnlyList<string> isbns, CancellationToken cancellationToken = default)
        {
            IsbnCallCount++;
            LastIsbnBatch = isbns;
            return isbnBehavior is null ? Task.FromResult<IReadOnlyList<AcademicSourceRecord>>([]) : isbnBehavior(isbns);
        }
    }

    private sealed class FakeValidator(Func<string, Task<AcademicDoiValidationResult>> behavior) : IAcademicDoiValidator
    {
        public string ProviderKey => "crossref";
        public bool IsConfigured { get; } = true;
        public int CallCount { get; private set; }
        public Task<AcademicDoiValidationResult> ValidateAsync(string doi, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return behavior(doi);
        }
    }

    /// <summary>A catalog that never answers, honouring the token it was given, so the wall clock a
    /// search actually spends is measured rather than reasoned about.</summary>
    private sealed class SlowWorkProvider(string key, TimeSpan delay) : IAcademicWorkSearchProvider
    {
        public string ProviderKey => key;
        public bool IsConfigured { get; } = true;
        public int CallCount { get; private set; }

        public async Task<IReadOnlyList<AcademicSourceRecord>> SearchWorksAsync(AcademicDiscoveryQuery query, CancellationToken cancellationToken = default)
        {
            CallCount++;
            await Task.Delay(delay, cancellationToken);
            return [];
        }
    }

    private sealed class SlowValidator(TimeSpan delay) : IAcademicDoiValidator
    {
        public string ProviderKey => "crossref";
        public bool IsConfigured { get; } = true;
        public int CallCount { get; private set; }

        public async Task<AcademicDoiValidationResult> ValidateAsync(string doi, CancellationToken cancellationToken = default)
        {
            CallCount++;
            await Task.Delay(delay, cancellationToken);
            return new AcademicDoiValidationResult { ResolvedDoi = doi, IsRegistered = true };
        }
    }

    private static AcademicSourceRecord Record(string provider, string id, string title, string? doi = null, string? isbn13 = null) => new()
    {
        ProviderKey = provider,
        ProviderWorkId = id,
        Title = title,
        Doi = doi,
        Isbn13 = isbn13,
        SourceType = AcademicSourceTypes.JournalArticle,
        AccessLevel = AcademicAccessLevel.Metadata,
    };

    /// <summary>The same row carrying the access evidence a catalog would have to show for it to
    /// count as "an open location", used by the open-access filter tests.</summary>
    private static AcademicSourceRecord OpenRecord(string provider, string id, string title, string? doi = null, string? isbn13 = null) => new()
    {
        ProviderKey = provider,
        ProviderWorkId = id,
        Title = title,
        Doi = doi,
        Isbn13 = isbn13,
        SourceType = AcademicSourceTypes.JournalArticle,
        AccessLevel = AcademicAccessLevel.FullTextIndicated,
        FullTextUrl = "https://example.org/open-access-copy",
    };

    private static AcademicSourceDiscoveryService Create(
        IAcademicWorkSearchProvider[] works,
        IAcademicBookCatalogProvider[] books,
        IAcademicDoiValidator[] validators,
        Action<AcademicSourceDiscoveryOptions>? configure = null)
    {
        var options = AcademicFixture.Defaults(configure);
        return new AcademicSourceDiscoveryService(works, books, validators, AcademicFixture.Wrapped(options), NullLogger<AcademicSourceDiscoveryService>.Instance);
    }

    [Fact]
    public async Task Merges_doi_duplicates_across_providers_into_one_identity()
    {
        var service = Create(
            [
                new FakeWorkProvider("openalex", true, _ => Task.FromResult<IReadOnlyList<AcademicSourceRecord>>(
                    [Record("openalex", "W1", "Market access study", doi: "10.5555/doi.dup")])),
                new FakeWorkProvider("googlebooks", true, _ => Task.FromResult<IReadOnlyList<AcademicSourceRecord>>(
                    [Record("googlebooks", "V1", "Market access study (volume)", doi: "https://doi.org/10.5555/DOI.DUP")])),
            ],
            [new FakeBookProvider("openlibrary", true)],
            []);

        var batch = await service.DiscoverAsync(new AcademicDiscoveryQuery { SearchTerm = "market access" });
        var record = Assert.Single(batch.Sources);
        Assert.Equal("10.5555/doi.dup", record.Doi); // provider casing is preserved; only the dedup key is case-folded
        Assert.Contains("openalex", record.SafeMetadataJson);
        Assert.Contains("googlebooks", record.SafeMetadataJson);
        Assert.Equal(2, batch.ProviderOutcomes.Count(outcome => outcome.Status == AcademicProviderOutcomeStatus.Ok));
    }

    [Fact]
    public async Task One_failed_catalog_degrades_the_batch_to_partial_without_losing_other_results()
    {
        var service = Create(
            [
                new FakeWorkProvider("openalex", true, _ => Task.FromResult<IReadOnlyList<AcademicSourceRecord>>(
                    [Record("openalex", "W1", "Survivor", doi: "10.5555/survivor")])),
                new FakeWorkProvider("googlebooks", true, _ => Task.FromException<IReadOnlyList<AcademicSourceRecord>>(
                    new AcademicSourceRateLimitedException(new AcademicProviderErrorDetails(429, "rate_limited")))),
            ],
            [],
            []);

        var batch = await service.DiscoverAsync(new AcademicDiscoveryQuery { SearchTerm = "anything" });
        Assert.True(batch.PartialResult);
        Assert.Single(batch.Sources);
        var failed = Assert.Single(batch.ProviderOutcomes, outcome => outcome.ProviderKey == "googlebooks");
        Assert.Equal("failed", failed.Status);
        Assert.Equal("rate_limited", failed.FailureCategory);
        // Outcome categories are coarse; provider payloads never enter them.
        Assert.DoesNotContain("429", failed.FailureCategory);
    }

    [Fact]
    public async Task Unconfigured_providers_are_skipped_and_search_still_completes()
    {
        var service = Create(
            [new FakeWorkProvider("openalex", true), new FakeWorkProvider("googlebooks", false)],
            [new FakeBookProvider("openlibrary", false)],
            []);
        var batch = await service.DiscoverAsync(new AcademicDiscoveryQuery { SearchTerm = "x", Isbns = ["9781234567897"] });
        Assert.Empty(batch.Sources);
        Assert.DoesNotContain(batch.ProviderOutcomes, outcome => outcome.ProviderKey == "googlebooks");
    }

    [Fact]
    public async Task Identifier_inputs_are_normalized_invalid_values_dropped_before_provider_calls()
    {
        var bookProvider = new FakeBookProvider("openlibrary", true, isbnBehavior: isbns =>
            Task.FromResult<IReadOnlyList<AcademicSourceRecord>>(isbns.Select(isbn => Record("openlibrary", $"B{isbn}", $"Book {isbn}", isbn13: isbn)).ToArray()));
        var validatorSeen = new List<string>();
        var service = Create(
            [],
            [bookProvider],
            [new FakeValidator(doi =>
            {
                validatorSeen.Add(doi);
                return Task.FromResult(new AcademicDoiValidationResult { ResolvedDoi = doi, IsRegistered = true, Source = Record("crossref", doi, $"Validated {doi}", doi: doi) });
            })]);

        var batch = await service.DiscoverAsync(new AcademicDiscoveryQuery
        {
            Isbns = ["978-1-2345-6789-7", "not-an-isbn", "123456789X", "9781234567897"],
            Dois = ["https://doi.org/10.5555/A", "junk", "10.5555/a"],
        });

        Assert.Equal(["9781234567897"], bookProvider.LastIsbnBatch);
        Assert.Equal(1, bookProvider.IsbnCallCount);
        Assert.Equal(0, bookProvider.SearchCallCount); // identifier-only request: no term searches
        Assert.Equal(["10.5555/a"], validatorSeen);
        // The ISBN row (deduped across the three spellings) and the DOI row are distinct identities.
        Assert.Equal(2, batch.Sources.Count);
        Assert.Contains(batch.Sources, record => record.Isbn13 == "9781234567897");
        Assert.Contains(batch.Sources, record => record.Doi == "10.5555/a");
    }

    [Fact]
    public async Task Identifier_lookups_respect_the_configured_budget()
    {
        var bookProvider = new FakeBookProvider("openlibrary", true, isbns => Task.FromResult<IReadOnlyList<AcademicSourceRecord>>([]));
        var service = Create([], [bookProvider], [], options => options.MaxIdentifierLookups = 2);
        await service.DiscoverAsync(new AcademicDiscoveryQuery
        {
            Isbns = ["9781234567897", "9789290000006", "9780306406157", "9783030500009"],
        });
        Assert.Equal(2, bookProvider.LastIsbnBatch!.Count);
    }

    [Fact]
    public async Task Registered_and_unregistered_dois_are_both_reported_in_validations()
    {
        var service = Create([], [], [new FakeValidator(doi => Task.FromResult(doi == "10.5555/real"
            ? new AcademicDoiValidationResult { ResolvedDoi = doi, IsRegistered = true, Source = Record("crossref", doi, "Real", doi: doi) }
            : new AcademicDoiValidationResult { ResolvedDoi = doi, IsRegistered = false, FailureCategory = "not_found" }))]);

        var batch = await service.DiscoverAsync(new AcademicDiscoveryQuery { Dois = ["10.5555/real", "10.5555/ghost"] });
        Assert.Equal(2, batch.DoiValidations.Count);
        Assert.True(batch.DoiValidations[0].IsRegistered);
        Assert.False(batch.DoiValidations[1].IsRegistered);
        Assert.Equal("not_found", batch.DoiValidations[1].FailureCategory);
        // An unregistered DOI contributes no citation record.
        Assert.Single(batch.Sources);
    }

    [Fact]
    public async Task Validator_failure_does_not_abort_the_batch()
    {
        var service = Create([], [], [new FakeValidator(_ => Task.FromException<AcademicDoiValidationResult>(
            new AcademicSourceTimeoutException()))]);
        var batch = await service.DiscoverAsync(new AcademicDiscoveryQuery { Dois = ["10.5555/x"] });
        var validation = Assert.Single(batch.DoiValidations);
        Assert.False(validation.IsRegistered);
        Assert.Equal("timeout", validation.FailureCategory);
    }

    [Fact]
    public async Task Globally_disabled_switch_short_circuits_everything()
    {
        var works = new FakeWorkProvider("openalex", true, _ => throw new InvalidOperationException("must not be called"));
        var service = Create([works], [], [], options => options.Enabled = false);
        var batch = await service.DiscoverAsync(new AcademicDiscoveryQuery { SearchTerm = "x" });
        Assert.Empty(batch.Sources);
        Assert.Equal(0, works.CallCount);
        var outcome = Assert.Single(batch.ProviderOutcomes);
        Assert.Equal("disabled", outcome.Status);
    }

    [Fact]
    public async Task Combined_results_are_capped_and_outcomes_marked_partial()
    {
        var many = Enumerable.Range(0, 120).Select(index => Record("openalex", $"W{index}", $"Title {index}", doi: $"10.5555/{index}")).ToList();
        var service = Create(
            [new FakeWorkProvider("openalex", true, _ => Task.FromResult<IReadOnlyList<AcademicSourceRecord>>(many))],
            [],
            [],
            options => options.MaxCombinedResults = 40);
        var batch = await service.DiscoverAsync(new AcademicDiscoveryQuery { SearchTerm = "flood" });
        Assert.Equal(40, batch.Sources.Count);
        Assert.True(batch.PartialResult);
    }

    [Fact]
    public async Task Search_terms_are_truncated_to_the_configured_bound()
    {
        string? observed = null;
        var service = Create(
            [new FakeWorkProvider("openalex", true, query =>
            {
                observed = query.SearchTerm;
                return Task.FromResult<IReadOnlyList<AcademicSourceRecord>>([]);
            })],
            [],
            [],
            options => options.MaxSearchTermCharacters = 20);
        await service.DiscoverAsync(new AcademicDiscoveryQuery { SearchTerm = new string('q', 500) });
        Assert.Equal(20, observed!.Length);
    }

    [Fact]
    public async Task Catalog_ceiling_is_reported_as_truncation_even_when_the_combined_pool_is_far_away()
    {
        // One catalog asked for at most five records hands back exactly five: it was cut off, whatever
        // the combined pool bound says. In a single- or two-catalog deployment that bound is never
        // reachable, so the old inference from `pool >= MaxCombinedResults` alone reported this list as
        // everything there was.
        var hits = Enumerable.Range(0, 5).Select(index => Record("openalex", $"W{index}", $"Title {index}", doi: $"10.5555/ceil.{index}")).ToList();
        var service = Create(
            [new FakeWorkProvider("openalex", true, _ => Task.FromResult<IReadOnlyList<AcademicSourceRecord>>(hits))],
            [],
            [],
            options => options.MaxResultsPerProvider = 5);

        var batch = await service.DiscoverAsync(new AcademicDiscoveryQuery { SearchTerm = "flood" });

        Assert.Equal(5, batch.Sources.Count);
        Assert.True(batch.ResultCapped);
        // A ceiling is not a failure: nothing went wrong, the search simply was not permitted to go on.
        Assert.False(batch.PartialResult);
    }

    [Fact]
    public async Task A_completed_search_below_every_ceiling_is_not_reported_as_truncated()
    {
        var hits = Enumerable.Range(0, 3).Select(index => Record("openalex", $"W{index}", $"Title {index}", doi: $"10.5555/short.{index}")).ToList();
        var service = Create(
            [new FakeWorkProvider("openalex", true, _ => Task.FromResult<IReadOnlyList<AcademicSourceRecord>>(hits))],
            [],
            [],
            options => options.MaxResultsPerProvider = 5);

        var batch = await service.DiscoverAsync(new AcademicDiscoveryQuery { SearchTerm = "flood" });

        Assert.Equal(3, batch.Sources.Count);
        Assert.False(batch.ResultCapped);
    }

    [Fact]
    public async Task A_tightened_page_budget_becomes_the_ceiling_the_provider_is_asked_for()
    {
        // MaxResultsPerProvider says how much a catalog may return; MaxPages x MaxPerPage says how
        // much the caller can ever look at. When the second is smaller, asking the catalog for the
        // first is a promise the batch cannot keep: it returns ten rows, the truncation test then
        // compares them against a record ceiling of two hundred, and a list that WAS cut off is
        // reported as complete. The reachable number is what must be requested.
        AcademicDiscoveryQuery? observed = null;
        var hits = Enumerable.Range(0, 10).Select(index => Record("openalex", $"W{index}", $"Title {index}", doi: $"10.5555/page.{index}")).ToList();
        var service = Create(
            [new FakeWorkProvider("openalex", true, query =>
            {
                observed = query;
                return Task.FromResult<IReadOnlyList<AcademicSourceRecord>>(hits);
            })],
            [],
            [],
            options =>
            {
                options.MaxResultsPerProvider = 200;
                options.MaxPerPage = 5;
                options.MaxPages = 2;
            });

        var batch = await service.DiscoverAsync(new AcademicDiscoveryQuery { SearchTerm = "flood" });

        Assert.NotNull(observed);
        Assert.Equal(10, observed!.MaxResults);
        Assert.Equal(10, batch.Sources.Count);
        Assert.True(batch.ResultCapped);
    }

    [Fact]
    public async Task The_default_page_budget_neither_raises_nor_lowers_the_configured_ceiling()
    {
        AcademicDiscoveryQuery? observed = null;
        var service = Create(
            [new FakeWorkProvider("openalex", true, query =>
            {
                observed = query;
                return Task.FromResult<IReadOnlyList<AcademicSourceRecord>>([]);
            })],
            [],
            [],
            options => options.MaxResultsPerProvider = 50);

        await service.DiscoverAsync(new AcademicDiscoveryQuery { SearchTerm = "flood" });

        // A query that asked for nothing kept its own smaller default ask: the reachable ceiling is
        // a limit on what may be requested, not a new request volume, so this path performs exactly
        // the calls it performed before the ceiling existed.
        Assert.NotNull(observed);
        Assert.Equal(25, observed!.MaxResults);

        // Shipped defaults are 25 rows over 2 pages, exactly the 50-record catalog ceiling, so the
        // reachable ceiling equals the configured one and no deployment notices a difference.
        // 50 is also what the API asks for when it builds a discovery query, so this is the shape of
        // a real request rather than a synthetic one.
        await service.DiscoverAsync(new AcademicDiscoveryQuery { SearchTerm = "flood", MaxResults = 50 });
        Assert.Equal(50, observed!.MaxResults);

        // A caller asking for more than the walk can reach is pulled back to it rather than served
        // the larger number and then told nothing was truncated.
        AcademicDiscoveryQuery? clamped = null;
        var deep = Create(
            [new FakeWorkProvider("openalex", true, query =>
            {
                clamped = query;
                return Task.FromResult<IReadOnlyList<AcademicSourceRecord>>([]);
            })],
            [],
            [],
            options => { options.MaxResultsPerProvider = 50; options.MaxPerPage = 10; options.MaxPages = 1; });
        await deep.DiscoverAsync(new AcademicDiscoveryQuery { SearchTerm = "flood", MaxResults = 40 });
        Assert.Equal(10, clamped!.MaxResults);
    }

    [Fact]
    public async Task Open_access_request_drops_identifier_rows_that_show_no_open_location()
    {
        // The catalogs' open-access parameter exists only on their text-search endpoints. An ISBN
        // lookup and a DOI resolution cannot be filtered upstream, so without a merged-result rule the
        // checkbox would silently be ignored for identifier-only searches.
        var bookProvider = new FakeBookProvider("openlibrary", true, isbnBehavior: _ => Task.FromResult<IReadOnlyList<AcademicSourceRecord>>(
            [
                OpenRecord("openlibrary", "B1", "Open book", isbn13: "9781234567897"),
                Record("openlibrary", "B2", "Closed book", isbn13: "9789290000006"),
            ]));
        var validator = new FakeValidator(doi => Task.FromResult(new AcademicDoiValidationResult
        {
            ResolvedDoi = doi,
            IsRegistered = true,
            Source = Record("crossref", doi, "Closed article", doi: doi),
        }));
        var service = Create([], [bookProvider], [validator]);

        var batch = await service.DiscoverAsync(new AcademicDiscoveryQuery
        {
            Isbns = ["9781234567897", "9789290000006"],
            Dois = ["10.5555/closed"],
            OpenAccessOnly = true,
        });

        var surfaced = Assert.Single(batch.Sources);
        Assert.Equal("Open book", surfaced.Title);
        // Dropping the row is not dropping the check: the DOI was registered, and that stays visible.
        var validation = Assert.Single(batch.DoiValidations);
        Assert.True(validation.IsRegistered);
    }

    [Fact]
    public async Task Mixed_query_filters_identifier_rows_without_touching_text_search_rows()
    {
        // A text-search row already passed the catalog's own open-access filter; re-applying a local
        // evidence rule to it would throw away results the catalog knowingly marked open, so the
        // filter's reach is exactly the two identifier paths.
        var service = Create(
            [new FakeWorkProvider("openalex", true, _ => Task.FromResult<IReadOnlyList<AcademicSourceRecord>>(
                [Record("openalex", "W1", "Catalog-marked open text row", doi: "10.5555/text.1")]))],
            [new FakeBookProvider("openlibrary", true, isbnBehavior: _ => Task.FromResult<IReadOnlyList<AcademicSourceRecord>>(
                [Record("openlibrary", "B1", "Closed book", isbn13: "9781234567897")]))],
            [new FakeValidator(doi => Task.FromResult(new AcademicDoiValidationResult
            {
                ResolvedDoi = doi,
                IsRegistered = true,
                Source = OpenRecord("crossref", doi, "Open article", doi: doi),
            }))]);

        var batch = await service.DiscoverAsync(new AcademicDiscoveryQuery
        {
            SearchTerm = "market access",
            Isbns = ["9781234567897"],
            Dois = ["10.5555/open"],
            OpenAccessOnly = true,
        });

        Assert.Contains(batch.Sources, record => record.Title == "Catalog-marked open text row");
        Assert.Contains(batch.Sources, record => record.Title == "Open article");
        Assert.DoesNotContain(batch.Sources, record => record.Title == "Closed book");
    }

    [Fact]
    public async Task Enabled_switch_with_no_capable_catalog_answers_that_it_cannot_answer()
    {
        // The master switch is on, but every catalog behind it is switched off or incomplete, so
        // nothing here can answer at all. That is not "nothing matched".
        var works = new FakeWorkProvider("openalex", false);
        var books = new FakeBookProvider("openlibrary", false);
        var service = Create([works], [books], []);

        var batch = await service.DiscoverAsync(new AcademicDiscoveryQuery { SearchTerm = "x", Isbns = ["9781234567897"] });

        Assert.True(batch.NoProvidersConfigured);
        Assert.Empty(batch.Sources);
        Assert.Equal(0, works.CallCount);
        Assert.Equal(0, books.SearchCallCount);
        Assert.Equal(0, books.IsbnCallCount);
        Assert.Equal(2, batch.ProviderOutcomes.Count);
        Assert.All(batch.ProviderOutcomes, outcome => Assert.Equal("disabled", outcome.Status));
    }

    [Fact]
    public async Task Whole_request_budget_bounds_the_sum_of_participant_timeouts()
    {
        // Participation is sequential, so before this budget the worst case of ONE search was the sum
        // of every participant's own timeout: two catalogs that never answer would each spend a full
        // operation budget inside the same request, holding a shared search slot the whole time.
        // Measured here, not reasoned about: with a 3-second budget the pair cannot take the 60 that
        // two 30-second hangs would have cost.
        var first = new SlowWorkProvider("openalex", TimeSpan.FromSeconds(30));
        var second = new SlowWorkProvider("googlebooks", TimeSpan.FromSeconds(30));
        var service = Create([first, second], [], [], options =>
        {
            options.RequestTimeoutSeconds = 2;
            options.DiscoveryBudgetSeconds = 3;
        });

        var started = Stopwatch.GetTimestamp();
        var batch = await service.DiscoverAsync(new AcademicDiscoveryQuery { SearchTerm = "x" });
        var elapsed = Stopwatch.GetElapsedTime(started);

        Assert.True(elapsed < TimeSpan.FromSeconds(15), $"the request budget did not bound the search: it took {elapsed.TotalSeconds:0.0}s");
        Assert.Empty(batch.Sources);
        Assert.True(batch.PartialResult);
        Assert.True(batch.ResultCapped);
        Assert.Equal(2, batch.ProviderOutcomes.Count);
        Assert.All(batch.ProviderOutcomes, outcome =>
        {
            Assert.Equal("failed", outcome.Status);
            Assert.Equal("timeout", outcome.FailureCategory);
        });
        // And the bound costs no extra egress: the participant the budget had no room for was never
        // asked, rather than being fired off and discarded.
        Assert.Equal(1, first.CallCount);
        Assert.Equal(0, second.CallCount);
    }

    [Fact]
    public async Task Budget_running_out_labels_each_cut_off_doi_check()
    {
        // The DOI loop was the widest part of that sum: the controller accepts ten DOIs and each check
        // ran after the last. A check the budget stopped is reported as stopped, not as unregistered.
        var validator = new SlowValidator(TimeSpan.FromSeconds(30));
        var service = Create(
            [new FakeWorkProvider("openalex", true, _ => Task.FromResult<IReadOnlyList<AcademicSourceRecord>>([]))],
            [],
            [validator],
            options =>
            {
                options.RequestTimeoutSeconds = 2;
                options.DiscoveryBudgetSeconds = 3;
            });

        var started = Stopwatch.GetTimestamp();
        var batch = await service.DiscoverAsync(new AcademicDiscoveryQuery { Dois = ["10.5555/a", "10.5555/b", "10.5555/c"] });
        var elapsed = Stopwatch.GetElapsedTime(started);

        Assert.True(elapsed < TimeSpan.FromSeconds(15), $"the DOI loop ignored the request budget: it took {elapsed.TotalSeconds:0.0}s");
        Assert.Equal(3, batch.DoiValidations.Count);
        Assert.All(batch.DoiValidations, validation =>
        {
            Assert.False(validation.IsRegistered);
            Assert.Equal("timeout", validation.FailureCategory);
        });
        Assert.True(batch.ResultCapped);
        // Two of the three were never attempted, and the answer says so instead of leaving them out.
        Assert.Equal(1, validator.CallCount);
    }

    [Fact]
    public async Task A_caller_who_walks_away_is_still_a_cancellation()
    {
        // The budget must not swallow the caller's own cancellation: a student who navigated away is
        // an abandoned request, not a partial result served to nobody.
        var slow = new SlowWorkProvider("openalex", TimeSpan.FromSeconds(30));
        var service = Create([slow], [], [], options =>
        {
            options.RequestTimeoutSeconds = 2;
            options.DiscoveryBudgetSeconds = 60;
        });
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.DiscoverAsync(new AcademicDiscoveryQuery { SearchTerm = "x" }, caller.Token));

        Assert.Equal(1, slow.CallCount);
    }
}
