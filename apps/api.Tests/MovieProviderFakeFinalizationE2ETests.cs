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
using Taslim.Api.Music;
using Taslim.Api.Persistence;
using Taslim.Api.Tests.ProviderTesting;
using Taslim.Api.Voice;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// A single provider-fake acceptance host for the Movie Studio delivery chain.
/// It keeps all production HTTP, authorization, persistence, job execution,
/// publication, QC, and download paths intact while replacing every media
/// provider with an in-process deterministic fake.
/// </summary>
public sealed class MovieProviderFakeFinalizationApiFactory : MovieOperationalApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("MusicGeneration:Enabled", "true");
        builder.UseSetting("MusicGeneration:ProviderKey", "fake-music");
        builder.UseSetting("VoiceGeneration:Enabled", "true");
        builder.UseSetting("VoiceGeneration:ProviderKey", "fake-voice");
        builder.UseSetting("MovieDialogueVoice:Enabled", "true");
        builder.UseSetting("MovieDialogueVoice:ProviderKey", "fake");
        builder.UseSetting("MovieFinalAssembly:Enabled", "true");
        builder.UseSetting("MovieFinalAssembly:FfmpegPath", "unused-deterministic-executor");
        builder.UseSetting("GenerationJobs:PollIntervalMilliseconds", "250");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMusicGenerationProvider>();
            services.AddSingleton<IMusicGenerationProvider>(sp => new FakeMusicGenerationProvider(
                sp.GetRequiredService<FakeProviderScenarioCatalog>(),
                sp.GetRequiredService<FakeProviderCallLog>()));
            services.RemoveAll<IVoiceGenerationProvider>();
            services.AddSingleton<IVoiceGenerationProvider>(sp => new FakeVoiceGenerationProvider(
                sp.GetRequiredService<FakeProviderScenarioCatalog>(),
                sp.GetRequiredService<FakeProviderCallLog>()));
            services.RemoveAll<IMovieFinalAssemblyExecutor>();
            services.AddSingleton<IMovieFinalAssemblyExecutor, DeterministicMovieFinalAssemblyExecutor>();
        });
    }
}

[CollectionDefinition("MovieProviderFakeFinalization", DisableParallelization = true)]
public sealed class MovieProviderFakeFinalizationCollectionDefinition { }

[Collection("MovieProviderFakeFinalization")]
public sealed class MovieProviderFakeFinalizationE2ETests : IClassFixture<MovieProviderFakeFinalizationApiFactory>
{
    private readonly MovieProviderFakeFinalizationApiFactory factory;

