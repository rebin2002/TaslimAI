using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieSoundTests : IClassFixture<GenerationJobsNoWorkerFactory>
{
    private readonly GenerationJobsNoWorkerFactory factory;

    public MovieSoundTests(GenerationJobsNoWorkerFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Scene_and_shot_cues_persist_timing_layers_and_keep_generation_disabled()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var project = await Post<MovieStudioProjectResponse>(client, "/api/movie-studio/projects", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            mode = MovieProjectModes.Full,
            title = "Sound cue contract",
            description = "Sound cue API contract test.",
            durationSeconds = 12,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "en",
        });
        var scene = await Post<MovieSceneDto>(client, $"/api/movie-studio/projects/{project.Project.Id}/scenes", new
        {
            title = "Harbor",
            summary = "A quiet harbor at blue hour.",
            durationSeconds = 12,
        });

        var draft = await Post<MovieSoundTrackDto>(client, $"/api/movie-sound/scenes/{scene.Id}/tracks", new
        {
            kind = MovieSoundKinds.Ambience,
            layer = MovieSoundLayers.Environment,
            name = "Harbor air",
            description = "Distant water and restrained harbor air.",
            startMilliseconds = 500,
            endMilliseconds = 4_500,
            fadeInMilliseconds = 250,
            fadeOutMilliseconds = 500,
        });

        Assert.Equal(MovieSoundStatuses.Draft, draft.Status);
        Assert.Equal(MovieSoundSourceKinds.Imported, draft.SourceKind);
        Assert.Equal(500, draft.StartMilliseconds);
        Assert.Equal(4_500, draft.EndMilliseconds);
        Assert.Empty(draft.Approvals);

        var list = await client.GetFromJsonAsync<MovieSoundTrackListDto>($"/api/movie-sound/scenes/{scene.Id}/tracks");
        Assert.NotNull(list);
        Assert.Single(list!.Tracks);
        Assert.Equal(draft.Id, list.Tracks[0].Id);

        var invalid = await PostRaw(client, $"/api/movie-sound/scenes/{scene.Id}/tracks", new
        {
            kind = MovieSoundKinds.SoundEffect,
            layer = MovieSoundLayers.Foreground,
            name = "Bad cue",
            description = "End before start.",
            startMilliseconds = 2_000,
            endMilliseconds = 1_000,
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains(GenerationJobErrorCodes.MovieSoundCueInvalid, await invalid.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var generated = await Post<MovieSoundTrackDto>(client, $"/api/movie-sound/scenes/{scene.Id}/tracks", new
        {
            kind = MovieSoundKinds.SoundEffect,
            layer = MovieSoundLayers.Foreground,
            name = "Door latch",
            description = "A restrained metal latch closing.",
            startMilliseconds = 6_000,
            endMilliseconds = 6_750,
            generate = true,
        });
        Assert.Equal(MovieSoundStatuses.Queued, generated.Status);
        Assert.NotNull(generated.GenerationJobId);
        Assert.DoesNotContain("provider", JsonSerializer.Serialize(generated), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model", JsonSerializer.Serialize(generated), StringComparison.OrdinalIgnoreCase);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var job = await db.GenerationJobs.SingleAsync(item => item.Id == generated.GenerationJobId);
        Assert.Equal(GenerationJobTypes.MovieSoundGenerate, job.JobType);
        Assert.Equal(GenerationJobStatus.Queued, job.Status);

        var cancelled = await PostRaw(client, $"/api/generation/jobs/{generated.GenerationJobId}/cancel", new { });
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        var cancelledTrack = await client.GetFromJsonAsync<MovieSoundTrackDto>($"/api/movie-sound/tracks/{generated.Id}");
        Assert.NotNull(cancelledTrack);
        Assert.Equal(MovieSoundStatuses.Cancelled, cancelledTrack!.Status);
    }

    [Fact]
    public async Task Library_reference_can_be_reused_and_approved_with_audit_history()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var project = await Post<MovieStudioProjectResponse>(client, "/api/movie-studio/projects", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            mode = MovieProjectModes.Full,
            title = "Sound library contract",
            description = "Sound library API contract test.",
            durationSeconds = 20,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "en",
        });
        var scene = await Post<MovieSceneDto>(client, $"/api/movie-studio/projects/{project.Project.Id}/scenes", new
        {
            title = "Interior",
            summary = "A room tone cue.",
            durationSeconds = 20,
        });

        var assetId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var now = DateTime.UtcNow;
            var fileId = Guid.NewGuid();
            db.StoredFiles.Add(new StoredFile
            {
                Id = fileId,
                WorkspaceId = auth.PersonalWorkspace.Id,
                UserId = auth.User.Id,
                OriginalFileName = "room-tone.wav",
                StoredFileName = "room-tone.wav",
                ContentType = "audio/wav",
                Extension = ".wav",
                SizeBytes = 44,
                StorageProvider = FileStorageProviders.Local,
                StorageKey = "tests/room-tone.wav",
                Status = StoredFileStatus.Ready,
                CreatedAt = now,
                ProcessedAt = now,
            });
            db.Assets.Add(new Asset
            {
                Id = assetId,
                WorkspaceId = auth.PersonalWorkspace.Id,
                CreatedByUserId = auth.User.Id,
                StoredFileId = fileId,
                Name = "Room tone",
                AssetType = AssetTypes.Audio,
                MimeType = "audio/wav",
                Status = AssetStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        var reference = await Post<MovieSoundLibraryReferenceDto>(client, $"/api/movie-sound/projects/{project.Project.Id}/library", new
        {
            assetId,
            label = "Reusable room tone",
        });
        var track = await Post<MovieSoundTrackDto>(client, $"/api/movie-sound/scenes/{scene.Id}/tracks", new
        {
            kind = MovieSoundKinds.Ambience,
            layer = MovieSoundLayers.RoomTone,
            name = "Interior room tone",
            description = "Reuse the approved room tone under the scene.",
            startMilliseconds = 0,
            endMilliseconds = 8_000,
            libraryReferenceId = reference.Id,
        });
        Assert.Equal(MovieSoundStatuses.ReadyForReview, track.Status);
        Assert.Equal(MovieSoundSourceKinds.Library, track.SourceKind);
        Assert.Equal(assetId, track.AssetId);
        Assert.Equal(reference.Id, track.LibraryReferenceId);

        var approved = await Post<MovieSoundTrackDto>(client, $"/api/movie-sound/tracks/{track.Id}/review", new
        {
            approve = true,
            comment = "Room tone is approved for assembly.",
        });
        Assert.Equal(MovieSoundStatuses.Approved, approved.Status);
        Assert.Single(approved.Approvals);
        Assert.Equal(MovieSoundStatuses.Approved, approved.Approvals[0].Decision);

        using var repeatedReview = await PostRaw(client, $"/api/movie-sound/tracks/{track.Id}/review", new
        {
            approve = true,
            comment = "A repeated review must not create a second approval.",
        });
        Assert.Equal(HttpStatusCode.BadRequest, repeatedReview.StatusCode);
        Assert.Contains("MOVIE_SOUND_REVIEW_NOT_PENDING", await repeatedReview.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var reloaded = await client.GetFromJsonAsync<MovieSoundTrackDto>($"/api/movie-sound/tracks/{track.Id}");
        Assert.NotNull(reloaded);
        Assert.Equal(MovieSoundStatuses.Approved, reloaded!.Status);
        Assert.Single(reloaded.Approvals);

        var library = await client.GetFromJsonAsync<MovieSoundLibraryDto>($"/api/movie-sound/projects/{project.Project.Id}/library");
        Assert.NotNull(library);
        Assert.Contains(library!.References, item => item.Id == reference.Id && item.AssetId == assetId);
    }

    [Fact]
    public async Task Library_reference_rejects_audio_asset_from_another_project_in_the_same_workspace()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var project = await Post<MovieStudioProjectResponse>(client, "/api/movie-studio/projects", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            mode = MovieProjectModes.Full,
            title = "Sound scope contract",
            description = "Sound asset scope test.",
            durationSeconds = 20,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "en",
        });
        var foreignProjectId = Guid.NewGuid();
        var foreignAssetId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var now = DateTime.UtcNow;
            var fileId = Guid.NewGuid();
            db.Projects.Add(new Project
            {
                Id = foreignProjectId,
                WorkspaceId = auth.PersonalWorkspace.Id,
                Name = "Another project",
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.StoredFiles.Add(new StoredFile
            {
                Id = fileId,
                WorkspaceId = auth.PersonalWorkspace.Id,
                ProjectId = foreignProjectId,
                UserId = auth.User.Id,
                OriginalFileName = "foreign.wav",
                StoredFileName = "foreign.wav",
                ContentType = "audio/wav",
                Extension = ".wav",
                SizeBytes = 44,
                StorageProvider = FileStorageProviders.Local,
                StorageKey = "tests/foreign.wav",
                Status = StoredFileStatus.Ready,
                CreatedAt = now,
                ProcessedAt = now,
            });
            db.Assets.Add(new Asset
            {
                Id = foreignAssetId,
                WorkspaceId = auth.PersonalWorkspace.Id,
                ProjectId = foreignProjectId,
                CreatedByUserId = auth.User.Id,
                StoredFileId = fileId,
                Name = "Foreign room tone",
                AssetType = AssetTypes.Audio,
                MimeType = "audio/wav",
                Status = AssetStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        using var response = await PostRaw(client, $"/api/movie-sound/projects/{project.Project.Id}/library", new
        {
            assetId = foreignAssetId,
            label = "Must not cross project boundary",
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(GenerationJobErrorCodes.MovieSoundAssetInvalid, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var library = await client.GetFromJsonAsync<MovieSoundLibraryDto>($"/api/movie-sound/projects/{project.Project.Id}/library");
        Assert.NotNull(library);
        Assert.Empty(library!.References);
    }

    [Fact]
    public async Task Review_rechecks_that_the_sound_asset_is_still_ready_and_private()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var project = await Post<MovieStudioProjectResponse>(client, "/api/movie-studio/projects", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            mode = MovieProjectModes.Full,
            title = "Sound approval recheck",
            description = "Sound approval readiness test.",
            durationSeconds = 20,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "en",
        });
        var scene = await Post<MovieSceneDto>(client, $"/api/movie-studio/projects/{project.Project.Id}/scenes", new
        {
            title = "Interior",
            summary = "A room tone cue.",
            durationSeconds = 20,
        });
        var assetId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var now = DateTime.UtcNow;
            db.StoredFiles.Add(new StoredFile
            {
                Id = fileId,
                WorkspaceId = auth.PersonalWorkspace.Id,
                UserId = auth.User.Id,
                OriginalFileName = "room-tone.wav",
                StoredFileName = "room-tone.wav",
                ContentType = "audio/wav",
                Extension = ".wav",
                SizeBytes = 44,
                StorageProvider = FileStorageProviders.Local,
                StorageKey = "tests/recheck-room-tone.wav",
                Status = StoredFileStatus.Ready,
                CreatedAt = now,
                ProcessedAt = now,
            });
            db.Assets.Add(new Asset
            {
                Id = assetId,
                WorkspaceId = auth.PersonalWorkspace.Id,
                CreatedByUserId = auth.User.Id,
                StoredFileId = fileId,
                Name = "Room tone",
                AssetType = AssetTypes.Audio,
                MimeType = "audio/wav",
                Status = AssetStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }
        var track = await Post<MovieSoundTrackDto>(client, $"/api/movie-sound/scenes/{scene.Id}/tracks", new
        {
            kind = MovieSoundKinds.Ambience,
            layer = MovieSoundLayers.RoomTone,
            name = "Interior room tone",
            description = "Recheck the private room tone before approval.",
            startMilliseconds = 0,
            endMilliseconds = 8_000,
            assetId,
        });
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var file = await db.StoredFiles.SingleAsync(item => item.Id == fileId);
            file.Status = StoredFileStatus.Uploading;
            await db.SaveChangesAsync();
        }

        using var response = await PostRaw(client, $"/api/movie-sound/tracks/{track.Id}/review", new
        {
            approve = true,
            comment = "The file is no longer ready.",
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(GenerationJobErrorCodes.MovieSoundNotReady, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var persisted = await verifyDb.MovieSoundTracks.SingleAsync(item => item.Id == track.Id);
        Assert.Equal(MovieSoundStatuses.ReadyForReview, persisted.Status);
        Assert.Empty(await verifyDb.MovieSoundApprovals.Where(item => item.MovieSoundTrackId == track.Id).ToListAsync());
    }

    [Fact]
    public async Task Fake_adapter_emits_valid_audio_without_external_calls()
    {
        var request = new MovieSoundGenerationInput(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, MovieSoundKinds.SoundEffect, MovieSoundLayers.Foreground, "A soft latch", 750, null);
        var result = await new FakeMovieSoundProvider().GenerateAsync(request);

        Assert.Equal("audio/wav", result.ContentType);
        Assert.Equal("wav", result.Format);
        Assert.True(MovieSoundOutputInspector.HasValidAudioSignature(result.Content.Span, result.Format));
        Assert.Equal(750, result.DurationMilliseconds);
        Assert.Equal(0m, result.Usage.ActualCostUsd);
    }

    private static async Task<AuthResponse> Register(HttpClient client)
    {
        var response = await PostRaw(client, "/api/auth/register", new
        {
            displayName = "Sound Tester",
            email = $"sound-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<T> Post<T>(HttpClient client, string path, object payload)
    {
        using var response = await PostRaw(client, path, payload);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<HttpResponseMessage> PostRaw(HttpClient client, string path, object payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}
