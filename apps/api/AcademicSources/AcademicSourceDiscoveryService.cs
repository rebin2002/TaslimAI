using Microsoft.Extensions.Options;

namespace Taslim.Api.AcademicSources;

/// <summary>
/// Orchestrates scholarly discovery across all configured catalogs. Provider failures are
/// isolated: one unavailable catalog degrades the batch to a partial result instead of
/// failing it, and the batch reports a safe per-provider outcome summary for the caller.
/// </summary>
public interface IAcademicSourceDiscoveryService
{
    Task<AcademicDiscoveryBatch> DiscoverAsync(AcademicDiscoveryQuery query, CancellationToken cancellationToken = default);
}

public sealed class AcademicSourceDiscoveryService(
    IEnumerable<IAcademicWorkSearchProvider> workSearchProviders,
    IEnumerable<IAcademicBookCatalogProvider> bookCatalogProviders,
    IEnumerable<IAcademicDoiValidator> doiValidators,
    IOptions<AcademicSourceDiscoveryOptions> optionsAccessor,
    ILogger<AcademicSourceDiscoveryService> logger) : IAcademicSourceDiscoveryService
{
    private readonly AcademicSourceDiscoveryOptions options = optionsAccessor.Value.Normalize();

    public async Task<AcademicDiscoveryBatch> DiscoverAsync(AcademicDiscoveryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var normalized = Normalize(query);
        var outcomes = new List<AcademicProviderOutcome>();
        var records = new List<AcademicSourceRecord>();
        var validations = new List<AcademicDoiValidationResult>();
        var validator = doiValidators.FirstOrDefault(candidate => candidate.IsConfigured);

        IEnumerable<string> AllKeys()
            => workSearchProviders.Select(provider => provider.ProviderKey)
                .Concat(bookCatalogProviders.Select(provider => provider.ProviderKey))
                .Concat(doiValidators.Select(provider => provider.ProviderKey))
                .Distinct(StringComparer.Ordinal);

        if (!options.Enabled)
        {
            foreach (var key in AllKeys())
                outcomes.Add(new AcademicProviderOutcome { ProviderKey = key, Status = AcademicProviderOutcomeStatus.Disabled });
            return new AcademicDiscoveryBatch { Sources = [], ProviderOutcomes = outcomes, DoiValidations = validations };
        }

        // An enabled master switch is not an enabled search. If every catalog is switched off, or is
        // switched on without what it needs (Google Books without its key), nothing here can answer
        // this query at all, and an ordinary empty batch would let the studio say "no records
        // matched" about a deployment that has no records to match against. Say what is true: there
        // is no capable source, and the caller turns that into an unavailable response.
        var canAnswerTerm = normalized.SearchTerm is not null
            && (workSearchProviders.Any(provider => provider.IsConfigured) || bookCatalogProviders.Any(provider => provider.IsConfigured));
        var canAnswerIsbn = normalized.Isbns.Count > 0 && bookCatalogProviders.Any(provider => provider.IsConfigured);
        var canAnswerDoi = normalized.Dois.Count > 0 && validator is not null;
        if (!canAnswerTerm && !canAnswerIsbn && !canAnswerDoi)
        {
            foreach (var key in AllKeys())
                outcomes.Add(new AcademicProviderOutcome { ProviderKey = key, Status = AcademicProviderOutcomeStatus.Disabled });
            logger.LogWarning("Academic discovery is enabled but no configured catalog can serve this query; answering unavailable.");
            return new AcademicDiscoveryBatch { Sources = [], ProviderOutcomes = outcomes, DoiValidations = validations, NoProvidersConfigured = true };
        }

        // One budget for the whole request, not one per participant. Participation is sequential, so
        // a search's wall-clock cost is the SUM of its participants: four catalogs and ten DOI checks
        // could each spend a full operation timeout, and a single request would hold a shared search
        // slot for minutes while every individual call stayed inside "its" timeout. The deadline is
        // tracked separately from the caller's token, because a caller who navigated away must still
        // see cancellation propagate as cancellation, not as a partial result.
        using var deadline = new CancellationTokenSource();
        deadline.CancelAfter(TimeSpan.FromSeconds(options.DiscoveryBudgetSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var participantToken = linked.Token;
        var deadlineReached = false;

        // Past the deadline no further call is started; that is what bounds the request. The
        // participant is then recorded as a timeout rather than dropped in silence, so the partial
        // result says exactly which part of the pool did not answer.
        bool BudgetGone(string providerKey)
        {
            if (!deadline.IsCancellationRequested) return false;
            deadlineReached = true;
            logger.LogWarning("Academic provider skipped by the whole-request discovery budget. ProviderKey={Provider}", providerKey);
            outcomes.Add(new AcademicProviderOutcome { ProviderKey = providerKey, Status = AcademicProviderOutcomeStatus.Failed, FailureCategory = AcademicSourceFailureCategories.Timeout });
            return true;
        }

        // The catalogs' own open-access filter exists only on the text-search endpoints, so an ISBN
        // lookup or a DOI-resolved record asked for with "records with an open location only" would
        // otherwise return rows the student explicitly excluded. The same evidence rule the search
        // adapters apply is applied to those rows here: keep a record only if a catalog actually
        // pointed at an open location for it.
        IEnumerable<AcademicSourceRecord> RequireOpenLocation(IReadOnlyList<AcademicSourceRecord> candidates)
            => normalized.OpenAccessOnly
                ? candidates.Where(record => record.AccessLevel >= AcademicAccessLevel.FullTextIndicated || !string.IsNullOrWhiteSpace(record.FullTextUrl))
                : candidates;

        if (normalized.SearchTerm is not null)
        {
            foreach (var provider in workSearchProviders.Where(provider => provider.IsConfigured))
            {
                if (BudgetGone(provider.ProviderKey)) continue;
                var batch = await RunAsync(provider.ProviderKey, () => provider.SearchWorksAsync(normalized, participantToken), cancellationToken);
                records.AddRange(batch.sources);
                outcomes.Add(batch.outcome);
            }
            foreach (var provider in bookCatalogProviders.Where(provider => provider.IsConfigured))
            {
                if (BudgetGone(provider.ProviderKey)) continue;
                var batch = await RunAsync(provider.ProviderKey, () => provider.SearchBooksAsync(normalized, participantToken), cancellationToken);
                records.AddRange(batch.sources);
                outcomes.Add(batch.outcome);
            }
        }

        foreach (var provider in bookCatalogProviders.Where(provider => provider.IsConfigured))
        {
            if (normalized.Isbns.Count == 0) continue;
            if (BudgetGone(provider.ProviderKey)) continue;
            var batch = await RunAsync(provider.ProviderKey, () => provider.LookupByIsbnAsync(normalized.Isbns, participantToken), cancellationToken);
            records.AddRange(RequireOpenLocation(batch.sources));
            outcomes.Add(batch.outcome);
        }

        foreach (var doi in normalized.Dois)
        {
            if (validator is null)
            {
                validations.Add(new AcademicDoiValidationResult { SubmittedDoi = doi, ResolvedDoi = null, IsRegistered = false, FailureCategory = AcademicSourceFailureCategories.Configuration });
                continue;
            }
            if (deadline.IsCancellationRequested)
            {
                // A check the budget cut off is not a check that found nothing: say which it was.
                deadlineReached = true;
                validations.Add(new AcademicDoiValidationResult { SubmittedDoi = doi, ResolvedDoi = null, IsRegistered = false, FailureCategory = AcademicSourceFailureCategories.Timeout });
                continue;
            }
            try
            {
                var result = await validator.ValidateAsync(doi, participantToken);
                validations.Add(result with { SubmittedDoi = result.SubmittedDoi ?? doi });
                if (result.IsRegistered && result.Source is { } source) records.AddRange(RequireOpenLocation([source]));
            }
            catch (AcademicSourceException exception)
            {
                logger.LogWarning("DOI validation attempt failed safely. Category={FailureCategory}", CategoryOf(exception));
                validations.Add(new AcademicDoiValidationResult { SubmittedDoi = doi, IsRegistered = false, FailureCategory = CategoryOf(exception) });
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("DOI validation stopped by the whole-request discovery budget.");
                deadlineReached = true;
                validations.Add(new AcademicDoiValidationResult { SubmittedDoi = doi, IsRegistered = false, FailureCategory = AcademicSourceFailureCategories.Timeout });
            }
            // The caller walking away is still not a result. It propagates, exactly as `RunAsync`
            // treats it, so an abandoned request stays an abandoned request rather than becoming a
            // partial answer that looks like a completed one.
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            // And the same last-resort discipline the catalog loop gets from `RunAsync:217`: a check
            // that failed for a reason nobody predicted — an adapter throwing on a payload it could
            // not parse, a `UriFormatException` from a header this code has never seen — used to
            // escape the loop and turn the entire search into an HTTP 500, discarding every catalog
            // answer that had already arrived. One identifier failing unexpectedly is a partial
            // result with a reason attached, which is precisely what the `partial` flag exists to say.
            catch (Exception exception)
            {
                logger.LogWarning(exception, "DOI validation attempt failed unexpectedly.");
                validations.Add(new AcademicDoiValidationResult { SubmittedDoi = doi, IsRegistered = false, FailureCategory = AcademicSourceFailureCategories.Unknown });
            }
        }
        if (validator is null && normalized.Dois.Count > 0 && !outcomes.Any(outcome => outcome.ProviderKey == "doi-validator"))
            logger.LogWarning("DOI lookups requested but no validator is configured.");

        var merged = AcademicSourceMerger.Merge(records, options.MaxCombinedResults);
        var truncated = merged.Count < DedupCount(records);
        foreach (var outcome in outcomes.ToList())
        {
            if (truncated && outcome.Status == AcademicProviderOutcomeStatus.Ok) outcomes[outcomes.IndexOf(outcome)] = outcome with { Truncated = true, Status = AcademicProviderOutcomeStatus.Partial };
        }

        // Whether this list is complete is something the search can answer about itself, and the
        // combined bound is not the only way it fails to be. A deployment with one catalog configured
        // asks that catalog for at most MaxResultsPerProvider records; if it came back with exactly
        // that many, it was cut off whatever the combined pool size was — and in a small deployment
        // the combined bound is never reached, so the combined bound alone proves nothing either way.
        // A budget that expired is the third way the answer is "not everything".
        var capped = merged.Count >= options.MaxCombinedResults
            || deadlineReached
            || outcomes.Any(outcome => outcome.Status == AcademicProviderOutcomeStatus.Ok && outcome.SourceCount >= normalized.MaxResults);

        if (outcomes.Count == 0)
            logger.LogWarning("Academic discovery requested but no provider is configured.");
        return new AcademicDiscoveryBatch
        {
            Sources = merged,
            ProviderOutcomes = outcomes,
            DoiValidations = validations,
            ResultCapped = capped,
        };
    }

    private async Task<(IReadOnlyList<AcademicSourceRecord> sources, AcademicProviderOutcome outcome)> RunAsync(
        string providerKey,
        Func<Task<IReadOnlyList<AcademicSourceRecord>>> action,
        CancellationToken callerToken)
    {
        try
        {
            var sources = await action();
            var status = sources.Count == 0 ? AcademicProviderOutcomeStatus.Empty : AcademicProviderOutcomeStatus.Ok;
            return (sources, new AcademicProviderOutcome { ProviderKey = providerKey, Status = status, SourceCount = sources.Count });
        }
        catch (AcademicSourceException exception)
        {
            var category = CategoryOf(exception);
            logger.LogWarning("Academic provider participation failed safely. ProviderKey={Provider}; Category={FailureCategory}", providerKey, category);
            return ([], new AcademicProviderOutcome { ProviderKey = providerKey, Status = AcademicProviderOutcomeStatus.Failed, FailureCategory = category });
        }
        // Two different cancellations reach this line and must not be confused. The caller walking
        // away is not a result: it propagates, so the request is abandoned as an abandoned request.
        // The discovery budget running out while the caller is still waiting IS a result: this
        // catalog did not answer in time, which is exactly what a partial result is for.
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Academic provider stopped by the whole-request discovery budget. ProviderKey={Provider}", providerKey);
            return ([], new AcademicProviderOutcome { ProviderKey = providerKey, Status = AcademicProviderOutcomeStatus.Failed, FailureCategory = AcademicSourceFailureCategories.Timeout });
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Academic provider participation failed unexpectedly. ProviderKey={Provider}", providerKey);
            return ([], new AcademicProviderOutcome { ProviderKey = providerKey, Status = AcademicProviderOutcomeStatus.Failed, FailureCategory = AcademicSourceFailureCategories.Unknown });
        }
    }

    private static string CategoryOf(AcademicSourceException exception) => exception switch
    {
        AcademicSourceRateLimitedException => AcademicSourceFailureCategories.RateLimited,
        AcademicSourceTimeoutException => AcademicSourceFailureCategories.Timeout,
        AcademicSourceMalformedException => AcademicSourceFailureCategories.Malformed,
        AcademicSourceRejectedException rejected => rejected.Details?.FailureCategory ?? AcademicSourceFailureCategories.Rejected,
        AcademicSourceUnavailableException => exception.Details?.FailureCategory ?? AcademicSourceFailureCategories.Transient,
        _ => AcademicSourceFailureCategories.Unknown,
    };

    private static int DedupCount(IReadOnlyList<AcademicSourceRecord> records)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        foreach (var record in records) if (seen.Add(record.DedupKey)) count++;
        return count;
    }

    private AcademicDiscoveryQuery Normalize(AcademicDiscoveryQuery query)
    {
        string? searchTerm = string.IsNullOrWhiteSpace(query.SearchTerm) ? null : Truncate(query.SearchTerm.Trim(), options.MaxSearchTermCharacters);
        var dois = (query.Dois ?? []).Select(AcademicSourceIdentity.NormalizeDoi)
            .Where(doi => doi is not null).Select(doi => doi!)
            .Distinct(StringComparer.Ordinal).Take(options.MaxIdentifierLookups).ToArray();
        var isbns = (query.Isbns ?? []).Select(AcademicSourceIdentity.ToIsbn13)
            .Where(isbn => isbn is not null).Select(isbn => isbn!)
            .Distinct(StringComparer.Ordinal).Take(options.MaxIdentifierLookups).ToArray();
        // What a catalog can actually hand back is bounded twice over: by the configured record
        // ceiling, and by its own pagination budget of MaxPages walks of MaxPerPage rows. When the
        // second is the smaller one, asking for the first is a promise the batch then repeats as
        // fact — the provider returns its page budget, the "did this catalog stop at its ceiling"
        // test compares that against a number it could never have reached, and a list that WAS cut
        // off is reported as complete. The ask is therefore the reachable ceiling, which also keeps a
        // tightened page budget from ever increasing the number of external calls per page.
        var fetchCeiling = Math.Min(options.MaxResultsPerProvider, Math.Max(1, options.MaxPerPage * options.MaxPages));
        var maxResults = Math.Clamp(query.MaxResults <= 0 ? fetchCeiling : query.MaxResults, 1, fetchCeiling);
        int? yearFrom = query.YearFrom is { } from && from >= 1000 && from <= 9999 ? from : null;
        int? yearTo = query.YearTo is { } to && to >= 1000 && to <= 9999 && (yearFrom is null || to >= yearFrom) ? to : null;
        var language = string.IsNullOrWhiteSpace(query.Language) ? null : Truncate(query.Language.Trim(), 16);
        return query with { SearchTerm = searchTerm, Dois = dois, Isbns = isbns, MaxResults = maxResults, YearFrom = yearFrom, YearTo = yearTo, Language = language };
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..Math.Max(1, max - 3)].TrimEnd() + "...";
}
