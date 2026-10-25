using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieDirectorScenePlanExecutorTests
{
    [Fact]
    public async Task Duplicate_existing_scene_targets_are_rejected_before_any_scene_mutation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        var seeded = await SeedAsync(db);
        var assembler = new MovieDirectorContextAssembler(
            db,
            new MovieWorldContinuityProjector(db),
            new MovieStoryScenePlanningContextAssembler(db));
        var assembled = await assembler.AssembleStoryAsync(seeded.MovieId, null, null);
        Assert.NotNull(assembled);

        var proposal = new DirectorSceneProposal(
            1,
            "The bridge opens",
            "Mara crosses the bridge.",
            "Mara chooses the safe path.",
            "Old bridge",
            "Dawn",
            ["Mara"],
            "Resolve the crossing.",
            15,
            "Open into wind.",
            ["The red scarf remains tied."],
            "A restrained tracking shot.",
            ExistingMovieSceneId: seeded.SceneId);
        var secondProposal = proposal with { Sequence = 2, Title = "The bridge closes", EstimatedDurationSeconds = 15 };
        var payload = new DirectorScenePlanActionPayload(
            DirectorScenePlanActionTypes.PlanScenes,
            seeded.RevisionId,
            assembled!.Value.SnapshotHash,
            "en",
            "16:9",
            30,
            30,
            DirectorQualityLevels.Fast,
            DirectorQualityLevels.Fast,
            null,
            [proposal, secondProposal]);
        var action = new DirectorAction
        {
            Id = Guid.NewGuid(),
            MovieProjectId = seeded.MovieId,
            PayloadJson = JsonSerializer.Serialize(payload, DirectorJson.Options),
        };

        var executor = new MovieDirectorScenePlanActionExecutor(db, assembler, new AcceptAllScenePlanValidator());
        var execution = await executor.ExecuteAsync(action, seeded.UserId);

        Assert.False(execution.Succeeded);
        Assert.Equal("DIRECTOR_SCENE_PLAN_DUPLICATE_TARGET", execution.FailureCode);
        Assert.Contains("DIRECTOR_SCENE_PLAN_DUPLICATE_TARGET", execution.FailureReasonCodes ?? []);
        var persisted = await db.MovieScenes.SingleAsync(item => item.Id == seeded.SceneId);
        Assert.Equal("Original scene", persisted.Title);
        Assert.Equal("Original summary", persisted.Summary);
        Assert.Equal(1, await db.MovieScenes.CountAsync(item => item.MovieProjectId == seeded.MovieId));
    }

    private static async Task<SeededMovie> SeedAsync(TaslimDbContext db)
    {
        var now = DateTime.UtcNow;
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        var guideId = Guid.NewGuid();
        var guideRevisionId = Guid.NewGuid();
        var storyId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var sceneId = Guid.NewGuid();

        db.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = "director-scene-plan-owner",
            NormalizedUserName = "DIRECTOR-SCENE-PLAN-OWNER",
            DisplayName = "Director Scene Plan Owner",
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.Workspaces.Add(new Workspace
        {
            Id = workspaceId,
            Name = "Director Scene Plan Workspace",
            Slug = $"director-scene-plan-{Guid.NewGuid():N}",
            Type = WorkspaceType.Personal,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            Role = WorkspaceRole.Owner,
        });
        db.MovieProjects.Add(new MovieProject
        {
            Id = movieId,
            WorkspaceId = workspaceId,
            CreatedByUserId = userId,
            Title = "Duplicate target film",
            Description = "A deterministic scene-plan executor fixture.",
            DurationSeconds = 30,
            AspectRatio = "16:9",
            Language = "en",
            CreatedAt = now,
            UpdatedAt = now,
            Guide = new MovieContinuityGuide
            {
                Id = guideId,
                VisualLanguage = "Natural dawn light.",
                CameraLanguage = "A restrained tracking camera.",
                ColorAndLighting = "Warm dawn tones.",
                SoundAndNarration = "Wind over the bridge.",
                ContinuityRules = "The red scarf remains tied.",
                CurrentRevisionNumber = 1,
                LockedRevisionNumber = 1,
                LockedAt = now,
                LockedByUserId = userId,
                UpdatedAt = now,
                Revisions =
                [
                    new MovieGuideRevision
                    {
                        Id = guideRevisionId,
                        RevisionNumber = 1,
                        Status = MovieGuideRevisionStatuses.Locked,
                        CreatedByUserId = userId,
                        CreatedAt = now,
                    },
                ],
            },
        });
        db.MovieScenes.Add(new MovieScene
        {
            Id = sceneId,
            MovieProjectId = movieId,
            Sequence = 1,
            Title = "Original scene",
            Summary = "Original summary",
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.MovieStories.Add(new MovieStory
        {
            Id = storyId,
            MovieProjectId = movieId,
            WorkspaceId = workspaceId,
            CreatedByUserId = userId,
            ApprovalState = MovieStoryApprovalStates.Approved,
            CurrentRevisionId = revisionId,
            ApprovedRevisionId = revisionId,
            CreatedAt = now,
            UpdatedAt = now,
            Revisions =
            [
                new MovieStoryRevision
                {
                    Id = revisionId,
                    CreatedByUserId = userId,
                    RevisionNumber = 1,
                    Premise = "Mara crosses the bridge.",
                    Logline = "Mara chooses the safe path.",
                    Synopsis = "Mara protects the last seed at dawn.",
                    Treatment = "A quiet crossing resolves the conflict.",
                    Status = MovieStoryRevisionStatuses.Approved,
                    Authorship = MovieStoryAuthorship.Human,
                    CreatedAt = now,
                    UpdatedAt = now,
                    Scenes = [],
                },
            ],
        });

        await db.SaveChangesAsync();
        return new(userId, movieId, guideRevisionId, revisionId, sceneId);
    }

    private sealed record SeededMovie(Guid UserId, Guid MovieId, Guid GuideRevisionId, Guid RevisionId, Guid SceneId);

    private sealed class AcceptAllScenePlanValidator : IMovieDirectorScenePlanValidator
    {
        public DirectorScenePlanValidationResult Validate(
            DirectorScenePlanActionPayload payload,
            DirectorStoryBoundedContextDto context,
            string? expectedContextHash = null) => new(true, payload, []);
    }
}
