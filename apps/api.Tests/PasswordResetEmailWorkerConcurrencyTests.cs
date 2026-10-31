using System.Collections.Concurrent;
using System.Net.Mail;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Email;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class PasswordResetEmailWorkerConcurrencyTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(8);

    [Fact]
    public async Task Second_worker_does_not_consume_or_send_while_first_worker_holds_an_active_lease()
    {
        using var factory = CreateFactoryWithDefaultWorkerDisabled();
        var sender = new ControlledEmailSender(async (controlled, _, _, cancellationToken) =>
        {
            await controlled.ReleaseFirstAttempt.Task.WaitAsync(cancellationToken);
            controlled.RecordAcceptedDelivery();
        });
        var logger = new CapturingWorkerLogger();
        var userId = await CreateUserAndQueueAsync(factory);
        var first = CreateWorker(factory, sender, logger);
        var second = CreateWorker(factory, sender, logger);
        var firstStarted = false;
        var secondStarted = false;

        try
        {
            await first.StartAsync(CancellationToken.None);
            firstStarted = true;
            await sender.FirstAttemptStarted.Task.WaitAsync(WaitLimit);

            var claimed = await ReadEntryAsync(factory, userId);
            Assert.Equal(PasswordResetDeliveryStates.Processing, claimed.Status);
            Assert.True(claimed.LeaseExpiresAt > DateTime.UtcNow);
            Assert.Equal(1, claimed.AttemptCount);

            await second.StartAsync(CancellationToken.None);
            secondStarted = true;
            // The worker's first poll is immediate and the next poll interval is
            // one second in the test configuration. Keep the first send blocked
            // across a full interval so the second worker observes the live lease.
            await Task.Delay(TimeSpan.FromMilliseconds(1_200));

            Assert.Equal(1, sender.Attempts);
            Assert.Equal(1, sender.ActiveSends);
            Assert.Equal(1, sender.MaximumConcurrentSends);

            sender.ReleaseFirstAttempt.TrySetResult(true);
            await sender.FirstSendCompleted.Task.WaitAsync(WaitLimit);
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            await StopWorkerAsync(first);
            firstStarted = false;
            await StopWorkerAsync(second);
            secondStarted = false;

            var delivered = await ReadEntryAsync(factory, userId);

            Assert.Equal(PasswordResetDeliveryStates.Delivered, delivered.Status);
            Assert.Equal(1, delivered.AttemptCount);
            Assert.Equal(1, sender.Attempts);
            Assert.Equal(1, sender.AcceptedDeliveries);
            Assert.Equal(1, sender.MaximumConcurrentSends);
        }
        finally
        {
            sender.ReleaseFirstAttempt.TrySetResult(true);
            if (firstStarted) await StopWorkerAsync(first);
            if (secondStarted) await StopWorkerAsync(second);
        }
    }

    [Fact]
    public async Task Transient_smtp_failure_is_sanitized_and_a_due_retry_delivers_once()
    {
        using var factory = CreateFactoryWithDefaultWorkerDisabled();
        var email = $"worker-retry-{Guid.NewGuid():N}@example.test";
        var sender = new ControlledEmailSender((controlled, attempt, recipient, _) =>
        {
            if (attempt == 1)
            {
                return Task.FromException(new SmtpException(
                    $"Sensitive relay response for {recipient}: AUTH payload must not be logged."));
            }

            controlled.RecordAcceptedDelivery();
            return Task.CompletedTask;
        });
        var logger = new CapturingWorkerLogger();
        var userId = await CreateUserAndQueueAsync(factory, email);
        var worker = CreateWorker(factory, sender, logger);
        var retryWorker = CreateWorker(factory, sender, logger);
        var workerStarted = false;
        var retryWorkerStarted = false;

        try
        {
            await worker.StartAsync(CancellationToken.None);
            workerStarted = true;
            await sender.FirstSendCompleted.Task.WaitAsync(WaitLimit);
            await logger.FirstMessageLogged.Task.WaitAsync(WaitLimit);
            await StopWorkerAsync(worker);
            workerStarted = false;

            var failed = await ReadEntryAsync(factory, userId);

            Assert.Equal(PasswordResetDeliveryStates.Pending, failed.Status);
            Assert.Equal(1, failed.AttemptCount);
            Assert.Equal("SMTP_DELIVERY_FAILED", failed.LastFailureCode);
            var loggedText = string.Join("\n", logger.Messages);
            Assert.DoesNotContain(email, loggedText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Sensitive relay response", loggedText, StringComparison.Ordinal);
            Assert.DoesNotContain("AUTH payload", loggedText, StringComparison.Ordinal);

            // The production retry backoff is intentionally bounded but minutes
            // long. Move only this disposable SQLite test row into the due window.
            await MakeRetryDueAsync(factory, userId);
            await retryWorker.StartAsync(CancellationToken.None);
            retryWorkerStarted = true;
            await sender.SecondSendCompleted.Task.WaitAsync(WaitLimit);
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            await StopWorkerAsync(retryWorker);
            retryWorkerStarted = false;

            var delivered = await ReadEntryAsync(factory, userId);

            Assert.Equal(PasswordResetDeliveryStates.Delivered, delivered.Status);
            Assert.Equal(2, delivered.AttemptCount);
            Assert.Null(delivered.LastFailureCode);
            Assert.Equal(2, sender.Attempts);
            Assert.Equal(1, sender.AcceptedDeliveries);
            Assert.Equal(1, sender.MaximumConcurrentSends);
        }
        finally
        {
            if (workerStarted) await StopWorkerAsync(worker);
            if (retryWorkerStarted) await StopWorkerAsync(retryWorker);
        }
    }

    [Fact]
    public async Task Cancelled_worker_is_reclaimed_after_lease_expiry_and_documents_smtp_at_least_once_window()
    {
        using var factory = CreateFactoryWithDefaultWorkerDisabled();
        var sender = new ControlledEmailSender(async (controlled, attempt, _, cancellationToken) =>
        {
            controlled.RecordAcceptedDelivery();
            if (attempt == 1)
            {
                controlled.FirstAttemptAccepted.TrySetResult(true);
                // Simulate a relay that accepted the message while the worker
                // still awaits completion. Cancellation models a process stop
                // before the database can record Delivered.
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        });
        var logger = new CapturingWorkerLogger();
        var userId = await CreateUserAndQueueAsync(factory);
        var first = CreateWorker(factory, sender, logger);
        var restarted = CreateWorker(factory, sender, logger);
        var firstStarted = false;
        var restartedStarted = false;

        try
        {
            await first.StartAsync(CancellationToken.None);
            firstStarted = true;
            await sender.FirstAttemptAccepted.Task.WaitAsync(WaitLimit);

            var leased = await ReadEntryAsync(factory, userId);
            Assert.Equal(PasswordResetDeliveryStates.Processing, leased.Status);
            Assert.Equal(1, leased.AttemptCount);
            Assert.Equal(1, sender.AcceptedDeliveries);

            await StopWorkerAsync(first);
            firstStarted = false;
            var leftForRecovery = await ReadEntryAsync(factory, userId);
            Assert.Equal(PasswordResetDeliveryStates.Processing, leftForRecovery.Status);
            Assert.True(leftForRecovery.LeaseExpiresAt > DateTime.UtcNow);

            // Simulate lease time elapsing without waiting for the configured
            // 60-second lease; this modifies only the disposable SQLite test row.
            await ExpireLeaseAsync(factory, userId);
            await restarted.StartAsync(CancellationToken.None);
            restartedStarted = true;
            await sender.SecondSendCompleted.Task.WaitAsync(WaitLimit);
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            await StopWorkerAsync(restarted);
            restartedStarted = false;

            var delivered = await ReadEntryAsync(factory, userId);

            Assert.Equal(PasswordResetDeliveryStates.Delivered, delivered.Status);
            Assert.Equal(2, delivered.AttemptCount);
            Assert.Null(delivered.LeaseExpiresAt);
            Assert.Equal(2, sender.Attempts);
            Assert.Equal(2, sender.AcceptedDeliveries);
            Assert.Equal(1, sender.MaximumConcurrentSends);
        }
        finally
        {
            if (firstStarted) await StopWorkerAsync(first);
            if (restartedStarted) await StopWorkerAsync(restarted);
        }
    }

    private static WorkerTestApiFactory CreateFactoryWithDefaultWorkerDisabled() => new();

    private static PasswordResetEmailWorker CreateWorker(
        WorkerTestApiFactory factory,
        IEmailSender sender,
        ILogger<PasswordResetEmailWorker> logger)
    {
        var services = factory.Services;
        return new PasswordResetEmailWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<IOptionsMonitor<PasswordRecoveryOptions>>(),
            sender,
            services.GetRequiredService<IWebHostEnvironment>(),
            TimeProvider.System,
            logger);
    }

    private static async Task<Guid> CreateUserAndQueueAsync(
        WorkerTestApiFactory factory,
        string? requestedEmail = null)
    {
        var email = requestedEmail ?? $"worker-{Guid.NewGuid():N}@example.test";
        var now = DateTime.UtcNow;
        ApplicationUser user;
        using (var scope = factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                DisplayName = "Password recovery worker test",
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
        WorkerTestApiFactory factory,
        Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return await db.PasswordResetEmailOutbox.AsNoTracking().SingleAsync(entry => entry.UserId == userId);
    }

    private static async Task MakeRetryDueAsync(WorkerTestApiFactory factory, Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var entry = await db.PasswordResetEmailOutbox.SingleAsync(item => item.UserId == userId);
        entry.NextAttemptAt = DateTime.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
    }

    private static async Task ExpireLeaseAsync(WorkerTestApiFactory factory, Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var entry = await db.PasswordResetEmailOutbox.SingleAsync(item => item.UserId == userId);
        entry.LeaseExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
    }

    private static async Task StopWorkerAsync(PasswordResetEmailWorker worker)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await worker.StopAsync(timeout.Token);
    }

    private sealed class WorkerTestApiFactory : WebApplicationFactory<Program>
    {
        private readonly string databasePath = Path.Combine(
            Path.GetTempPath(),
            $"taslim-password-reset-worker-{Guid.NewGuid():N}.sqlite");
        private readonly string connectionString;
        private readonly RecordingEmailSender backgroundEmailSender = new();

        public WorkerTestApiFactory()
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

    private sealed class ControlledEmailSender(
        Func<ControlledEmailSender, int, string, CancellationToken, Task> onSend) : IEmailSender
    {
        private int attempts;
        private int acceptedDeliveries;
        private int activeSends;
        private int maximumConcurrentSends;

        public bool IsConfigured => true;
        public TaskCompletionSource<bool> FirstAttemptStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> FirstAttemptAccepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> FirstSendCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> SecondSendCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseFirstAttempt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Attempts => Volatile.Read(ref attempts);
        public int AcceptedDeliveries => Volatile.Read(ref acceptedDeliveries);
        public int ActiveSends => Volatile.Read(ref activeSends);
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
            FirstAttemptStarted.TrySetResult(true);
            try
            {
                await onSend(this, attempt, recipient, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref activeSends);
                if (attempt == 1) FirstSendCompleted.TrySetResult(true);
                if (attempt == 2) SecondSendCompleted.TrySetResult(true);
            }
        }

        public void RecordAcceptedDelivery() => Interlocked.Increment(ref acceptedDeliveries);

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

    private sealed class CapturingWorkerLogger : ILogger<PasswordResetEmailWorker>
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public TaskCompletionSource<bool> FirstMessageLogged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Enqueue(formatter(state, exception));
            FirstMessageLogged.TrySetResult(true);
        }
    }
}
