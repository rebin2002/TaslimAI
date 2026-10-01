using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Taslim.Api.Tests.ProviderTesting;
using Xunit;

namespace Taslim.Api.Tests;

[Collection("MovieWave4Acceptance")]
public sealed class MovieWave5IntegrationE2ETests : IClassFixture<MovieWave4ApiFactory>
{
    private readonly MovieWave4ApiFactory factory;

    public MovieWave5IntegrationE2ETests(MovieWave4ApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Last_seed_production_intelligence_preserves_references_salvage_and_selected_only_mastering()
    {
        using var client = factory.CreateClient();
        factory.Scenarios[FakeProviderKind.Movie] = FakeProviderScenario.ImmediateSuccess;
        factory.Scenarios[FakeProviderKind.Voice] = FakeProviderScenario.ImmediateSuccess;
        factory.Scenarios[FakeProviderKind.Music] = FakeProviderScenario.ImmediateSuccess;

        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client,
            title: "The Last Seed — Wave5 intelligence",
            lastSeedStory: true,
            sceneDurationSeconds: null);
        var projectId = fixture.Project.Project.Id;
        var shotId = fixture.Shot.Id;

        var planning = await MovieOperationalFixtures.GetAsync<MovieShotDto>(client, $"/api/movie-studio/shots/{shotId}");
        Assert.True(planning.Readiness.Ready);
        Assert.Equal(MovieShotPlanStates.ReadyForStoryboard, planning.PlanState);

        var storyboard = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(client,
            $"/api/movie-studio/shots/{shotId}/production/versions", new
            {
                stage = MovieProductionStages.StoryboardCandidate,
                label = "Last Seed reference-locked storyboard",
                compositionJson = "{\"blocking\":\"seed-left\",\"screenDirection\":\"left-to-right\"}",
                stageProvenanceJson = "{\"source\":\"last-seed-wave5-acceptance\",\"step\":\"reference-lock\"}",
            });
        var approvedStoryboard = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(client,
            $"/api/movie-studio/production/versions/{storyboard.Id}/review", new { approve = true, reason = "Lock the reference composition before any expensive pass." });
        var keyframe = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(client,
            $"/api/movie-studio/shots/{shotId}/production/versions", new
            {
                stage = MovieProductionStages.ProductionKeyframe,
                sourceVersionId = approvedStoryboard.Id,
                compositionJson = "{\"frame\":\"last-seed-keyframe\",\"seed\":42,\"screenDirection\":\"left-to-right\"}",
                stageProvenanceJson = $"{{\"sourceVersionId\":\"{approvedStoryboard.Id}\",\"projectId\":\"{projectId}\",\"step\":\"reference-lock\"}}",
            });
        var approvedKeyframe = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(client,
            $"/api/movie-studio/production/versions/{keyframe.Id}/review", new { approve = true, reason = "Keyframe continuity and screen direction are approved." });
        Assert.Equal(MovieProductionStages.ApprovedKeyframe, approvedKeyframe.Stage);

        // Production Kit refs are a real, bounded, persisted hand-off. The package
        // hash is stable even though its capture timestamp is intentionally fresh.
        var productionKit = await MovieOperationalFixtures.GetAsync<MovieProductionReferencePackageDto>(client,
            $"/api/movie-studio/shots/{shotId}/production-references");
        var productionKitReload = await MovieOperationalFixtures.GetAsync<MovieProductionReferencePackageDto>(client,
            $"/api/movie-studio/shots/{shotId}/production-references");
        Assert.Equal(MovieProductionReferenceLimits.SchemaVersion, productionKit.SchemaVersion);
        Assert.Equal(productionKit.PackageHash, productionKitReload.PackageHash);
        Assert.True(productionKit.Guide.IsLocked);
        Assert.NotEmpty(productionKit.Characters);
        Assert.NotEmpty(productionKit.Locations);
        Assert.NotEmpty(productionKit.Props);
        Assert.NotNull(productionKit.Keyframe);

        var preflight = MovieWave5IntegrationContract.EvaluatePreflight(
            projectId,
            shotId,
            productionKit.PackageHash,
            productionKit.Guide.IsLocked,
            planning.Readiness.Ready,
            providerReady: true,
            budgetAllowsDraft: true);
        Assert.True(preflight.Ready, string.Join(", ", preflight.BlockingReasons));
        Assert.Empty(preflight.BlockingReasons);

        // Keep the Last Seed acceptance on the already-proven generation boundary:
        // queue three independent raw-footage passes sequentially and persist each
        // as a distinct take without selecting one implicitly.
        var createdTakeIds = new List<Guid>();
        for (var takeNumber = 1; takeNumber <= 3; takeNumber++)
        {
            var generated = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(client,
                $"/api/movie-studio/shots/{shotId}/generate", new { title = $"Last Seed economical review take {takeNumber}" },
                idempotencyKey: $"last-seed-wave5-take-{takeNumber}");
            var terminal = await WaitForTerminalAsync(client, generated.Job.Id);
            Assert.Equal(GenerationJobStatus.Succeeded.ToString(), terminal.Status);
            var generatedClip = await WaitForClipAsync(client, projectId, generated.ClipId);
            Assert.NotNull(generatedClip.AssetId);
            var take = await MovieOperationalFixtures.PostAsync<MovieV2TakeDto>(client,
                $"/api/movie-studio/shots/{shotId}/takes", new
                {
                    label = $"Last Seed raw take {takeNumber}",
                    qualityLevel = takeNumber == 1 ? MovieQualityLevels.Cinematic : MovieQualityLevels.Standard,
                    autoDirectorEnabled = false,
                    movieClipId = generated.ClipId,
                    generationJobId = generated.Job.Id,
                    assetId = generatedClip.AssetId,
                    notes = "Raw footage candidate retained for segment salvage review.",
                });
            createdTakeIds.Add(take.Id);
        }

        MovieV2ShotDto? reloadedShot = null;
        for (var attempt = 0; attempt < 120; attempt++)
        {
            var hierarchy = await MovieOperationalFixtures.GetAsync<MovieV2HierarchyDto>(client,
                $"/api/movie-studio/projects/{projectId}/hierarchy");
            reloadedShot = hierarchy.Acts.Single().Sequences.Single().Scenes.Single().Shots.Single();
            if (createdTakeIds.All(takeId => reloadedShot.Takes.Any(take => take.Id == takeId && take.AssetId.HasValue))) break;
            await Task.Delay(25);
        }
        Assert.NotNull(reloadedShot);
        Assert.Equal(3, reloadedShot!.Takes.Count);
        Assert.Equal(3, reloadedShot.Takes.Select(take => take.Id).Distinct().Count());
        Assert.All(createdTakeIds, takeId => Assert.Contains(reloadedShot.Takes, take => take.Id == takeId && take.AssetId.HasValue));
        Assert.DoesNotContain(reloadedShot.Takes, take => take.SelectedAt.HasValue || take.Status == MovieTakeStatuses.Selected);

        var selected = reloadedShot.Takes.OrderBy(take => take.VersionNumber).First();
        var alternates = reloadedShot.Takes.Where(take => take.Id != selected.Id).ToArray();
        foreach (var take in reloadedShot.Takes)
        {
            var approved = await MovieOperationalFixtures.PostAsync<MovieV2TakeDto>(client,
                $"/api/movie-studio/takes/{take.Id}/approvals", new
                { decision = MovieApprovalDecisions.Approved, comment = "Reviewable Last Seed take." });
            Assert.Equal(MovieTakeStatuses.Approved, approved.Status);
        }
        using (var select = await MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post,
                   $"/api/movie-studio/takes/{selected.Id}/select", null))
            Assert.Equal(HttpStatusCode.NoContent, select.StatusCode);
        using (var finalize = await MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post,
                   $"/api/movie-studio/takes/{selected.Id}/finalize", null))
            Assert.Equal(HttpStatusCode.NoContent, finalize.StatusCode);

        // Wave5 salvage is deliberately evaluated before a regeneration request.
        var continuityKey = "young-farmer:last-seed:dry-village:old-tree-field";
        var segmentCandidates = new[]
        {
            new MovieWave5SegmentCandidate(selected.Id, "opening", 0m, 2m, true, continuityKey, "left-to-right"),
            new MovieWave5SegmentCandidate(alternates[0].Id, "middle", 2m, 3m, true, continuityKey, "left-to-right"),
            new MovieWave5SegmentCandidate(alternates[1].Id, "unstable-tail", 5m, 2m, false, continuityKey, "right-to-left"),
        };
        var salvage = MovieWave5IntegrationContract.BuildSalvagePlan(shotId, segmentCandidates, 5m);
        Assert.False(salvage.RequiresRegeneration);
        Assert.Equal(MovieWave5IntegrationContract.NoRegenerationScope, salvage.RegenerationScope);
        Assert.Equal(2, salvage.SelectedSegments.Count);
        Assert.Empty(salvage.Inserts);
        var continuity = MovieWave5IntegrationContract.CheckContinuity(salvage.SelectedSegments, continuityKey, "left-to-right");
        Assert.True(continuity.Passed, continuity.Message);

        // A one-second uncovered tail produces one minimal insert proposal, not a
        // whole-scene regeneration. This is the future Wave5 proposal contract.
        var insertProposal = MovieWave5IntegrationContract.BuildSalvagePlan(shotId, segmentCandidates, 6m);
        Assert.True(insertProposal.RequiresRegeneration);
        Assert.Equal(MovieWave5IntegrationContract.MinimalInsertScope, insertProposal.RegenerationScope);
        var insert = Assert.Single(insertProposal.Inserts);
        Assert.True(insert.IsMinimal);
        Assert.Equal(5m, insert.StartSeconds);
        Assert.Equal(1m, insert.DurationSeconds);
        Assert.DoesNotContain(insertProposal.SelectedSegments, item => item.SegmentId == "unstable-tail");
        Assert.False(MovieWave5IntegrationContract.CheckContinuity(
            [new MovieWave5SegmentSelection(alternates[1].Id, "unstable-tail", 5m, 1m, continuityKey, "right-to-left")],
            continuityKey,
            "left-to-right").Passed);

        var budget = MovieWave5IntegrationContract.OptimizeDraftFirst(new MovieWave5BudgetInput(
            MovieResolutionTiers.P2160,
            BudgetConstrained: true,
            PreferredSourceResolution: MovieResolutionTiers.P1080,
            EstimatedCostUsd: null));
        Assert.Equal(MovieResolutionTiers.P720, budget.DraftSourceResolution);
        Assert.Equal(MovieResolutionTiers.P2160, budget.TargetMasterResolution);
        Assert.True(budget.UpgradeSelectedOnly);
        Assert.Null(budget.EstimatedCostUsd);

        using (var blockedAlternate = await MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post,
                   $"/api/movie-studio/takes/{alternates[0].Id}/upscale", new
                   { targetMasterResolution = MovieUpscaleResolutionCatalog.P4K, sourceResolution = MovieResolutionTiers.P1080 }))
        {
            Assert.Equal(HttpStatusCode.Conflict, blockedAlternate.StatusCode);
            var body = await blockedAlternate.Content.ReadFromJsonAsync<MovieTakeUpscaleEligibilityDto>();
            Assert.NotNull(body);
            Assert.False(body!.Eligible);
            Assert.Equal(MovieTakeUpscaleEligibilityCodes.TakeNotSelected, body.Code);
        }
        using (var selectedUpgrade = await MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post,
                   $"/api/movie-studio/takes/{selected.Id}/upscale", new
                   { targetMasterResolution = MovieUpscaleResolutionCatalog.P4K, sourceResolution = MovieResolutionTiers.P1080 }))
        {
            Assert.Equal(HttpStatusCode.Accepted, selectedUpgrade.StatusCode);
            var body = await selectedUpgrade.Content.ReadFromJsonAsync<MovieTakeUpscaleEligibilityDto>();
            Assert.NotNull(body);
            Assert.True(body!.Eligible);
            Assert.Equal(MovieTakeUpscaleAuditStatuses.PendingExecution, body.AuditStatus);
        }
        var finalMaster = await MovieOperationalFixtures.PostAsync<MovieFinalMasterDto>(client,
            $"/api/movie-studio/takes/{selected.Id}/final-mastering", new { targetProfile = MovieFinalMasteringProfiles.Uhd4K });
        Assert.Equal(selected.Id, finalMaster.SourceTakeId);
        Assert.Null(finalMaster.OutputAssetId);
        Assert.Null(finalMaster.GenerationJobId);
        Assert.Contains(selected.Id.ToString(), finalMaster.ProvenanceJson, StringComparison.OrdinalIgnoreCase);

        var voice = await MovieOperationalFixtures.PostAsync<CreateVoiceGenerationResponse>(client, "/api/voice-generation/jobs", new
        {
            workspaceId = fixture.Owner.PersonalWorkspace.Id,
            text = "We can still begin.",
            language = "en",
            voiceStyle = "neutral",
            speakingStyle = "clear",
            title = "Last Seed Wave5 dialogue",
        });
        var voiceJob = await WaitForTerminalAsync(client, voice.Job.Id);
        Assert.Equal(GenerationJobStatus.Succeeded.ToString(), voiceJob.Status);
        var voiceAsset = await FindAssetIdAsync(voice.Job.Id);

        var music = await MovieOperationalFixtures.PostAsync<CreateMusicGenerationResponse>(client, "/api/music-generation/jobs", new
        {
            workspaceId = fixture.Owner.PersonalWorkspace.Id,
            description = "A restrained hopeful instrumental bed for the first rain.",
            purpose = "Last Seed Wave5 timeline acceptance",
            genre = "ambient",
            mood = "inspiring",
            durationSeconds = 30,
            vocalPreference = "instrumental",
            language = "auto",
            title = "Last Seed Wave5 music",
        });
        var musicJob = await WaitForTerminalAsync(client, music.Job.Id);
        Assert.Equal(GenerationJobStatus.Succeeded.ToString(), musicJob.Status);
        var musicAsset = await FindAssetIdAsync(music.Job.Id);

        var timelineRevision = await MovieOperationalFixtures.PostAsync<MovieTimelineRevisionDto>(client,
            $"/api/movie-studio/projects/{projectId}/timeline/revisions", new
            {
                label = "Last Seed Wave5 salvaged timeline",
                changeSummary = "Usable ranges first, minimal insert marker, selected take, dialogue, music, and captions.",
                tracks = new object[]
                {
                    new { kind = MovieTimelineTrackKinds.Video, name = "Selected picture", trackNumber = 1, items = new[] { new { kind = MovieTimelineItemKinds.VisualTake, sourceTakeId = selected.Id, timelineInMilliseconds = 0, sourceInMilliseconds = 0, label = "Salvaged selected take" } } },
                    new { kind = MovieTimelineTrackKinds.Audio, name = "Dialogue", trackNumber = 2, items = new[] { new { kind = MovieTimelineItemKinds.AudioAsset, sourceAssetId = voiceAsset, timelineInMilliseconds = 0, sourceInMilliseconds = 0, label = "Dialogue" } } },
                    new { kind = MovieTimelineTrackKinds.Audio, name = "Music", trackNumber = 3, items = new[] { new { kind = MovieTimelineItemKinds.AudioAsset, sourceAssetId = musicAsset, timelineInMilliseconds = 0, sourceInMilliseconds = 0, label = "Music" } } },
                },
            });
        Assert.Equal(MovieTimelineRevisionStatuses.Draft, timelineRevision.Status);
        var reloadedTimeline = await MovieOperationalFixtures.GetAsync<MovieTimelineDto>(client, $"/api/movie-studio/projects/{projectId}/timeline");
        Assert.Equal(timelineRevision.Id, reloadedTimeline.CurrentRevisionId);
        Assert.Contains(reloadedTimeline.CurrentRevision!.Tracks, track => track.Name == "Dialogue");

        var exportTimeline = MovieWave4ExportSeam.BuildTimeline(
        [
            new(MovieWave4TrackKinds.Video, selected.AssetId!.Value, 0, 5_000, selected.Id),
            new(MovieWave4TrackKinds.Voice, voiceAsset, 0, 1_200),
            new(MovieWave4TrackKinds.Music, musicAsset, 0, 5_000),
            new(MovieWave4TrackKinds.Captions, null, 0, 1_200, CaptionText: "We can still begin."),
        ], 5_000);
        var export = MovieWave4ExportSeam.Evaluate(new MovieWave4ExportRequest(
            projectId, shotId, selected.Id, selected.Id, approvedKeyframe.Id, exportTimeline));
        Assert.True(export.Ready);
        Assert.Equal(MovieWave4ExportCodes.ReadyForHandoff, export.Code);
        Assert.False(string.IsNullOrWhiteSpace(export.HandoffProvenanceHash));

        var afterReload = await MovieOperationalFixtures.GetAsync<MovieV2HierarchyDto>(client, $"/api/movie-studio/projects/{projectId}/hierarchy");
        var afterReloadShot = afterReload.Acts.Single().Sequences.Single().Scenes.Single().Shots.Single();
        Assert.Equal(shotId, afterReloadShot.Id);
        Assert.Equal(3, afterReloadShot.Takes.Count);
        Assert.Equal(0m, (await client.GetFromJsonAsync<UsageSummaryDto>($"/api/workspaces/{fixture.Owner.PersonalWorkspace.Id}/usage/summary"))!.CustomerChargedAmount);
        var userFacingContracts = string.Join("\n", JsonSerializer.Serialize(productionKit), JsonSerializer.Serialize(planning), JsonSerializer.Serialize(export));
        Assert.DoesNotContain("\"provider\":", userFacingContracts, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"model\":", userFacingContracts, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"prompt\":", userFacingContracts, StringComparison.OrdinalIgnoreCase);
        Assert.True(factory.Calls.Calls(FakeProviderKind.Movie) >= 3);
        Assert.True(factory.Calls.Calls(FakeProviderKind.Voice) > 0);
        Assert.True(factory.Calls.Calls(FakeProviderKind.Music) > 0);
    }

    private async Task<Guid> FindAssetIdAsync(Guid jobId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Assets.AsNoTracking()
            .Where(item => item.SourceGenerationJobId == jobId)
            .Select(item => item.Id)
            .SingleAsync();
    }

    private static async Task<MovieClipDto> WaitForClipAsync(HttpClient client, Guid projectId, Guid clipId)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            var project = await MovieOperationalFixtures.GetAsync<MovieStudioProjectDto>(client,
                $"/api/movie-studio/projects/{projectId}");
            var clip = project.Clips
                .Concat(project.Scenes.SelectMany(scene => scene.Clips.Concat(scene.Shots.SelectMany(shot => shot.Clips))))
                .SingleOrDefault(item => item.Id == clipId);
            if (clip?.AssetId.HasValue == true) return clip;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Movie clip {clipId} did not receive a persisted Asset.");
    }

    private static async Task<GenerationJobDto> WaitForTerminalAsync(HttpClient client, Guid jobId)
    {
        for (var attempt = 0; attempt < 180; attempt++)
        {
            var current = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{jobId}");
            if (current?.Status is "Succeeded" or "Failed" or "Cancelled") return current;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Generation job {jobId} did not reach a terminal state.");
    }
}

