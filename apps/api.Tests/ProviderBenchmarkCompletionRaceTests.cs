using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Taslim.Api.Benchmarking;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ProviderBenchmarkCompletionRaceTests
{
    [Fact]
    public async Task Concurrent_terminal_completions_have_one_winner_and_sync_tracked_rows()
    {
        var connectionString = $"Data Source=benchmark-completion-race-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var runId = Guid.NewGuid();
        await using var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync();
        await using (var setup = CreateDb(keeper))
        {
            await setup.Database.EnsureCreatedAsync();
            var createdAt = new DateTime(2026, 10, 26, 1, 0, 0, DateTimeKind.Utc);
            setup.ProviderBenchmarkRuns.Add(new ProviderBenchmarkRun
            {
                Id = runId,
                RunKey = "completion-race-probe",
                BenchmarkDefinition = "provider-neutral-probe",
                Status = ProviderBenchmarkRunStatus.Running,
                CreatedAt = createdAt,
                StartedAt = createdAt,
            });
            await setup.SaveChangesAsync();
        }

        await using var firstConnection = new SqliteConnection(connectionString);
        await using var secondConnection = new SqliteConnection(connectionString);
        await firstConnection.OpenAsync();
        await secondConnection.OpenAsync();
        var saveBarrier = new CompletionSaveChangesBarrier();
        var updateBarrier = new CompletionUpdateOrderingInterceptor();
        await using var firstDb = CreateDb(firstConnection, saveBarrier, updateBarrier);
        await using var secondDb = CreateDb(secondConnection, saveBarrier, updateBarrier);
        var firstService = new ProviderBenchmarkService(firstDb, TimeProvider.System);
        var secondService = new ProviderBenchmarkService(secondDb, TimeProvider.System);
        var cancelledAt = new DateTime(2026, 10, 26, 1, 1, 0, DateTimeKind.Utc);
        var completedAt = cancelledAt.AddSeconds(1);

        var outcomes = await Task.WhenAll(
            Capture(firstService.CompleteRunAsync(runId,
                new ProviderBenchmarkRunCompletion(ProviderBenchmarkRunStatus.Cancelled, "cancelled", cancelledAt))),
            Capture(secondService.CompleteRunAsync(runId,
                new ProviderBenchmarkRunCompletion(ProviderBenchmarkRunStatus.Completed, "completed", completedAt))));

        Assert.True(saveBarrier.Participants == 2 || updateBarrier.UpdateAttempts == 2,
            "The test must rendezvous both writers at the transition before asserting ownership.");
        Assert.Equal(1, outcomes.Count(outcome => outcome is null));
        Assert.Single(outcomes.OfType<InvalidOperationException>());

        await using var verify = CreateDb(keeper);
        var saved = await verify.ProviderBenchmarkRuns.SingleAsync(item => item.Id == runId);
        Assert.NotEqual(ProviderBenchmarkRunStatus.Running, saved.Status);
        var expectedCode = saved.Status == ProviderBenchmarkRunStatus.Cancelled ? "cancelled" : "completed";
        var expectedCompletedAt = saved.Status == ProviderBenchmarkRunStatus.Cancelled ? cancelledAt : completedAt;
        Assert.Equal(expectedCode, saved.CompletionCode);
        Assert.Equal(expectedCompletedAt, saved.CompletedAt);

        Assert.Equal(saved.Status, firstDb.ProviderBenchmarkRuns.Local.Single(item => item.Id == runId).Status);
        Assert.Equal(saved.CompletionCode, firstDb.ProviderBenchmarkRuns.Local.Single(item => item.Id == runId).CompletionCode);
        Assert.Equal(saved.Status, secondDb.ProviderBenchmarkRuns.Local.Single(item => item.Id == runId).Status);
        Assert.Equal(saved.CompletionCode, secondDb.ProviderBenchmarkRuns.Local.Single(item => item.Id == runId).CompletionCode);

        var winner = saved.Status == ProviderBenchmarkRunStatus.Cancelled ? firstService : secondService;
        var replay = await winner.CompleteRunAsync(runId,
            new ProviderBenchmarkRunCompletion(saved.Status, new string('x', 101)));
        Assert.Equal(saved.CompletionCode, replay.CompletionCode);
        Assert.Equal(saved.CompletedAt, replay.CompletedAt);
    }

    private static async Task<Exception?> Capture(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static TaslimDbContext CreateDb(SqliteConnection connection, params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection);
        if (interceptors.Length > 0) builder.AddInterceptors(interceptors);
        return new TaslimDbContext(builder.Options);
    }

    private sealed class CompletionSaveChangesBarrier : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource<bool> bothTransitionsReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int participants;

        public int Participants => Volatile.Read(ref participants);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            eventData.Context?.ChangeTracker.DetectChanges();
            if (eventData.Context?.ChangeTracker.Entries<ProviderBenchmarkRun>()
                    .Any(entry => entry.State == EntityState.Modified) == true)
            {
                if (Interlocked.Increment(ref participants) == 2)
                    bothTransitionsReady.TrySetResult(true);
                await bothTransitionsReady.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }

            return result;
        }
    }

    private sealed class CompletionUpdateOrderingInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource<bool> secondUpdateCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int updateAttempts;

        public int UpdateAttempts => Volatile.Read(ref updateAttempts);

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (IsCompletionUpdate(command) && Interlocked.Increment(ref updateAttempts) == 1)
                await secondUpdateCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return result;
        }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (IsCompletionUpdate(command)) secondUpdateCompleted.TrySetResult(true);
            return new(result);
        }

        private static bool IsCompletionUpdate(DbCommand command) =>
            command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
            && command.CommandText.Contains("ProviderBenchmarkRuns", StringComparison.Ordinal);
    }
}
