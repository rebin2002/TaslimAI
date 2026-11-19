using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Autopilot;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// TaslimAI #1029 items 1, 2 and 5 - synthetic, provider-free negative controls.
///
/// The reported defect is an interpretation error, not a transport error: a single
/// latest-page Manus listing (the 20 newest records, <c>has_more = true</c> plus a
/// <c>next_cursor</c>, of which 3 were running and 17 stopped) was being read as if it
/// were the account's complete 20-worker census, which manufactures spare capacity out
/// of a truncated page.
///
/// These tests pin the opposite behaviour on the components that already exist on this
/// stack (<see cref="AutopilotGlobalTaskInventoryReader"/> and
/// <see cref="AutopilotGlobalTaskSlotAllocator"/>) using only fake page sources and a
/// disposable SQLite database. Nothing is enabled: no Manus, Bridge, network, provider
/// or credential call is made, no real task is created, no refill or launch flag is
/// touched, and no production source file is modified.
///
/// They also drive the census-integrity branches that no existing suite reaches from
/// the reader itself - page-cap exhaustion, cross-page identity conflict, cross-page
/// deduplication, unknown status, missing task identity, oversized idempotency key and
/// a throwing page source - each of which must fail closed instead of reporting a
/// partial count, including when several staggered schedules observe the same
/// truncated page at the same instant.
/// </summary>
public sealed class AutopilotLatestPageCensusNegativeControlTests : IAsyncLifetime
{
    private const string ScopeId = "taslimai-autopilot";

    /// <summary>
    /// The live page shape recorded on #1029: 20 newest records, 3 running, 17 stopped,
    /// more pages available.
    /// </summary>
    private const int LatestPageSize = 20;
    private const int LatestPageRunning = 3;

    private readonly string databasePath =
        Path.Combine(Path.GetTempPath(), $"taslim-latest-page-census-{Guid.NewGuid():N}.db");
    private readonly string connectionString;

    // Two option instances over the same file model two controller processes:
    // separate DbContexts and separate allocator instances, one shared durable cap.
    private readonly DbContextOptions<TaslimDbContext> controllerA;
    private readonly DbContextOptions<TaslimDbContext> controllerB;

