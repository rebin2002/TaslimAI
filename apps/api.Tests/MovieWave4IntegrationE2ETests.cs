using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Taslim.Api.Tests.ProviderTesting;
using Taslim.Api.Voice;
using Taslim.Api.Music;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Test-host-only composition of the existing fake/disabled adapters. No
/// provider credentials, network generation, or customer charging is enabled.
/// </summary>
public sealed class MovieWave4ApiFactory : GenerationJobsApiFactory
{
    public FakeProviderScenarioCatalog Scenarios { get; } = new();
    public FakeProviderCallLog Calls { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("MovieVideo:Enabled", "true");
        builder.UseSetting("MovieVideo:ProviderKey", "fake-movie");
        builder.UseSetting("MovieVideo:StatusPollIntervalSeconds", "0");
        builder.UseSetting("MovieVideo:MaxStatusPolls", "3");
        builder.UseSetting("MusicGeneration:Enabled", "true");
        builder.UseSetting("MusicGeneration:ProviderKey", "fake-music");
        builder.UseSetting("VoiceGeneration:Enabled", "true");
        builder.UseSetting("VoiceGeneration:ProviderKey", "fake-voice");
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(Scenarios);
            services.AddSingleton(Calls);
            services.RemoveAll<IMovieVideoProvider>();
            services.AddSingleton<IMovieVideoProvider>(sp => new FakeMovieVideoProvider(
                sp.GetRequiredService<FakeProviderScenarioCatalog>(),
                sp.GetRequiredService<FakeProviderCallLog>()));
            services.RemoveAll<IMusicGenerationProvider>();
            services.AddSingleton<IMusicGenerationProvider>(sp => new FakeMusicGenerationProvider(
                sp.GetRequiredService<FakeProviderScenarioCatalog>(),
                sp.GetRequiredService<FakeProviderCallLog>()));
            services.RemoveAll<IVoiceGenerationProvider>();
            services.AddSingleton<IVoiceGenerationProvider>(sp => new FakeVoiceGenerationProvider(
                sp.GetRequiredService<FakeProviderScenarioCatalog>(),
                sp.GetRequiredService<FakeProviderCallLog>()));
        });
    }
}

[CollectionDefinition("MovieWave4Acceptance", DisableParallelization = true)]
public sealed class MovieWave4AcceptanceCollectionDefinition { }

[Collection("MovieWave4Acceptance")]
public sealed class MovieWave4IntegrationE2ETests : IClassFixture<MovieWave4ApiFactory>
{
    private static readonly Guid DisabledSfxReference = Guid.Parse("70000000-0000-0000-0000-000000000007");
    private readonly MovieWave4ApiFactory factory;

