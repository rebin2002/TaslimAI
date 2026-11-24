using System.Data.Common;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Independent-review follow-ups on the character-sheet lane. (1) ApproveAsync/LockAsync/UnlockAsync
/// load the sheet through the AsNoTracking Query() helper, mutate detached entities and call
/// SaveChanges without attaching, so approval/lock state never reaches the database; the returned
/// DTO is in-memory fiction. This is pre-existing on main and carried unchanged by #1229.
/// (2) SaveDraftAsync commits the version row with SaveChangesAsync and only afterwards advances the
/// pointer/status with a separate ExecuteUpdateAsync filtered only by Id and monotonic pointer, with
/// no explicit transaction between the two: a failure or cancellation in that window leaves a
/// committed orphan version the canonical pointer never adopts, and the unguarded second write
/// silently erases a lock or approval a concurrent writer committed in the window. The concurrency
/// legs run on real PostgreSQL because SQLite single-writer locking cannot interleave an open write
/// transaction with a second connection.
/// </summary>
public sealed class MovieCharacterProductionSheetPersistenceAtomicityTests
{
    private const string Approved = MovieCharacterProductionSheetStatuses.Approved;
    private const string Locked = MovieCharacterProductionSheetStatuses.Locked;
    private const string Draft = MovieCharacterProductionSheetStatuses.Draft;

    internal static MovieCharacterProductionSheetService CreateService(TaslimDbContext db) =>
        new(db, new MovieCollaborationAccess(db, new WorkspaceAccessService(db)));

    internal sealed record Seed(Guid UserId, Guid WorkspaceId, Guid CharacterId);

