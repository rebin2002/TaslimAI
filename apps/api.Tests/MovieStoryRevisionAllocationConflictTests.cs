using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Controllers;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Same-base create contention on the atomic Story advance (T-1 of the #1276 review).
/// Two creators that observed the same base allocate the same next RevisionNumber, so on
/// PostgreSQL the loser is stopped by the unique (MovieStoryId, RevisionNumber) index at the
/// insert flush — before the guarded compare-and-set can decide — and two concurrent first
/// revisions race the unique story-per-project index instead. The advertised contract for both
/// is the conflict outcome (HTTP 409 MOVIE_STORY_REVISION_CONFLICT on the controller,
/// DIRECTOR_STORY_BASE_CHANGED through the Director executor), never a constraint fault.
///
/// The races are real: separate DbContext instances on separate connections, driven through the
/// production service, released together by a read barrier so both actors snapshot the base
/// before either writes (the same declared-interleave convention as
/// <c>MovieProductionKitTests.Concurrent_revision_creation_retries_the_losing_revision_number_allocation</c>).
/// Pre-fix, the contended round throws <see cref="DbUpdateException"/> where this expects the
/// conflict exception.
/// </summary>
public sealed class MovieStoryRevisionAllocationConflictTests
{
    private static readonly CancellationToken TestToken = CancellationToken.None;

    // ------------------------------------------------------------------------------------------------
    // Deterministic detector: exactly the two create-contention indexes match; nothing else does.
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Allocation_collision_detector_matches_only_the_story_create_contention()
    {
        var sqliteRevisionNumber = new DbUpdateException(
            "An error occurred while saving the entity changes.",
            new InvalidOperationException("SQLite Error 19: UNIQUE constraint failed: MovieStoryRevisions.MovieStoryId, MovieStoryRevisions.RevisionNumber"));
        var postgresRevisionNumber = new DbUpdateException(
            "An error occurred while saving the entity changes.",
            new InvalidOperationException("Npgsql.Schema.PostgresException: 23505: duplicate key value violates unique constraint \"IX_MovieStoryRevisions_MovieStoryId_RevisionNumber\""));
        var postgresFirstStory = new DbUpdateException(
            "An error occurred while saving the entity changes.",
            new InvalidOperationException("Npgsql.Schema.PostgresException: 23505: duplicate key value violates unique constraint \"IX_MovieStories_MovieProjectId\""));
        var unrelatedUnique = new DbUpdateException(
            "An error occurred while saving the entity changes.",
            new InvalidOperationException("SQLite Error 19: UNIQUE constraint failed: MovieScreenplayScenes.MovieStoryRevisionId, MovieScreenplayScenes.SceneIdentifier"));
        var foreignKeyViolation = new DbUpdateException(
            "An error occurred while saving the entity changes.",
            new InvalidOperationException("SQLite Error 19: FOREIGN KEY constraint failed"));

        Assert.True(MovieStoryService.IsRevisionAllocationUniqueViolation(sqliteRevisionNumber));
        Assert.True(MovieStoryService.IsRevisionAllocationUniqueViolation(postgresRevisionNumber));
        Assert.True(MovieStoryService.IsRevisionAllocationUniqueViolation(postgresFirstStory));
        // A constraint violation that is not the documented create contention must keep faulting.
        Assert.False(MovieStoryService.IsRevisionAllocationUniqueViolation(unrelatedUnique));
        Assert.False(MovieStoryService.IsRevisionAllocationUniqueViolation(foreignKeyViolation));
    }

    // ------------------------------------------------------------------------------------------------
    // Real contention on the production path (SQLite, runs everywhere): two writers from the same
    // base race the revision-number allocation; the loser must surface the conflict contract and
    // the shared request scope must stay free of the rolled-back create.
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Concurrent_same_base_creates_race_the_revision_number_allocation_and_the_loser_gets_the_conflict_contract()
    {
        var databasePath = TempDatabasePath("same-base");
        var barrier = new StorySnapshotBarrier();
        try
        {
            // The barrier must gate only the racing creates: a seeded base revision would
            // otherwise wait forever for a second snapshot reader that never arrives.
            var seedOptions = FileOptions(databasePath);
            var raceOptions = FileOptions(databasePath, barrier);
            await using (var seedDb = new TaslimDbContext(seedOptions))
            {
                await seedDb.Database.EnsureCreatedAsync(TestToken);
                var seeded = await SeedAsync(seedDb);
                // One committed base revision so both racing writers aim at revision number 2.
                var baseStory = await BaseService(seedDb).CreateRevisionAsync(seeded.UserId, seeded.MovieId, Request("Base premise."), TestToken);
                var baseId = baseStory!.CurrentRevisionId!.Value;

                var first = RaceCreateAsync(raceOptions, seeded, baseId);
                var second = RaceCreateAsync(raceOptions, seeded, baseId);
                var outcomes = await Task.WhenAll(first, second);
                await using var _firstDb = outcomes[0].Database;
                await using var _secondDb = outcomes[1].Database;

                // The barrier had to release two simultaneous snapshot readers for this to be a
                // real race, not two accidentally serialized requests.
                Assert.True(barrier.Arrivals >= 2, $"Expected at least two concurrent story snapshots, saw {barrier.Arrivals}.");
                Assert.Equal(1, outcomes.Count(outcome => outcome.Success));
                var loser = outcomes.Single(outcome => !outcome.Success);
                // Pre-fix this was the DbUpdateException behind the 500: never any exception but
                // the advertised conflict contract on the contended path.
                Assert.IsType<MovieStoryRevisionConflictException>(loser.Failure);

                // The losing create must not stay flushable in the shared request scope: this
                // mirrors the Director runner saving the same context for its action bookkeeping
                // after the executor returned the bounded outcome.
                Assert.Equal(0, await loser.Database!.SaveChangesAsync(TestToken));

                await using var verifyDb = new TaslimDbContext(seedOptions);
                var story = await verifyDb.MovieStories.AsNoTracking().SingleAsync(TestToken);
                var revisions = await verifyDb.MovieStoryRevisions.AsNoTracking()
                    .Where(item => item.MovieStoryId == story.Id).OrderBy(item => item.RevisionNumber).ToListAsync(TestToken);
                Assert.Equal(2, revisions.Count); // loser rolled back: no ghost revision row
                Assert.Equal(new[] { 1, 2 }, revisions.Select(item => item.RevisionNumber).ToArray());
                Assert.Equal(story.CurrentRevisionId, revisions[1].Id);
                Assert.Equal(MovieStoryRevisionStatuses.Draft, revisions[1].Status);
                Assert.Equal(MovieStoryRevisionStatuses.Superseded, revisions[0].Status); // replaced by the winner's create
            }
        }
        finally
        {
            TryDelete(databasePath);
        }
    }

    [Fact]
    public async Task Concurrent_first_revisions_race_the_story_row_and_the_loser_gets_the_conflict_contract()
    {
        var databasePath = TempDatabasePath("first-revision");
        var barrier = new StorySnapshotBarrier();
        try
        {
            var options = FileOptions(databasePath, barrier);
            await using (var seedDb = new TaslimDbContext(options))
            {
                await seedDb.Database.EnsureCreatedAsync(TestToken);
                var seeded = await SeedAsync(seedDb);

                var first = RaceCreateAsync(options, seeded, parentRevisionId: null);
                var second = RaceCreateAsync(options, seeded, parentRevisionId: null);
                var outcomes = await Task.WhenAll(first, second);
                await using var _firstDb = outcomes[0].Database;
                await using var _secondDb = outcomes[1].Database;

                Assert.True(barrier.Arrivals >= 2, $"Expected at least two concurrent story snapshots, saw {barrier.Arrivals}.");
                Assert.Equal(1, outcomes.Count(outcome => outcome.Success));
                Assert.IsType<MovieStoryRevisionConflictException>(outcomes.Single(outcome => !outcome.Success).Failure);
                Assert.Equal(0, await outcomes.Single(outcome => !outcome.Success).Database!.SaveChangesAsync(TestToken));

                await using var verifyDb = new TaslimDbContext(FileOptions(databasePath));
                Assert.Equal(1, await verifyDb.MovieStories.CountAsync(TestToken)); // winner's story, loser rolled back
                var story = await verifyDb.MovieStories.AsNoTracking().SingleAsync(TestToken);
                Assert.Equal(1, await verifyDb.MovieStoryRevisions.CountAsync(item => item.MovieStoryId == story.Id, TestToken));
                Assert.NotNull(story.CurrentRevisionId);
            }
        }
        finally
        {
            TryDelete(databasePath);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // The same race against the production engine. PostgreSQL-gated, same convention as
    // GeneratedAssetNameTruncationTests: a private schema, dropped afterwards; a no-op without
    // ConnectionStrings__Postgres (the CI api job provides it).
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Contended_same_base_create_on_postgres_maps_the_unique_violation_to_the_conflict_contract()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var schema = $"story_cas_{Guid.NewGuid():N}";
        await using (var admin = new Npgsql.NpgsqlConnection(connectionString))
        {
            await admin.OpenAsync(TestToken);
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE SCHEMA \"{schema}\"";
            await create.ExecuteNonQueryAsync(TestToken);
        }

        try
        {
            var noBarrier = new DbContextOptionsBuilder<TaslimDbContext>()
                .UseNpgsql(new Npgsql.NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema }.ConnectionString)
                .Options;
            // Only the two racing writers carry the barrier; the base-revision seed create must not
            // wait for a second snapshot reader that never arrives on that single path.
            var barrier = new StorySnapshotBarrier();
            var raceOptions = new DbContextOptionsBuilder<TaslimDbContext>()
                .UseNpgsql(new Npgsql.NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema }.ConnectionString)
                .AddInterceptors(barrier)
                .Options;
            await using var seedDb = new TaslimDbContext(noBarrier);
            await seedDb.Database.EnsureCreatedAsync(TestToken);
            var seeded = await SeedAsync(seedDb);
            var baseStory = await BaseService(seedDb).CreateRevisionAsync(seeded.UserId, seeded.MovieId, Request("Base premise for the postgres race."), TestToken);
            var baseId = baseStory!.CurrentRevisionId!.Value;

            // Both writers commit against the same base on two real connections. On PostgreSQL the
            // loser's INSERT into the unique (MovieStoryId, RevisionNumber) index takes row-lock
            // precedence over the compare-and-set, which is exactly where this path used to fault.
            var results = await Task.WhenAll(
                RaceCreateAsync(raceOptions, seeded, baseId),
                RaceCreateAsync(raceOptions, seeded, baseId));
            foreach (var result in results)
            {
                if (result.Database is not null) await result.Database.DisposeAsync();
            }
            Assert.True(barrier.Arrivals >= 2, $"Expected at least two concurrent story snapshots, saw {barrier.Arrivals}.");

            // Both snapshot readers were released together, so both allocated the same revision
            // number: this is the contended shape by construction. Exactly one writer wins, and
            // the other must surface the conflict contract — never the DbUpdateException that
            // reached the client as a 500 before the mapping.
            Assert.Equal(1, results.Count(result => result.Success));
            var loser = Assert.Single(results, result => !result.Success);
            Assert.IsType<MovieStoryRevisionConflictException>(loser.Failure);

            await using var verifyDb = new TaslimDbContext(noBarrier);
            var story = await verifyDb.MovieStories.AsNoTracking().SingleAsync(TestToken);
            var revisions = await verifyDb.MovieStoryRevisions.AsNoTracking()
                .Where(item => item.MovieStoryId == story.Id).OrderBy(item => item.RevisionNumber).ToListAsync(TestToken);
            Assert.Equal(revisions.Count, revisions.Select(item => item.RevisionNumber).Distinct().Count()); // no duplicated numbers
            Assert.Equal(story.CurrentRevisionId, revisions[^1].Id);
            Assert.Equal(MovieStoryRevisionStatuses.Draft, revisions[^1].Status);
            // The rolled-back loser persisted nothing: exactly one winner revision on top of the
            // base, no ghost rows, contiguous numbers.
            Assert.Equal(2, revisions.Count);
            Assert.Equal(new[] { 1, 2 }, revisions.Select(item => item.RevisionNumber).ToArray());
            Assert.Equal(MovieStoryRevisionStatuses.Superseded, revisions[0].Status);
        }
        finally
        {
            await using var admin = new Npgsql.NpgsqlConnection(connectionString);
            await admin.OpenAsync(TestToken);
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
            await drop.ExecuteNonQueryAsync(TestToken);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // The advertised mappings that consume the conflict exception, asserted directly.
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_story_revision_maps_the_conflict_to_409_with_the_advertised_code()
    {
        var userId = Guid.NewGuid();
        var controller = new MovieStudioController(
            null!, null!, null!, null!, new ConflictThrowingStoryService(), null!, null!, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], authenticationType: "Test")),
                },
            },
        };

        var response = await controller.CreateStoryRevision(Guid.NewGuid(), Request("Conflicting premise."), TestToken);

        var result = Assert.IsType<ObjectResult>(response);
        Assert.Equal(409, result.StatusCode);
        var envelope = Assert.IsType<ErrorEnvelope>(result.Value);
        Assert.Equal("MOVIE_STORY_REVISION_CONFLICT", envelope.Error.Code);
    }

    [Fact]
    public async Task Director_apply_reports_the_bounded_base_changed_code_when_the_create_conflicts()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestToken);
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        await using var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync(TestToken);
        var seeded = await SeedAsync(db);
        var story = await BaseService(db).CreateRevisionAsync(seeded.UserId, seeded.MovieId, Request("Director base."), TestToken);
        var baseId = story!.CurrentRevisionId!.Value;

        // A locked guide revision is the Director context precondition; the validator is a stub
        // that passes the payload through unchanged, so the executor's create is the only thing
        // this test exercises past the pre-apply base check.
        await using (var guideDb = new TaslimDbContext(options))
        {
            var now = DateTime.UtcNow;
            var guide = new MovieContinuityGuide
            {
                Id = Guid.NewGuid(), MovieProjectId = seeded.MovieId, LockedRevisionNumber = 1, UpdatedAt = now,
            };
            guideDb.MovieContinuityGuides.Add(guide);
            guideDb.MovieGuideRevisions.Add(new MovieGuideRevision
            {
                Id = Guid.NewGuid(),
                MovieContinuityGuideId = guide.Id,
                RevisionNumber = 1,
                Status = MovieGuideRevisionStatuses.Locked,
                CreatedByUserId = seeded.UserId,
                CreatedAt = now,
            });
            await guideDb.SaveChangesAsync(TestToken);
        }

        var payload = new DirectorStoryActionPayload(
            DirectorStoryActionTypes.ImproveLogline, baseId,
            "Premise.", "Improved logline.", "Synopsis.", "Treatment.",
            null, null, null, null, [], []);
        var action = new DirectorAction
        {
            Id = Guid.NewGuid(),
            MovieProjectId = seeded.MovieId,
            WorkspaceId = seeded.WorkspaceId,
            ActionType = DirectorActionTypes.StoryAssistance,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(payload, DirectorJson.Options),
            Status = DirectorActionStatuses.Ready,
            CreatedAt = DateTime.UtcNow,
        };
        var executor = new MovieDirectorStoryActionExecutor(
            db, new ConflictThrowingStoryService(),
            new MovieDirectorContextAssembler(db, new MovieWorldContinuityProjector(db), new MovieStoryScenePlanningContextAssembler(db)),
            new PassThroughCreativeValidator());

        var execution = await executor.ExecuteAsync(action, seeded.UserId, TestToken);

        Assert.False(execution.Succeeded);
        Assert.Equal("DIRECTOR_STORY_BASE_CHANGED", execution.FailureCode);
    }

    // ------------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------------

    private sealed record RaceOutcome(bool Success, Exception? Failure, MovieStoryDto? Result, TaslimDbContext? Database);

    private static async Task<RaceOutcome> RaceCreateAsync(
        DbContextOptions<TaslimDbContext> options, Harness.Seeded seeded, Guid? parentRevisionId)
    {
        // Deliberately not disposed here: the caller asserts the residue probe on the losing
        // scope after the race, mirroring the Director runner saving the shared context
        // post-conflict. Callers dispose both contexts when done.
        var db = new TaslimDbContext(options);
        try
        {
            var result = await BaseService(db).CreateRevisionAsync(seeded.UserId, seeded.MovieId, Request("Premise " + Guid.NewGuid().ToString("N"), parentRevisionId), TestToken);
            return new RaceOutcome(true, null, result, db);
        }
        catch (Exception exception)
        {
            return new RaceOutcome(false, exception, null, db);
        }
    }

    private static DbContextOptions<TaslimDbContext> FileOptions(string databasePath, DbCommandInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<TaslimDbContext>()
            .UseSqlite($"Data Source={databasePath};Default Timeout=30");
        if (interceptor is not null) builder = builder.AddInterceptors(interceptor);
        return builder.Options;
    }

    private static string TempDatabasePath(string label) => Path.Combine(Path.GetTempPath(), $"taslim-story-allocation-{label}-{Guid.NewGuid():N}.db");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }

    private static MovieStoryService BaseService(TaslimDbContext db) =>
        new(db, new MovieCollaborationAccess(db, new WorkspaceAccessService(db)));

    private sealed class StorySnapshotBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource<bool> released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;

        public int Arrivals => arrivals;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            // Gate on the snapshot read only: the MovieStories join to MovieStoryRevisions that
            // CreateRevisionAsync performs before allocating the next revision number.
            if (command.CommandText.Contains("MovieStories", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("MovieStoryRevisions", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("MovieProjectId", StringComparison.OrdinalIgnoreCase))
            {
                if (Interlocked.Increment(ref arrivals) >= 2) released.TrySetResult(true);
                await released.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }

            return result;
        }
    }

    private sealed class ConflictThrowingStoryService : IMovieStoryService
    {
        public Task<MovieStoryDto?> GetAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken) => Task.FromResult<MovieStoryDto?>(null);
        public Task<IReadOnlyList<MovieStoryRevisionSummaryDto>?> ListRevisionsAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MovieStoryRevisionSummaryDto>?>(null);
        public Task<MovieStoryDto?> CreateRevisionAsync(Guid userId, Guid movieProjectId, MovieStoryRevisionRequest request, CancellationToken cancellationToken) =>
            throw new MovieStoryRevisionConflictException("The Story current revision changed while this revision was being created. Reload the Story and propose again.");
        public Task<MovieStoryRevisionDto?> UpdateDraftAsync(Guid userId, Guid movieProjectId, Guid revisionId, MovieStoryRevisionRequest request, CancellationToken cancellationToken) => Task.FromResult<MovieStoryRevisionDto?>(null);
        public Task<MovieStoryRevisionDto?> GetRevisionAsync(Guid userId, Guid movieProjectId, Guid revisionId, CancellationToken cancellationToken) => Task.FromResult<MovieStoryRevisionDto?>(null);
        public Task<MovieStoryRevisionDto?> SubmitAsync(Guid userId, Guid movieProjectId, Guid revisionId, CancellationToken cancellationToken) => Task.FromResult<MovieStoryRevisionDto?>(null);
        public Task<MovieStoryRevisionDto?> ApproveAsync(Guid userId, Guid movieProjectId, Guid revisionId, CancellationToken cancellationToken) => Task.FromResult<MovieStoryRevisionDto?>(null);
        public Task<MovieStoryRevisionDto?> RejectAsync(Guid userId, Guid movieProjectId, Guid revisionId, string reason, CancellationToken cancellationToken) => Task.FromResult<MovieStoryRevisionDto?>(null);
    }

    private sealed class PassThroughCreativeValidator : IMovieDirectorCreativeOutputValidator
    {
        public DirectorCreativeOutputValidationResult ValidateAndRepair(DirectorStoryActionPayload output, DirectorStoryBoundedContextDto context) =>
            new(DirectorCreativeValidationOutcome.Passed, output, []);
        public DirectorCreativeOutputValidationResult ValidateJson(string? serializedOutput, DirectorStoryBoundedContextDto context) =>
            new(DirectorCreativeValidationOutcome.Passed, null, []);
    }

    private static MovieStoryRevisionRequest Request(string premise, Guid? parentRevisionId = null) => new()
    {
        Premise = premise,
        Logline = "Mara carries one message through the blackout.",
        Synopsis = "Mara reaches the harbor before dawn.",
        Treatment = "The harbor crossing becomes a test of resolve.",
        Authorship = MovieStoryAuthorship.Human,
        ParentRevisionId = parentRevisionId,
        ChangeSummary = "Allocation conflict regression",
        Scenes = [],
    };

    private static async Task<Harness.Seeded> SeedAsync(TaslimDbContext db) => await Harness.SeedAsync(db);

    // Reuses the exact fixture shape of MovieStoryRevisionConcurrencyTests so both files seed
    // identically; kept private per test file by repository convention.
    private static class Harness
    {
        public static async Task<Seeded> SeedAsync(TaslimDbContext db)
        {
            var now = DateTime.UtcNow;
            var userId = Guid.NewGuid();
            var workspaceId = Guid.NewGuid();
            var movieId = Guid.NewGuid();
            db.Users.Add(new ApplicationUser
            {
                Id = userId,
                UserName = $"story-allocation-{userId:N}",
                NormalizedUserName = "STORY-ALLOCATION",
                DisplayName = "Story Allocation Conflict Owner",
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.Workspaces.Add(new Workspace
            {
                Id = workspaceId,
                Name = "Story Allocation Workspace",
                Slug = $"story-allocation-{Guid.NewGuid():N}",
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
                Title = "Allocation conflict film",
                Description = "A deterministic same-base create contention fixture.",
                DurationSeconds = 30,
                AspectRatio = "16:9",
                Language = "en",
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.MovieTeamMembers.Add(new MovieTeamMember
            {
                Id = Guid.NewGuid(), MovieProjectId = movieId, UserId = userId,
                Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now, UpdatedAt = now,
            });
            await db.SaveChangesAsync();
            return new Seeded(userId, workspaceId, movieId);
        }

        public sealed record Seeded(Guid UserId, Guid WorkspaceId, Guid MovieId);
    }
}