    public MovieWave4IntegrationE2ETests(MovieWave4ApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Complete_wave4_flow_reloads_provenance_and_stops_at_provider_neutral_export_handoff()
    {
        using var client = factory.CreateClient();
        factory.Scenarios[FakeProviderKind.Movie] = FakeProviderScenario.ImmediateSuccess;
        factory.Scenarios[FakeProviderKind.Voice] = FakeProviderScenario.ImmediateSuccess;
        factory.Scenarios[FakeProviderKind.Music] = FakeProviderScenario.ImmediateSuccess;
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(client, title: "Wave 4 deterministic acceptance");

        var storyboard = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(client,
            $"/api/movie-studio/shots/{fixture.Shot.Id}/production/versions", new
            {
                stage = MovieProductionStages.StoryboardCandidate,
                label = "Wave 4 storyboard candidate",
                compositionJson = "{\"blocking\":\"subject-left\",\"seed\":42}",
                stageProvenanceJson = "{\"source\":\"wave4-acceptance\",\"step\":\"storyboard\"}",
            });
        var approvedStoryboard = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(client,
            $"/api/movie-studio/production/versions/{storyboard.Id}/review", new { approve = true, reason = "Storyboard approved." });
        var keyframe = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(client,
            $"/api/movie-studio/shots/{fixture.Shot.Id}/production/versions", new
            {
                stage = MovieProductionStages.ProductionKeyframe,
                sourceVersionId = approvedStoryboard.Id,
                compositionJson = "{\"frame\":\"approved-keyframe\",\"seed\":42}",
                stageProvenanceJson = "{\"sourceVersionId\":\"" + approvedStoryboard.Id + "\",\"step\":\"keyframe\"}",
            });
        var approvedKeyframe = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(client,
            $"/api/movie-studio/production/versions/{keyframe.Id}/review", new { approve = true, reason = "Keyframe continuity approved." });
        Assert.Equal(MovieProductionStages.ApprovedKeyframe, approvedKeyframe.Stage);

        var generated = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(client,
            $"/api/movie-studio/shots/{fixture.Shot.Id}/generate", new { title = "Wave 4 selected take source" }, idempotencyKey: "wave4-selected-source");
        var generatedJob = await WaitForTerminalAsync(client, generated.Job.Id);
        Assert.Equal("Succeeded", generatedJob.Status);
        var generatedClip = await WaitForClipAsync(client, fixture.Project.Project.Id, generated.ClipId);
        Assert.NotNull(generatedClip.AssetId);
        var videoAssetId = generatedClip.AssetId!.Value;

        var selectedCandidate = await MovieOperationalFixtures.PostAsync<MovieV2TakeDto>(client,
            $"/api/movie-studio/shots/{fixture.Shot.Id}/takes", new
            {
                label = "Selected final take",
                qualityLevel = MovieQualityLevels.Cinematic,
                autoDirectorEnabled = false,
                movieClipId = generated.ClipId,
                generationJobId = generated.Job.Id,
                assetId = videoAssetId,
                notes = "The only take permitted to cross the selected-take upgrade boundary.",
            });
        var approvedTake = await MovieOperationalFixtures.PostAsync<MovieV2TakeDto>(client,
            $"/api/movie-studio/takes/{selectedCandidate.Id}/approvals", new { decision = MovieApprovalDecisions.Approved, comment = "Approved for selection." });
        Assert.Equal(MovieTakeStatuses.Approved, approvedTake.Status);
        using (var select = await MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post, $"/api/movie-studio/takes/{selectedCandidate.Id}/select", null))
            Assert.Equal(HttpStatusCode.NoContent, select.StatusCode);
        using (var finalize = await MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post, $"/api/movie-studio/takes/{selectedCandidate.Id}/finalize", null))
            Assert.Equal(HttpStatusCode.NoContent, finalize.StatusCode);

        var unselectedTake = await MovieOperationalFixtures.PostAsync<MovieV2TakeDto>(client,
            $"/api/movie-studio/shots/{fixture.Shot.Id}/takes", new
            {
                label = "Unselected alternate take",
                qualityLevel = MovieQualityLevels.Standard,
                autoDirectorEnabled = false,
                notes = "This candidate must remain outside the upgrade boundary.",
            });
        var approvedAlternate = await MovieOperationalFixtures.PostAsync<MovieV2TakeDto>(client,
            $"/api/movie-studio/takes/{unselectedTake.Id}/approvals", new { decision = MovieApprovalDecisions.Approved, comment = "Alternate is reviewable but not selected." });

        using (var blocked = await MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post,
                   $"/api/movie-studio/takes/{approvedAlternate.Id}/upscale", new { targetMasterResolution = MovieUpscaleResolutionCatalog.P4K, sourceResolution = "1080p" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
            var body = await blocked.Content.ReadFromJsonAsync<MovieTakeUpscaleEligibilityDto>();
            Assert.NotNull(body);
            Assert.False(body!.Eligible);
            Assert.Equal(MovieTakeUpscaleEligibilityCodes.TakeNotSelected, body.Code);
        }

        using (var selectedUpgrade = await MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post,
                   $"/api/movie-studio/takes/{selectedCandidate.Id}/upscale", new { targetMasterResolution = MovieUpscaleResolutionCatalog.P4K, sourceResolution = "1080p" }))
        {
            Assert.Equal(HttpStatusCode.Accepted, selectedUpgrade.StatusCode);
            var body = await selectedUpgrade.Content.ReadFromJsonAsync<MovieTakeUpscaleEligibilityDto>();
            Assert.NotNull(body);
            Assert.True(body!.Eligible);
            Assert.Equal(MovieTakeUpscaleAuditStatuses.PendingExecution, body.AuditStatus);
        }

