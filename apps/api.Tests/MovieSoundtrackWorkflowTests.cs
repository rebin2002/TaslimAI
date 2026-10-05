using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Infrastructure;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieSoundtrackWorkflowTests
{
    [Fact]
    public async Task Disabled_media_service_never_executes_external_work()
    {
        var adapter = new UnavailableMovieSoundtrackMediaService();
        Assert.False(adapter.IsAvailable);
        Assert.Empty(adapter.SupportedOperations);
        await Assert.ThrowsAsync<MovieSoundtrackMediaUnavailableException>(() => adapter.SubmitAsync(
            new MovieSoundtrackMediaRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Cue", "calm", 20, 10, "piano"),
            CancellationToken.None));
    }

    [Fact]
    public async Task Cue_workflow_persists_hierarchy_timing_ducking_provenance_and_review_history()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var seed = await SeedAsync(db);
        var service = CreateService(db, new FakeSoundtrackMediaService());

        var cue = await service.CreateCueAsync(seed.UserId, seed.MovieProjectId, new MovieSoundtrackCueRequest
        {
            MovieActId = seed.ActId,
            MovieSceneId = seed.SceneId,
            Title = "Mara chooses",
            NarrativeIntent = "Hold the audience in the decision before the reveal.",
            Mood = "tense",
            Intensity = 72,
            ActStartSeconds = 8,
            SceneStartSeconds = 4,
            TimelineStartSeconds = 18,
            DurationSeconds = 20,
            DuckingIntents =
            [
                new MovieSoundtrackDuckingIntentRequest
                {
                    TargetLane = MovieSoundtrackDuckingTargets.Dialogue,
                    StartOffsetSeconds = 5,
                    EndOffsetSeconds = 12,
                    DuckDecibels = 9,
                    AttackMilliseconds = 40,
                    ReleaseMilliseconds = 300,
                    Rationale = "Make the line land without losing the music bed.",
                },
            ],
        }, CancellationToken.None);

        Assert.NotNull(cue);
        Assert.Equal(seed.ActId, cue!.MovieActId);
        Assert.Equal(seed.SceneId, cue.MovieSceneId);
        Assert.Equal(18, cue.TimelineStartSeconds);
        Assert.Equal(20, cue.DurationSeconds);
        Assert.Single(cue.DuckingIntents);
        Assert.Equal(MovieSoundtrackApprovalStates.Draft, cue.ApprovalState);

        var draft = await service.CreateVersionAsync(seed.UserId, cue.Id, new MovieSoundtrackCueVersionRequest
        {
            Label = "Tense piano draft",
            ArrangementIntent = "Piano ostinato, low strings, no percussion.",
            Mood = "tense",
            Intensity = 76,
        }, CancellationToken.None);
        Assert.NotNull(draft);
        var draftVersion = Assert.Single(draft!.Versions);
        Assert.Null(draftVersion.AssetId);
        await Assert.ThrowsAsync<MovieSoundtrackValidationException>(() => service.ReviewVersionAsync(seed.UserId, draftVersion.Id,
            new MovieSoundtrackCueVersionReviewRequest { Decision = MovieSoundtrackApprovalStates.Approved }, CancellationToken.None));

        var withAsset = await service.CreateVersionAsync(seed.UserId, cue.Id, new MovieSoundtrackCueVersionRequest
        {
            Label = "Tense piano approved candidate",
            ArrangementIntent = "Piano ostinato, low strings, no percussion.",
            AssetId = seed.AssetId,
        }, CancellationToken.None);
        var candidate = withAsset!.Versions.Single(item => item.VersionNumber == 2);
        Assert.NotNull(candidate.AudioAssetProvenance);
        Assert.Equal(seed.AssetId, candidate.AudioAssetProvenance!.AssetId);
        Assert.Equal(seed.StoredFileId, candidate.AudioAssetProvenance.StoredFileId);

        var approved = await service.ReviewVersionAsync(seed.UserId, candidate.Id, new MovieSoundtrackCueVersionReviewRequest
        {
            Decision = MovieSoundtrackApprovalStates.Approved,
            Comment = "The cue supports the scene without competing with dialogue.",
        }, CancellationToken.None);
        Assert.NotNull(approved);
        Assert.Equal(MovieSoundtrackApprovalStates.Approved, approved!.ApprovalState);
        Assert.Equal(candidate.Id, approved.ApprovedVersionId);
        Assert.Equal(MovieSoundtrackApprovalStates.Approved, approved.Versions.Single(item => item.Id == candidate.Id).ApprovalState);
        Assert.Single(approved.Versions.Single(item => item.Id == candidate.Id).Reviews);

        var persisted = await db.MovieSoundtrackCues
            .Include(item => item.DuckingIntents)
            .Include(item => item.Versions).ThenInclude(item => item.AudioAssetProvenance)
            .SingleAsync(item => item.Id == cue.Id);
        Assert.Equal(18, persisted.TimelineStartSeconds);
        Assert.Single(persisted.DuckingIntents);
        Assert.NotNull(persisted.Versions.Single(item => item.VersionNumber == 2).AudioAssetProvenance);
    }

    [Fact]
    public async Task Version_rejects_audio_asset_from_another_project_in_the_same_workspace()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var seed = await SeedAsync(db);
        var otherProject = new Project
        {
            Id = Guid.NewGuid(), WorkspaceId = (await db.MovieProjects.SingleAsync(item => item.Id == seed.MovieProjectId)).WorkspaceId,
            Name = "Another project", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.Projects.Add(otherProject);
        var asset = await db.Assets.Include(item => item.StoredFile).SingleAsync(item => item.Id == seed.AssetId);
        asset.ProjectId = otherProject.Id;
        asset.StoredFile!.ProjectId = otherProject.Id;
        await db.SaveChangesAsync();

        var service = CreateService(db, new FakeSoundtrackMediaService());
        var cue = await service.CreateCueAsync(seed.UserId, seed.MovieProjectId, new MovieSoundtrackCueRequest
        {
            MovieActId = seed.ActId, MovieSceneId = seed.SceneId, Title = "Out-of-scope cue", Mood = "tense",
            Intensity = 50, ActStartSeconds = 0, SceneStartSeconds = 0, TimelineStartSeconds = 0, DurationSeconds = 10,
        }, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<MovieSoundtrackValidationException>(() => service.CreateVersionAsync(seed.UserId, cue!.Id,
            new MovieSoundtrackCueVersionRequest { Label = "Foreign project candidate", AssetId = seed.AssetId }, CancellationToken.None));

        Assert.Equal("MOVIE_SOUNDTRACK_ASSET_OUT_OF_SCOPE", exception.Code);
    }

    [Fact]
    public async Task Approval_rechecks_that_the_soundtrack_asset_is_still_ready_and_private()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var seed = await SeedAsync(db);
        var service = CreateService(db, new FakeSoundtrackMediaService());
        var cue = await service.CreateCueAsync(seed.UserId, seed.MovieProjectId, new MovieSoundtrackCueRequest
        {
            MovieActId = seed.ActId, MovieSceneId = seed.SceneId, Title = "Revalidated cue", Mood = "calm",
            Intensity = 40, ActStartSeconds = 0, SceneStartSeconds = 0, TimelineStartSeconds = 0, DurationSeconds = 10,
        }, CancellationToken.None);
        var versioned = await service.CreateVersionAsync(seed.UserId, cue!.Id,
            new MovieSoundtrackCueVersionRequest { Label = "Candidate", AssetId = seed.AssetId }, CancellationToken.None);
        var versionId = Assert.Single(versioned!.Versions).Id;

        var asset = await db.Assets.Include(item => item.StoredFile).SingleAsync(item => item.Id == seed.AssetId);
        asset.StoredFile!.Status = StoredFileStatus.Processing;
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<MovieSoundtrackValidationException>(() => service.ReviewVersionAsync(seed.UserId, versionId,
            new MovieSoundtrackCueVersionReviewRequest { Decision = MovieSoundtrackApprovalStates.Approved }, CancellationToken.None));

        Assert.Equal("MOVIE_SOUNDTRACK_ASSET_NOT_READY", exception.Code);
    }

    [Fact]
    public async Task Approval_rejects_a_stale_canonical_pointer_instead_of_overwriting_a_newer_approval()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var seed = await SeedAsync(db);
        var service = CreateService(db, new FakeSoundtrackMediaService());
        var cue = await service.CreateCueAsync(seed.UserId, seed.MovieProjectId, new MovieSoundtrackCueRequest
        {
            MovieActId = seed.ActId, MovieSceneId = seed.SceneId, Title = "Concurrent review cue", Mood = "tense",
            Intensity = 50, ActStartSeconds = 0, SceneStartSeconds = 0, TimelineStartSeconds = 0, DurationSeconds = 10,
        }, CancellationToken.None);
        var first = await service.CreateVersionAsync(seed.UserId, cue!.Id,
            new MovieSoundtrackCueVersionRequest { Label = "First candidate", AssetId = seed.AssetId }, CancellationToken.None);
        var second = await service.CreateVersionAsync(seed.UserId, cue.Id,
            new MovieSoundtrackCueVersionRequest { Label = "Second candidate", AssetId = seed.AssetId }, CancellationToken.None);
        var firstVersionId = first!.Versions.Single(item => item.VersionNumber == 1).Id;
        var secondVersionId = second!.Versions.Single(item => item.VersionNumber == 2).Id;

        // Keep the service's tracked cue snapshot stale while a separate writer
        // records the approval that won the race in the database.
        _ = await db.MovieSoundtrackCues.SingleAsync(item => item.Id == cue.Id);
        await db.MovieSoundtrackCueVersions.Where(item => item.Id == firstVersionId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ApprovalState, MovieSoundtrackApprovalStates.Approved));
        await db.MovieSoundtrackCues.Where(item => item.Id == cue.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.ApprovedVersionId, firstVersionId)
                .SetProperty(item => item.ApprovalState, MovieSoundtrackApprovalStates.Approved));

        var exception = await Assert.ThrowsAsync<MovieSoundtrackValidationException>(() => service.ReviewVersionAsync(seed.UserId, secondVersionId,
            new MovieSoundtrackCueVersionReviewRequest { Decision = MovieSoundtrackApprovalStates.Approved }, CancellationToken.None));

        Assert.Equal("MOVIE_SOUNDTRACK_REVIEW_CONFLICT", exception.Code);
        db.ChangeTracker.Clear();
        var persistedCue = await db.MovieSoundtrackCues.AsNoTracking().SingleAsync(item => item.Id == cue.Id);
        var persistedVersions = await db.MovieSoundtrackCueVersions.AsNoTracking().Where(item => item.MovieSoundtrackCueId == cue.Id).ToListAsync();
        Assert.Equal(firstVersionId, persistedCue.ApprovedVersionId);
        Assert.Equal(MovieSoundtrackApprovalStates.Approved, persistedVersions.Single(item => item.Id == firstVersionId).ApprovalState);
        Assert.Equal(MovieSoundtrackApprovalStates.Draft, persistedVersions.Single(item => item.Id == secondVersionId).ApprovalState);
    }

    private static MovieSoundtrackService CreateService(TaslimDbContext db, IMovieSoundtrackMediaService media) =>
        new(db, new MovieAuthorizationService(new MovieCollaborationAccess(db, new WorkspaceAccessService(db))), media);

    private static TaslimDbContext CreateDb(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);

    private static async Task<Seed> SeedAsync(TaslimDbContext db)
    {
        db.Database.EnsureCreated();
        var now = DateTime.UtcNow;
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var movieProjectId = Guid.NewGuid();
        var actId = Guid.NewGuid();
        var sequenceId = Guid.NewGuid();
        var sceneId = Guid.NewGuid();
        var storedFileId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser
        {
            Id = userId, UserName = "soundtrack-owner", NormalizedUserName = "SOUNDTRACK-OWNER", Email = "soundtrack-owner@example.com", NormalizedEmail = "SOUNDTRACK-OWNER@EXAMPLE.COM",
            DisplayName = "Soundtrack Owner", SecurityStamp = Guid.NewGuid().ToString(), CreatedAt = now, UpdatedAt = now,
        });
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Soundtrack Workspace", Slug = $"soundtrack-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner, JoinedAt = now });
        await db.SaveChangesAsync();
        db.MovieProjects.Add(new MovieProject
        {
            Id = movieProjectId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = "Soundtrack Film", Description = "A film.", DurationSeconds = 120,
            CreatedAt = now, UpdatedAt = now, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now },
            Acts = [new MovieAct
            {
                Id = actId, Sequence = 1, Title = "Act One", CreatedAt = now, UpdatedAt = now,
                Sequences = [new MovieSequence
                {
                    Id = sequenceId, Sequence = 1, Title = "Arrival", CreatedAt = now, UpdatedAt = now,
                    Scenes = [new MovieScene { Id = sceneId, MovieProjectId = movieProjectId, Sequence = 1, Title = "The choice", Summary = "A choice is made.", DurationSeconds = 60, CreatedAt = now, UpdatedAt = now }],
                }],
            }],
        });
        await db.SaveChangesAsync();
        db.MovieTeamMembers.Add(new MovieTeamMember { Id = Guid.NewGuid(), MovieProjectId = movieProjectId, UserId = userId, Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now });
        db.StoredFiles.Add(new StoredFile
        {
            Id = storedFileId, WorkspaceId = workspaceId, UserId = userId, OriginalFileName = "choice.mp3", StoredFileName = "choice.mp3", Extension = ".mp3", ContentType = "audio/mpeg",
            SizeBytes = 24_000, StorageProvider = FileStorageProviders.Local, StorageKey = "test/choice.mp3", Status = StoredFileStatus.Ready, CreatedAt = now,
        });
        await db.SaveChangesAsync();
        db.Assets.Add(new Asset
        {
            Id = assetId, WorkspaceId = workspaceId, CreatedByUserId = userId, StoredFileId = storedFileId, Name = "Choice music", AssetType = AssetTypes.Music,
            MimeType = "audio/mpeg", Status = AssetStatus.Active, MetadataJson = "{\"durationSeconds\":20}", CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return new Seed(userId, movieProjectId, actId, sceneId, storedFileId, assetId);
    }

    private sealed record Seed(Guid UserId, Guid MovieProjectId, Guid ActId, Guid SceneId, Guid StoredFileId, Guid AssetId);

    private sealed class FakeSoundtrackMediaService : IMovieSoundtrackMediaService
    {
        public bool IsAvailable => false;
        public IReadOnlyCollection<string> SupportedOperations { get; } = [];
        public Task<MovieSoundtrackMediaSubmission> SubmitAsync(MovieSoundtrackMediaRequest request, CancellationToken cancellationToken) =>
            Task.FromException<MovieSoundtrackMediaSubmission>(new MovieSoundtrackMediaUnavailableException());
    }
}