    internal static async Task<Seed> SeedAsync(TaslimDbContext db)
    {
        var now = DateTime.UtcNow;
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var movieProjectId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = $"persist-owner-{Guid.NewGuid():N}",
            NormalizedUserName = "PERSIST-OWNER-" + Guid.NewGuid().ToString("N").ToUpperInvariant(),
            Email = "persist-owner@example.com",
            NormalizedEmail = "PERSIST-OWNER-" + Guid.NewGuid().ToString("N").ToUpperInvariant() + "@EXAMPLE.COM",
            DisplayName = "Persistence Fixture Owner",
            SecurityStamp = Guid.NewGuid().ToString(),
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.Workspaces.Add(new Workspace
        {
            Id = workspaceId, Name = "Persistence Workspace", Slug = $"persistence-{Guid.NewGuid():N}",
            Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now,
        });
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner, JoinedAt = now,
        });
        db.MovieProjects.Add(new MovieProject
        {
            Id = movieProjectId, WorkspaceId = workspaceId, CreatedByUserId = userId,
            Title = "Persistence Film", Description = "Fixture", DurationSeconds = 30,
            CreatedAt = now, UpdatedAt = now,
            Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now },
            Characters =
            [
                new MovieCharacter { Id = characterId, Name = "Mara", Description = "Fixture character.", CreatedAt = now, UpdatedAt = now },
            ],
        });
        db.MovieTeamMembers.Add(new MovieTeamMember
        {
            Id = Guid.NewGuid(), MovieProjectId = movieProjectId, UserId = userId,
            Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return new Seed(userId, workspaceId, characterId);
    }

    /// <summary>Creates the five reference assets a complete sheet needs and returns their ids.</summary>
    internal static async Task<Guid[]> SeedAssetsAsync(TaslimDbContext db, Seed seed)
    {
        var now = DateTime.UtcNow;
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var (id, name) in ids.Zip(new[] { "face", "body", "front", "side", "back" }))
        {
            db.Assets.Add(new Asset
            {
                Id = id, WorkspaceId = seed.WorkspaceId, CreatedByUserId = seed.UserId,
                Name = name, AssetType = AssetTypes.File, Status = AssetStatus.Active,
                CreatedAt = now, UpdatedAt = now,
            });
        }
        await db.SaveChangesAsync();
        return ids;
    }

    internal static MovieCharacterProductionSheetRequest CompleteRequest(Guid[] assetIds, string note) => new()
    {
        CanonicalIdentityFaceAssetId = assetIds[0],
        BodyReferenceAssetId = assetIds[1],
        FrontReferenceAssetId = assetIds[2],
        SideReferenceAssetId = assetIds[3],
        BackReferenceAssetId = assetIds[4],
        ProvenanceNote = note,
    };

    /// <summary>
    /// Runs an async hook exactly once, immediately before the next UPDATE that targets the sheet
    /// table (whether it comes from a SaveChanges pipeline statement or an ExecuteUpdateAsync call),
    /// and waits for it. Used to open a deterministic race window in the production write path.
    /// </summary>
    private sealed class SheetUpdateGateInterceptor : DbCommandInterceptor
    {
        private readonly Func<ValueTask> hook;
        private int armed;
        public SheetUpdateGateInterceptor(Func<ValueTask> hook) => this.hook = hook;

        private static bool IsSheetUpdate(DbCommand command) =>
            command.CommandText.StartsWith("UPDATE \"MovieCharacterProductionSheets\"", StringComparison.Ordinal);

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            if (IsSheetUpdate(command) && Interlocked.Exchange(ref armed, 1) == 0) hook().AsTask().GetAwaiter().GetResult();
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (IsSheetUpdate(command) && Interlocked.Exchange(ref armed, 1) == 0) await hook();
            return result;
        }
    }

    /// <summary>Seeds a persisted approved/locked DB state through a tracked context write, so the
    /// lock/unlock facts do not depend on the (broken) service transitions themselves.</summary>
    internal static async Task SeedPersistedStateAsync(TaslimDbContext db, Guid sheetId, Guid userId, string status)
    {
        var sheet = await db.MovieCharacterProductionSheets.FirstAsync(item => item.Id == sheetId);
        var versions = await db.MovieCharacterProductionSheetVersions
            .Where(item => item.MovieCharacterProductionSheetId == sheetId && item.VersionNumber == sheet.CurrentVersionNumber)
            .ToListAsync();
        var now = DateTime.UtcNow;
        sheet.Status = status;
        sheet.ApprovedAt = now;
        sheet.ApprovedByUserId = userId;
        sheet.LockedAt = status == Locked ? now : null;
        sheet.LockedByUserId = status == Locked ? userId : null;
        sheet.UpdatedAt = now;
        foreach (var version in versions)
        {
            version.Status = status;
            version.ApprovedAt = now;
            version.ApprovedByUserId = userId;
            if (status == Locked) { version.LockedAt = now; version.LockedByUserId = userId; }
        }
        await db.SaveChangesAsync();
    }

    private static TException FindException<TException>(Exception exception) where TException : Exception =>
        exception switch
        {
            TException match => match,
            Exception when exception.InnerException is not null => FindException<TException>(exception.InnerException),
            _ => throw new InvalidOperationException($"Expected a {typeof(TException).Name} in the exception chain but found {exception.GetType().Name}: {exception.Message}"),
        };

    // ---------------------------------------------------------------------------------------------
    // Persistence legs: state transitions must survive the request boundary (separate context read).
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Approve_persists_approved_state_for_an_independent_context()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"taslim-sheet-persist-{Guid.NewGuid():N}.db");
        try
        {
            await using var setup = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>()
                .UseSqlite($"Data Source={databasePath};Default Timeout=30").Options);
            await setup.Database.EnsureCreatedAsync();
            var seed = await SeedAsync(setup);
            var assets = await SeedAssetsAsync(setup, seed);
            var draft = await CreateService(setup).SaveDraftAsync(seed.UserId, seed.CharacterId, CompleteRequest(assets, "to-approve"));
            Assert.NotNull(draft);
            var sheetId = draft!.Id;

            var approved = await CreateService(setup).ApproveAsync(seed.UserId, sheetId);
            Assert.NotNull(approved);

            await using var verify = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>()
                .UseSqlite($"Data Source={databasePath};Default Timeout=30").Options);
            var stored = await verify.MovieCharacterProductionSheets.AsNoTracking().FirstAsync(item => item.Id == sheetId);
            Assert.Equal(Approved, stored.Status);
            Assert.NotNull(stored.ApprovedAt);
            Assert.Equal(seed.UserId, stored.ApprovedByUserId);
            var storedVersion = await verify.MovieCharacterProductionSheetVersions.AsNoTracking()
                .FirstAsync(item => item.MovieCharacterProductionSheetId == sheetId && item.VersionNumber == 1);
            Assert.Equal(Approved, storedVersion.Status);
            Assert.NotNull(storedVersion.ApprovedAt);

            // The API read path (fresh context, fresh service) must agree with the row state.
            var seen = await CreateService(verify).GetAsync(seed.UserId, seed.CharacterId);
            Assert.Equal(Approved, seen!.Status);
        }
        finally { if (File.Exists(databasePath)) File.Delete(databasePath); }
    }

    [Fact]
    public async Task Lock_persists_locked_state_for_an_independent_context()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"taslim-sheet-persist-{Guid.NewGuid():N}.db");
        try
        {
            await using var setup = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>()
                .UseSqlite($"Data Source={databasePath};Default Timeout=30").Options);
            await setup.Database.EnsureCreatedAsync();
            var seed = await SeedAsync(setup);
            var assets = await SeedAssetsAsync(setup, seed);
            var draft = await CreateService(setup).SaveDraftAsync(seed.UserId, seed.CharacterId, CompleteRequest(assets, "to-lock"));
            Assert.NotNull(draft);
            var sheetId = draft!.Id;

            await SeedPersistedStateAsync(setup, sheetId, seed.UserId, Approved);

            var locked = await CreateService(setup).LockAsync(seed.UserId, sheetId);
            Assert.NotNull(locked);

            await using var verify = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>()
                .UseSqlite($"Data Source={databasePath};Default Timeout=30").Options);
            var stored = await verify.MovieCharacterProductionSheets.AsNoTracking().FirstAsync(item => item.Id == sheetId);
            Assert.Equal(Locked, stored.Status);
            Assert.NotNull(stored.LockedAt);
            Assert.Equal(seed.UserId, stored.LockedByUserId);
            var storedVersion = await verify.MovieCharacterProductionSheetVersions.AsNoTracking()
                .FirstAsync(item => item.MovieCharacterProductionSheetId == sheetId && item.VersionNumber == 1);
            Assert.Equal(Locked, storedVersion.Status);
        }
        finally { if (File.Exists(databasePath)) File.Delete(databasePath); }
    }

    [Fact]
    public async Task Unlock_persists_cleared_state_for_an_independent_context()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"taslim-sheet-persist-{Guid.NewGuid():N}.db");
        try
        {
            await using var setup = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>()
                .UseSqlite($"Data Source={databasePath};Default Timeout=30").Options);
            await setup.Database.EnsureCreatedAsync();
            var seed = await SeedAsync(setup);
            var assets = await SeedAssetsAsync(setup, seed);
            var draft = await CreateService(setup).SaveDraftAsync(seed.UserId, seed.CharacterId, CompleteRequest(assets, "to-unlock"));
            Assert.NotNull(draft);
            var sheetId = draft!.Id;

            await SeedPersistedStateAsync(setup, sheetId, seed.UserId, Locked);

            var unlocked = await CreateService(setup).UnlockAsync(seed.UserId, sheetId);
            Assert.NotNull(unlocked);

            await using var verify = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>()
                .UseSqlite($"Data Source={databasePath};Default Timeout=30").Options);
            var stored = await verify.MovieCharacterProductionSheets.AsNoTracking().FirstAsync(item => item.Id == sheetId);
            Assert.Equal(Draft, stored.Status);
            Assert.Null(stored.LockedAt);
            Assert.Null(stored.LockedByUserId);
            Assert.Null(stored.ApprovedAt);
            var storedVersion = await verify.MovieCharacterProductionSheetVersions.AsNoTracking()
                .FirstAsync(item => item.MovieCharacterProductionSheetId == sheetId && item.VersionNumber == 1);
            Assert.Equal(Draft, storedVersion.Status);
            Assert.Null(storedVersion.LockedAt);
        }
        finally { if (File.Exists(databasePath)) File.Delete(databasePath); }
    }

    // ---------------------------------------------------------------------------------------------
    // Rollback legs: a version must never be committable without its pointer advance.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Failed_pointer_update_leaves_no_orphan_version_and_keeps_the_pointer()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"taslim-sheet-atomic-{Guid.NewGuid():N}.db");
        try
        {
            await using var setup = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>()
                .UseSqlite($"Data Source={databasePath};Default Timeout=30").Options);
            await setup.Database.EnsureCreatedAsync();
            var seed = await SeedAsync(setup);
            var assets = await SeedAssetsAsync(setup, seed);
            var draft = await CreateService(setup).SaveDraftAsync(seed.UserId, seed.CharacterId, CompleteRequest(assets, "first"));
            Assert.NotNull(draft);
            var sheetId = draft!.Id;

            var boom = new InvalidOperationException("injected pointer-update failure");
            var gate = new SheetUpdateGateInterceptor(() => throw boom);
            await using (var failing = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>()
                .UseSqlite($"Data Source={databasePath};Default Timeout=30").AddInterceptors(gate).Options))
            {
                var failure = await Assert.ThrowsAnyAsync<Exception>(
                    () => CreateService(failing).SaveDraftAsync(seed.UserId, seed.CharacterId, CompleteRequest(assets, "orphan-candidate")));
                Assert.Same(boom, FindException<InvalidOperationException>(failure));
            }

            await using var verify = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>()
                .UseSqlite($"Data Source={databasePath};Default Timeout=30").Options);
            var stored = await verify.MovieCharacterProductionSheets.AsNoTracking().FirstAsync(item => item.Id == sheetId);
            Assert.Equal(1, stored.CurrentVersionNumber);
            var versions = await verify.MovieCharacterProductionSheetVersions.AsNoTracking()
                .Where(item => item.MovieCharacterProductionSheetId == sheetId)
                .OrderBy(item => item.VersionNumber).Select(item => item.VersionNumber).ToListAsync();
            Assert.Equal(new[] { 1 }, versions); // the failed second version must not survive
        }
        finally { if (File.Exists(databasePath)) File.Delete(databasePath); }
    }

    [Fact]
    public async Task Cancelled_pointer_update_rolls_back_the_allocation()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"taslim-sheet-atomic-{Guid.NewGuid():N}.db");
        try
        {
            await using var setup = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>()
                .UseSqlite($"Data Source={databasePath};Default Timeout=30").Options);
            await setup.Database.EnsureCreatedAsync();
            var seed = await SeedAsync(setup);
            var assets = await SeedAssetsAsync(setup, seed);
            var draft = await CreateService(setup).SaveDraftAsync(seed.UserId, seed.CharacterId, CompleteRequest(assets, "first"));
            Assert.NotNull(draft);
            var sheetId = draft!.Id;

            using var cancellation = new CancellationTokenSource();
            var gate = new SheetUpdateGateInterceptor(() =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            });
            await using (var cancelled = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>()
                .UseSqlite($"Data Source={databasePath};Default Timeout=30").AddInterceptors(gate).Options))
            {
                var thrown = await Assert.ThrowsAnyAsync<Exception>(
                    () => CreateService(cancelled).SaveDraftAsync(seed.UserId, seed.CharacterId, CompleteRequest(assets, "cancelled-candidate"), cancellation.Token));
                Assert.IsType<OperationCanceledException>(FindException<OperationCanceledException>(thrown));
            }

            await using var verify = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>()
                .UseSqlite($"Data Source={databasePath};Default Timeout=30").Options);
            var stored = await verify.MovieCharacterProductionSheets.AsNoTracking().FirstAsync(item => item.Id == sheetId);
            Assert.Equal(1, stored.CurrentVersionNumber);
            Assert.False(await verify.MovieCharacterProductionSheetVersions.AsNoTracking()
                .AnyAsync(item => item.MovieCharacterProductionSheetId == sheetId && item.VersionNumber == 2));
        }
        finally { if (File.Exists(databasePath)) File.Delete(databasePath); }
    }

    // ---------------------------------------------------------------------------------------------
    // Concurrency legs on real PostgreSQL: a committed lock/approval must never be erased by a
    // draft save that started before it, and the losing draft save must roll its version back.
    // ---------------------------------------------------------------------------------------------

    public sealed class Postgres : IAsyncLifetime
    {
        private readonly string schema = "sheet_atomicity_" + Guid.NewGuid().ToString("N");
        private DbContextOptions<TaslimDbContext> options = null!;
        private string connectionString = null!;
        private bool ready;

        public async Task InitializeAsync()
        {
            connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(connectionString)) return;
            await using (var admin = new NpgsqlConnection(connectionString))
            {
                await admin.OpenAsync();
                await using var create = admin.CreateCommand();
                create.CommandText = "CREATE SCHEMA \"" + schema + "\"";
                await create.ExecuteNonQueryAsync();
            }
            options = BuildOptions();
            await using var db = new TaslimDbContext(options);
            await db.Database.EnsureCreatedAsync();
            ready = true;
        }

        private DbContextOptions<TaslimDbContext> BuildOptions(params IInterceptor[] interceptors)
        {
            var builder = new DbContextOptionsBuilder<TaslimDbContext>()
                .UseNpgsql(new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema }.ConnectionString);
            if (interceptors.Length > 0) builder.AddInterceptors(interceptors);
            return builder.Options;
        }

        public async Task DisposeAsync()
        {
            if (string.IsNullOrWhiteSpace(connectionString)) return;
            await using var admin = new NpgsqlConnection(connectionString);
            await admin.OpenAsync();
            await using var drop = admin.CreateCommand();
            drop.CommandText = "DROP SCHEMA IF EXISTS \"" + schema + "\" CASCADE";
            await drop.ExecuteNonQueryAsync();
        }

        [DisposablePostgresFact]
        public async Task Concurrent_lock_during_draft_save_cannot_be_erased_and_the_draft_rolls_back()
        {
            Assert.True(ready);
            await using var setup = new TaslimDbContext(options);
            var seed = await SeedAsync(setup);
            var assets = await SeedAssetsAsync(setup, seed);
            var draft = await CreateService(setup).SaveDraftAsync(seed.UserId, seed.CharacterId, CompleteRequest(assets, "before-race"));
            Assert.NotNull(draft);
            var sheetId = draft!.Id;

            var reachedGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var lockSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new SheetUpdateGateInterceptor(async () =>
            {
                reachedGate.TrySetResult();
                await lockSettled.Task.WaitAsync(TimeSpan.FromSeconds(25));
            });

            var draftSave = Task.Run(async () =>
            {
                try
                {
                    await using var racing = new TaslimDbContext(BuildOptions(gate));
                    await CreateService(racing).SaveDraftAsync(seed.UserId, seed.CharacterId, CompleteRequest(assets, "racing-draft"));
                    return (Exception?)null;
                }
                catch (Exception exception) { return exception; }
            });

            await reachedGate.Task.WaitAsync(TimeSpan.FromSeconds(25));
            // A concurrent writer locks the sheet while the draft save sits between its two writes.
            await using (var locking = new TaslimDbContext(options))
            {
                await SeedPersistedStateAsync(locking, sheetId, seed.UserId, Locked);
            }
            lockSettled.TrySetResult();

            var thrown = await draftSave;
            Assert.NotNull(thrown);
            Assert.IsType<MovieCharacterProductionSheetLockedException>(FindException<MovieCharacterProductionSheetLockedException>(thrown!));

            await using var verify = new TaslimDbContext(options);
            var stored = await verify.MovieCharacterProductionSheets.AsNoTracking().FirstAsync(item => item.Id == sheetId);
            Assert.Equal(Locked, stored.Status);          // the lock survived
            Assert.NotNull(stored.LockedAt);
            Assert.Equal(1, stored.CurrentVersionNumber); // the racing allocation rolled back
            Assert.False(await verify.MovieCharacterProductionSheetVersions.AsNoTracking()
                .AnyAsync(item => item.MovieCharacterProductionSheetId == sheetId && item.VersionNumber == 2));
        }

        [DisposablePostgresFact]
        public async Task Concurrent_approve_during_draft_save_cannot_be_erased_and_the_draft_rolls_back()
        {
            Assert.True(ready);
            await using var setup = new TaslimDbContext(options);
            var seed = await SeedAsync(setup);
            var assets = await SeedAssetsAsync(setup, seed);
            var draft = await CreateService(setup).SaveDraftAsync(seed.UserId, seed.CharacterId, CompleteRequest(assets, "before-approve-race"));
            Assert.NotNull(draft);
            var sheetId = draft!.Id;

            var reachedGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var approveSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new SheetUpdateGateInterceptor(async () =>
            {
                reachedGate.TrySetResult();
                await approveSettled.Task.WaitAsync(TimeSpan.FromSeconds(25));
            });

            var draftSave = Task.Run(async () =>
            {
                try
                {
                    await using var racing = new TaslimDbContext(BuildOptions(gate));
                    await CreateService(racing).SaveDraftAsync(seed.UserId, seed.CharacterId, CompleteRequest(assets, "racing-over-approved"));
                    return (Exception?)null;
                }
                catch (Exception exception) { return exception; }
            });

            await reachedGate.Task.WaitAsync(TimeSpan.FromSeconds(25));
            // A concurrent writer approves the current version inside the draft-save window.
            await using (var approving = new TaslimDbContext(options))
            {
                await SeedPersistedStateAsync(approving, sheetId, seed.UserId, Approved);
            }
            approveSettled.TrySetResult();

            var thrown = await draftSave;
            Assert.NotNull(thrown);
            Assert.IsType<MovieCharacterProductionSheetValidationException>(FindException<MovieCharacterProductionSheetValidationException>(thrown!));

            await using var verify = new TaslimDbContext(options);
            var stored = await verify.MovieCharacterProductionSheets.AsNoTracking().FirstAsync(item => item.Id == sheetId);
            Assert.Equal(Approved, stored.Status);        // the approval survived
            Assert.NotNull(stored.ApprovedAt);
            Assert.Equal(1, stored.CurrentVersionNumber); // the racing allocation rolled back
            Assert.False(await verify.MovieCharacterProductionSheetVersions.AsNoTracking()
                .AnyAsync(item => item.MovieCharacterProductionSheetId == sheetId && item.VersionNumber == 2));
        }

        [DisposablePostgresFact]
        public async Task Approve_persists_approved_state_under_postgres()
        {
            Assert.True(ready);
            await using var setup = new TaslimDbContext(options);
            var seed = await SeedAsync(setup);
            var assets = await SeedAssetsAsync(setup, seed);
            var draft = await CreateService(setup).SaveDraftAsync(seed.UserId, seed.CharacterId, CompleteRequest(assets, "pg-approve"));
            Assert.NotNull(draft);

            var approved = await CreateService(setup).ApproveAsync(seed.UserId, draft!.Id);
            Assert.NotNull(approved);

            await using var verify = new TaslimDbContext(options);
            var stored = await verify.MovieCharacterProductionSheets.AsNoTracking().FirstAsync(item => item.Id == draft.Id);
            Assert.Equal(Approved, stored.Status);
            Assert.NotNull(stored.ApprovedAt);
            var storedVersion = await verify.MovieCharacterProductionSheetVersions.AsNoTracking()
                .FirstAsync(item => item.MovieCharacterProductionSheetId == draft.Id && item.VersionNumber == 1);
            Assert.Equal(Approved, storedVersion.Status);
            Assert.NotNull(storedVersion.ApprovedAt);
        }
    }
}
