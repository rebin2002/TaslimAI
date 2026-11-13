using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

// Provider-free pricing-intent persistence contract for idempotent Movie clip
// replays (issue #677 / owner PR #269).
//
// Duplicate-ownership audit (2026-11-13, provider-free review):
// - PR #269 owns MovieStudioService.cs, MovieEntities.cs, MovieVideoExecution.cs,
//   the adapters and the Movie idempotency/execution test files. Untouched here.
// - PR #269's MovieStudioIdempotencyTests cover semantically CHANGED priced
//   intents (Replaying_a_priced_option_change_rejects_the_key_without_changing_the_canonical_job)
//   and the surrounding whitespace of the idempotency KEY
//   (Eighty_character_key_with_surrounding_spaces_replays_the_same_job).
// - PR #767 owns the job-handler boundary contract
//   (MovieVideoQualityIntentContract677Tests).
// - No open PR claims the normalization-EQUIVALENCE facet asserted here:
//   the same priced intent spelled with surrounding whitespace or letter-case
//   variants (and a padded key) must keep resolving to the single canonical
//   priced job, while the canonical job's persisted payload, trimmed key and
//   priced estimate stay byte-stable across those replays.
public sealed class MoviePricingIntentNormalizationReplay677Tests : IClassFixture<MovieOperationalNoWorkerFactory>
{
    private readonly MovieOperationalNoWorkerFactory factory;

    public MoviePricingIntentNormalizationReplay677Tests(MovieOperationalNoWorkerFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    private static Dictionary<string, object?> PricedIntent(
        string sourceResolution,
        string targetResolution,
        string qualityTier,
        string processingPath,
        string title) => new()
    {
        ["sourceResolution"] = sourceResolution,
        ["targetResolution"] = targetResolution,
        ["qualityTier"] = qualityTier,
        ["processingPath"] = processingPath,
        ["title"] = title,
    };

    [Fact]
    public async Task NormalizedEquivalentPricedIntentReplays_ResolveToTheCanonicalJob_WithoutMutatingPersistedIntent()
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Movie pricing intent normalization replay");
        var trimmedKey = $"movie-pricing-intent-normalization-{Guid.NewGuid():N}";
        var paddedKey = $"  {trimmedKey}  ";
        var path = $"/api/movie-studio/shots/{fixture.Shot.Id}/generate";

        // Canonical queue: every priced field spelled in its exact normalized form.
        var first = await MovieOperationalFixtures.PostWithConfirmedMovieQuoteAsync<MovieStudioGenerationResponse>(
            factory, client, path,
            PricedIntent("1080p", "1080p", "Standard", "native", "Canonical priced intent"),
            idempotencyKey: trimmedKey);
        Guid canonicalJobId = first.Job.Id;
        Guid canonicalClipId = first.ClipId;

        string canonicalInputJson;
        decimal? canonicalEstimate;
        int jobCountBefore;
        int clipCountBefore;
        int usageCountBefore;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var job = await db.GenerationJobs.AsNoTracking().SingleAsync(item => item.Id == canonicalJobId);
            canonicalInputJson = job.InputJson;
            canonicalEstimate = job.EstimatedProviderCostUsd;
            // The canonical job persists the trimmed key, never the padded spelling.
            Assert.Equal(trimmedKey, job.IdempotencyKey);
            var input = JsonSerializer.Deserialize<MovieGenerationInput>(job.InputJson);
            Assert.NotNull(input);
            // The persisted priced intent is present in normalized-comparable form.
            Assert.Equal("1080p", input!.SourceResolution!.Trim(), StringComparer.OrdinalIgnoreCase);
            Assert.Equal("1080p", input.TargetResolution!.Trim(), StringComparer.OrdinalIgnoreCase);
            Assert.Equal("Standard", input.QualityTier!.Trim(), StringComparer.OrdinalIgnoreCase);
            Assert.Equal("native", input.ProcessingPath!.Trim(), StringComparer.OrdinalIgnoreCase);
            Assert.False(input.UpscalingRequested);
            jobCountBefore = await db.GenerationJobs.CountAsync(item => item.IdempotencyKey == trimmedKey);
            clipCountBefore = await db.MovieClips.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id);
            usageCountBefore = await db.UsageTransactions.CountAsync(
                item => item.RequestId == $"generation:{canonicalJobId:N}");
        }

        // Replays: the SAME priced intent with formatting noise only - padding,
        // letter-case and a padded key header. Each must resolve idempotently to
        // the canonical job/clip, never to a new priced job or a false conflict.
        var equivalences = new[]
        {
            PricedIntent("1080P", "1080p", "standard", "NATIVE", "Canonical priced intent"),
            PricedIntent(" 1080p ", "1080p ", " Standard ", "native ", "Canonical priced intent"),
            PricedIntent("1080p", "1080P", "STANDARD", "Native", " Canonical priced intent "),
        };
        var replayKeys = new[] { trimmedKey, paddedKey, $" {trimmedKey} " };
        for (var i = 0; i < equivalences.Length; i++)
        {
            using var replay = await MovieOperationalFixtures.SendWithConfirmedMovieQuoteAsync(
                factory, client, HttpMethod.Post, path, equivalences[i], idempotencyKey: replayKeys[i]);
            var body = await replay.Content.ReadAsStringAsync();
            Assert.True(replay.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted,
                $"Formatted replay #{i} must stay idempotent, got {(int)replay.StatusCode}: {body}");
            var response = JsonSerializer.Deserialize<MovieStudioGenerationResponse>(body,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.Equal(canonicalJobId, response.Job.Id);
            Assert.Equal(canonicalClipId, response.ClipId);
        }

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        // One canonical job and clip survive all replays; the persisted priced
        // intent, estimate and trimmed key are byte-stable; usage accounting frozen.
        Assert.Equal(jobCountBefore, await verifyDb.GenerationJobs.CountAsync(item => item.IdempotencyKey == trimmedKey));
        Assert.Equal(clipCountBefore, await verifyDb.MovieClips.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.Equal(usageCountBefore, await verifyDb.UsageTransactions.CountAsync(
            item => item.RequestId == $"generation:{canonicalJobId:N}"));
        var verified = await verifyDb.GenerationJobs.AsNoTracking().SingleAsync(item => item.IdempotencyKey == trimmedKey);
        Assert.Equal(canonicalJobId, verified.Id);
        Assert.Equal(canonicalInputJson, verified.InputJson);
        Assert.Equal(canonicalEstimate, verified.EstimatedProviderCostUsd);
        var canonicalClip = Assert.Single(await verifyDb.MovieClips.AsNoTracking()
            .Where(item => item.MovieProjectId == fixture.Project.Project.Id).ToListAsync());
        Assert.Equal(canonicalClipId, canonicalClip.Id);
        Assert.Equal(canonicalJobId, canonicalClip.GenerationJobId);
    }
}
