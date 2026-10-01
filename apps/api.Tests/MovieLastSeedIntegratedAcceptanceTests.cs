using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Movies.Wave3Integration;
using Taslim.Api.Music;
using Taslim.Api.Persistence;
using Taslim.Api.Tests.ProviderTesting;
using Taslim.Api.Voice;
using Xunit;

namespace Taslim.Api.Tests;

[Collection("MovieWave4Acceptance")]
public sealed class MovieLastSeedIntegratedAcceptanceTests : IClassFixture<MovieWave4ApiFactory>
{
    private static readonly Guid DisabledSfxReference = Guid.Parse("70000000-0000-0000-0000-000000000007");
    private readonly MovieWave4ApiFactory factory;

    public MovieLastSeedIntegratedAcceptanceTests(MovieWave4ApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Last_seed_single_fixture_runs_story_guide_wave2_wave3_wave4_to_safe_export_handoff()
    {
        using var client = factory.CreateClient();
        factory.Scenarios[FakeProviderKind.Movie] = FakeProviderScenario.ImmediateSuccess;
        factory.Scenarios[FakeProviderKind.Voice] = FakeProviderScenario.ImmediateSuccess;
        factory.Scenarios[FakeProviderKind.Music] = FakeProviderScenario.ImmediateSuccess;

        Assert.IsType<FakeMovieVideoProvider>(factory.Services.GetRequiredService<IMovieVideoProvider>());
        Assert.IsType<FakeVoiceGenerationProvider>(factory.Services.GetRequiredService<IVoiceGenerationProvider>());
        Assert.IsType<FakeMusicGenerationProvider>(factory.Services.GetRequiredService<IMusicGenerationProvider>());
        Assert.False(factory.Services.GetRequiredService<IMovieSoundtrackMediaService>().IsAvailable);

        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client,
            title: "The Last Seed",
            lastSeedStory: true,
            sceneDurationSeconds: 30);
        var projectId = fixture.Project.Project.Id;
        var sceneId = fixture.Scene.Id;
        var shotId = fixture.Shot.Id;

        // Story + Guide are the authoritative inputs; this is a Full Movie project,
        // not Quick Movie seeding and not a generated-output shortcut.
        var project = await MovieOperationalFixtures.GetAsync<MovieStudioProjectDto>(client, $"/api/movie-studio/projects/{projectId}");
        Assert.Equal("The Last Seed", project.Title);
        Assert.Equal(MovieProjectModes.Full, project.Mode);
        Assert.Single(project.Scenes, item => item.Id == sceneId);
        Assert.Empty(project.Clips);
        Assert.Empty(project.Assemblies);
        Assert.Empty(project.World.References);

        var story = await MovieOperationalFixtures.GetAsync<MovieStoryDto>(client, $"/api/movie-studio/projects/{projectId}/story");
        Assert.Equal(projectId, story.MovieProjectId);
        Assert.Equal(story.ApprovedRevisionId, story.CurrentRevisionId);
        var storyScene = Assert.Single(story.ApprovedRevision!.Scenes);
        Assert.Equal(sceneId, storyScene.MovieSceneId);
        Assert.Contains("last seed", $"{story.Premise} {story.Logline} {story.Synopsis}".ToLowerInvariant());

        var guide = await MovieOperationalFixtures.GetAsync<MovieDirectorContextDto>(client, $"/api/movie-studio/projects/{projectId}/director-context");
        Assert.Equal(projectId, guide.MovieProjectId);
        Assert.True(guide.IsAuthoritative);
        Assert.True(guide.RevisionNumber > 0);

        // Wave2 scene/shot planning and production contract are persisted on the same IDs.
        var shotPlan = await MovieOperationalFixtures.GetAsync<MovieSceneShotPlanDto>(client, $"/api/movie-studio/scenes/{sceneId}/shots");
        Assert.Equal(sceneId, shotPlan.SceneId);
        Assert.Contains(shotPlan.Shots, item => item.Id == shotId);
        Assert.True(shotPlan.ReadyShotCount >= 1);

        var plannedShot = await SendJsonAsync<MovieShotDto>(client, HttpMethod.Patch, $"/api/movie-studio/shots/{shotId}", new
        {
            description = "The young farmer closes a hand around the last seed as the first rain arrives.",
            purpose = "Protect the last seed and make the irreversible choice visible.",
            subjects = "Young farmer and the last seed",
            locationSet = "Dry Village / Old Tree Field",
            durationSeconds = 30,
            productionRequirements = "Readable seed, wet soil, and consistent hand continuity.",
            continuityReferences = "Locked Guide: the last seed remains in the left hand until planted.",
            cameraAndFraming = "Medium-wide, subject left with the old tree in the background",
            cameraMotion = "slow push",
            narration = "The future begins with one small thing.",
            dialogue = "We can still begin.",
            visualContinuityNotes = "Ochre dust resolves into first rain; seed remains visible.",
            cinematography = new
            {
                intent = "natural",
                shotSize = "medium_wide",
                focalLength = "35mm",
                lensIntent = "documentary",
                apertureDepthOfField = "deep focus",
                cameraAngle = "eye level",
                cameraMovement = "slow push",
                frameRateIntent = "24fps",
                lighting = "soft motivated",
                paletteLook = "ochre to rain green",
                compositionNotes = "Old tree anchors the left third; hand and seed remain readable.",
            },
            narrativeImportance = MovieShotNarrativeImportance.Critical,
            productionComplexity = new { level = MovieShotComplexityLevels.High, drivers = new[] { "continuity", "performance", "fine_detail" }, notes = "Protect the seed, hand, and rain transition." },
            qualityRequirements = new { minimumLevel = MovieQualityLevels.Cinematic, acceptanceCriteria = new[] { "Readable hand performance", "Stable seed identity", "Consistent rain transition" } },
            continuitySensitivity = MovieShotContinuitySensitivities.Locked,
            upscaleSuitability = MovieShotUpscaleSuitabilities.Preferred,
            targetOutputRequirements = new { aspectRatio = "16:9", resolutionIntent = "uhd", frameRateIntent = "24 fps", audioIntent = "dialogue and ambience" },
        });
        Assert.Equal(projectId, (await MovieOperationalFixtures.GetAsync<MovieStudioProjectDto>(client, $"/api/movie-studio/projects/{projectId}")).Id);
        Assert.Equal(shotId, plannedShot.Id);
        Assert.True(plannedShot.Readiness.Ready);
        Assert.Equal(MovieShotPlanStates.ReadyForStoryboard, plannedShot.PlanState);
        Assert.NotNull(plannedShot.ProductionContract);
        Assert.Equal(MovieShotNarrativeImportance.Critical, plannedShot.ProductionContract!.NarrativeImportance);
        Assert.Equal(MovieQualityLevels.Cinematic, plannedShot.ProductionContract.QualityRequirements!.MinimumLevel);
        Assert.DoesNotContain("\"provider\":", JsonSerializer.Serialize(plannedShot), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"model\":", JsonSerializer.Serialize(plannedShot), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"prompt\":", JsonSerializer.Serialize(plannedShot), StringComparison.OrdinalIgnoreCase);

        var complexity = await SendJsonAsync<MovieProductionComplexityAssessmentDto>(client, HttpMethod.Put, $"/api/movie-studio/shots/{shotId}/production-complexity", new
        {
            motionComplexity = 80, cameraComplexity = 80, faceImportance = 80, handBodyInteractionComplexity = 90,
            fineDetailImportance = 90, environmentComplexity = 85, vfxComplexity = 70, continuitySensitivity = 95,
            textSignageSensitivity = 70, dialogueLipSyncDependency = 85, durationComplexity = 70,
            declaredOverallBand = MovieProductionComplexityBands.High, source = MovieProductionComplexitySources.Manual,
            evidence = new[] { new { dimension = MovieProductionComplexityDimensions.ContinuitySensitivity, reason = "Locked seed and hand continuity.", evidence = "Last Seed guide continuity rule." } },
        });
        Assert.Equal(MovieProductionComplexityBands.High, complexity.Profile.OverallBand);
        Assert.Equal(MovieProductionComplexitySources.Manual, complexity.Source);

        var importance = await SendJsonAsync<MovieShotImportanceDto>(client, HttpMethod.Patch, $"/api/movie-studio/shots/{shotId}/importance", new { classification = MovieShotImportanceLevels.Hero });
        Assert.Equal(shotId, importance.ShotId);
        Assert.Equal(MovieShotImportanceLevels.Hero, importance.UserOverride);

        var qualityAndRuntime = await MovieOperationalFixtures.GetAsync<MovieShotDto>(client, $"/api/movie-studio/shots/{shotId}");
        Assert.Equal(projectId, qualityAndRuntime.AdaptiveResolutionDirectorInput!.MovieProjectId);
        Assert.Equal(sceneId, qualityAndRuntime.AdaptiveResolutionDirectorInput.MovieSceneId);
        Assert.Equal(shotId, qualityAndRuntime.AdaptiveResolutionDirectorInput.MovieShotId);
        Assert.NotEmpty(qualityAndRuntime.QualityRequirements!.Requirements);
        var durationBudget = await MovieOperationalFixtures.GetAsync<MovieDurationBudgetResult>(client, $"/api/movie-studio/projects/{projectId}/duration-budget");
        Assert.True(durationBudget.IsComplete, $"Duration budget incomplete: {JsonSerializer.Serialize(durationBudget)}");
        Assert.Equal(MovieDurationBudgetStatuses.Valid, durationBudget.Status);
        Assert.Equal(30, durationBudget.TargetDurationSeconds);

        var cinematography = await MovieOperationalFixtures.PostAsync<MovieCinematographyPlanResponse>(client, $"/api/movie-studio/shots/{shotId}/cinematography/plan", new
        {
            characterEmotionalPurpose = "Choose hope while protecting the last seed.",
            creativeNotes = "Keep the first rain legible without exposing generation implementation details.",
        });
        Assert.Equal(projectId, cinematography.MovieProjectId);
        Assert.Equal(sceneId, cinematography.SceneId);
        Assert.Equal(shotId, cinematography.ShotId);
        Assert.True(cinematography.GuideGrounded);
        Assert.True(cinematography.CanonPreserved);

        var characterContinuity = await MovieOperationalFixtures.GetAsync<JsonElement>(client, $"/api/movie-studio/projects/{projectId}/continuity/characters?sceneId={sceneId}&shotId={shotId}");
        Assert.Equal(projectId, characterContinuity.GetProperty("movieProjectId").GetGuid());
        var snapshot = await MovieOperationalFixtures.PostAsync<MovieCharacterContinuitySnapshotDto>(client, $"/api/movie-studio/projects/{projectId}/continuity/snapshots", new { movieSceneId = sceneId, movieShotId = shotId });
        Assert.Equal(projectId, snapshot.MovieProjectId);
        Assert.Equal(sceneId, snapshot.MovieSceneId);
        Assert.Equal(shotId, snapshot.MovieShotId);
        var worldContinuity = await MovieOperationalFixtures.GetAsync<MovieWorldContinuitySnapshotDto>(client, $"/api/movie-studio/shots/{shotId}/world-continuity");
        Assert.Equal(projectId, worldContinuity.MovieProjectId);
        Assert.Equal(sceneId, worldContinuity.SceneId);
        Assert.Equal(shotId, worldContinuity.ShotId);
        var continuityReview = await MovieOperationalFixtures.GetAsync<MovieProductionContinuityReviewDto>(client, $"/api/movie-studio/shots/{shotId}/continuity-review");
        Assert.Equal(projectId, continuityReview.MovieProjectId);
        Assert.Equal(shotId, continuityReview.ShotId);

        // Wave3 consumes the persisted provider-neutral quality profile and returns a
        // deterministic decision with no provider/model/prompt or cost commitment.
        var adaptive = MovieWave3AdaptiveResolutionCompatibility.Recommend(new MovieWave3AdaptiveResolutionRequest(
            MovieWave3ResolutionContract.P2160,
            MovieQualityLevels.Cinematic,
            Importance: 95,
            MotionComplexity: 80,
            CameraComplexity: 80,
            FaceImportance: 90,
            FineDetailImportance: 90,
            ContinuitySensitivity: 95,
            UpscaleSuitability: 95,
            EconomicalDraft: true));
        Assert.Equal(MovieWave3ResolutionContract.P720, adaptive.SourceResolution);
        Assert.False(adaptive.CostEstimate.IsKnown);
        var adaptiveProductContract = MovieWave3AdaptiveResolutionCompatibility.SerializeProductSafe(adaptive);
        Assert.DoesNotContain("provider", adaptiveProductContract, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model", adaptiveProductContract, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", adaptiveProductContract, StringComparison.OrdinalIgnoreCase);

        // Wave4 approved storyboard -> approved keyframe -> deterministic movie output.
        var storyboard = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(client, $"/api/movie-studio/shots/{shotId}/production/versions", new
        {
            stage = MovieProductionStages.StoryboardCandidate,
            label = "Last Seed storyboard candidate",
            compositionJson = "{\"blocking\":\"seed-left\",\"transition\":\"first-rain\",\"seed\":42}",
            stageProvenanceJson = "{\"source\":\"last-seed-integrated-acceptance\",\"projectLineage\":\"same-project\"}",
        });
        var approvedStoryboard = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(client, $"/api/movie-studio/production/versions/{storyboard.Id}/review", new { approve = true, reason = "Last Seed storyboard approved." });
        var keyframe = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(client, $"/api/movie-studio/shots/{shotId}/production/versions", new
        {
            stage = MovieProductionStages.ProductionKeyframe,
            sourceVersionId = approvedStoryboard.Id,
            compositionJson = "{\"frame\":\"approved-last-seed-keyframe\",\"seed\":42}",
            stageProvenanceJson = $"{{\"sourceVersionId\":\"{approvedStoryboard.Id}\",\"projectId\":\"{projectId}\",\"sceneId\":\"{sceneId}\",\"shotId\":\"{shotId}\"}}",
        });
        var approvedKeyframe = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(client, $"/api/movie-studio/production/versions/{keyframe.Id}/review", new { approve = true, reason = "Last Seed keyframe approved." });
        Assert.Equal(MovieProductionStages.ApprovedKeyframe, approvedKeyframe.Stage);
        Assert.Equal(storyboard.Id, keyframe.SourceVersionId);

        var generated = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(client, $"/api/movie-studio/shots/{shotId}/generate", new { title = "Last Seed deterministic take source" }, idempotencyKey: "last-seed-movie-source");
        var duplicateGenerated = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(client, $"/api/movie-studio/shots/{shotId}/generate", new { title = "Last Seed deterministic take source" }, idempotencyKey: "last-seed-movie-source");
        Assert.Equal(generated.Job.Id, duplicateGenerated.Job.Id);
        Assert.Equal(generated.ClipId, duplicateGenerated.ClipId);
        var generatedJob = await WaitForTerminalAsync(client, generated.Job.Id);
        Assert.Equal("Succeeded", generatedJob.Status);
        var generatedClip = await WaitForClipAsync(client, projectId, generated.ClipId);
        Assert.NotNull(generatedClip.AssetId);

        var selectedTake = await MovieOperationalFixtures.PostAsync<MovieV2TakeDto>(client, $"/api/movie-studio/shots/{shotId}/takes", new
        {
            label = "Last Seed selected take",
            qualityLevel = MovieQualityLevels.Cinematic,
            autoDirectorEnabled = false,
            movieClipId = generated.ClipId,
            generationJobId = generated.Job.Id,
            assetId = generatedClip.AssetId,
            notes = "Only this explicitly selected take may cross downstream mastering boundaries.",
        });
        var alternateTake = await MovieOperationalFixtures.PostAsync<MovieV2TakeDto>(client, $"/api/movie-studio/shots/{shotId}/takes", new
        {
            label = "Last Seed rejected alternate",
            qualityLevel = MovieQualityLevels.Standard,
            autoDirectorEnabled = false,
            movieClipId = generated.ClipId,
            generationJobId = generated.Job.Id,
            assetId = generatedClip.AssetId,
            notes = "Approved for review only; never selected for downstream upgrade.",
        });
        var approvedSelected = await MovieOperationalFixtures.PostAsync<MovieV2TakeDto>(client, $"/api/movie-studio/takes/{selectedTake.Id}/approvals", new { decision = MovieApprovalDecisions.Approved, comment = "Selected take approved." });
        var approvedAlternate = await MovieOperationalFixtures.PostAsync<MovieV2TakeDto>(client, $"/api/movie-studio/takes/{alternateTake.Id}/approvals", new { decision = MovieApprovalDecisions.Approved, comment = "Alternate rejected for final selection." });
        Assert.Equal(MovieTakeStatuses.Approved, approvedSelected.Status);
        Assert.Equal(MovieTakeStatuses.Approved, approvedAlternate.Status);
        using (var select = await MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post, $"/api/movie-studio/takes/{selectedTake.Id}/select", null))
            Assert.Equal(HttpStatusCode.NoContent, select.StatusCode);
        using (var finalize = await MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post, $"/api/movie-studio/takes/{selectedTake.Id}/finalize", null))
            Assert.Equal(HttpStatusCode.NoContent, finalize.StatusCode);

        using (var blockedAlternate = await MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post, $"/api/movie-studio/takes/{alternateTake.Id}/upscale", new { targetMasterResolution = MovieUpscaleResolutionCatalog.P4K, sourceResolution = "1080p" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, blockedAlternate.StatusCode);
            var body = await blockedAlternate.Content.ReadFromJsonAsync<MovieTakeUpscaleEligibilityDto>();
            Assert.NotNull(body);
            Assert.False(body!.Eligible);
            Assert.Equal(MovieTakeUpscaleEligibilityCodes.TakeNotSelected, body.Code);
        }
        using (var selectedUpgrade = await MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post, $"/api/movie-studio/takes/{selectedTake.Id}/upscale", new { targetMasterResolution = MovieUpscaleResolutionCatalog.P4K, sourceResolution = "1080p" }))
        {
            Assert.Equal(HttpStatusCode.Accepted, selectedUpgrade.StatusCode);
            var body = await selectedUpgrade.Content.ReadFromJsonAsync<MovieTakeUpscaleEligibilityDto>();
            Assert.NotNull(body);
            Assert.True(body!.Eligible);
            Assert.Equal(MovieTakeUpscaleAuditStatuses.PendingExecution, body.AuditStatus);
        }
        var finalMaster = await MovieOperationalFixtures.PostAsync<MovieFinalMasterDto>(client, $"/api/movie-studio/takes/{selectedTake.Id}/final-mastering", new { targetProfile = MovieFinalMasteringProfiles.Uhd4K });
        Assert.Equal(selectedTake.Id, finalMaster.SourceTakeId);
        Assert.Equal(MovieFinalMasteringStates.Blocked, finalMaster.State);
        Assert.Null(finalMaster.GenerationJobId);
        Assert.Null(finalMaster.OutputAssetId);

        // Voice/dialogue and music are generated by deterministic fake adapters and
        // are then linked to the same project soundtrack/timeline.
        var voiceResponse = await MovieOperationalFixtures.PostAsync<CreateVoiceGenerationResponse>(client, "/api/voice-generation/jobs", new
        {
            workspaceId = fixture.Owner.PersonalWorkspace.Id,
            text = "We can still begin.",
            language = "en",
            voiceStyle = "neutral",
            speakingStyle = "clear",
            title = "Last Seed dialogue",
        });
        var voiceJob = await WaitForTerminalAsync(client, voiceResponse.Job.Id);
        Assert.Equal("Succeeded", voiceJob.Status);
        var voiceAssetId = await FindAssetIdAsync(voiceJob.Id);

        var musicResponse = await MovieOperationalFixtures.PostAsync<CreateMusicGenerationResponse>(client, "/api/music-generation/jobs", new
        {
            workspaceId = fixture.Owner.PersonalWorkspace.Id,
            description = "A restrained hopeful instrumental bed for the first rain.",
            purpose = "Last Seed deterministic ambience and music",
            genre = "ambient",
            mood = "inspiring",
            durationSeconds = 30,
            vocalPreference = "instrumental",
            language = "auto",
            title = "Last Seed music",
        });
        var musicJob = await WaitForTerminalAsync(client, musicResponse.Job.Id);
        Assert.Equal("Succeeded", musicJob.Status);
        var musicAssetId = await FindAssetIdAsync(musicJob.Id);

        var soundtrackCue = await MovieOperationalFixtures.PostAsync<MovieSoundtrackCueDto>(client, $"/api/movie-studio/projects/{projectId}/soundtrack/cues", new
        {
            movieActId = fixture.Act.Id,
            movieSceneId = sceneId,
            title = "First rain music",
            narrativeIntent = "Hope arrives without erasing the drought.",
            mood = "inspiring",
            intensity = 35,
            actStartSeconds = 0,
            sceneStartSeconds = 0,
            timelineStartSeconds = 0,
            durationSeconds = 30,
            duckingIntents = new[] { new { targetLane = MovieSoundtrackDuckingTargets.Dialogue, startOffsetSeconds = 0, endOffsetSeconds = 3, duckDecibels = 6, attackMilliseconds = 50, releaseMilliseconds = 250 } },
        });
        var soundtrackVersion = await MovieOperationalFixtures.PostAsync<MovieSoundtrackCueDto>(client, $"/api/movie-studio/soundtrack/cues/{soundtrackCue.Id}/versions", new { label = "Approved first-rain bed", arrangementIntent = "Sparse strings and field texture.", assetId = musicAssetId });
        var approvedSoundtrack = await MovieOperationalFixtures.PostAsync<MovieSoundtrackCueDto>(client, $"/api/movie-studio/soundtrack/versions/{soundtrackVersion.Versions.Single().Id}/review", new { decision = MovieSoundtrackApprovalStates.Approved, comment = "Music is approved for the Last Seed timeline." });
        Assert.Equal(MovieSoundtrackApprovalStates.Approved, approvedSoundtrack.ApprovalState);
        Assert.Equal(musicAssetId, approvedSoundtrack.Versions.Single(item => item.Id == soundtrackVersion.Versions.Single().Id).AssetId);

        var captionTrack = await MovieOperationalFixtures.PostAsync<MovieCaptionTrackDto>(client, $"/api/movie-studio/projects/{projectId}/caption-tracks", new { name = "Last Seed English", trackType = MovieCaptionTrackTypes.Subtitle, language = "en", isDefault = true, status = MovieCaptionTrackStatuses.Ready });
        var captionCue = await MovieOperationalFixtures.PostAsync<MovieCaptionCueDto>(client, $"/api/movie-studio/caption-tracks/{captionTrack.Id}/cues", new
        {
            sequence = 1,
            startTimecode = "00:00:00.000",
            endTimecode = "00:00:01.200",
            text = "We can still begin.",
            speakerName = "YOUNG FARMER",
            movieSceneId = sceneId,
            movieShotId = shotId,
            movieTakeId = selectedTake.Id,
        });
        Assert.Equal(selectedTake.Id, captionCue.MovieTakeId);
        using (var captionExport = await client.GetAsync($"/api/movie-studio/caption-tracks/{captionTrack.Id}/export?format=srt"))
        {
            Assert.Equal(HttpStatusCode.OK, captionExport.StatusCode);
            Assert.Contains("We can still begin.", await captionExport.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // Persist the canonical timeline on the same project, then build the
        // provider-neutral Wave4 transition/export handoff from the same lineage.
        var persistedTimeline = await MovieOperationalFixtures.PostAsync<MovieTimelineRevisionDto>(client, $"/api/movie-studio/projects/{projectId}/timeline/revisions", new
        {
            label = "Last Seed canonical timeline",
            changeSummary = "Selected take, dialogue, ambience, music, and caption handoff.",
            tracks = new object[]
            {
                new { kind = MovieTimelineTrackKinds.Video, name = "Selected picture", trackNumber = 1, items = new[] { new { kind = MovieTimelineItemKinds.VisualTake, sourceTakeId = selectedTake.Id, timelineInMilliseconds = 0, sourceInMilliseconds = 0, label = "Selected take" } } },
                new { kind = MovieTimelineTrackKinds.Audio, name = "Dialogue", trackNumber = 2, items = new[] { new { kind = MovieTimelineItemKinds.AudioAsset, sourceAssetId = voiceAssetId, timelineInMilliseconds = 0, sourceInMilliseconds = 0, label = "Dialogue" } } },
                new { kind = MovieTimelineTrackKinds.Audio, name = "Music and ambience", trackNumber = 3, items = new[] { new { kind = MovieTimelineItemKinds.AudioAsset, sourceAssetId = musicAssetId, timelineInMilliseconds = 0, sourceInMilliseconds = 0, label = "Music / ambience" } } },
            },
        });
        Assert.Equal(projectId, persistedTimeline.MovieProjectId);
        Assert.Equal(MovieTimelineRevisionStatuses.Draft, persistedTimeline.Status);
        Assert.Contains(persistedTimeline.Tracks, track => track.Kind == MovieTimelineTrackKinds.Video && track.Items.Single().SourceTakeId == selectedTake.Id);
        var reloadedTimeline = await MovieOperationalFixtures.GetAsync<MovieTimelineDto>(client, $"/api/movie-studio/projects/{projectId}/timeline");
        Assert.Equal(persistedTimeline.Id, reloadedTimeline.CurrentRevisionId);
        Assert.Contains(reloadedTimeline.CurrentRevision!.Tracks, track => track.Name == "Dialogue");

        var canonicalTimeline = MovieWave4ExportSeam.BuildTimeline(
        [
            new(MovieWave4TrackKinds.Video, generatedClip.AssetId, 0, 5_000, selectedTake.Id),
            new(MovieWave4TrackKinds.Voice, voiceAssetId, 0, 1_200),
            new(MovieWave4TrackKinds.SoundEffect, DisabledSfxReference, 1_000, 300),
            new(MovieWave4TrackKinds.Music, musicAssetId, 0, 5_000),
            new(MovieWave4TrackKinds.Captions, null, 0, 1_200, CaptionText: "We can still begin."),
        ], 5_000);
        Assert.Equal(new[] { MovieWave4TrackKinds.Captions, MovieWave4TrackKinds.Music, MovieWave4TrackKinds.Video, MovieWave4TrackKinds.Voice, MovieWave4TrackKinds.SoundEffect }, canonicalTimeline.Items.Select(item => item.TrackKind));
        var blockedHandoff = MovieWave4ExportSeam.Evaluate(new MovieWave4ExportRequest(projectId, shotId, selectedTake.Id, alternateTake.Id, approvedKeyframe.Id, canonicalTimeline));
        Assert.False(blockedHandoff.Ready);
        Assert.Equal(MovieWave4ExportCodes.FinalTakeRequired, blockedHandoff.Code);
        var jobsBeforeHandoff = await CountGenerationJobsAsync(projectId);
        var assetsBeforeHandoff = await CountProjectAssetsAsync(projectId);
        var handoff = MovieWave4ExportSeam.Evaluate(new MovieWave4ExportRequest(projectId, shotId, selectedTake.Id, selectedTake.Id, approvedKeyframe.Id, canonicalTimeline));
        Assert.True(handoff.Ready);
        Assert.Equal(MovieWave4ExportCodes.ReadyForHandoff, handoff.Code);
        Assert.False(string.IsNullOrWhiteSpace(handoff.HandoffProvenanceHash));
        Assert.Equal(jobsBeforeHandoff, await CountGenerationJobsAsync(projectId));
        Assert.Equal(assetsBeforeHandoff, await CountProjectAssetsAsync(projectId));

        // A failed no-asset path in the same project is free, and retry is
        // idempotent while retaining the original job lineage.
        factory.Scenarios[FakeProviderKind.Voice] = FakeProviderScenario.PermanentFailure;
        var failedVoiceResponse = await MovieOperationalFixtures.PostAsync<CreateVoiceGenerationResponse>(client, "/api/voice-generation/jobs", new
        {
            workspaceId = fixture.Owner.PersonalWorkspace.Id,
            text = "This deterministic attempt must fail before publication.",
            language = "en",
            voiceStyle = "neutral",
            speakingStyle = "clear",
            title = "Last Seed failure path",
        });
        var failedVoice = await WaitForTerminalAsync(client, failedVoiceResponse.Job.Id);
        Assert.Equal("Failed", failedVoice.Status);
        Assert.Empty(failedVoice.Outputs);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.GenerationJobId == failedVoice.Id);
            Assert.Equal(UsageTransactionStatus.Failed, usage.Status);
            Assert.Equal(0m, usage.ChargedAmount);
            Assert.False(await db.Assets.AsNoTracking().AnyAsync(item => item.SourceGenerationJobId == failedVoice.Id));
        }
        factory.Scenarios[FakeProviderKind.Voice] = FakeProviderScenario.ImmediateSuccess;
        var retryOne = await MovieOperationalFixtures.PostAsync<GenerationJobDto>(client, $"/api/generation/jobs/{failedVoice.Id}/retry", null, idempotencyKey: "last-seed-failure-retry");
        var retryTwo = await MovieOperationalFixtures.PostAsync<GenerationJobDto>(client, $"/api/generation/jobs/{failedVoice.Id}/retry", null, idempotencyKey: "last-seed-failure-retry");
        Assert.Equal(retryOne.Id, retryTwo.Id);
        var recoveredVoice = await WaitForTerminalAsync(client, retryOne.Id);
        Assert.Equal("Succeeded", recoveredVoice.Status);
        Assert.Equal(failedVoice.Id, recoveredVoice.RetryOfJobId);
        Assert.NotEmpty(recoveredVoice.Outputs);

        var usageSummary = await client.GetFromJsonAsync<UsageSummaryDto>($"/api/workspaces/{fixture.Owner.PersonalWorkspace.Id}/usage/summary");
        Assert.NotNull(usageSummary);
        Assert.Equal(0m, usageSummary!.CustomerChargedAmount);
        var userFacingContracts = string.Join("\n", JsonSerializer.Serialize(project), JsonSerializer.Serialize(story), JsonSerializer.Serialize(guide), JsonSerializer.Serialize(plannedShot), JsonSerializer.Serialize(canonicalTimeline), JsonSerializer.Serialize(handoff));
        Assert.DoesNotContain("\"provider\":", userFacingContracts, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"model\":", userFacingContracts, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"prompt\":", userFacingContracts, StringComparison.OrdinalIgnoreCase);
        Assert.True(factory.Calls.Calls(FakeProviderKind.Movie) > 0);
        Assert.True(factory.Calls.Calls(FakeProviderKind.Voice) > 0);
        Assert.True(factory.Calls.Calls(FakeProviderKind.Music) > 0);
    }

    private static async Task<T> SendJsonAsync<T>(HttpClient client, HttpMethod method, string path, object payload)
    {
        using var response = await MovieOperationalFixtures.SendWithCsrfAsync(client, method, path, payload);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{method} {path} failed with {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private async Task<Guid> FindAssetIdAsync(Guid jobId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Assets.AsNoTracking()
            .Where(item => item.SourceGenerationJobId == jobId)
            .Select(item => item.Id)
            .SingleAsync();
    }

    private async Task<int> CountGenerationJobsAsync(Guid projectId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TaslimDbContext>().GenerationJobs.AsNoTracking()
            .CountAsync(item => item.InputJson.Contains(projectId.ToString()));
    }

    private async Task<int> CountProjectAssetsAsync(Guid projectId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Assets.AsNoTracking()
            .CountAsync(item => item.ProjectId == projectId);
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
