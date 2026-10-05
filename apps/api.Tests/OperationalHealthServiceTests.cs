using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Files;
using Taslim.Api.Infrastructure;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class OperationalHealthServiceTests
{
    [Fact]
    public async Task Readiness_bounds_storage_probe_and_returns_safe_status()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<TaslimDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new TaslimDbContext(dbOptions);
        await db.Database.EnsureCreatedAsync();

        var storage = new BlockingStorageService();
        var service = new OperationalHealthService(
            db,
            storage,
            Options.Create(new HealthOptions { StorageRequired = true, ProbeTimeoutSeconds = 1 }),
            NullLogger<OperationalHealthService>.Instance);

        var result = await service.ReadinessAsync("health-test", CancellationToken.None);

        Assert.False(result.IsReady);
        Assert.Equal("not_ready", result.Response.Status);
        Assert.Equal("unavailable", result.Response.Checks.Single(check => check.Name == "storage").Status);
        Assert.True(storage.CancellationObserved);
    }

    private sealed class BlockingStorageService : IFileStorageService
    {
        public string ProviderKey => "test-storage";
        public bool CancellationObserved { get; private set; }

        public Task StoreAsync(string storageKey, Stream content, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) => Task.FromResult<Stream?>(null);
        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return false;
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
        }
    }
}
