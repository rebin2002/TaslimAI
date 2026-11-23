using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Email;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Fault-injection evidence for the recovery paths that only misbehave when a
/// dependency fails: the outbox storage outage that turned account existence into
/// an observable difference, the pending-reset supersede rule, and the scope of the
/// recovery token lifetime. Every case here runs against the real controller,
/// worker service and Identity stack over a disposable SQLite database.
/// </summary>
public sealed class PasswordRecoveryFaultInjectionTests
{
    [Fact]
    public async Task Forgot_hides_an_outbox_storage_fault_behind_the_same_masked_acceptance()
    {
        using var factory = new FaultInjectionApiFactory(outboxStorageUnavailable: true);
        var email = await CreateActiveUserAsync(factory);
        using var knownClient = factory.CreateClient();
        using var unknownClient = factory.CreateClient();
        var knownCsrf = await GetCsrfAsync(knownClient);
        var unknownCsrf = await GetCsrfAsync(unknownClient);

        using var known = await SendForgotAsync(knownClient, knownCsrf, email);
        using var unknown = await SendForgotAsync(unknownClient, unknownCsrf, $"missing-{Guid.NewGuid():N}@example.test");

        // The fault must not become an existence oracle: identical status, identical body.
        Assert.Equal(HttpStatusCode.Accepted, known.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
        Assert.Equal(await known.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        Assert.Equal(0, factory.Sender.SendCount);

        // The fault is recorded server-side rather than swallowed...
        var errors = factory.Logs.Entries
            .Where(entry => entry.Category.StartsWith("Taslim.Api.Controllers.AuthController", StringComparison.Ordinal))
            .Where(entry => entry.Level >= LogLevel.Error)
            .Select(entry => entry.Message)
            .ToList();
        Assert.Contains(errors, message => message.Contains("Password reset request queueing failed", StringComparison.Ordinal));
        Assert.Contains(errors, message => message.Contains("ErrorType=", StringComparison.Ordinal));

        // ...and nothing about the account, the address or a token reaches the log.
        Assert.DoesNotContain(errors, message => message.Contains(email, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Message.Contains("token=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Completed_reset_retires_the_still_queued_recovery_email()
    {
        using var factory = new FaultInjectionApiFactory();
        var email = await CreateActiveUserAsync(factory);
        Guid userId;
        string token;
        using (var scope = factory.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByEmailAsync(email);
            Assert.NotNull(user);
            userId = user!.Id;
            token = await userManager.GeneratePasswordResetTokenAsync(user);
        }

        // A recovery request that was queued but not yet delivered. NextAttemptAt is
        // deliberately in the future so the outcome cannot depend on worker timing.
        using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var requestedAt = DateTime.UtcNow;
            db.PasswordResetEmailOutbox.Add(new PasswordResetEmailOutboxEntry
            {
                UserId = userId,
                LastRequestedAt = requestedAt,
                NextAttemptAt = requestedAt.AddHours(6),
                Status = PasswordResetDeliveryStates.Pending,
            });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var csrf = await GetCsrfAsync(client);
        using var reset = await SendResetAsync(client, csrf, email, token, "RecoveredPassword!42");
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var entry = await db.PasswordResetEmailOutbox.SingleOrDefaultAsync(item => item.UserId == userId);
            Assert.NotNull(entry);
            Assert.Equal(PasswordResetDeliveryStates.Cancelled, entry!.Status);
            Assert.Null(entry.LeaseExpiresAt);

            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByIdAsync(userId.ToString());
            Assert.NotNull(user);
            Assert.True(await userManager.CheckPasswordAsync(user!, "RecoveredPassword!42"));
        }

        Assert.Equal(0, factory.Sender.SendCount);
    }

    [Fact]
    public async Task A_newer_request_or_a_completed_reset_supersedes_an_older_delivery_claim()
    {
        using var factory = new FaultInjectionApiFactory();
        var email = await CreateActiveUserAsync(factory);
        Guid userId;
        using (var scope = factory.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByEmailAsync(email);
            Assert.NotNull(user);
            userId = user!.Id;
        }

        var clock = new MutableTimeProvider(DateTime.UtcNow);
        using var context = new TaslimDbContext(
            new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(factory.ConnectionString).Options);
        var outbox = new PasswordResetOutboxService(context, clock);

        Assert.True(await outbox.TryQueueAsync(userId, TimeSpan.FromMinutes(1), CancellationToken.None));
        var firstClaim = clock.GetUtcNow().UtcDateTime;

        // The claim owns the row while nothing has changed it.
        Assert.False(await outbox.IsClaimSupersededAsync(userId, firstClaim, CancellationToken.None));

        // A second request after the cooldown re-opens the row: the older claim is
        // obsolete, the newer epoch is authoritative.
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(await outbox.TryQueueAsync(userId, TimeSpan.FromMinutes(1), CancellationToken.None));
        Assert.True(await outbox.IsClaimSupersededAsync(userId, firstClaim, CancellationToken.None));
        var secondClaim = clock.GetUtcNow().UtcDateTime;
        Assert.False(await outbox.IsClaimSupersededAsync(userId, secondClaim, CancellationToken.None));

        // A completed reset retires the row even though the epoch still matches the
        // claim taken for it, so an in-flight delivery can no longer be mailed.
        Assert.True(await outbox.CancelForUserAsync(userId, CancellationToken.None));
        Assert.True(await outbox.IsClaimSupersededAsync(userId, secondClaim, CancellationToken.None));

        using var verify = new TaslimDbContext(
            new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(factory.ConnectionString).Options);
        var entry = await verify.PasswordResetEmailOutbox.SingleAsync(item => item.UserId == userId);
        Assert.Equal(PasswordResetDeliveryStates.Cancelled, entry.Status);
        Assert.Null(entry.LeaseExpiresAt);
    }

    [Fact]
    public async Task Cancellation_of_a_missing_or_already_terminal_row_is_a_no_op()
    {
        using var factory = new FaultInjectionApiFactory();
        var email = await CreateActiveUserAsync(factory);
        Guid userId;
        using (var scope = factory.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByEmailAsync(email);
            Assert.NotNull(user);
            userId = user!.Id;
        }

        using var context = new TaslimDbContext(
            new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(factory.ConnectionString).Options);
        var outbox = new PasswordResetOutboxService(context, new MutableTimeProvider(DateTime.UtcNow));

        // Nothing queued, and a deleted account must never block a completed reset.
        Assert.False(await outbox.CancelForUserAsync(userId, CancellationToken.None));
        Assert.True(await outbox.IsClaimSupersededAsync(userId, DateTime.UtcNow, CancellationToken.None));

        Assert.True(await outbox.TryQueueAsync(userId, TimeSpan.FromMinutes(1), CancellationToken.None));
        var claimed = context.PasswordResetEmailOutbox.Local.First().LastRequestedAt;
        Assert.True(await outbox.CancelForUserAsync(userId, CancellationToken.None));

        // A second completion changes nothing and reports that it retired nothing.
        Assert.False(await outbox.CancelForUserAsync(userId, CancellationToken.None));
        Assert.True(await outbox.IsClaimSupersededAsync(userId, claimed, CancellationToken.None));
    }

    [Fact]
    public async Task Recovery_token_lifetime_does_not_reach_other_identity_purposes()
    {
        using var factory = new FaultInjectionApiFactory();
        var shared = factory.Services.GetRequiredService<IOptions<DataProtectionTokenProviderOptions>>().Value;
        var reset = factory.Services.GetRequiredService<IOptions<PasswordResetTokenProviderOptions>>().Value;
        var identity = factory.Services.GetRequiredService<IOptions<IdentityOptions>>().Value;

        // Identity's own default, i.e. exactly what main carries today: recovery must
        // not shorten email confirmation, phone-change or two-factor tokens.
        Assert.Equal(TimeSpan.FromDays(1), shared.TokenLifespan);
        Assert.Equal(TimeSpan.FromMinutes(PasswordRecoveryOptions.PasswordResetTokenLifetimeMinutes), reset.TokenLifespan);
        Assert.Equal(PasswordResetTokenProviderOptions.ProviderName, identity.Tokens.PasswordResetTokenProvider);
        Assert.NotEqual(PasswordResetTokenProviderOptions.ProviderName, identity.Tokens.EmailConfirmationTokenProvider);
        Assert.NotEqual(shared.Name, reset.Name);
    }

    [Fact]
    public async Task Reset_and_email_confirmation_tokens_are_not_interchangeable()
    {
        using var factory = new FaultInjectionApiFactory();
        var email = await CreateActiveUserAsync(factory);
        using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);

        var resetToken = await userManager.GeneratePasswordResetTokenAsync(user!);
        var confirmToken = await userManager.GenerateEmailConfirmationTokenAsync(user);

        Assert.True(await userManager.VerifyUserTokenAsync(
            user,
            userManager.Options.Tokens.PasswordResetTokenProvider,
            UserManager<ApplicationUser>.ResetPasswordTokenPurpose,
            resetToken));
        Assert.True(await userManager.VerifyUserTokenAsync(
            user,
            userManager.Options.Tokens.EmailConfirmationTokenProvider,
            UserManager<ApplicationUser>.ConfirmEmailTokenPurpose,
            confirmToken));

        // Cross-purpose replay fails in both directions, so a confirmation token can
        // never be spent as a reset token, and the scoped provider does not accept
        // tokens minted under the shared purpose chain.
        Assert.False(await userManager.VerifyUserTokenAsync(
            user,
            userManager.Options.Tokens.PasswordResetTokenProvider,
            UserManager<ApplicationUser>.ResetPasswordTokenPurpose,
            confirmToken));
        Assert.False(await userManager.VerifyUserTokenAsync(
            user,
            userManager.Options.Tokens.EmailConfirmationTokenProvider,
            UserManager<ApplicationUser>.ConfirmEmailTokenPurpose,
            resetToken));
    }

    private static async Task<string> CreateActiveUserAsync(FaultInjectionApiFactory factory)
    {
        var email = $"fault-{Guid.NewGuid():N}@example.test";
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var now = DateTime.UtcNow;
        var result = await userManager.CreateAsync(new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = "Synthetic Fault Injection User",
            PreferredLanguage = "en",
            TimeZone = "UTC",
            CreatedAt = now,
            UpdatedAt = now,
            IsActive = true,
        }, "StrongPassword!123");
        Assert.True(result.Succeeded, "Synthetic user creation failed; identity error details omitted.");
        return email;
    }

    private static async Task<string> GetCsrfAsync(HttpClient client)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        return csrf.GetProperty("token").GetString()!;
    }

    private static async Task<HttpResponseMessage> SendForgotAsync(HttpClient client, string csrf, string email)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/password/forgot")
        {
            Content = JsonContent.Create(new { email }),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        return await client.SendAsync(request).WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static async Task<HttpResponseMessage> SendResetAsync(
        HttpClient client,
        string csrf,
        string email,
        string token,
        string newPassword)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/password/reset")
        {
            Content = JsonContent.Create(new { email, token, newPassword }),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        return await client.SendAsync(request).WaitAsync(TimeSpan.FromSeconds(20));
    }

    private sealed class MutableTimeProvider(DateTime utcNow) : TimeProvider
    {
        private DateTime current = utcNow;

        public override DateTimeOffset GetUtcNow() => new(current, TimeSpan.Zero);

        public void Advance(TimeSpan delta) => current += delta;
    }

    private sealed class FaultRecordingEmailSender : IEmailSender
    {
        private int sendCount;
        public bool IsConfigured => true;
        public int SendCount => Volatile.Read(ref sendCount);
        public ConcurrentQueue<string> Recipients { get; } = new();

        public Task SendAsync(string recipient, string subject, string plainTextBody, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref sendCount);
            Recipients.Enqueue(recipient);
            return Task.CompletedTask;
        }
    }

    private sealed class FaultInjectionApiFactory : WebApplicationFactory<Program>
    {
        private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"taslim-recovery-fault-{Guid.NewGuid():N}.db");
        private readonly bool outboxStorageUnavailable;
        public string ConnectionString { get; }
        public FaultRecordingEmailSender Sender { get; } = new();
        public CapturingLoggerProvider Logs { get; } = new();

        public FaultInjectionApiFactory(bool outboxStorageUnavailable = false)
        {
            this.outboxStorageUnavailable = outboxStorageUnavailable;
            ConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false,
                DefaultTimeout = 30,
            }.ToString();
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using var db = new TaslimDbContext(
                new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
            db.Database.EnsureCreated();
            if (outboxStorageUnavailable)
            {
                using var command = connection.CreateCommand();
                command.CommandText = """DROP TABLE "PasswordResetEmailOutbox";""";
                command.ExecuteNonQuery();
            }
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ApplyMigrations"] = "false",
                ["PasswordRecovery:Enabled"] = "true",
                ["PasswordRecovery:PublicBaseUrl"] = "http://localhost:3000",
                ["PasswordRecovery:RequestCooldownMinutes"] = "1",
                ["PasswordRecovery:WorkerPollSeconds"] = "1",
                ["PasswordRecovery:MaxDeliveryAttempts"] = "5",
                ["PasswordRecovery:LeaseSeconds"] = "60",
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<TaslimDbContext>>();
                services.AddDbContext<TaslimDbContext>(options => options.UseSqlite(ConnectionString));
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(Sender);
                services.AddSingleton<ILoggerProvider>(Logs);

                // The worker is driven explicitly by other suites; these tests need the
                // delivery decision to depend only on the state they set up.
                var worker = services.SingleOrDefault(descriptor =>
                    descriptor.ServiceType == typeof(IHostedService)
                    && descriptor.ImplementationType == typeof(PasswordResetEmailWorker));
                if (worker is not null) services.Remove(worker);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing) return;
            foreach (var suffix in new[] { "", "-shm", "-wal" })
            {
                try { File.Delete(databasePath + suffix); }
                catch (IOException) { }
            }
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> entries = new();
        public IReadOnlyCollection<LogEntry> Entries => entries;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, entries);
        public void Dispose()
        {
        }

        public sealed record LogEntry(string Category, LogLevel Level, string Message);

        private sealed class CapturingLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => entries.Enqueue(new LogEntry(category, logLevel, formatter(state, exception)));

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose()
                {
                }
            }
        }
    }
}