        var finalMaster = await MovieOperationalFixtures.PostAsync<MovieFinalMasterDto>(client,
            $"/api/movie-studio/takes/{selectedCandidate.Id}/final-mastering", new { targetProfile = MovieFinalMasteringProfiles.Uhd4K });
        Assert.Equal(selectedCandidate.Id, finalMaster.SourceTakeId);
        Assert.Equal(MovieFinalMasteringStates.Blocked, finalMaster.State);
        Assert.Null(finalMaster.OutputAssetId);
        Assert.Null(finalMaster.GenerationJobId);
        Assert.Contains(selectedCandidate.Id.ToString(), finalMaster.ProvenanceJson, StringComparison.OrdinalIgnoreCase);

        var voiceResponse = await MovieOperationalFixtures.PostAsync<CreateVoiceGenerationResponse>(client, "/api/voice-generation/jobs", new
        {
            workspaceId = fixture.Owner.PersonalWorkspace.Id,
            text = "The courier keeps moving through the rain.",
            language = "en",
            voiceStyle = "neutral",
            speakingStyle = "clear",
            title = "Wave 4 voice track",
        });
        var voiceJob = await WaitForTerminalAsync(client, voiceResponse.Job.Id);
        Assert.Equal("Succeeded", voiceJob.Status);
        var voiceAssetId = await FindAssetIdAsync(voiceJob.Id);

        var musicResponse = await MovieOperationalFixtures.PostAsync<CreateMusicGenerationResponse>(client, "/api/music-generation/jobs", new
        {
            workspaceId = fixture.Owner.PersonalWorkspace.Id,
            description = "A restrained instrumental bed for the harbor arrival.",
            purpose = "Wave 4 deterministic timeline acceptance",
            genre = "ambient",
            mood = "dramatic",
            durationSeconds = 30,
            vocalPreference = "instrumental",
            language = "auto",
            title = "Wave 4 music bed",
        });
        var musicJob = await WaitForTerminalAsync(client, musicResponse.Job.Id);
        Assert.Equal("Succeeded", musicJob.Status);
        var musicAssetId = await FindAssetIdAsync(musicJob.Id);

        var timeline = MovieWave4ExportSeam.BuildTimeline(
        [
            new(MovieWave4TrackKinds.Video, videoAssetId, 0, 5_000, selectedCandidate.Id),
            new(MovieWave4TrackKinds.Voice, voiceAssetId, 0, 1_800),
            new(MovieWave4TrackKinds.SoundEffect, DisabledSfxReference, 900, 300),
            new(MovieWave4TrackKinds.Music, musicAssetId, 0, 5_000),
            new(MovieWave4TrackKinds.Captions, null, 0, 1_800, CaptionText: "The courier keeps moving through the rain."),
        ], 5_000);
        var export = MovieWave4ExportSeam.Evaluate(new MovieWave4ExportRequest(
            fixture.Project.Project.Id,
            fixture.Shot.Id,
            selectedCandidate.Id,
            selectedCandidate.Id,
            approvedKeyframe.Id,
            timeline));
        Assert.True(export.Ready);
        Assert.Equal(MovieWave4ExportCodes.ReadyForHandoff, export.Code);
        Assert.False(await HasAssemblyAssetAsync(fixture.Project.Project.Id));

        var hierarchy = await MovieOperationalFixtures.GetAsync<MovieV2HierarchyDto>(client, $"/api/movie-studio/projects/{fixture.Project.Project.Id}/hierarchy");
        var reloadedShot = hierarchy.Acts.Single().Sequences.Single().Scenes.Single().Shots.Single(item => item.Id == fixture.Shot.Id);
        Assert.Equal(selectedCandidate.Id, reloadedShot.SelectedTakeId);
        Assert.Equal(selectedCandidate.Id, reloadedShot.FinalTakeId);
        Assert.Equal(2, reloadedShot.Takes.Count);
        Assert.Contains(reloadedShot.Takes, item => item.Id == selectedCandidate.Id && item.Status == MovieTakeStatuses.Selected);
        Assert.Contains(reloadedShot.Takes, item => item.Id == approvedAlternate.Id && item.Status == MovieTakeStatuses.Approved);

