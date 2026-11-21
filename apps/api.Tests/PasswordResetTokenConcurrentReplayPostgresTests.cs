using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Taslim.Api.Domain;
using Taslim.Api.Email;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Independent PostgreSQL axis of the concurrent single-use reset-token evidence opened by draft
/// #1189 (itself stacked on #1121 release gate 2, "reset token ... single-use").
///
/// #1189 forces the double-spend interleaving on a file-backed SQLite TestServer and pins the
/// outcomes, but states its own limitation: "The statement shape above is what EF emits for
/// Postgres too, but production-Postgres behaviour remains." This lane closes exactly that
/// sentence and nothing else: the same two-request, same-token race executed against a disposable
/// PostgreSQL server through the same TestServer stack, plus a provider-level witness of the
/// optimistic-concurrency UPDATEs on the users table that decide the winner - the evidence the
/// SQLite harness could only quote from captured logs.
///
/// Deliberate non-goals: no production code, no migration file, no configuration, no
/// AuthController change, no account-settings or billing change, no provider or SMTP call, no
/// real user data, no live service migration. The token is minted in-process and the delivery
/// worker is removed, so nothing here can send mail or be read as delivery evidence. When no
/// disposable PostgreSQL is configured the lane SKIPS - a skip is never presented as proof.
/// </summary>
public sealed class PasswordResetTokenConcurrentReplayPostgresTests
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan RendezvousTimeout = TimeSpan.FromSeconds(20);

    [DisposablePostgresFact]
    public async Task One_reset_token_redeemed_twice_simultaneously_on_postgres_changes_exactly_one_password()
    {
        using var fixture = DisposablePostgresTestDatabase.Create();
        using var factory = new ConcurrentReplayPostgresApiFactory(fixture.ConnectionString);
        var email = $"concurrent-replay-pg-{Guid.NewGuid():N}@example.test";
        const string originalPassword = "StrongPassword!123";
        const string alphaPassword = "AlphaConcurrent!111";
        const string bravoPassword = "BravoConcurrent!222";
        const string thirdPassword = "ThirdConcurrent!333";

        Guid userId;
        string token;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var now = DateTime.UtcNow;
            var user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                DisplayName = "Synthetic Concurrent Replay Postgres User",
                PreferredLanguage = "en",
                TimeZone = "UTC",
                CreatedAt = now,
                UpdatedAt = now,
                IsActive = true,
            };
            var created = await userManager.CreateAsync(user, originalPassword);
            Assert.True(created.Succeeded, "Synthetic user creation failed on the PostgreSQL fixture.");
            userId = user.Id;

            // Minted in process, exactly as on the SQLite axis: no queue, no transport, no delivery.
            token = await userManager.GeneratePasswordResetTokenAsync(user);
        }

        var stampBeforeReset = await ReadSecurityStampAsync(factory, userId);
        Assert.False(string.IsNullOrWhiteSpace(stampBeforeReset));

        factory.Hasher.ArmDoubleSpend(userId, alphaPassword, bravoPassword);
        using var alphaClient = factory.CreateClient();
        using var bravoClient = factory.CreateClient();
        HttpResponseMessage alpha;
        HttpResponseMessage bravo;
        try
        {
            var alphaTask = ResetAsync(alphaClient, email, token, alphaPassword);
            var bravoTask = ResetAsync(bravoClient, email, token, bravoPassword);
            alpha = await alphaTask.WaitAsync(RequestTimeout);
            bravo = await bravoTask.WaitAsync(RequestTimeout);
        }
        finally
        {
            factory.Hasher.Disarm();
        }

        using (alpha)
        using (bravo)
        {
            // Non-vacuity, identical to the SQLite axis: both submissions must have been inside
            // Identity at the same time, each holding the identical pre-reset security stamp.
            Assert.False(
                factory.Hasher.RendezvousTimedOut,
                "The two reset submissions never met inside Identity; the double-spend window was not exercised.");
            Assert.Equal(2, factory.Hasher.ObservedStamps.Count);
            Assert.All(factory.Hasher.ObservedStamps, observed => Assert.Equal(stampBeforeReset, observed));

            var statuses = new[] { alpha.StatusCode, bravo.StatusCode };
            Assert.Equal(1, statuses.Count(status => status == HttpStatusCode.OK));
            Assert.Equal(1, statuses.Count(status => status == HttpStatusCode.BadRequest));

            var loser = alpha.StatusCode == HttpStatusCode.OK ? bravo : alpha;

            // Provider-level witness on the production engine: the forced race resolves through
            // exactly two optimistic-concurrency-guarded UPDATEs against the users table, each
            // carrying the ConcurrencyStamp predicate in its WHERE clause - one per submission.
            // This is the statement-level evidence the SQLite lane could not produce.
            var guardedUserUpdates = factory.Witness.Records
                .Where(record => record.IsGuardedAspNetUsersUpdate)
                .ToList();
            Assert.Equal(2, guardedUserUpdates.Count);

            // Generic loser envelope on PostgreSQL too: the ordinary invalid-link answer, never a
            // timeout, never a 500 from the concurrency failure, never a distinguishable error.
            var loserBody = await loser.Content.ReadAsStringAsync();
            Assert.Equal(
                "PASSWORD_RESET_INVALID",
                JsonDocument.Parse(loserBody).RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.False(loser.Headers.Contains("Set-Cookie"));

            // Byte-identical to what an unknown account receives with the same token: losing the
            // race on the production provider reveals nothing about whether the address exists.
            using var unknownAccountClient = factory.CreateClient();
            using var unknownAccount = await ResetAsync(
                unknownAccountClient,
                $"missing-{Guid.NewGuid():N}@example.test",
                token,
                thirdPassword);
            Assert.Equal(HttpStatusCode.BadRequest, unknownAccount.StatusCode);
            Assert.Equal(loserBody, await unknownAccount.Content.ReadAsStringAsync());

            // Redemption still signs nobody in, winner included.
            var winnerClient = alpha.StatusCode == HttpStatusCode.OK ? alphaClient : bravoClient;
            Assert.Equal(HttpStatusCode.Unauthorized, (await winnerClient.GetAsync("/api/auth/me")).StatusCode);

            var stampAfterReset = await ReadSecurityStampAsync(factory, userId);
            Assert.NotEqual(stampBeforeReset, stampAfterReset);

            var winnerPassword = alpha.StatusCode == HttpStatusCode.OK ? alphaPassword : bravoPassword;
            var loserPassword = alpha.StatusCode == HttpStatusCode.OK ? bravoPassword : alphaPassword;

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await userManager.FindByIdAsync(userId.ToString());
                Assert.NotNull(user);

                // Winner-only credential on PostgreSQL: no split-brain, no last writer silently
                // owning the account while the other client was told the link was invalid.
                Assert.True(await userManager.CheckPasswordAsync(user!, winnerPassword));
                Assert.False(await userManager.CheckPasswordAsync(user!, loserPassword));
                Assert.False(await userManager.CheckPasswordAsync(user!, originalPassword));

                // The row's persisted hash itself is re-verified against the winner's password:
                // the loser's password never reached the stored credential.
                var storedHash = await scope.ServiceProvider
                    .GetRequiredService<TaslimDbContext>()
                    .Users
                    .Where(item => item.Id == userId)
                    .Select(item => item.PasswordHash)
                    .SingleAsync();
                Assert.NotNull(storedHash);
                var verified = new PasswordHasher<ApplicationUser>().VerifyHashedPassword(
                    new ApplicationUser { Id = userId },
                    storedHash!,
                    winnerPassword);
                Assert.Contains(
                    verified,
                    new[] { PasswordVerificationResult.Success, PasswordVerificationResult.SuccessRehashNeeded });

                // Spent, not merely rotated.
                Assert.False(await userManager.VerifyUserTokenAsync(
                    user!,
                    userManager.Options.Tokens.PasswordResetTokenProvider,
                    UserManager<ApplicationUser>.ResetPasswordTokenPurpose,
                    token));
            }

            using var thirdClient = factory.CreateClient();
            using var thirdAttempt = await ResetAsync(thirdClient, email, token, thirdPassword);
            Assert.Equal(HttpStatusCode.BadRequest, thirdAttempt.StatusCode);
            Assert.Equal(loserBody, await thirdAttempt.Content.ReadAsStringAsync());
        }

        // Mail isolation on this axis as well: nothing sent, nothing queued.
        Assert.Equal(0, factory.Sender.SendCount);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            Assert.Equal(0, await db.PasswordResetEmailOutbox.CountAsync());
        }
    }

    private static async Task<string?> ReadSecurityStampAsync(ConcurrentReplayPostgresApiFactory factory, Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByIdAsync(userId.ToString());
        Assert.NotNull(user);
        return await userManager.GetSecurityStampAsync(user!);
    }

    private static async Task<string> GetCsrfTokenAsync(HttpClient client)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        return csrf.GetProperty("token").GetString()!;
    }

    private static async Task<HttpResponseMessage> ResetAsync(
        HttpClient client,
        string email,
        string token,
        string newPassword)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/password/reset")
        {
            Content = JsonContent.Create(new { email, token, newPassword }),
        };

        // Each attempt carries its own browser-bound antiforgery pair, exactly as two real tabs would.
        request.Headers.Add("X-CSRF-TOKEN", await GetCsrfTokenAsync(client));
        return await client.SendAsync(request).WaitAsync(RequestTimeout);
    }

    /// <summary>
    /// Opt-in disposable PostgreSQL gate, byte-equivalent in behaviour to
    /// <c>DisposablePostgresFactAttribute</c> already defined in
    /// <c>GenerationJobDeferredPublicationTests.cs</c>: CI must be declared and the connection
    /// string must target the disposable <c>taslim_tests</c> database. Re-declared privately here
    /// so this file stays the single-file artifact of the lane and never edits another lane's
    /// fixture. Skips (never fails) when absent.
    /// </summary>
    public sealed class DisposablePostgresFactAttribute : FactAttribute
    {
        public DisposablePostgresFactAttribute()
        {
            var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
            if (!string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(connectionString))
            {
                Skip = "Requires the disposable PostgreSQL service configured by the API CI job.";
                return;
            }

            try
            {
                if (!string.Equals(
                        new NpgsqlConnectionStringBuilder(connectionString).Database,
                        "taslim_tests",
                        StringComparison.Ordinal))
                {
                    Skip = "Runs only against the disposable taslim_tests database configured by API CI.";
                }
            }
            catch (ArgumentException)
            {
                Skip = "The API CI PostgreSQL test connection string is unavailable.";
            }
        }
    }

    /// <summary>
    /// Creates one throwaway schema inside the disposable database and returns a connection string
    /// pinned to it through the search path, so this lane cannot collide with the other Postgres
    /// evidence lanes and never touches the public schema. The schema is dropped with the fixture.
    /// No production migration is executed: schema shape comes from EnsureCreated, mirroring the
    /// SQLite axis's Database:ApplyMigrations=false isolation.
    /// </summary>
    private sealed class DisposablePostgresTestDatabase : IDisposable
    {
        private readonly string schemaName;

        public string ConnectionString { get; }

        private DisposablePostgresTestDatabase(string schemaName, string connectionString)
        {
            this.schemaName = schemaName;
            ConnectionString = connectionString;
        }

        public static DisposablePostgresTestDatabase Create()
        {
            var adminBuilder = new NpgsqlConnectionStringBuilder(
                Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")!)
            {
                Pooling = false,
            };
            var schema = $"replay_pg_{Guid.NewGuid():N}";
            using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
            {
                admin.Open();
                using var create = admin.CreateCommand();
                create.CommandText = $"CREATE SCHEMA \"{schema}\"";
                create.ExecuteNonQuery();
            }

            var testBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
            {
                SearchPath = schema,
                Pooling = false,
            };
            return new DisposablePostgresTestDatabase(schema, testBuilder.ConnectionString);
        }

        public void Dispose()
        {
            var adminBuilder = new NpgsqlConnectionStringBuilder(
                Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")!)
            {
                Pooling = false,
            };
            try
            {
                using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
                admin.Open();
                using var drop = admin.CreateCommand();
                drop.CommandText = $"DROP SCHEMA IF EXISTS \"{schemaName}\" CASCADE";
                drop.ExecuteNonQuery();
            }
            catch (PostgresException)
            {
                // Best effort: the disposable database belongs to CI and is torn down anyway.
            }
        }
    }

    /// <summary>
    /// TestServer fixture for the PostgreSQL axis. Isolation is identical to the SQLite axis -
    /// recovery flag on, migrations off, in-process recording transport, delivery worker removed -
    /// with the provider swapped to the disposable schema and the SQL witness attached.
    /// </summary>
    private sealed class ConcurrentReplayPostgresApiFactory : WebApplicationFactory<Program>
    {
        private readonly RecordingEmailTransport sender = new();
        private readonly DoubleSpendPasswordHasher hasher = new();
        private readonly ConcurrencyUpdateWitnessInterceptor witness = new();

        public RecordingEmailTransport Sender => sender;

        public DoubleSpendPasswordHasher Hasher => hasher;

        public ConcurrencyUpdateWitnessInterceptor Witness => witness;

        public string ConnectionString { get; }

        public ConcurrentReplayPostgresApiFactory(string connectionString)
        {
            ConnectionString = connectionString;
            var options = new DbContextOptionsBuilder<TaslimDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            using var db = new TaslimDbContext(options);
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
                services.AddDbContext<TaslimDbContext>(options => options
                    .UseNpgsql(ConnectionString)
                    .AddInterceptors(witness));
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(sender);
                services.AddSingleton(hasher);

                // Decorate, never replace behaviour: the inner hasher is the application's own
                // PasswordHasher<TUser> built from the application's own registered options, so the
                // PostgreSQL rows carry the identical hash format the SQLite axis produced. The
                // wrapper only synchronizes and records while armed.
                services.RemoveAll<IPasswordHasher<ApplicationUser>>();
                services.AddSingleton<IPasswordHasher<ApplicationUser>>(provider => hasher.WithInner(
                    ActivatorUtilities.CreateInstance<PasswordHasher<ApplicationUser>>(provider)));

                var worker = services.SingleOrDefault(descriptor =>
                    descriptor.ServiceType == typeof(IHostedService)
                    && descriptor.ImplementationType == typeof(PasswordResetEmailWorker));
                if (worker is not null) services.Remove(worker);
            });
        }
    }

    /// <summary>
    /// One captured provider statement plus the row count the PostgreSQL server itself returned.
    /// </summary>
    public sealed record CommandWitnessRecord(string Sql, int? RowsAffected)
    {
        public bool IsGuardedAspNetUsersUpdate
        {
            get
            {
                var flattened = Sql.Replace("\"", string.Empty).Replace("[", string.Empty)
                    .Replace("]", string.Empty).ToLowerInvariant();
                return flattened.Contains("update aspnetusers")
                    && flattened.Contains("where")
                    && flattened.Contains("concurrencystamp");
            }
        }
    }

    /// <summary>
    /// Read-only EF Core interceptor: delegates every behaviour to the provider and records
    /// statement text (and, where the pipeline reports it, affected rows) for UPDATE commands.
    /// EF Core routes UPDATEs through the reader pipeline; the non-query overrides are attached
    /// for completeness. Nothing here changes execution.
    /// </summary>
    private sealed class ConcurrencyUpdateWitnessInterceptor : DbCommandInterceptor
    {
        private readonly object syncRoot = new();
        private readonly List<CommandWitnessRecord> records = new();

        public IReadOnlyList<CommandWitnessRecord> Records
        {
            get
            {
                lock (syncRoot)
                {
                    return records.ToList();
                }
            }
        }

        public override InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(
            System.Data.Common.DbCommand command,
            CommandEventData eventData,
            InterceptionResult<System.Data.Common.DbDataReader> result)
        {
            Capture(command, null);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command,
            CommandEventData eventData,
            InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Capture(command, null);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override int NonQueryExecuted(
            System.Data.Common.DbCommand command,
            CommandExecutedEventData eventData,
            int result)
        {
            Capture(command, result);
            return result;
        }

        public override async ValueTask<int> NonQueryExecutedAsync(
            System.Data.Common.DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            Capture(command, result);
            return await base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        }

        private void Capture(System.Data.Common.DbCommand command, int? rows)
        {
            var sql = command.CommandText;
            if (!sql.Contains("AspNetUsers", StringComparison.OrdinalIgnoreCase)
                || !sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            lock (syncRoot)
            {
                // One record per execution, never deduplicated by text: the two racers issue
                // parameter-identical statements, so collapsing by SQL text would erase the very
                // evidence this interceptor exists to capture.
                records.Add(new CommandWitnessRecord(sql, rows));
            }
        }
    }

    /// <summary>
    /// Pass-through <see cref="IPasswordHasher{TUser}"/> identical to the one on the SQLite axis:
    /// while armed for one synthetic account it makes the two reset submissions meet at the point
    /// Identity reaches only after a successful reset-token check, records the security stamp each
    /// was working from, and releases them together so both persist against the same pre-reset
    /// state on the production provider.
    /// </summary>
    private sealed class DoubleSpendPasswordHasher : IPasswordHasher<ApplicationUser>
    {
        private readonly object syncRoot = new();
        private IPasswordHasher<ApplicationUser>? inner;
        private Guid armedUserId;
        private string? firstPassword;
        private string? secondPassword;
        private int arrivals;
        private TaskCompletionSource<bool>? released;
        private List<string?>? observedStamps;
        private bool rendezvousTimedOut;

        public IReadOnlyList<string?> ObservedStamps
        {
            get
            {
                lock (syncRoot)
                {
                    return observedStamps ?? (IReadOnlyList<string?>)Array.Empty<string?>();
                }
            }
        }

        public bool RendezvousTimedOut
        {
            get
            {
                lock (syncRoot)
                {
                    return rendezvousTimedOut;
                }
            }
        }

        public DoubleSpendPasswordHasher WithInner(IPasswordHasher<ApplicationUser> inner)
        {
            lock (syncRoot)
            {
                this.inner = inner;
            }

            return this;
        }

        public void ArmDoubleSpend(Guid userId, string firstPassword, string secondPassword)
        {
            lock (syncRoot)
            {
                arrivals = 0;
                observedStamps = new List<string?>();
                rendezvousTimedOut = false;
                released = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                armedUserId = userId;
                this.firstPassword = firstPassword;
                this.secondPassword = secondPassword;
            }
        }

        public void Disarm()
        {
            lock (syncRoot)
            {
                armedUserId = Guid.Empty;
                firstPassword = null;
                secondPassword = null;
                released?.TrySetResult(true);
            }
        }

        public string HashPassword(ApplicationUser user, string password)
        {
            bool race;
            lock (syncRoot)
            {
                race = inner is not null
                    && armedUserId != Guid.Empty
                    && user.Id == armedUserId
                    && (string.Equals(firstPassword, password, StringComparison.Ordinal)
                        || string.Equals(secondPassword, password, StringComparison.Ordinal));
            }

            if (!race)
            {
                return RequireInner().HashPassword(user, password);
            }

            Task rendezvous;
            lock (syncRoot)
            {
                observedStamps!.Add(user.SecurityStamp);
                arrivals++;
                if (arrivals == 2)
                {
                    released!.TrySetResult(true);
                }

                rendezvous = released!.Task;
            }

            // Both submissions now hold the identical pre-reset stamp; releasing them together is
            // what turns this into a genuine double-spend rather than an accidental replay.
            var met = rendezvous.Wait(RendezvousTimeout);
            if (!met)
            {
                lock (syncRoot)
                {
                    rendezvousTimedOut = true;
                }
            }

            return RequireInner().HashPassword(user, password);
        }

        public PasswordVerificationResult VerifyHashedPassword(
            ApplicationUser user,
            string hashedPassword,
            string password)
        {
            return RequireInner().VerifyHashedPassword(user, hashedPassword, password);
        }

        private IPasswordHasher<ApplicationUser> RequireInner() =>
            inner ?? throw new InvalidOperationException(
                "The observing hasher is not attached to the application password hasher.");
    }

    private sealed class RecordingEmailTransport : IEmailSender
    {
        private int sendCount;

        public bool IsConfigured => true;

        public int SendCount => Volatile.Read(ref sendCount);

        public Task SendAsync(string recipient, string subject, string plainTextBody, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref sendCount);
            return Task.CompletedTask;
        }
    }
}
