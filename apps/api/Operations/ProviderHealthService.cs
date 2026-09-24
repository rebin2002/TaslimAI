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
    private sealed record RecentFailure(string JobType, DateTime? FailedAt, string? ErrorCode);

    public async Task<IReadOnlyList<AdminProviderHealthDto>> GetAsync(CancellationToken cancellationToken)
    {
        var recentFailureRows = await db.GenerationJobs.AsNoTracking()
            .Where(job => job.Status == GenerationJobStatus.Failed && job.FailedAt >= DateTime.UtcNow.AddHours(-24))
            .OrderByDescending(job => job.FailedAt)
            .Select(job => new { job.JobType, job.FailedAt, job.ErrorCode })
            .Take(500)
            .ToListAsync(cancellationToken);
        var recentFailures = recentFailureRows.Select(item => new RecentFailure(item.JobType, item.FailedAt, item.ErrorCode)).ToArray();

        var movieProvider = movieProviders.FirstOrDefault(item => string.Equals(item.Key, movieOptions.Value.ProviderKey, StringComparison.OrdinalIgnoreCase));
        var results = new List<AdminProviderHealthDto>
        {
            BuildAiStatus(recentFailures),
            BuildStatus("openai-image", "image", imageOptions.Value.Enabled, string.Equals(imageOptions.Value.ProviderKey, "openai", StringComparison.OrdinalIgnoreCase) && IsOpenAiConfigured(), recentFailures, [GenerationJobTypes.ImageGenerate]),
            BuildStatus("openai-document", "document", documentOptions.Value.Enabled, IsOpenAiConfigured(), recentFailures, [GenerationJobTypes.DocumentGenerate]),
            BuildStatus("openai-presentation", "presentation", presentationOptions.Value.Enabled, IsOpenAiConfigured(), recentFailures, [GenerationJobTypes.PresentationGenerate]),
            BuildStatus("openai-research", "research", researchOptions.Value.Enabled, IsOpenAiConfigured(), recentFailures, [GenerationJobTypes.ResearchGenerate]),
            BuildStatus("openai-social", "social", socialOptions.Value.Enabled, IsOpenAiConfigured(), recentFailures, [GenerationJobTypes.SocialGenerate]),
            BuildStatus("mubert", "music", musicOptions.Value.Enabled, IsMubertConfigured(), recentFailures, [GenerationJobTypes.MusicGenerate]),
            BuildStatus("openai-voice", "voice", voiceOptions.Value.Enabled, IsOpenAiConfigured() && !string.Equals(voiceOptions.Value.ProviderKey, "unconfigured", StringComparison.OrdinalIgnoreCase), recentFailures, [GenerationJobTypes.VoiceGenerate]),
            BuildMovieStatus(movieProvider, recentFailures),
        };
        return results;
    }

    private AdminProviderHealthDto BuildAiStatus(IReadOnlyList<RecentFailure> failures)
    {
        var configured = aiOptions.Value.OpenAI.Enabled && !string.IsNullOrWhiteSpace(aiOptions.Value.OpenAI.ApiKey);
        var mock = aiOptions.Value.AllowMockProvider;
        var failure = FindFailure(failures, [GenerationJobTypes.SystemTest]);
        var status = !configured && !mock ? "unconfigured" : failure is not null ? "recent_operational_failure" : "available_unknown";
        return new("chat", "chat", status, failure?.FailedAt, failure?.ErrorCode);
    }

    private AdminProviderHealthDto BuildMovieStatus(IMovieVideoProvider? provider, IReadOnlyList<RecentFailure> failures)
    {
        var enabled = movieOptions.Value.Enabled;
        var configured = provider is not null && provider.IsAvailable && !string.Equals(movieOptions.Value.ProviderKey, "unconfigured", StringComparison.OrdinalIgnoreCase);
        var failure = FindFailure(failures, GenerationJobTypes.MovieTypes);
        var status = !enabled ? "disabled" : !configured ? "unconfigured" : failure is not null ? "recent_operational_failure" : "available_unknown";
        return new("movie-video", "movie", status, failure?.FailedAt, failure?.ErrorCode);
    }

    private AdminProviderHealthDto BuildStatus(
        string key,
        string category,
        bool enabled,
        bool configured,
        IReadOnlyList<RecentFailure> failures,
        IEnumerable<string> jobTypes)
    {
        var failure = FindFailure(failures, jobTypes);
        var status = !enabled ? "disabled" : !configured ? "unconfigured" : failure is not null ? "recent_operational_failure" : "available_unknown";
        return new(key, category, status, failure?.FailedAt, failure?.ErrorCode);
    }

    private bool IsOpenAiConfigured() => aiOptions.Value.OpenAI.Enabled && !string.IsNullOrWhiteSpace(aiOptions.Value.OpenAI.ApiKey);
    private bool IsMubertConfigured() => string.Equals(musicOptions.Value.ProviderKey, "mubert", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(musicOptions.Value.MubertCustomerId)
        && !string.IsNullOrWhiteSpace(musicOptions.Value.MubertAccessToken);

    private static RecentFailure? FindFailure(IReadOnlyList<RecentFailure> failures, IEnumerable<string> jobTypes)
    {
        return failures.FirstOrDefault(item => jobTypes.Contains(item.JobType)
            && IsProviderFailure(item.ErrorCode));
    }

    private static bool IsProviderFailure(string? errorCode) =>
        !string.IsNullOrWhiteSpace(errorCode) && (errorCode.Contains("PROVIDER", StringComparison.OrdinalIgnoreCase) || errorCode.Contains("GENERATION_FAILED", StringComparison.OrdinalIgnoreCase));
}
