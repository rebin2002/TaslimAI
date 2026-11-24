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
        public Task<AcademicDoiValidationResult> ValidateAsync(string doi, CancellationToken cancellationToken = default) => behavior(doi);
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
}