public sealed class MovieWave5SafetyApiFactory : GenerationJobsApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("MovieVideo:Enabled", "false");
        builder.UseSetting("Billing:CustomerChargingEnabled", "false");
        builder.UseSetting("GenerationJobs:PollIntervalMilliseconds", "25");
    }
}

[CollectionDefinition("MovieWave5Safety", DisableParallelization = true)]
public sealed class MovieWave5SafetyCollectionDefinition { }

[Collection("MovieWave5Safety")]
public sealed class MovieWave5SafetyE2ETests : IClassFixture<MovieWave5SafetyApiFactory>
{
    private readonly MovieWave5SafetyApiFactory factory;

    public MovieWave5SafetyE2ETests(MovieWave5SafetyApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Provider_off_last_seed_path_fails_without_asset_or_charge()
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client,
            title: "The Last Seed — Wave5 provider-off safety",
            lastSeedStory: true,
            sceneDurationSeconds: 5);
        var readiness = await client.GetFromJsonAsync<MovieStudioProviderResponse>("/api/movie-studio/provider");
        Assert.NotNull(readiness);
        Assert.False(readiness!.Provider.Ready);
        Assert.Empty(readiness.Provider.SupportedOperations);

        var response = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(client,
            $"/api/movie-studio/shots/{fixture.Shot.Id}/generate",
            new { title = "Provider-off safety attempt" },
            idempotencyKey: "last-seed-wave5-provider-off");
        var failed = await WaitForTerminalAsync(client, response.Job.Id);
        Assert.Equal(GenerationJobStatus.Failed.ToString(), failed.Status);
        Assert.Equal(GenerationJobErrorCodes.MovieProviderUnavailable, failed.ErrorCode);
        Assert.Empty(failed.Outputs);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.GenerationJobId == response.Job.Id);
        Assert.Equal(UsageTransactionStatus.Failed, usage.Status);
        Assert.Equal(0m, usage.ChargedAmount);
        Assert.False(await db.Assets.AsNoTracking().AnyAsync(item => item.SourceGenerationJobId == response.Job.Id));
        Assert.False(await db.GenerationJobOutputs.AsNoTracking().AnyAsync(item => item.GenerationJobId == response.Job.Id));
        Assert.Equal(0m, (await client.GetFromJsonAsync<UsageSummaryDto>($"/api/workspaces/{fixture.Owner.PersonalWorkspace.Id}/usage/summary"))!.CustomerChargedAmount);
    }

    private static async Task<GenerationJobDto> WaitForTerminalAsync(HttpClient client, Guid jobId)
    {
        for (var attempt = 0; attempt < 160; attempt++)
        {
            var current = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{jobId}");
            if (current?.Status is "Succeeded" or "Failed" or "Cancelled") return current;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Generation job {jobId} did not reach a terminal state.");
    }
}
