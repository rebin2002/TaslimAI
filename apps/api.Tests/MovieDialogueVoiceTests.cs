using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieDialogueVoiceTests
{
    [Fact]
    public async Task Default_adapter_is_disabled_and_makes_no_external_call()
    {
        var provider = new UnavailableMovieDialogueVoiceProvider();
        var request = new MovieDialogueVoiceProviderRequest(Guid.NewGuid(), Guid.NewGuid(), null, "Narrator", "Hello", MovieDialogueLanguages.English, 0, 1_000, null);

        await Assert.ThrowsAsync<MovieDialogueVoiceProviderUnavailableException>(() => provider.GenerateAsync(request));
    }

    [Fact]
    public async Task Deterministic_adapter_returns_zero_cost_audio_for_execution_tests()
    {
        var provider = new DeterministicMovieDialogueVoiceProvider();
        var request = new MovieDialogueVoiceProviderRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Mara", "Hello", MovieDialogueLanguages.Arabic, 250, 1_250, "Calm");

        var result = await provider.GenerateAsync(request);

        Assert.Equal("audio/mpeg", result.ContentType);
        Assert.Equal("mp3", result.Format);
        Assert.Equal(1_000, result.DurationMilliseconds);
        Assert.Equal(0m, result.Usage.ActualCostUsd);
        Assert.Equal(request.Text.Length, result.Usage.InputCharacters);
    }

    [Fact]
    public async Task Handler_keeps_voice_execution_disabled_by_default()
    {
        var handler = new MovieDialogueVoiceGenerationJobHandler(
            [new UnavailableMovieDialogueVoiceProvider()],
            Options.Create(new MovieDialogueVoiceOptions()),
            NullLogger<MovieDialogueVoiceGenerationJobHandler>.Instance);
        var input = new MovieDialogueVoiceInput(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "Mara", "Hello", MovieDialogueLanguages.English, 0, 1_000, null, Guid.NewGuid());
        var job = new GenerationJob { Id = Guid.NewGuid(), JobType = GenerationJobTypes.MovieDialogueVoiceGenerate, InputJson = System.Text.Json.JsonSerializer.Serialize(input) };

        await Assert.ThrowsAsync<MovieDialogueVoiceProviderUnavailableException>(() => handler.ExecuteAsync(job, new Progress<int>(), CancellationToken.None));
    }

    [Fact]
    public void Take_selection_requires_approval_and_published_assets_are_explicit()
    {
        Assert.False(MovieDialogueTakeLifecycle.CanSelect(MovieDialogueTakeStatuses.Succeeded));
        Assert.True(MovieDialogueTakeLifecycle.CanSelect(MovieDialogueTakeStatuses.Approved));
        Assert.False(MovieDialogueTakeLifecycle.HasPublishedAsset(Guid.NewGuid(), null));
        Assert.True(MovieDialogueTakeLifecycle.HasPublishedAsset(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public async Task Approval_and_selection_require_a_scoped_ready_audio_output()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        var (userId, takeId, storedFileId) = await SeedReadyTakeAsync(db);
        var service = new MovieDialogueProductionService(
            db,
            new MovieAuthorizationService(new MovieCollaborationAccess(db, new WorkspaceAccessService(db))),
            null!);

        var storedFile = await db.StoredFiles.SingleAsync(item => item.Id == storedFileId);
        storedFile.Status = StoredFileStatus.Failed;
        await db.SaveChangesAsync();
        var approvalException = await Assert.ThrowsAsync<MovieDialogueValidationException>(() => service.ApproveTakeAsync(userId, takeId, new MovieDialogueTakeApprovalRequest(), CancellationToken.None));
        Assert.Equal("MOVIE_DIALOGUE_TAKE_OUTPUT_NOT_READY", approvalException.Code);

        storedFile.Status = StoredFileStatus.Ready;
        await db.SaveChangesAsync();
        var approved = await service.ApproveTakeAsync(userId, takeId, new MovieDialogueTakeApprovalRequest(), CancellationToken.None);
        Assert.NotNull(approved);
        Assert.Equal(MovieDialogueTakeStatuses.Approved, approved!.Takes.Single(item => item.Id == takeId).Status);

        storedFile.Status = StoredFileStatus.Failed;
        await db.SaveChangesAsync();
        var selectionException = await Assert.ThrowsAsync<MovieDialogueValidationException>(() => service.SelectTakeAsync(userId, takeId, CancellationToken.None));
        Assert.Equal("MOVIE_DIALOGUE_TAKE_OUTPUT_NOT_READY", selectionException.Code);

        storedFile.Status = StoredFileStatus.Ready;
        await db.SaveChangesAsync();
        var selected = await service.SelectTakeAsync(userId, takeId, CancellationToken.None);
        Assert.Equal(MovieDialogueTakeStatuses.Selected, selected!.Takes.Single(item => item.Id == takeId).Status);
    }

    private static async Task<(Guid UserId, Guid TakeId, Guid StoredFileId)> SeedReadyTakeAsync(TaslimDbContext db)
    {
        var now = DateTime.UtcNow;
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        var clipId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var takeId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var storedFileId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "dialogue-owner", NormalizedUserName = "DIALOGUE-OWNER", DisplayName = "Dialogue Owner", CreatedAt = now, UpdatedAt = now });
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Dialogue Workspace", Slug = $"dialogue-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner });
        db.MovieProjects.Add(new MovieProject
        {
            Id = movieId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = "Dialogue Movie", Description = "A dialogue test.",
            DurationSeconds = 30, CreatedAt = now, UpdatedAt = now, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now },
        });
        db.MovieTeamMembers.Add(new MovieTeamMember
        {
            Id = Guid.NewGuid(), MovieProjectId = movieId, UserId = userId, Role = MovieTeamRoles.Producer, IsProjectOwner = true,
            PermissionOverrides =
            [
                new MovieTeamMemberPermission { Id = Guid.NewGuid(), Permission = MoviePermissions.Approve, Granted = true },
                new MovieTeamMemberPermission { Id = Guid.NewGuid(), Permission = MoviePermissions.Edit, Granted = true },
            ],
            CreatedAt = now, UpdatedAt = now,
        });
        db.MovieClips.Add(new MovieClip { Id = clipId, MovieProjectId = movieId, Status = MovieClipStatuses.Ready, CreatedAt = now, UpdatedAt = now });
        db.MovieDialogueLines.Add(new MovieDialogueLine
        {
            Id = lineId, MovieClipId = clipId, Sequence = 1, SpeakerName = "Mara", Language = MovieDialogueLanguages.English,
            Text = "The handoff is ready.", StartMilliseconds = 0, EndMilliseconds = 1_000, Status = MovieDialogueLineStatuses.Ready,
            CreatedByUserId = userId, CreatedAt = now, UpdatedAt = now,
        });
        db.MovieDialogueTakes.Add(new MovieDialogueTake
        {
            Id = takeId, MovieDialogueLineId = lineId, MovieClipId = clipId, AssetId = assetId, StoredFileId = storedFileId,
            VersionNumber = 1, Label = "Take 1", Status = MovieDialogueTakeStatuses.Succeeded, DurationMilliseconds = 1_000, CreatedAt = now, UpdatedAt = now,
        });
        db.StoredFiles.Add(new StoredFile
        {
            Id = storedFileId, WorkspaceId = workspaceId, UserId = userId, OriginalFileName = "dialogue.mp3", StoredFileName = "dialogue.mp3",
            ContentType = "audio/mpeg", Extension = ".mp3", SizeBytes = 14, StorageKey = "dialogue-test", Status = StoredFileStatus.Ready, CreatedAt = now,
        });
        db.Assets.Add(new Asset
        {
            Id = assetId, WorkspaceId = workspaceId, CreatedByUserId = userId, StoredFileId = storedFileId, Name = "Mara dialogue",
            AssetType = AssetTypes.Audio, MimeType = "audio/mpeg", Status = AssetStatus.Active, CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return (userId, takeId, storedFileId);
    }
}
