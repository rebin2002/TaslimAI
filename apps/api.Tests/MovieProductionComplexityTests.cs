using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Taslim.Api.Authorization;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieProductionComplexityTests
{
    [Fact]
    public void Low_profile_is_valid_and_explainable()
    {
        var result = MovieProductionComplexityValidator.Validate(Profile(0));

        Assert.True(result.IsValid);
        Assert.NotNull(result.Profile);
        Assert.Equal(0, result.Profile!.OverallScore);
        Assert.Equal(MovieProductionComplexityBands.Low, result.Profile.OverallBand);
        Assert.Equal(11, result.Profile.Reasons.Count);
        Assert.All(result.Profile.Reasons, reason => Assert.Equal(MovieProductionComplexityBands.Low, reason.Band));
    }

    [Fact]
    public void High_profile_is_valid_and_explainable()
    {
        var request = Profile(100);
        request.DeclaredOverallBand = MovieProductionComplexityBands.High;
        request.Evidence =
        [
            new(MovieProductionComplexityDimensions.MotionComplexity, "Fast subject movement", "The subject crosses the frame while the camera tracks."),
            new(MovieProductionComplexityDimensions.VfxComplexity, "Visible atmospheric effect", "A dense practical-and-digital storm is required."),
        ];

        var result = MovieProductionComplexityValidator.Validate(request);

        Assert.True(result.IsValid);
        Assert.Equal(100, result.Profile!.OverallScore);
        Assert.Equal(MovieProductionComplexityBands.High, result.Profile.OverallBand);
        Assert.Contains(result.Profile.Reasons, reason => reason.Dimension == MovieProductionComplexityDimensions.MotionComplexity && reason.Evidence!.Contains("tracks", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_dimension_is_rejected_instead_of_defaulting_to_low()
    {
        var request = Profile(20);
        request.DialogueLipSyncDependency = null;

        var result = MovieProductionComplexityValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Findings, finding => finding.Code == MovieProductionComplexityValidationCodes.MissingField && finding.Field == MovieProductionComplexityDimensions.DialogueLipSyncDependency);
    }

    [Fact]
    public void Malformed_json_is_rejected_deterministically()
    {
        var result = MovieProductionComplexityValidator.ValidateJson("{\"motionComplexity\":not-json}");

        Assert.False(result.IsValid);
        Assert.Contains(MovieProductionComplexityValidationCodes.MalformedJson, result.ReasonCodes);
    }

    [Fact]
    public void Contradictory_declared_band_is_rejected()
    {
        var request = Profile(100);
        request.DeclaredOverallBand = MovieProductionComplexityBands.Low;

        var result = MovieProductionComplexityValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Findings, finding => finding.Code == MovieProductionComplexityValidationCodes.ContradictoryProfile);
    }

    [Fact]
    public void Canonical_profile_round_trips_without_provider_or_cost_fields()
    {
        var result = MovieProductionComplexityValidator.Validate(Profile(55));
        var json = JsonSerializer.Serialize(result.Profile, MovieProductionComplexityValidator.JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<MovieProductionComplexityProfile>(json, MovieProductionComplexityValidator.JsonOptions);

        Assert.NotNull(roundTrip);
        Assert.Equal(result.Profile!.OverallScore, roundTrip!.OverallScore);
        Assert.DoesNotContain("provider", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cost", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("resolution", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_retries_a_transient_complexity_version_conflict()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"taslim-movie-complexity-{Guid.NewGuid():N}.db");
        var conflict = new ComplexityVersionConflictInterceptor();
        try
        {
            var seedOptions = new DbContextOptionsBuilder<TaslimDbContext>()
                .UseSqlite($"Data Source={databasePath};Default Timeout=30")
                .Options;
            await using (var seedDb = new TaslimDbContext(seedOptions))
            {
                await seedDb.Database.EnsureCreatedAsync();
                var seed = await SeedAsync(seedDb);
                seedDb.MovieProductionComplexityAssessments.Add(new MovieProductionComplexityAssessment
                {
                    Id = Guid.NewGuid(), MovieShotId = seed.ShotId, Version = 1,
                    Source = MovieProductionComplexitySources.Manual, ProfileJson = "{}",
                    OverallScore = 0, OverallBand = MovieProductionComplexityBands.Low,
                    CreatedByUserId = seed.UserId, CreatedAt = DateTime.UtcNow,
                });
                await seedDb.SaveChangesAsync();
            }

            await using var db = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>()
                .UseSqlite($"Data Source={databasePath};Default Timeout=30")
                .AddInterceptors(conflict)
                .Options);
            var seedData = await db.MovieProjects.Select(project => new
            {
                ProjectId = project.Id,
                ShotId = project.Scenes.SelectMany(scene => scene.Shots).Select(shot => shot.Id).First(),
                UserId = project.CreatedByUserId,
            }).SingleAsync();
            var service = new MovieProductionComplexityService(
                db,
                new MovieCollaborationAccess(db, new WorkspaceAccessService(db)));

            var result = await service.SaveAsync(seedData.UserId, seedData.ShotId, Profile(55));

            Assert.NotNull(result);
            Assert.Equal(2, result!.Version);
            Assert.Equal(1, conflict.InjectedConflicts);
            Assert.Equal(2, await db.MovieProductionComplexityAssessments.CountAsync(item => item.MovieShotId == seedData.ShotId));
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public void Version_retry_classifier_does_not_swallow_unrelated_unique_errors()
    {
        var versionConflict = new DbUpdateException(
            "complexity version conflict",
            new InvalidOperationException("SQLite Error 19: 'UNIQUE constraint failed: MovieProductionComplexityAssessments.MovieShotId, MovieProductionComplexityAssessments.Version'."));
        var postgresVersionConflict = new DbUpdateException(
            "complexity PostgreSQL version conflict",
            new InvalidOperationException("23505: duplicate key value violates unique constraint \"IX_MovieProductionComplexityAssessments_MovieShotId_Version\""));
        var otherConflict = new DbUpdateException(
            "other conflict",
            new InvalidOperationException("SQLite Error 19: 'UNIQUE constraint failed: MovieProductionComplexityAssessments.MovieShotId, MovieProductionComplexityAssessments.Id'."));

        Assert.True(MovieProductionComplexityService.IsVersionUniqueViolation(versionConflict));
        Assert.True(MovieProductionComplexityService.IsVersionUniqueViolation(postgresVersionConflict));
        Assert.False(MovieProductionComplexityService.IsVersionUniqueViolation(otherConflict));
    }

    private static async Task<(Guid UserId, Guid ProjectId, Guid ShotId)> SeedAsync(TaslimDbContext db)
    {
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var sceneId = Guid.NewGuid();
        var shotId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "complexity-owner", NormalizedUserName = "COMPLEXITY-OWNER", DisplayName = "Complexity Owner", CreatedAt = now, UpdatedAt = now });
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Complexity Workspace", Slug = $"complexity-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner, JoinedAt = now });
        db.MovieProjects.Add(new MovieProject { Id = projectId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = "Complexity Movie", Description = "A complexity fixture.", DurationSeconds = 30, CreatedAt = now, UpdatedAt = now, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now } });
        db.MovieTeamMembers.Add(new MovieTeamMember { Id = Guid.NewGuid(), MovieProjectId = projectId, UserId = userId, Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now, UpdatedAt = now });
        db.MovieScenes.Add(new MovieScene { Id = sceneId, MovieProjectId = projectId, Sequence = 1, Title = "Opening", Summary = "Complexity opening.", CreatedAt = now, UpdatedAt = now });
        db.MovieShots.Add(new MovieShot { Id = shotId, MovieSceneId = sceneId, Sequence = 1, Description = "Complexity shot.", CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        return (userId, projectId, shotId);
    }

    private sealed class ComplexityVersionConflictInterceptor : DbCommandInterceptor
    {
        public int InjectedConflicts { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            ThrowOnFirstComplexityInsert(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowOnFirstComplexityInsert(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            ThrowOnFirstComplexityInsert(command);
            return result;
        }

        private void ThrowOnFirstComplexityInsert(DbCommand command)
        {
            if (InjectedConflicts != 0
                || !command.CommandText.Contains("MovieProductionComplexityAssessments", StringComparison.OrdinalIgnoreCase)
                || !command.CommandText.Contains("INSERT", StringComparison.OrdinalIgnoreCase)) return;

            InjectedConflicts++;
            throw new DbUpdateException(
                "synthetic complexity version conflict",
                new InvalidOperationException("SQLite Error 19: 'UNIQUE constraint failed: MovieProductionComplexityAssessments.MovieShotId, MovieProductionComplexityAssessments.Version'."));
        }
    }

    private static MovieProductionComplexityProfileRequest Profile(int value) => new()
    {
        MotionComplexity = value,
        CameraComplexity = value,
        FaceImportance = value,
        HandBodyInteractionComplexity = value,
        FineDetailImportance = value,
        EnvironmentComplexity = value,
        VfxComplexity = value,
        ContinuitySensitivity = value,
        TextSignageSensitivity = value,
        DialogueLipSyncDependency = value,
        DurationComplexity = value,
    };
}
