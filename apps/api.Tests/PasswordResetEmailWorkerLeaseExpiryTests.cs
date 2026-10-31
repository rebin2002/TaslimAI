using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Email;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class PasswordResetEmailWorkerLeaseExpiryTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(8);

    [Fact]
    public async Task In_flight_sender_may_be_reclaimed_after_lease_expiry_but_stale_completion_cannot_overwrite_delivery()
    {
        using var factory = new WorkerLeaseTestApiFactory();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var firstWorkerClock = new TrackingTimeProvider(clock);
        var recoveryWorkerClock = new TrackingTimeProvider(clock);
        var sender = new SlowAcceptingEmailSender();
        var logger = factory.Services.GetRequiredService<ILogger<PasswordResetEmailWorker>>();
        var userId = await CreateUserAndQueueAsync(factory, clock.UtcNow.UtcDateTime);
        var firstWorker = CreateWorker(factory, sender, firstWorkerClock, logger);
        var recoveryWorker = CreateWorker(factory, sender, recoveryWorkerClock, logger);
        var firstStarted = false;
        var recoveryStarted = false;

        try
        {
            await firstWorker.StartAsync(CancellationToken.None);
            firstStarted = true;
            await sender.FirstSendAccepted.Task.WaitAsync(WaitLimit);

            var initialLease = await ReadEntryAsync(factory, userId);
            Assert.Equal(PasswordResetDeliveryStates.Processing, initialLease.Status);
            Assert.Equal(1, initialLease.AttemptCount);
            Assert.True(initialLease.LeaseExpiresAt > clock.UtcNow.UtcDateTime);

            // Advance only this test's clock. The fake SMTP sender has already
            // accepted the first message but is still withholding its response.
            clock.Advance(TimeSpan.FromSeconds(PasswordRecoveryOptions.MinimumLeaseSeconds + 1));
            await recoveryWorker.StartAsync(CancellationToken.None);
            recoveryStarted = true;
            await sender.SecondSendCompleted.Task.WaitAsync(WaitLimit);

            var recovered = await WaitForDeliveredAsync(factory, userId);
            Assert.Equal(PasswordResetDeliveryStates.Delivered, recovered.Status);
            Assert.Equal(2, recovered.AttemptCount);
            Assert.Null(recovered.LeaseExpiresAt);
            Assert.Equal(2, sender.AcceptedDeliveries);
            Assert.Equal(2, sender.MaximumConcurrentSends);

            // The first sender now returns after the newer worker has committed
            // Delivered. Its stale EF concurrency values must not overwrite that
            // newer database state. SMTP remains at-least-once: both accepts count.
            sender.ReleaseFirstResponse.TrySetResult(true);
            await sender.FirstSendCompleted.Task.WaitAsync(WaitLimit);
            // Two GetUtcNow calls occur during the first claim. The third is the
            // next worker cycle, proving the stale completion/save path finished.
            await firstWorkerClock.ThirdUtcNowRead.Task.WaitAsync(WaitLimit);

            await StopWorkerAsync(firstWorker);
            firstStarted = false;
            await StopWorkerAsync(recoveryWorker);
            recoveryStarted = false;

            var final = await ReadEntryAsync(factory, userId);
            Assert.Equal(PasswordResetDeliveryStates.Delivered, final.Status);
            Assert.Equal(2, final.AttemptCount);
            Assert.Null(final.LeaseExpiresAt);
            Assert.Null(final.LastFailureCode);
            Assert.Equal(2, sender.AcceptedDeliveries);
        }
        finally
        {
            sender.ReleaseFirstResponse.TrySetResult(true);
            if (recoveryStarted) await StopWorkerAsync(recoveryWorker);
            if (firstStarted) await StopWorkerAsync(firstWorker);
        }
    }

    private static PasswordResetEmailWorker CreateWorker(
        WorkerLeaseTestApiFactory factory,
        IEmailSender sender,
        TimeProvider clock,
        ILogger<PasswordResetEmailWorker> logger) =>
        new(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            factory.Services.GetRequiredService<IOptionsMonitor<PasswordRecoveryOptions>>(),
            sender,
            factory.Services.GetRequiredService<IWebHostEnvironment>(),
            clock,
            logger);

    private static async Task<Guid> CreateUserAndQueueAsync(WorkerLeaseTestApiFactory factory, DateTime now)
    {
        var email = $"lease-expiry-{Guid.NewGuid():N}@example.test";
        ApplicationUser user;
        using (var scope = factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                DisplayName = "Password recovery lease test",
                PreferredLanguage = "en",
                TimeZone = "UTC",
                CreatedAt = now,
                UpdatedAt = now,
            };
            var result = await userManager.CreateAsync(user, "WorkerPass!234");
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(error => error.Code)));
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.PasswordResetEmailOutbox.Add(new PasswordResetEmailOutboxEntry
            {
                UserId = user.Id,
                LastRequestedAt = now,
                NextAttemptAt = now,
                Status = PasswordResetDeliveryStates.Pending,
            });
            await db.SaveChangesAsync();
        }

        return user.Id;
    }

    private static async Task<PasswordResetEmailOutboxEntry> ReadEntryAsync(
        WorkerLeaseTestApiFactory factory,
        Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return await db.PasswordResetEmailOutbox.AsNoTracking().SingleAsync(entry => entry.UserId == userId);
    }

    private static async Task<PasswordResetEmailOutboxEntry> WaitForDeliveredAsync(
        WorkerLeaseTestApiFactory factory,
        Guid userId)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            var entry = await ReadEntryAsync(factory, userId);
            if (entry.Status == PasswordResetDeliveryStates.Delivered && entry.AttemptCount == 2)
                return entry;
            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        throw new TimeoutException("The recovery worker did not persist the second delivery before the test deadline.");
    }

    private static async Task StopWorkerAsync(PasswordResetEmailWorker worker)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await worker.StopAsync(timeout.Token);
    }

    private sealed class WorkerLeaseTestApiFactory : WebApplicationFactory<Program>
    {
        private readonly string databasePath = Path.Combine(
            Path.GetTempPath(),
            $"taslim-password-reset-lease-{Guid.NewGuid():N}.sqlite");
        private readonly string connectionString;
        private readonly RecordingEmailSender backgroundEmailSender = new();

        public WorkerLeaseTestApiFactory()
        {
            backgroundEmailSender.SetConfigured(false);
            connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false,
                DefaultTimeout = 15,
            }.ToString();

            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var db = new TaslimDbContext(
                new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
            db.Database.EnsureCreated();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:ApplyMigrations"] = "false",
                    ["PasswordRecovery:Enabled"] = "true",
                    ["PasswordRecovery:PublicBaseUrl"] = "http://localhost:3000",
                    ["PasswordRecovery:RequestCooldownMinutes"] = "5",
                    ["PasswordRecovery:WorkerPollSeconds"] = "1",
                    ["PasswordRecovery:MaxDeliveryAttempts"] = "5",
                    ["PasswordRecovery:LeaseSeconds"] = "60",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<TaslimDbContext>>();
                services.AddDbContext<TaslimDbContext>(options => options.UseSqlite(connectionString));
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(backgroundEmailSender);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    private sealed class SlowAcceptingEmailSender : IEmailSender
    {
        private int attempts;
        private int acceptedDeliveries;
        private int activeSends;
        private int maximumConcurrentSends;

        public bool IsConfigured => true;
        public TaskCompletionSource<bool> FirstSendAccepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> FirstSendCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> SecondSendCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseFirstResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int AcceptedDeliveries => Volatile.Read(ref acceptedDeliveries);
        public int MaximumConcurrentSends => Volatile.Read(ref maximumConcurrentSends);

        public async Task SendAsync(
            string recipient,
            string subject,
            string plainTextBody,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attempt = Interlocked.Increment(ref attempts);
            var active = Interlocked.Increment(ref activeSends);
            UpdateMaximumConcurrentSends(active);
            Interlocked.Increment(ref acceptedDeliveries);

            if (attempt == 1)
                FirstSendAccepted.TrySetResult(true);

            try
            {
                if (attempt == 1)
                    await ReleaseFirstResponse.Task.WaitAsync(cancellationToken);
                else if (attempt == 2)
                    SecondSendCompleted.TrySetResult(true);
            }
            finally
            {
                Interlocked.Decrement(ref activeSends);
                if (attempt == 1)
                    FirstSendCompleted.TrySetResult(true);
            }
        }

        private void UpdateMaximumConcurrentSends(int candidate)
        {
            while (true)
            {
                var observed = Volatile.Read(ref maximumConcurrentSends);
                if (candidate <= observed) return;
                if (Interlocked.CompareExchange(ref maximumConcurrentSends, candidate, observed) == observed)
                    return;
            }
        }
    }

    private sealed class ManualClock(DateTimeOffset initialTime)
    {
        private readonly object gate = new();
        private DateTimeOffset currentTime = initialTime;

        public DateTimeOffset UtcNow
        {
            get { lock (gate) return currentTime; }
        }

        public void Advance(TimeSpan amount)
        {
            lock (gate) currentTime = currentTime.Add(amount);
        }
    }

    private sealed class TrackingTimeProvider(ManualClock clock) : TimeProvider
    {
        private int utcNowReads;

        public TaskCompletionSource<bool> ThirdUtcNowRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Increment(ref utcNowReads) >= 3)
                ThirdUtcNowRead.TrySetResult(true);
            return clock.UtcNow;
        }
    }
}
