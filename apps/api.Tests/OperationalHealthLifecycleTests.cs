using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Files;
using Taslim.Api.Infrastructure;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class OperationalHealthLifecycleTests
{
    [Fact]
    public async Task Readiness_fails_closed_without_dependency_probes_when_host_shutdown_starts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        var lifetime = new TestHostApplicationLifetime();
        var storage = new RecordingStorageService();
        var service = new OperationalHealthService(
            db,
            storage,
            Options.Create(new HealthOptions { StorageRequired = true, ProbeTimeoutSeconds = 1 }),
            NullLogger<OperationalHealthService>.Instance,
            lifetime);

        lifetime.BeginStopping();
        var result = await service.ReadinessAsync("shutdown-health-test", CancellationToken.None);

        Assert.False(result.IsReady);
        Assert.Equal("not_ready", result.Response.Status);
        var check = Assert.Single(result.Response.Checks);
        Assert.Equal("application", check.Name);
        Assert.Equal("stopping", check.Status);
        Assert.True(check.Required);
        Assert.False(storage.ProbeCalled);
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource started = new();
        private readonly CancellationTokenSource stopping = new();
        private readonly CancellationTokenSource stopped = new();

        public CancellationToken ApplicationStarted => started.Token;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => stopped.Token;

        public void BeginStopping() => stopping.Cancel();
        public void StopApplication() => BeginStopping();
    }

    private sealed class RecordingStorageService : IFileStorageService
    {
        public string ProviderKey => "test-storage";
        public bool ProbeCalled { get; private set; }

        public Task StoreAsync(string storageKey, Stream content, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) => Task.FromResult<Stream?>(null);
        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default)
        {
            ProbeCalled = true;
            return Task.FromResult(true);
        }
    }
}
