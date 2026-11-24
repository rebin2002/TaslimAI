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

        if (!options.Enabled)
        {
            foreach (var key in EnumerateKeys())
                outcomes.Add(new AcademicProviderOutcome { ProviderKey = key, Status = AcademicProviderOutcomeStatus.Disabled });
            return new AcademicDiscoveryBatch { Sources = [], ProviderOutcomes = outcomes, DoiValidations = validations };

            IEnumerable<string> EnumerateKeys()
                => workSearchProviders.Select(provider => provider.ProviderKey)
                    .Concat(bookCatalogProviders.Select(provider => provider.ProviderKey))
                    .Concat(doiValidators.Select(provider => provider.ProviderKey))
                    .Distinct(StringComparer.Ordinal);
        }

        if (!string.IsNullOrWhiteSpace(normalized.SearchTerm))
        {
            foreach (var provider in workSearchProviders.Where(provider => provider.IsConfigured))
            {
                var batch = await RunAsync(provider.ProviderKey, () => provider.SearchWorksAsync(normalized, cancellationToken));
                records.AddRange(batch.sources);
                outcomes.Add(batch.outcome);
            }
            foreach (var provider in bookCatalogProviders.Where(provider => provider.IsConfigured))
            {
                var batch = await RunAsync(provider.ProviderKey, () => provider.SearchBooksAsync(normalized, cancellationToken));
                records.AddRange(batch.sources);
                outcomes.Add(batch.outcome);
            }
        }

        foreach (var provider in bookCatalogProviders.Where(provider => provider.IsConfigured))
        {
            if (normalized.Isbns.Count == 0) continue;
            var batch = await RunAsync(provider.ProviderKey, () => provider.LookupByIsbnAsync(normalized.Isbns, cancellationToken));
            records.AddRange(batch.sources);
            outcomes.Add(batch.outcome);
        }

        foreach (var doi in normalized.Dois)
        {
            if (validator is null)
            {
                validations.Add(new AcademicDoiValidationResult { SubmittedDoi = doi, ResolvedDoi = null, IsRegistered = false, FailureCategory = AcademicSourceFailureCategories.Configuration });
                continue;
            }
            try
            {
                var result = await validator.ValidateAsync(doi, cancellationToken);
                validations.Add(result with { SubmittedDoi = result.SubmittedDoi ?? doi });
                if (result.IsRegistered && result.Source is { } source) records.Add(source);
            }
            catch (AcademicSourceException exception)
            {
                logger.LogWarning("DOI validation attempt failed safely. Category={FailureCategory}", CategoryOf(exception));
                validations.Add(new AcademicDoiValidationResult { SubmittedDoi = doi, IsRegistered = false, FailureCategory = CategoryOf(exception) });
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

        if (outcomes.Count == 0)
            logger.LogWarning("Academic discovery requested but no provider is configured.");
        return new AcademicDiscoveryBatch { Sources = merged, ProviderOutcomes = outcomes, DoiValidations = validations };
    }

    private async Task<(IReadOnlyList<AcademicSourceRecord> sources, AcademicProviderOutcome outcome)> RunAsync(
        string providerKey,
        Func<Task<IReadOnlyList<AcademicSourceRecord>>> action)
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
        catch (OperationCanceledException)
        {
            throw;
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
        var maxResults = Math.Clamp(query.MaxResults <= 0 ? options.MaxResultsPerProvider : query.MaxResults, 1, options.MaxResultsPerProvider);
        int? yearFrom = query.YearFrom is { } from && from >= 1000 && from <= 9999 ? from : null;
        int? yearTo = query.YearTo is { } to && to >= 1000 && to <= 9999 && (yearFrom is null || to >= yearFrom) ? to : null;
        var language = string.IsNullOrWhiteSpace(query.Language) ? null : Truncate(query.Language.Trim(), 16);
        return query with { SearchTerm = searchTerm, Dois = dois, Isbns = isbns, MaxResults = maxResults, YearFrom = yearFrom, YearTo = yearTo, Language = language };
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..Math.Max(1, max - 3)].TrimEnd() + "...";
}
