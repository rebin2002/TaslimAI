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
    public async Task Concurrent_terminal_completions_have_one_winner()
    {
        const string connectionString = "Data Source=file:benchmark-completion-race;Mode=Memory;Cache=Shared";
        await using var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync();
        await using (var setup = CreateDb(keeper))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.ProviderBenchmarkRuns.Add(new ProviderBenchmarkRun
            {
                Id = RunId,
                RunKey = "completion-race-probe",
                BenchmarkDefinition = "provider-neutral-probe",
                CreatedAt = DateTime.UtcNow,
                StartedAt = DateTime.UtcNow,
            });
            await setup.SaveChangesAsync();
        }

        await using var firstConnection = new SqliteConnection(connectionString);
        await using var secondConnection = new SqliteConnection(connectionString);
        await firstConnection.OpenAsync();
        await secondConnection.OpenAsync();
        var interceptor = new CompletionUpdateOrderingInterceptor();
        await using var firstDb = CreateDb(firstConnection, interceptor);
        await using var secondDb = CreateDb(secondConnection, interceptor);
        var first = new ProviderBenchmarkService(firstDb, TimeProvider.System);
        var second = new ProviderBenchmarkService(secondDb, TimeProvider.System);

        var outcomes = await Task.WhenAll(
            Capture(first.CompleteRunAsync(RunId, new ProviderBenchmarkRunCompletion(ProviderBenchmarkRunStatus.Completed, "completed"))),
            Capture(second.CompleteRunAsync(RunId, new ProviderBenchmarkRunCompletion(ProviderBenchmarkRunStatus.Failed, "failed"))));

        Assert.Equal(1, outcomes.Count(outcome => outcome is null));
        Assert.Single(outcomes.OfType<InvalidOperationException>());

        await using var verify = CreateDb(keeper);
        var saved = await verify.ProviderBenchmarkRuns.SingleAsync(item => item.Id == RunId);
        Assert.NotEqual(ProviderBenchmarkRunStatus.Running, saved.Status);
    }

    private static readonly Guid RunId = Guid.Parse("d9d7dba4-5a50-4f4a-8f77-8f8df31cb9a4");

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

    private static TaslimDbContext CreateDb(SqliteConnection connection, params DbCommandInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection);
        if (interceptors.Length > 0) builder.AddInterceptors(interceptors);
        return new TaslimDbContext(builder.Options);
    }

    private sealed class CompletionUpdateOrderingInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource<bool> secondUpdateCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int updateAttempts;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (IsCompletionUpdate(command) && Interlocked.Increment(ref updateAttempts) == 1)
                await secondUpdateCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
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