        var reloadedProduction = await MovieOperationalFixtures.GetAsync<MovieShotProductionDto>(client, $"/api/movie-studio/shots/{fixture.Shot.Id}/production");
        Assert.Contains(reloadedProduction.Versions, item => item.Id == approvedKeyframe.Id && item.Stage == MovieProductionStages.ApprovedKeyframe && item.StageProvenanceJson!.Contains("keyframe", StringComparison.Ordinal));
        var reloadedMaster = await MovieOperationalFixtures.GetAsync<MovieFinalMasterDto>(client, $"/api/movie-studio/shots/{fixture.Shot.Id}/final-mastering");
        Assert.Equal(finalMaster.Id, reloadedMaster.Id);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var audits = await db.MovieTakeUpscaleAudits.AsNoTracking().Where(item => item.MovieProjectId == fixture.Project.Project.Id).ToListAsync();
            Assert.Contains(audits, item => item.MovieTakeId == approvedAlternate.Id && item.Status == MovieTakeUpscaleAuditStatuses.Blocked);
            Assert.Contains(audits, item => item.MovieTakeId == selectedCandidate.Id && item.Status == MovieTakeUpscaleAuditStatuses.PendingExecution);
            Assert.Null(finalMaster.GenerationJobId);
            Assert.True(factory.Calls.Calls(FakeProviderKind.Movie) > 0);
            Assert.True(factory.Calls.Calls(FakeProviderKind.Voice) > 0);
            Assert.True(factory.Calls.Calls(FakeProviderKind.Music) > 0);
        }

        var summary = await client.GetFromJsonAsync<UsageSummaryDto>($"/api/workspaces/{fixture.Owner.PersonalWorkspace.Id}/usage/summary");
        Assert.NotNull(summary);
        Assert.Equal(0m, summary!.CustomerChargedAmount);
    }

    [Fact]
    public async Task Failed_audio_work_recovers_by_retry_without_charge_or_partial_asset()
    {
        using var client = factory.CreateClient();
        factory.Scenarios[FakeProviderKind.Voice] = FakeProviderScenario.PermanentFailure;
        var auth = await MovieOperationalFixtures.RegisterAsync(client, "Wave 4 recovery owner");
        var response = await MovieOperationalFixtures.PostAsync<CreateVoiceGenerationResponse>(client, "/api/voice-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            text = "This deterministic voice attempt fails before publication.",
            language = "en",
            voiceStyle = "neutral",
            speakingStyle = "clear",
            title = "Wave 4 failed track",
        });
        var failed = await WaitForTerminalAsync(client, response.Job.Id);
        Assert.Equal("Failed", failed.Status);
        Assert.Empty(failed.Outputs);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.GenerationJobId == failed.Id);
            Assert.Equal(UsageTransactionStatus.Failed, usage.Status);
            Assert.Equal(0m, usage.ChargedAmount);
            Assert.False(await db.Assets.AsNoTracking().AnyAsync(item => item.SourceGenerationJobId == failed.Id));
            Assert.False(await db.GenerationJobOutputs.AsNoTracking().AnyAsync(item => item.GenerationJobId == failed.Id));
        }

        factory.Scenarios[FakeProviderKind.Voice] = FakeProviderScenario.ImmediateSuccess;
        var retry = await MovieOperationalFixtures.PostAsync<GenerationJobDto>(client, $"/api/generation/jobs/{failed.Id}/retry", null, idempotencyKey: "wave4-recovery-retry");
        var recovered = await WaitForTerminalAsync(client, retry.Id);
        Assert.Equal("Succeeded", recovered.Status);
        Assert.Equal(failed.Id, recovered.RetryOfJobId);
        Assert.NotEmpty(recovered.Outputs);
        Assert.True(factory.Calls.Calls(FakeProviderKind.Voice) >= 2);
    }

    private async Task<Guid> FindAssetIdAsync(Guid jobId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Assets.AsNoTracking()
            .Where(item => item.SourceGenerationJobId == jobId)
            .Select(item => item.Id)
            .SingleAsync();
    }

    private async Task<bool> HasAssemblyAssetAsync(Guid movieProjectId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TaslimDbContext>().MovieAssemblies.AsNoTracking()
            .AnyAsync(item => item.MovieProjectId == movieProjectId && item.AssetId.HasValue);
    }

    private static async Task<MovieClipDto> WaitForClipAsync(HttpClient client, Guid projectId, Guid clipId)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            var project = await MovieOperationalFixtures.GetAsync<MovieStudioProjectDto>(client, $"/api/movie-studio/projects/{projectId}");
            var clip = project.Clips
                .Concat(project.Scenes.SelectMany(scene => scene.Clips.Concat(scene.Shots.SelectMany(shot => shot.Clips))))
                .SingleOrDefault(item => item.Id == clipId);
            if (clip?.AssetId is not null) return clip;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Movie clip {clipId} did not receive a published asset.");
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