    public AutopilotLatestPageCensusNegativeControlTests()
    {
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 30,
        }.ToString();
        controllerA = BuildOptions();
        controllerB = BuildOptions();
    }

    private DbContextOptions<TaslimDbContext> BuildOptions() =>
        new DbContextOptionsBuilder<TaslimDbContext>()
            .UseSqlite(connectionString)
            .EnableDetailedErrors()
            .Options;

    public async Task InitializeAsync()
    {
        await using var db = new TaslimDbContext(controllerA);
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(databasePath))
        {
            File.Delete(databasePath);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Latest_page_is_not_the_census_until_cursor_pagination_completes()
    {
        var source = new ScriptedPageSource(ScopeId,
            (null, LatestPage("page-2")),
            ("page-2", new AutopilotGlobalTaskInventoryPage(ScopeId,
                [new("external-tail-0", ScopeId, "running", null),
                 new("external-tail-1", ScopeId, "waiting", null)],
                null)));

        var census = await new AutopilotGlobalTaskInventoryReader(source, ScopeId)
            .ReadAuthoritativeSnapshotAsync();

        // The reader had to follow the cursor: the latest page alone was never a census.
        Assert.Equal(2, source.PagesRequested);
        Assert.Equal(2, census.PagesRead);
        Assert.True(census.IsAuthoritative);
        Assert.False(census.HasMorePages);
        Assert.Null(census.FailureCode);

        // The total is the deduplicated union of every page, and a stopped record still
        // occupies a slot - so "3 running out of 20 latest" never means "3 active, 17 spare".
        Assert.Equal(LatestPageSize + 2, census.ActiveTasks.Count);
        Assert.Equal(LatestPageSize + 2, census.ActiveTasks.Select(item => item.TaskId).Distinct().Count());
        Assert.Equal(LatestPageRunning + 1, census.ActiveTasks.Count(item => item.Status == "running"));
        Assert.Equal(1, census.ActiveTasks.Count(item => item.Status == "waiting"));
        Assert.Equal(LatestPageSize - LatestPageRunning, census.ActiveTasks.Count(item => item.Status == "stopped"));
    }

    [Fact]
    public async Task Truncated_latest_page_carries_no_counts_and_cannot_authorize_a_launch()
    {
        // has_more promised a second page that never arrives: the listing is truncated.
        var source = new ScriptedPageSource(ScopeId,
            (null, LatestPage("page-2")),
            ("page-2", null));

        var census = await new AutopilotGlobalTaskInventoryReader(source, ScopeId)
            .ReadAuthoritativeSnapshotAsync();

        Assert.False(census.IsAuthoritative);
        Assert.True(census.HasMorePages);
        Assert.Equal("inventory_page_invalid", census.FailureCode);
        Assert.Equal(2, census.PagesRead);

        // A truncated census never leaks its partial counts as usable occupancy.
        Assert.Empty(census.ActiveTasks);

        var ids = await SeedLaunchTasksAsync(total: 2, initiallyActive: 0);
        var result = await ClaimAsync(ids[1], census, independentController: false);

        Assert.Equal(AutopilotGlobalTaskSlotOutcomes.InventoryUnknown, result.Outcome);
        Assert.False(result.SlotHeld);
        Assert.False(result.MayCallProvider);
        Assert.Equal(0, result.ActiveSlotsObserved);
        Assert.Equal("inventory_page_invalid", result.ReasonCode);

        await using var verify = new TaslimDbContext(controllerA);
        var task = await verify.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == ids[1]);
        Assert.Equal(AutopilotWaveLaunchStatuses.Planned, task.Status);
        Assert.Equal(0, task.LaunchAttempt);
    }

    [Fact]
    public async Task Latest_page_relabelled_as_complete_while_more_pages_exist_is_refused()
    {
        // The literal misinterpretation from #1029: a caller hands the allocator only the
        // running records it saw on page one and asserts authority while more pages are
        // still pending, implying 17 spare slots below the 20 ceiling.
        var naiveLatestPage = new AutopilotGlobalTaskInventorySnapshot(
            ScopeId,
            IsAuthoritative: true,
            PagesRead: 1,
            HasMorePages: true,
            FailureCode: null,
            ActiveTasks: Enumerable.Range(0, LatestPageRunning)
                .Select(index => new AutopilotGlobalTaskInventoryItem(
                    $"external-running-{index}", ScopeId, "running", null))
                .ToArray());

        var ids = await SeedLaunchTasksAsync(total: 2, initiallyActive: 0);
        var result = await ClaimAsync(ids[0], naiveLatestPage, independentController: false);

        Assert.Equal(AutopilotGlobalTaskSlotOutcomes.InventoryUnknown, result.Outcome);
        Assert.False(result.MayCallProvider);
        Assert.Equal(0, result.ActiveSlotsObserved);
        Assert.Equal("inventory_unknown", result.ReasonCode);

        await using var verify = new TaslimDbContext(controllerA);
        var task = await verify.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == ids[0]);
        Assert.Equal(AutopilotWaveLaunchStatuses.Planned, task.Status);
        Assert.Equal(0, task.LaunchAttempt);
    }

    [Fact]
    public async Task Honestly_paginated_latest_page_is_a_full_house_and_denies_both_staggered_schedules()
    {
        // The same 20 latest records, now honestly read and terminal: all 20 occupy a
        // slot. The truncated reading implied 17 free slots; the truthful one implies none.
        var source = new ScriptedPageSource(ScopeId, (null, LatestPage(null)));
        var census = await new AutopilotGlobalTaskInventoryReader(source, ScopeId)
            .ReadAuthoritativeSnapshotAsync();

        Assert.True(census.IsAuthoritative);
        Assert.Equal(1, census.PagesRead);
        Assert.Equal(LatestPageSize, census.ActiveTasks.Count);

        var ids = await SeedLaunchTasksAsync(total: 22, initiallyActive: 0);

        var results = await Task.WhenAll(
            ClaimAsync(ids[20], census, independentController: true),
            ClaimAsync(ids[21], census, independentController: false));

        Assert.All(results, item => Assert.Equal(AutopilotGlobalTaskSlotOutcomes.CapacityReached, item.Outcome));
        Assert.All(results, item => Assert.False(item.MayCallProvider));
        Assert.All(results, item => Assert.Equal(LatestPageSize, item.ActiveSlotsObserved));
        Assert.All(results, item => Assert.True(item.ActiveSlotsObserved <= AutopilotGlobalTaskSlotAllocator.HardMaximum));

        await using var verify = new TaslimDbContext(controllerA);
        Assert.Equal(0, await verify.AutopilotWaveLaunchTasks.CountAsync(item =>
            item.Status == AutopilotWaveLaunchStatuses.Launching || item.Status == AutopilotWaveLaunchStatuses.Launched));
    }

    [Fact]
    public async Task Staggered_schedules_observing_the_same_truncated_page_never_admit_overlapping_work()
    {
        var truncated = new ScriptedPageSource(ScopeId,
            (null, LatestPage("page-2")),
            ("page-2", null));
        var census = await new AutopilotGlobalTaskInventoryReader(truncated, ScopeId)
            .ReadAuthoritativeSnapshotAsync();
        Assert.False(census.IsAuthoritative);

        var ids = await SeedLaunchTasksAsync(total: 24, initiallyActive: 0);

        // Four staggered schedules, alternating between two independent controller
        // processes, all observe the same truncated latest page at the same instant.
        var targets = ids.Skip(LatestPageSize).Take(4).ToArray();
        var results = await Task.WhenAll(targets.Select((taskId, index) =>
            ClaimAsync(taskId, census, independentController: (index % 2) == 0)));

        Assert.Equal(4, results.Length);
        Assert.All(results, item => Assert.Equal(AutopilotGlobalTaskSlotOutcomes.InventoryUnknown, item.Outcome));
        Assert.All(results, item => Assert.False(item.MayCallProvider));
        Assert.All(results, item => Assert.Equal(0, item.ActiveSlotsObserved));

        // Fail closed leaves no residual occupancy marker anywhere.
        await using var verify = new TaslimDbContext(controllerA);
        Assert.Equal(0, await verify.AutopilotWaveLaunchTasks.CountAsync(item =>
            item.Status == AutopilotWaveLaunchStatuses.Launching || item.Status == AutopilotWaveLaunchStatuses.Launched));
        Assert.Equal(24, await verify.AutopilotWaveLaunchTasks.CountAsync(item =>
            item.Status == AutopilotWaveLaunchStatuses.Planned && item.LaunchAttempt == 0));
    }

    [Fact]
    public async Task Pagination_hitting_the_page_cap_fails_closed_instead_of_reporting_a_partial_count()
    {
        // Upstream keeps promising more pages; the crawl is bounded, so the census must
        // be reported as unknown, never as "the pages I managed to read".
        var source = new EndlessPageSource(ScopeId, pageSize: 2);
        var census = await new AutopilotGlobalTaskInventoryReader(source, ScopeId, maxPages: 4)
            .ReadAuthoritativeSnapshotAsync();

        Assert.False(census.IsAuthoritative);
        Assert.True(census.HasMorePages);
        Assert.Equal("inventory_page_limit_exceeded", census.FailureCode);
        Assert.Equal(4, census.PagesRead);
        Assert.Equal(4, source.PagesRequested);
        Assert.Empty(census.ActiveTasks);

        var ids = await SeedLaunchTasksAsync(total: 1, initiallyActive: 0);
        var result = await ClaimAsync(ids[0], census, independentController: false);

        Assert.Equal(AutopilotGlobalTaskSlotOutcomes.InventoryUnknown, result.Outcome);
        Assert.False(result.MayCallProvider);
        Assert.Equal("inventory_page_limit_exceeded", result.ReasonCode);
    }

    [Fact]
    public async Task Cross_page_duplicate_identity_is_deduplicated_but_a_conflicting_status_fails_closed()
    {
        // (a) A stable duplicate across pages is pagination overlap, not new occupancy.
        var overlap = new ScriptedPageSource(ScopeId,
            (null, new AutopilotGlobalTaskInventoryPage(ScopeId,
                [new("external-shared-1", ScopeId, "running", "autopilot:overlap:t1"),
                 new("external-shared-2", ScopeId, "stopped", null)],
                "page-2")),
            ("page-2", new AutopilotGlobalTaskInventoryPage(ScopeId,
                [new("external-shared-1", ScopeId, "running", "autopilot:overlap:t1")],
                null)));
        var overlapCensus = await new AutopilotGlobalTaskInventoryReader(overlap, ScopeId)
            .ReadAuthoritativeSnapshotAsync();

        Assert.True(overlapCensus.IsAuthoritative);
        Assert.Equal(2, overlapCensus.PagesRead);
        Assert.Equal(2, overlapCensus.ActiveTasks.Count);
        Assert.Equal(1, CountOccurrences(overlapCensus.ActiveTasks, "external-shared-1"));

        // (b) The same ID reported with a different still-active status means the census
        // cannot be trusted at all: neither count nor spare capacity may be derived.
        var conflict = new ScriptedPageSource(ScopeId,
            (null, new AutopilotGlobalTaskInventoryPage(ScopeId,
                [new("external-shared-1", ScopeId, "running", null)],
                "page-2")),
            ("page-2", new AutopilotGlobalTaskInventoryPage(ScopeId,
                [new("external-shared-1", ScopeId, "waiting", null)],
                null)));
        var conflictCensus = await new AutopilotGlobalTaskInventoryReader(conflict, ScopeId)
            .ReadAuthoritativeSnapshotAsync();

        Assert.False(conflictCensus.IsAuthoritative);
        Assert.Equal("inventory_duplicate_identity_conflict", conflictCensus.FailureCode);
        Assert.Empty(conflictCensus.ActiveTasks);

        var ids = await SeedLaunchTasksAsync(total: 1, initiallyActive: 0);
        var result = await ClaimAsync(ids[0], conflictCensus, independentController: false);
        Assert.Equal(AutopilotGlobalTaskSlotOutcomes.InventoryUnknown, result.Outcome);
        Assert.False(result.MayCallProvider);
    }

    [Theory]
    [InlineData("unknown_status")]
    [InlineData("missing_task_identity")]
    [InlineData("oversized_idempotency_key")]
    [InlineData("source_throws")]
    public async Task Census_integrity_faults_fail_closed_before_any_capacity_is_claimed(string fault)
    {
        IAutopilotGlobalTaskInventoryPageSource source = fault switch
        {
            "unknown_status" => new ScriptedPageSource(ScopeId,
                (null, new AutopilotGlobalTaskInventoryPage(ScopeId,
                    [new("external-ok-1", ScopeId, "running", null)], "page-2")),
                ("page-2", new AutopilotGlobalTaskInventoryPage(ScopeId,
                    [new("external-ok-2", ScopeId, "sentient_review_queue", null)], null))),
            "missing_task_identity" => new ScriptedPageSource(ScopeId,
                (null, new AutopilotGlobalTaskInventoryPage(ScopeId,
                    [new("   ", ScopeId, "running", null)], null))),
            "oversized_idempotency_key" => new ScriptedPageSource(ScopeId,
                (null, new AutopilotGlobalTaskInventoryPage(ScopeId,
                    [new("external-ok-3", ScopeId, "running", new string('k', 201))], null))),
            "source_throws" => new ThrowingPageSource(),
            _ => throw new InvalidOperationException($"Unknown fault case '{fault}'."),
        };

        var census = await new AutopilotGlobalTaskInventoryReader(source, ScopeId)
            .ReadAuthoritativeSnapshotAsync();

        Assert.False(census.IsAuthoritative);
        Assert.True(census.HasMorePages);
        Assert.Empty(census.ActiveTasks);
        Assert.Equal(fault switch
        {
            "unknown_status" => "inventory_task_status_unknown",
            "missing_task_identity" => "inventory_task_identity_or_scope_missing",
            "oversized_idempotency_key" => "inventory_idempotency_key_invalid",
            "source_throws" => "inventory_source_unavailable",
            _ => string.Empty,
        }, census.FailureCode);

        var ids = await SeedLaunchTasksAsync(total: 1, initiallyActive: 0);
        var result = await ClaimAsync(ids[0], census, independentController: false);

        Assert.Equal(AutopilotGlobalTaskSlotOutcomes.InventoryUnknown, result.Outcome);
        Assert.False(result.MayCallProvider);
        Assert.False(result.SlotHeld);

        await using var verify = new TaslimDbContext(controllerA);
        var task = await verify.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == ids[0]);
        Assert.Equal(AutopilotWaveLaunchStatuses.Planned, task.Status);
        Assert.Equal(0, task.LaunchAttempt);
    }

    private static AutopilotGlobalTaskInventoryPage LatestPage(string? nextCursor) => new(
        ScopeId,
        Enumerable.Range(0, LatestPageSize)
            .Select(index => new AutopilotGlobalTaskInventoryItem(
                $"external-latest-{index}",
                ScopeId,
                index < LatestPageRunning ? "running" : "stopped",
                null))
            .ToArray(),
        nextCursor);

    private static int CountOccurrences(
        IReadOnlyList<AutopilotGlobalTaskInventoryItem> items,
        string taskId) => items.Count(item => string.Equals(item.TaskId, taskId, StringComparison.Ordinal));

    private TaslimDbContext CreateContext(bool independentController) =>
        new(independentController ? controllerB : controllerA);

    private async Task<AutopilotGlobalTaskSlotResult> ClaimAsync(
        Guid taskId,
        AutopilotGlobalTaskInventorySnapshot inventory,
        bool independentController)
    {
        await using var db = CreateContext(independentController);
        var task = await db.AutopilotWaveLaunchTasks.AsNoTracking().SingleAsync(item => item.Id == taskId);
        var key = AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(task.WaveKey, task.TaskKey);
        var allocator = new AutopilotGlobalTaskSlotAllocator(
            db, ScopeId, TimeProvider.System, AutopilotGlobalTaskSlotAllocator.HardMaximum);
        return await allocator.ReserveOrRecoverLaunchAsync(
            taskId,
            key,
            inventory,
            TimeSpan.FromMinutes(5),
            providerGuaranteesIdempotency: true);
    }

    private async Task<IReadOnlyList<Guid>> SeedLaunchTasksAsync(int total, int initiallyActive)
    {
        var ids = Enumerable.Range(0, total).Select(_ => Guid.NewGuid()).ToArray();
        var now = DateTime.UtcNow;
        await using var db = CreateContext(independentController: false);
        var waves = new Dictionary<int, (AutopilotRun Run, AutopilotWaveLaunchBatch Batch)>();

        for (var index = 0; index < total; index++)
        {
            var waveNumber = index / 5;
            if (!waves.TryGetValue(waveNumber, out var pair))
            {
                var sourceWave = $"census-wave-{waveNumber}";
                var run = new AutopilotRun
                {
                    Id = Guid.NewGuid(),
                    WaveKey = sourceWave,
                    RunKey = $"census-run-{waveNumber}",
                    State = AutopilotRunStates.InProgress,
                    BaseSha = new string('a', 40),
                    CandidateSha = new string('b', 40),
                    Branch = $"feature/census-{waveNumber}",
                    CreatedAt = now,
                    UpdatedAt = now,
                    ConcurrencyToken = Guid.NewGuid(),
                };
                var batch = new AutopilotWaveLaunchBatch
                {
                    Id = Guid.NewGuid(),
                    SourceRunId = run.Id,
                    SourceWaveKey = sourceWave,
                    WaveKey = $"{sourceWave}-next",
                    Status = AutopilotWaveLaunchStatuses.Planned,
                    BaseSha = run.CandidateSha,
                    Branch = run.Branch,
                    PlanFingerprint = "census-negative-control",
                    CreatedAt = now,
                    UpdatedAt = now,
                    ConcurrencyToken = Guid.NewGuid(),
                };
                db.AutopilotRuns.Add(run);
                db.AutopilotWaveLaunchBatches.Add(batch);
                pair = (run, batch);
                waves.Add(waveNumber, pair);
            }

            var active = index < initiallyActive;
            db.AutopilotWaveLaunchTasks.Add(new AutopilotWaveLaunchTask
            {
                Id = ids[index],
                BatchId = pair.Batch.Id,
                SourceRunId = pair.Run.Id,
                SourceWaveKey = pair.Run.WaveKey,
                WaveKey = pair.Batch.WaveKey,
                TaskKey = $"census-task-{index}",
                Title = $"Census task {index}",
                BacklogItemKey = $"census-backlog-{index}",
                Status = active ? AutopilotWaveLaunchStatuses.Launched : AutopilotWaveLaunchStatuses.Planned,
                ExternalTaskId = active ? $"external-seeded-{index}" : null,
                Branch = pair.Run.Branch,
                BaseSha = pair.Run.CandidateSha,
                LaunchAttempt = active ? 1 : 0,
                MaxLaunchAttempts = 3,
                ReconcileCount = 0,
                MaxReconciles = 10,
                CreatedAt = now,
                UpdatedAt = now,
                LaunchedAt = active ? now : null,
                ConcurrencyToken = Guid.NewGuid(),
            });
        }

        await db.SaveChangesAsync();
        return ids;
    }

    /// <summary>Strict scripted listing: an unexpected cursor or extra page is a test failure.</summary>
    private sealed class ScriptedPageSource(
        string expectedScopeId,
        params (string? ExpectedCursor, AutopilotGlobalTaskInventoryPage? Response)[] pages)
        : IAutopilotGlobalTaskInventoryPageSource
    {
        private int nextPage;
        public int PagesRequested => nextPage;

        public Task<AutopilotGlobalTaskInventoryPage?> ReadPageAsync(
            string scopeId,
            string? cursor,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(scopeId, expectedScopeId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Census reader requested an unexpected Taslim scope.");
            }

            if (nextPage >= pages.Length)
            {
                throw new InvalidOperationException("Census reader requested an unexpected extra page.");
            }

            var page = pages[nextPage++];
            if (!string.Equals(cursor, page.ExpectedCursor, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Census reader sent an unexpected pagination cursor.");
            }

            return Task.FromResult(page.Response);
        }
    }

    /// <summary>Never-terminating listing: every page advertises more work under a fresh cursor.</summary>
    private sealed class EndlessPageSource(string expectedScopeId, int pageSize)
        : IAutopilotGlobalTaskInventoryPageSource
    {
        private int requested;
        public int PagesRequested => Volatile.Read(ref requested);

        public Task<AutopilotGlobalTaskInventoryPage?> ReadPageAsync(
            string scopeId,
            string? cursor,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(scopeId, expectedScopeId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Census reader requested an unexpected Taslim scope.");
            }

            var page = Interlocked.Increment(ref requested);
            var items = Enumerable.Range(0, pageSize)
                .Select(offset => new AutopilotGlobalTaskInventoryItem(
                    $"external-endless-{page}-{offset}", scopeId, "running", null))
                .ToArray();
            return Task.FromResult<AutopilotGlobalTaskInventoryPage?>(
                new AutopilotGlobalTaskInventoryPage(scopeId, items, $"cursor-{page + 1}"));
        }
    }

    /// <summary>Models an upstream listing that throws instead of answering (timeout/5xx).</summary>
    private sealed class ThrowingPageSource : IAutopilotGlobalTaskInventoryPageSource
    {
        public Task<AutopilotGlobalTaskInventoryPage?> ReadPageAsync(
            string scopeId,
            string? cursor,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Synthetic upstream listing failure.");
    }
}
