using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Domain;
using Taslim.Api.Images;
using Taslim.Api.Documents;
using Taslim.Api.Presentations;
using Taslim.Api.Research;
using Taslim.Api.Social;
using Taslim.Api.Music;
using Taslim.Api.Voice;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Taslim.Api.Contracts;

namespace Taslim.Api.Operations;

public sealed class ProviderHealthService(
    TaslimDbContext db,
    IOptions<AiOptions> aiOptions,
    IOptions<ImageGenerationOptions> imageOptions,
    IOptions<DocumentGenerationOptions> documentOptions,
    IOptions<PresentationGenerationOptions> presentationOptions,
    IOptions<ResearchGenerationOptions> researchOptions,
    IOptions<SocialGenerationOptions> socialOptions,
    IOptions<MusicGenerationOptions> musicOptions,
    IOptions<VoiceGenerationOptions> voiceOptions,
    IOptions<MovieVideoOptions> movieOptions,
    IEnumerable<IMovieVideoProvider> movieProviders)
{
    private const int RecentFailureLimit = 8;
    private sealed record ProviderDefinition(
        string Key,
        string Category,
        bool Enabled,
        bool Configured,
        UsageFeature? Feature,
        IReadOnlySet<string> JobTypes);

    private sealed record JobTelemetry(
        string JobType,
        GenerationJobStatus Status,
        string? ErrorCode,
        int RetryCount,
        DateTime CreatedAt,
        DateTime? CompletedAt,
        DateTime? FailedAt);

    private sealed record UsageTelemetry(
        UsageFeature Feature,
        UsageTransactionStatus Status,
        string? FailureCode,
        int? LatencyMs,
        decimal? EstimatedProviderCostUsd,
        decimal ProviderCostUsd,
        DateTime CreatedAt,
        DateTime? CompletedAt);

    public async Task<IReadOnlyList<AdminProviderHealthDto>> GetAsync(
        (DateTime FromUtc, DateTime ToUtc) range,
        CancellationToken cancellationToken)
    {
        // Only operational columns are selected. In particular, InputJson, ResultJson,
        // ErrorMessage, request IDs, and user/workspace identifiers never enter this DTO.
        var jobs = await db.GenerationJobs.AsNoTracking()
            .Where(job => (job.CreatedAt >= range.FromUtc && job.CreatedAt < range.ToUtc)
                || (job.FailedAt >= range.FromUtc && job.FailedAt < range.ToUtc))
            .OrderByDescending(job => job.FailedAt ?? job.CompletedAt ?? job.CreatedAt)
            .Take(2_000)
            .Select(job => new JobTelemetry(
                job.JobType,
                job.Status,
                job.ErrorCode,
                job.RetryCount,
                job.CreatedAt,
                job.CompletedAt,
                job.FailedAt))
            .ToArrayAsync(cancellationToken);
        var usage = await db.UsageTransactions.AsNoTracking()
            .Where(transaction => transaction.CreatedAt >= range.FromUtc && transaction.CreatedAt < range.ToUtc)
            .Select(transaction => new UsageTelemetry(
                transaction.Feature,
                transaction.Status,
                transaction.FailureCode,
                transaction.LatencyMs,
                transaction.EstimatedProviderCostUsd,
                transaction.ProviderCostUsd,
                transaction.CreatedAt,
                transaction.CompletedAt))
            .ToArrayAsync(cancellationToken);

        var definitions = Definitions();
        return definitions.Select(definition => Build(definition, jobs, usage)).ToArray();
    }

    private IReadOnlyList<ProviderDefinition> Definitions()
    {
        var openAiConfigured = IsOpenAiConfigured();
        var openAiEnabled = aiOptions.Value.OpenAI.Enabled;
        var mockAvailable = aiOptions.Value.AllowMockProvider;
        var movieProvider = movieProviders.FirstOrDefault(item => string.Equals(item.Key, movieOptions.Value.ProviderKey, StringComparison.OrdinalIgnoreCase));
        var movieConfigured = movieProvider is not null
            && movieProvider.IsAvailable
            && !string.Equals(movieOptions.Value.ProviderKey, "unconfigured", StringComparison.OrdinalIgnoreCase);

        return
        [
            new("chat", "chat", openAiEnabled || mockAvailable, openAiConfigured || mockAvailable, UsageFeature.Chat, new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
            new("openai-image", "image", imageOptions.Value.Enabled, string.Equals(imageOptions.Value.ProviderKey, "openai", StringComparison.OrdinalIgnoreCase) && openAiConfigured, UsageFeature.Image, new HashSet<string>([GenerationJobTypes.ImageGenerate], StringComparer.OrdinalIgnoreCase)),
            new("openai-document", "document", documentOptions.Value.Enabled, openAiConfigured, UsageFeature.Document, new HashSet<string>([GenerationJobTypes.DocumentGenerate], StringComparer.OrdinalIgnoreCase)),
            new("openai-presentation", "presentation", presentationOptions.Value.Enabled, openAiConfigured, UsageFeature.Presentation, new HashSet<string>([GenerationJobTypes.PresentationGenerate], StringComparer.OrdinalIgnoreCase)),
            new("openai-research", "research", researchOptions.Value.Enabled, openAiConfigured, UsageFeature.Research, new HashSet<string>([GenerationJobTypes.ResearchGenerate], StringComparer.OrdinalIgnoreCase)),
            new("openai-social", "social", socialOptions.Value.Enabled, openAiConfigured, UsageFeature.Social, new HashSet<string>([GenerationJobTypes.SocialGenerate], StringComparer.OrdinalIgnoreCase)),
            new("mubert", "music", musicOptions.Value.Enabled, IsMubertConfigured(), UsageFeature.Music, new HashSet<string>([GenerationJobTypes.MusicGenerate], StringComparer.OrdinalIgnoreCase)),
            new("openai-voice", "voice", voiceOptions.Value.Enabled, openAiConfigured && !string.Equals(voiceOptions.Value.ProviderKey, "unconfigured", StringComparison.OrdinalIgnoreCase), UsageFeature.Voice, new HashSet<string>([GenerationJobTypes.VoiceGenerate], StringComparer.OrdinalIgnoreCase)),
            new("movie-video", "movie", movieOptions.Value.Enabled, movieConfigured, UsageFeature.Movie, GenerationJobTypes.MovieTypes),
        ];
    }

    private static AdminProviderHealthDto Build(
        ProviderDefinition definition,
        IReadOnlyList<JobTelemetry> jobs,
        IReadOnlyList<UsageTelemetry> usage)
    {
        var providerUsage = definition.Feature.HasValue
            ? usage.Where(item => item.Feature == definition.Feature.Value).ToArray()
            : [];
        var providerJobs = definition.JobTypes.Count == 0
            ? Array.Empty<JobTelemetry>()
            : jobs.Where(item => definition.JobTypes.Contains(item.JobType)).ToArray();
        var successfulUsage = providerUsage.Where(item => item.Status == UsageTransactionStatus.Completed).ToArray();
        var failedUsage = providerUsage.Where(item => item.Status == UsageTransactionStatus.Failed).ToArray();
        var failedJobs = providerJobs.Where(item => item.Status == GenerationJobStatus.Failed).ToArray();
        var recentFailures = failedJobs
            .Where(item => !string.IsNullOrWhiteSpace(item.ErrorCode))
            .OrderByDescending(item => item.FailedAt ?? item.CreatedAt)
            .Take(RecentFailureLimit)
            .Select(item => new AdminSanitizedGenerationFailureDto(
                item.JobType,
                SanitizeErrorCode(item.ErrorCode),
                item.FailedAt ?? item.CreatedAt))
            .ToArray();
        var successCount = successfulUsage.Length > 0
            ? successfulUsage.Length
            : providerJobs.Count(item => item.Status == GenerationJobStatus.Succeeded);
        var failureCount = failedUsage.Length > 0
            ? failedUsage.Length
            : failedJobs.Length;
        var rateLimitCount = failedJobs.Count(item => IsRateLimit(item.ErrorCode)) + failedUsage.Count(item => IsRateLimit(item.FailureCode));
        var timeoutCount = failedJobs.Count(item => IsTimeout(item.ErrorCode)) + failedUsage.Count(item => IsTimeout(item.FailureCode));
        var qualityControlCount = failedJobs.Count(item => IsQualityControlFailure(item.ErrorCode)) + failedUsage.Count(item => IsQualityControlFailure(item.FailureCode));
        var lastSuccessAt = successfulUsage.Select(item => item.CompletedAt ?? item.CreatedAt)
            .Concat(providerJobs.Where(item => item.Status == GenerationJobStatus.Succeeded).Select(item => item.CompletedAt ?? item.CreatedAt))
            .OrderByDescending(item => item)
            .FirstOrDefault();
        var lastFailure = failedJobs
            .OrderByDescending(item => item.FailedAt ?? item.CreatedAt)
            .FirstOrDefault();
        var hasLastSuccess = successCount > 0;
        var hasLastFailure = failureCount > 0;
        var status = !definition.Enabled
            ? "disabled"
            : !definition.Configured
                ? "unconfigured"
                : hasLastFailure && !hasLastSuccess
                    ? "recent_operational_failure"
                    : hasLastSuccess
                        ? "operational"
                        : "available_unknown";
        var latencies = providerUsage.Where(item => item.LatencyMs.HasValue && item.LatencyMs.Value >= 0).Select(item => item.LatencyMs!.Value).ToArray();

        return new AdminProviderHealthDto(
            definition.Key,
            definition.Category,
            status,
            definition.Enabled,
            definition.Configured,
            successCount,
            failureCount,
            latencies.Length == 0 ? null : (int)Math.Round(latencies.Average()),
            rateLimitCount,
            timeoutCount,
            qualityControlCount,
            providerJobs.Sum(item => item.RetryCount),
            0,
            false,
            providerUsage.Sum(item => item.EstimatedProviderCostUsd ?? 0m),
            providerUsage.Sum(item => item.ProviderCostUsd),
            hasLastSuccess ? lastSuccessAt : null,
            hasLastFailure ? lastFailure?.FailedAt ?? lastFailure?.CreatedAt : null,
            hasLastFailure ? SanitizeErrorCode(lastFailure?.ErrorCode) : null,
            recentFailures);
    }

    private bool IsOpenAiConfigured() => aiOptions.Value.OpenAI.Enabled && !string.IsNullOrWhiteSpace(aiOptions.Value.OpenAI.ApiKey);

    private bool IsMubertConfigured() => string.Equals(musicOptions.Value.ProviderKey, "mubert", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(musicOptions.Value.MubertCustomerId)
        && !string.IsNullOrWhiteSpace(musicOptions.Value.MubertAccessToken);

    private static string SanitizeErrorCode(string? errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode)) return "GENERATION_FAILURE";
        var safe = new string(errorCode.Trim().Take(96).Where(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.').ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "GENERATION_FAILURE" : safe;
    }

    private static bool IsRateLimit(string? code) => Has(code, "RATE_LIMIT") || Has(code, "RATE_LIMITED");
    private static bool IsTimeout(string? code) => Has(code, "TIMEOUT") || Has(code, "TIMED_OUT");
    private static bool IsQualityControlFailure(string? code) => Has(code, "OUTPUT_INVALID") || Has(code, "QUALITY") || Has(code, "QC") || Has(code, "CITATION_VALIDATION");
    private static bool Has(string? value, string token) => value?.Contains(token, StringComparison.OrdinalIgnoreCase) == true;
}