    public MovieProviderFakeFinalizationE2ETests(MovieProviderFakeFinalizationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Fake_provider_journey_projects_script_audio_selects_transitions_soundtrack_and_qc_download()
    {
        using var client = factory.CreateClient();
        factory.Scenarios[FakeProviderKind.Movie] = FakeProviderScenario.ImmediateSuccess;
        factory.Scenarios[FakeProviderKind.Voice] = FakeProviderScenario.ImmediateSuccess;
        factory.Scenarios[FakeProviderKind.Music] = FakeProviderScenario.ImmediateSuccess;

        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client,
            title: "Movie fake provider finalization",
            sceneDurationSeconds: null);
        var projectId = fixture.Project.Project.Id;

        // Script -> scene -> shot: the fixture creates the human-authored story
        // revision and first scene/shot. Add a second scene/shot so the edit path
        // has a real adjacent boundary for a dissolve transition.
        var secondScene = await MovieOperationalFixtures.PostAsync<MovieV2SceneDto>(
            client,
            $"/api/movie-studio/sequences/{fixture.Sequence.Id}/scenes",
            new { title = "Warehouse Exit", summary = "The courier leaves before the storm breaks." });
        var secondShot = await MovieOperationalFixtures.PostAsync<MovieShotDto>(
            client,
            $"/api/movie-studio/scenes/{secondScene.Id}/shots",
            new
            {
                description = "A handheld follow as the courier crosses the loading bay.",
                purpose = "Carry the decision into the exit.",
                subjects = "Mara carrying the brass compass.",
                locationSet = "Harbor Warehouse loading bay.",
                productionRequirements = "Wet practical surface and readable compass face.",
                durationSeconds = 5,
                cameraAndFraming = "35mm medium follow",
                cameraMotion = "handheld follow",
                visualContinuityNotes = "Keep the red practical and compass continuity consistent.",
                cinematography = new
                {
                    intent = "natural",
                    shotSize = "medium",
                    focalLength = "35mm",
                    lensIntent = "observational",
                    apertureDepthOfField = "shallow focus",
                    cameraAngle = "eye level",
                    cameraMovement = "handheld follow",
                    frameRateIntent = "24fps",
                    lighting = "warm practicals",
                    paletteLook = "teal and amber",
                    compositionNotes = "Keep Mara centered as the loading bay recedes.",
                },
            });

        var firstVideo = await CreateFinalizedVideoTakeAsync(
            client, projectId, fixture.Shot.Id, "first-shot", createApprovedSelect: true);
        var secondVideo = await CreateFinalizedVideoTakeAsync(
            client, projectId, secondShot.Id, "second-shot", createApprovedSelect: false);

        Assert.NotNull(firstVideo.Select);
        Assert.Equal(MovieTakeSelectStatuses.Approved, firstVideo.Select!.Status);
        Assert.Equal(0, firstVideo.Select.StartMilliseconds);
        Assert.Equal(1_000, firstVideo.Select.EndMilliseconds);

        // Script dialogue -> fake voice job -> private audio asset -> metadata ->
        // approved and selected dialogue take.
        var dialogueLine = await MovieOperationalFixtures.PostAsync<MovieDialogueLineDto>(
            client,
            $"/api/movie-studio/clips/{firstVideo.ClipId}/dialogue",
            new
            {
                movieCharacterId = fixture.Character.Id,
                speakerName = "Mara",
                language = MovieDialogueLanguages.English,
                text = "We have one hour.",
                startMilliseconds = 0,
                endMilliseconds = 1_200,
                deliveryNotes = "Quietly, then increasingly urgent.",
            });
        var dialogueQueued = await MovieOperationalFixtures.PostAsync<MovieDialogueTakeResponse>(
            client,
            $"/api/movie-studio/dialogue/{dialogueLine.Id}/takes",
            new { label = "Fake voice production take" },
            idempotencyKey: "movie-fake-dialogue-1");
        var dialogueJob = await WaitForJobAsync(client, dialogueQueued.Job.Id);
        Assert.Equal(GenerationJobStatus.Succeeded.ToString(), dialogueJob.Status);
        Assert.Single(dialogueJob.Outputs);

        var dialogueClip = await MovieOperationalFixtures.GetAsync<MovieDialogueClipDto>(
            client,
            $"/api/movie-studio/clips/{firstVideo.ClipId}/dialogue");
        var dialogueTake = dialogueClip.Lines.Single(item => item.Id == dialogueLine.Id).Takes.Single(item => item.Id == dialogueQueued.Take.Id);
        Assert.Equal(MovieDialogueTakeStatuses.Succeeded, dialogueTake.Status);
        Assert.Equal(dialogueJob.Id, dialogueTake.GenerationJobId);
        Assert.NotNull(dialogueTake.AssetId);
        Assert.NotNull(dialogueTake.StoredFileId);
        Assert.Equal(1_200, dialogueTake.DurationMilliseconds);
        using (var metadata = JsonDocument.Parse(dialogueTake.MetadataJson!))
        {
            Assert.Equal(dialogueLine.Id.ToString(), metadata.RootElement.GetProperty("dialogueLineId").GetString());
            Assert.Equal(firstVideo.ClipId.ToString(), metadata.RootElement.GetProperty("movieClipId").GetString());
            Assert.Equal("en", metadata.RootElement.GetProperty("language").GetString());
            Assert.Equal(1_200, metadata.RootElement.GetProperty("durationMilliseconds").GetInt32());
        }
        var approvedDialogue = await MovieOperationalFixtures.PostAsync<MovieDialogueLineDto>(
            client,
            $"/api/movie-studio/dialogue/takes/{dialogueTake.Id}/approval",
            new { decision = MovieDialogueApprovalDecisions.Approved, comment = "Dialogue is clear and timed." });
        var selectedDialogue = await MovieOperationalFixtures.PostAsync<MovieDialogueLineDto>(
            client,
            $"/api/movie-studio/dialogue/takes/{dialogueTake.Id}/select",
            null);
        var selectedDialogueTake = selectedDialogue.Takes.Single(item => item.Id == dialogueTake.Id);
        Assert.Equal(MovieDialogueTakeStatuses.Selected, selectedDialogueTake.Status);
        Assert.Equal(dialogueTake.AssetId, selectedDialogueTake.AssetId);
        Assert.Equal(MovieDialogueLineStatuses.Approved, approvedDialogue.Status);

        // Fake music job -> private music asset -> soundtrack cue/version approval.
        var musicQueued = await MovieOperationalFixtures.PostAsync<CreateMusicGenerationResponse>(
            client,
            "/api/music-generation/jobs",
            new
            {
                workspaceId = fixture.Owner.PersonalWorkspace.Id,
                description = "A restrained instrumental bed under the warehouse exit.",
                purpose = "Movie fake provider soundtrack projection",
                genre = "ambient",
                mood = "dramatic",
                durationSeconds = 15,
                vocalPreference = MusicGenerationValues.Instrumental,
                language = MusicGenerationValues.Auto,
                title = "Fake approved soundtrack bed",
            });
        var musicJob = await WaitForJobAsync(client, musicQueued.Job.Id);
        Assert.Equal(GenerationJobStatus.Succeeded.ToString(), musicJob.Status);
        var musicAssetId = await FindAssetIdAsync(musicJob.Id);

        var cue = await MovieOperationalFixtures.PostAsync<MovieSoundtrackCueDto>(
            client,
            $"/api/movie-studio/projects/{projectId}/soundtrack/cues",
            new
            {
                movieActId = fixture.Act.Id,
                movieSceneId = fixture.Scene.Id,
                title = "Storm decision bed",
                narrativeIntent = "Let the score duck under Mara's line.",
                mood = "dramatic",
                intensity = 55,
                actStartSeconds = 0,
                sceneStartSeconds = 0,
                timelineStartSeconds = 0,
                durationSeconds = 5,
                duckingIntents = new[]
                {
                    new
                    {
                        targetLane = MovieSoundtrackDuckingTargets.Dialogue,
                        startOffsetSeconds = 0,
                        endOffsetSeconds = 1.2m,
                        duckDecibels = 6,
                        attackMilliseconds = 50,
                        releaseMilliseconds = 250,
                        rationale = "Protect the selected dialogue take.",
                    },
                },
            });
        var versionedCue = await MovieOperationalFixtures.PostAsync<MovieSoundtrackCueDto>(
            client,
            $"/api/movie-studio/soundtrack/cues/{cue.Id}/versions",
            new
            {
                label = "Approved storm decision bed",
                arrangementIntent = "Sparse strings and distant field texture.",
                assetId = musicAssetId,
            });
        var soundtrackVersion = versionedCue.Versions.Single(item => item.AssetId == musicAssetId);
        var approvedCue = await MovieOperationalFixtures.PostAsync<MovieSoundtrackCueDto>(
            client,
            $"/api/movie-studio/soundtrack/versions/{soundtrackVersion.Id}/review",
            new { decision = MovieSoundtrackApprovalStates.Approved, comment = "Score is approved and dialogue ducking is intentional." });
        Assert.Equal(MovieSoundtrackApprovalStates.Approved, approvedCue.ApprovalState);
        Assert.Equal(soundtrackVersion.Id, approvedCue.ApprovedVersionId);
        Assert.Equal(MovieSoundtrackApprovalStates.Approved, approvedCue.Versions.Single(item => item.Id == soundtrackVersion.Id).ApprovalState);
        Assert.Contains(approvedCue.DuckingIntents, item => item.TargetLane == MovieSoundtrackDuckingTargets.Dialogue);

        // Approved selects and audio assets are projected into the editable timeline.
        var initialTimeline = new
        {
            timelineId = Guid.NewGuid(),
            version = 1,
            clips = new[]
            {
                new { clipId = firstVideo.ClipId, movieShotId = fixture.Shot.Id, sequence = 1, startSeconds = 0m, durationSeconds = 1m },
                new { clipId = secondVideo.ClipId, movieShotId = secondShot.Id, sequence = 2, startSeconds = 1m, durationSeconds = 1m },
            },
            transitions = Array.Empty<object>(),
            contractVersion = MovieTimelineContract.Version,
        };
        var timelineRevision = await MovieOperationalFixtures.PostAsync<MovieTimelineRevisionDto>(
            client,
            $"/api/movie-studio/projects/{projectId}/timeline/revisions",
            new
            {
                label = "Selected takes with dialogue and soundtrack",
                changeSummary = "Project the approved select, selected dialogue take, and approved score.",
                canonicalTimelineJson = JsonSerializer.Serialize(initialTimeline),
                tracks = new object[]
                {
                    new
                    {
                        kind = MovieTimelineTrackKinds.Video,
                        name = "Selected picture",
                        trackNumber = 1,
                        items = new object[]
                        {
                            new
                            {
                                kind = MovieTimelineItemKinds.VisualTake,
                                sourceTakeId = firstVideo.Take.Id,
                                sourceSelectId = firstVideo.Select!.Id,
                                timelineInMilliseconds = 0,
                                sourceInMilliseconds = firstVideo.Select.StartMilliseconds,
                                sourceOutMilliseconds = firstVideo.Select.EndMilliseconds,
                                label = "Approved opening select",
                            },
                            new
                            {
                                kind = MovieTimelineItemKinds.VisualTake,
                                sourceTakeId = secondVideo.Take.Id,
                                timelineInMilliseconds = 1_000,
                                sourceInMilliseconds = 0,
                                sourceOutMilliseconds = 1_000,
                                label = "Approved exit take",
                            },
                        },
                    },
                    new
                    {
                        kind = MovieTimelineTrackKinds.Audio,
                        name = "Dialogue",
                        trackNumber = 2,
                        items = new[]
                        {
                            new
                            {
                                kind = MovieTimelineItemKinds.AudioAsset,
                                sourceAssetId = selectedDialogueTake.AssetId,
                                timelineInMilliseconds = 0,
                                timelineOutMilliseconds = 1_200,
                                label = "Selected Mara dialogue",
                            },
                        },
                    },
                    new
                    {
                        kind = MovieTimelineTrackKinds.Audio,
                        name = "Music",
                        trackNumber = 3,
                        items = new[]
                        {
                            new
                            {
                                kind = MovieTimelineItemKinds.AudioAsset,
                                sourceAssetId = musicAssetId,
                                timelineInMilliseconds = 0,
                                timelineOutMilliseconds = 5_000,
                                sourceInMilliseconds = 0,
                                sourceOutMilliseconds = 5_000,
                                label = "Approved storm score",
                            },
                        },
                    },
                },
            });
        Assert.Equal(MovieTimelineRevisionStatuses.Draft, timelineRevision.Status);
        Assert.Contains(timelineRevision.Tracks, track => track.Name == "Dialogue" && track.Items.Any(item => item.SourceAssetId == selectedDialogueTake.AssetId));
        Assert.Contains(timelineRevision.Tracks, track => track.Kind == MovieTimelineTrackKinds.Video && track.Items.Any(item => item.SourceSelectId == firstVideo.Select.Id));

        // Canonical transition edit: only an explicit user override can mutate
        // the transition timeline consumed by final assembly.
        var persistedTimeline = await MovieOperationalFixtures.GetAsync<MovieTimelineDto>(
            client,
            $"/api/movie-studio/projects/{projectId}/timeline");
        Assert.Equal(timelineRevision.Id, persistedTimeline.CurrentRevisionId);
        var timelineId = persistedTimeline.Id;
        var transitionId = Guid.NewGuid();
        var transition = new
        {
            id = transitionId,
            type = MovieTimelineTransitionTypes.Dissolve,
            fromClipId = firstVideo.ClipId,
            toClipId = secondVideo.ClipId,
            startSeconds = 0m,
            durationSeconds = 1m,
        };
        var canonicalForEdit = new
        {
            timelineId,
            version = 1,
            clips = new[]
            {
                new { clipId = firstVideo.ClipId, movieShotId = fixture.Shot.Id, sequence = 1, startSeconds = 0m, durationSeconds = 1m },
                new { clipId = secondVideo.ClipId, movieShotId = secondShot.Id, sequence = 2, startSeconds = 0m, durationSeconds = 1m },
            },
            transitions = Array.Empty<object>(),
            contractVersion = MovieTimelineContract.Version,
        };
        var transitionEdit = await MovieOperationalFixtures.PostAsync<MovieTimelineTransitionEditDto>(
            client,
            $"/api/movie-studio/projects/{projectId}/timeline/transition-edits",
            new
            {
                timeline = canonicalForEdit,
                decision = new
                {
                    decisionId = Guid.NewGuid(),
                    timelineId,
                    baseTimelineVersion = 1,
                    action = MovieTimelineEditActions.Add,
                    transitionId = (Guid?)null,
                    proposedTransition = transition,
                    directorRecommendation = (object?)null,
                    userOverride = new
                    {
                        overrideId = Guid.NewGuid(),
                        timelineId,
                        timelineVersion = 1,
                        transitionId = (Guid?)null,
                        proposedTransition = transition,
                        reason = "User approved the dissolve at the scene boundary.",
                        supersedesRecommendationId = (Guid?)null,
                    },
                },
            });
        Assert.Equal(1, transitionEdit.BaseTimelineVersion);
        Assert.Equal(2, transitionEdit.ResultTimelineVersion);
        Assert.Contains(transitionEdit.Timeline.Transitions, item => item.Id == transitionId && item.Type == MovieTimelineTransitionTypes.Dissolve);
        var reloadedTransition = await MovieOperationalFixtures.GetAsync<MovieTimelineTransitionEditDto>(
            client,
            $"/api/movie-studio/projects/{projectId}/timeline/transition-edits");
        Assert.Contains(reloadedTransition.Timeline.Transitions, item => item.Id == transitionId);

        // Final assembly consumes both selected takes, the persisted transition,
        // the explicit dialogue asset, and the approved soundtrack projection.
        var assembly = await MovieOperationalFixtures.PostAsync<MovieFinalAssemblyDto>(
            client,
            $"/api/movie-studio/projects/{projectId}/final-assembly",
            new
            {
                resolutionProfile = MovieFinalAssemblyProfiles.Hd1080p,
                timeline = new[]
                {
                    new { takeId = firstVideo.Take.Id, inPointSeconds = 0m, outPointSeconds = 1m },
                    new { takeId = secondVideo.Take.Id, inPointSeconds = 0m, outPointSeconds = 1m },
                },
                audioMixInputs = new object[]
                {
                    new
                    {
                        assetId = selectedDialogueTake.AssetId,
                        role = "dialogue",
                        gainDb = 0m,
                        startTimeSeconds = 0m,
                        endTimeSeconds = 1.2m,
                        required = true,
                    },
                    new
                    {
                        assetId = musicAssetId,
                        role = "music",
                        gainDb = -6m,
                        startTimeSeconds = 0m,
                        endTimeSeconds = 5m,
                        required = true,
                    },
                },
                // The production soundtrack projection query currently orders by
                // decimal cue timing, which SQLite cannot translate. The approved
                // cue and its private asset are still exercised through the same
                // final mix contract explicitly, keeping this provider-fake test
                // portable across the repository's disposable SQLite host.
                includeApprovedSoundtrackCues = false,
            },
            idempotencyKey: "movie-fake-final-assembly-1");
        Assert.Equal(MovieAssemblyStatuses.Queued, assembly.Status);
        Assert.Equal(2, assembly.TimelineItemCount);
        Assert.Equal(2, assembly.AudioMixInputCount);
        Assert.Equal(MovieFinalAssemblyQcStatuses.NotRun, assembly.QcStatus);
        Assert.Contains(firstVideo.Take.Id, assembly.SourceTakeIds);
        Assert.Contains(secondVideo.Take.Id, assembly.SourceTakeIds);

        var ready = await WaitForAssemblyAsync(client, assembly.Id);
        Assert.Equal(MovieAssemblyStatuses.Ready, ready.Status);
        Assert.Equal(MovieFinalAssemblyQcStatuses.Passed, ready.QcStatus);
        Assert.Equal(100, ready.ProgressPercent);
        Assert.NotNull(ready.OutputAssetId);
        Assert.NotNull(ready.CompletedAt);
        Assert.Contains(transitionId.ToString(), ready.ProvenanceJson!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(musicAssetId.ToString(), ready.ProvenanceJson!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(selectedDialogueTake.AssetId!.Value.ToString(), ready.ProvenanceJson!, StringComparison.OrdinalIgnoreCase);

        using var download = await client.GetAsync($"/api/movie-studio/final-assemblies/{assembly.Id}/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("video/mp4", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(4_096, (await download.Content.ReadAsByteArrayAsync()).Length);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var outputAsset = await db.Assets.AsNoTracking().Include(item => item.StoredFile)
            .SingleAsync(item => item.Id == ready.OutputAssetId!.Value);
        Assert.Equal(AssetTypes.Video, outputAsset.AssetType);
        Assert.Equal(StoredFileStatus.Ready, outputAsset.StoredFile!.Status);
        Assert.Equal(fixture.Project.Project.ProjectId, outputAsset.ProjectId);
        Assert.True(factory.Calls.Calls(FakeProviderKind.Movie) >= 2);
        Assert.True(factory.Calls.Calls(FakeProviderKind.Music) >= 1);
    }

    private async Task<VideoTakeArtifacts> CreateFinalizedVideoTakeAsync(
        HttpClient client,
        Guid projectId,
        Guid shotId,
        string key,
        bool createApprovedSelect)
    {
        var generated = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(
            client,
            $"/api/movie-studio/shots/{shotId}/generate",
            new { title = $"Fake provider {key} output" },
            idempotencyKey: $"movie-fake-video-{key}");
        var job = await WaitForJobAsync(client, generated.Job.Id);
        Assert.Equal(GenerationJobStatus.Succeeded.ToString(), job.Status);
        var clip = await WaitForClipAsync(client, projectId, generated.ClipId);
        Assert.NotNull(clip.AssetId);
        var take = await MovieOperationalFixtures.PostAsync<MovieV2TakeDto>(
            client,
            $"/api/movie-studio/shots/{shotId}/takes",
            new
            {
                label = $"Fake provider {key} take",
                qualityLevel = MovieQualityLevels.Standard,
                autoDirectorEnabled = false,
                movieClipId = generated.ClipId,
                generationJobId = generated.Job.Id,
                assetId = clip.AssetId,
                notes = "Created by the provider-fake acceptance harness.",
            });
        var approved = await MovieOperationalFixtures.PostAsync<MovieV2TakeDto>(
            client,
            $"/api/movie-studio/takes/{take.Id}/approvals",
            new { decision = MovieApprovalDecisions.Approved, comment = "Fake output is reviewable." });
        Assert.Equal(MovieTakeStatuses.Approved, approved.Status);

        MovieTakeSelectDto? select = null;
        if (createApprovedSelect)
        {
            select = await MovieOperationalFixtures.PostAsync<MovieTakeSelectDto>(
                client,
                $"/api/movie-studio/projects/{projectId}/takes/{take.Id}/selects",
                new
                {
                    label = "Approved opening select",
                    startMilliseconds = 0,
                    endMilliseconds = 1_000,
                    notes = "Use the clean opening range.",
                });
            select = await MovieOperationalFixtures.PostAsync<MovieTakeSelectDto>(
                client,
                $"/api/movie-studio/projects/{projectId}/takes/{take.Id}/selects/{select.Id}/review",
                new { decision = MovieTakeSelectStatuses.Approved, comment = "Select is usable." });
        }

        using (var selected = await MovieOperationalFixtures.SendWithCsrfAsync(
                   client, HttpMethod.Post, $"/api/movie-studio/takes/{take.Id}/select", null))
            Assert.Equal(HttpStatusCode.NoContent, selected.StatusCode);
        using (var finalized = await MovieOperationalFixtures.SendWithCsrfAsync(
                   client, HttpMethod.Post, $"/api/movie-studio/takes/{take.Id}/finalize", null))
            Assert.Equal(HttpStatusCode.NoContent, finalized.StatusCode);
        return new VideoTakeArtifacts(generated.ClipId, clip.AssetId!.Value, take, select);
    }

    private async Task<Guid> FindAssetIdAsync(Guid jobId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Assets.AsNoTracking()
            .Where(item => item.SourceGenerationJobId == jobId)
            .Select(item => item.Id)
            .SingleAsync();
    }

    private async Task<MovieClipDto> WaitForClipAsync(HttpClient client, Guid projectId, Guid clipId)
    {
        for (var attempt = 0; attempt < 180; attempt++)
        {
            var project = await MovieOperationalFixtures.GetAsync<MovieStudioProjectDto>(
                client, $"/api/movie-studio/projects/{projectId}");
            var clip = project.Clips
                .Concat(project.Scenes.SelectMany(scene => scene.Clips.Concat(scene.Shots.SelectMany(shot => shot.Clips))))
                .SingleOrDefault(item => item.Id == clipId);
            if (clip?.AssetId is not null) return clip;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Movie clip {clipId} did not receive a persisted asset.");
    }

    private async Task<GenerationJobDto> WaitForJobAsync(HttpClient client, Guid jobId)
    {
        for (var attempt = 0; attempt < 240; attempt++)
        {
            var current = await MovieOperationalFixtures.GetAsync<GenerationJobDto>(client, $"/api/generation/jobs/{jobId}");
            if (current.Status is "Succeeded" or "Failed" or "Cancelled") return current;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Generation job {jobId} did not reach a terminal state.");
    }

    private async Task<MovieFinalAssemblyDto> WaitForAssemblyAsync(HttpClient client, Guid assemblyId)
    {
        MovieFinalAssemblyDto? current = null;
        for (var attempt = 0; attempt < 240; attempt++)
        {
            current = await MovieOperationalFixtures.GetAsync<MovieFinalAssemblyDto>(
                client, $"/api/movie-studio/final-assemblies/{assemblyId}");
            if (current.Status is MovieAssemblyStatuses.Ready or MovieAssemblyStatuses.Failed or MovieAssemblyStatuses.Cancelled)
                return current;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Final assembly {assemblyId} did not reach a terminal state. Last status: {current?.Status}.");
    }

    private sealed record VideoTakeArtifacts(Guid ClipId, Guid AssetId, MovieV2TakeDto Take, MovieTakeSelectDto? Select);
}
