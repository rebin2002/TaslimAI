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
using Taslim.Api.Domain;
using Taslim.Api.Email;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Independent security evidence for draft #1121 release gate 2 ("reset token ... single-use").
///
/// Every existing reset-token rejection test on this branch is strictly sequential, so the second
/// request only ever observes state the first request already committed:
/// - <c>PasswordResetTokenReusedBoundaryTests</c>: redeem, then replay.
/// - <c>PasswordRecoveryTests.Reset_requires_csrf_rejects_invalid_tokens_and_consumes_valid_token_once_without_signing_in</c>:
///   redeem, then replay.
/// - <c>PasswordRecoveryTests.Reset_with_valid_identity_token_and_wrong_csrf_preserves_token_and_account_state</c>:
///   CSRF reject, redeem, then replay.
/// - <c>PasswordRecoveryEmailDeliveryEndToEndTests</c>: supersession and expiry by sequential redemption.
/// Concurrency lanes exist for <c>/password/forgot</c>
/// (<c>PasswordRecoveryRateLimitHttpAcceptanceTests.Forgot_concurrency_limit_is_two_and_rejects_without_queueing</c>)
/// and for the delivery worker, but no test redeems one token twice SIMULTANEOUSLY - the state a
/// double-submitted or stolen link actually produces, where two requests validate the same pre-reset
/// security stamp before either one persists.
///
/// This lane pins that one replay invariant and nothing else. The double-spend window is forced with a
/// test-only rendezvous inside the password-hashing step, which Identity reaches only after the reset
/// token check has already passed, so the interleaving is reproduced on every run instead of being
/// left to thread timing; the lane fails closed if the overlap was not actually reached. Synthetic
/// account, ephemeral throwaway SQLite file, in-process transport, delivery worker removed: no email,
/// no SMTP, no provider call, no migration, no secret, and no change to production auth code, schema
/// or configuration.
/// </summary>
public sealed class PasswordResetTokenConcurrentReplayTests
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RendezvousTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task One_reset_token_redeemed_twice_simultaneously_changes_exactly_one_password()
    {
        using var factory = new ConcurrentReplayApiFactory();
        var email = $"concurrent-replay-{Guid.NewGuid():N}@example.test";
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
                DisplayName = "Synthetic Concurrent Replay User",
                PreferredLanguage = "en",
                TimeZone = "UTC",
                CreatedAt = now,
                UpdatedAt = now,
                IsActive = true,
            };
            var created = await userManager.CreateAsync(user, originalPassword);
            Assert.True(created.Succeeded, "Synthetic user creation failed; identity error details omitted.");
            userId = user.Id;

            // Minted in process, so this lane never queues or sends mail - the same isolation the
            // existing sequential replay boundary test uses.
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
            // Non-vacuity, by construction rather than by timing: Identity hashes the replacement
            // password only after the reset token check passed, so two submissions that both carry the
            // identical pre-reset security stamp were simultaneously inside the redemption window. Two
            // independent signals refuse a green if that did not happen: a rendezvous that the partner
            // never reached, and a second arrival reading a stamp the first submission already rotated.
            Assert.False(
                factory.Hasher.RendezvousTimedOut,
                "The two reset submissions never met inside Identity; the double-spend window was not exercised.");
            Assert.Equal(
                2,
                factory.Hasher.ObservedStamps.Count);
            Assert.All(factory.Hasher.ObservedStamps, observed => Assert.Equal(stampBeforeReset, observed));

            var statuses = new[] { alpha.StatusCode, bravo.StatusCode };
            Assert.Equal(1, statuses.Count(status => status == HttpStatusCode.OK));
            Assert.Equal(1, statuses.Count(status => status == HttpStatusCode.BadRequest));

            var loser = alpha.StatusCode == HttpStatusCode.OK ? bravo : alpha;
            var winnerClient = alpha.StatusCode == HttpStatusCode.OK ? alphaClient : bravoClient;
            var winnerPassword = alpha.StatusCode == HttpStatusCode.OK ? alphaPassword : bravoPassword;
            var loserPassword = alpha.StatusCode == HttpStatusCode.OK ? bravoPassword : alphaPassword;

            // The losing racer gets the ordinary invalid-link answer, never a timeout, a 500 from an
            // unhandled concurrency failure, or a distinguishable error.
            var loserBody = await loser.Content.ReadAsStringAsync();
            Assert.Equal(
                "PASSWORD_RESET_INVALID",
                JsonDocument.Parse(loserBody).RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.False(loser.Headers.Contains("Set-Cookie"));

            // ... and it is byte-identical to what an unknown account receives with the same token, so
            // losing a race reveals nothing about whether the address exists.
            using var unknownAccountClient = factory.CreateClient();
            using var unknownAccount = await ResetAsync(
                unknownAccountClient,
                $"missing-{Guid.NewGuid():N}@example.test",
                token,
                thirdPassword);
            Assert.Equal(HttpStatusCode.BadRequest, unknownAccount.StatusCode);
            Assert.Equal(loserBody, await unknownAccount.Content.ReadAsStringAsync());

            // Redemption still does not sign anyone in, winner included.
            Assert.Equal(HttpStatusCode.Unauthorized, (await winnerClient.GetAsync("/api/auth/me")).StatusCode);

            var stampAfterReset = await ReadSecurityStampAsync(factory, userId);
            Assert.NotEqual(stampBeforeReset, stampAfterReset);

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await userManager.FindByIdAsync(userId.ToString());
                Assert.NotNull(user);

                // Exactly one credential change survived: no split-brain in which the account answers
                // to the loser's password while its response claimed the link was invalid, and no
                // account left stuck on the pre-reset password.
                Assert.True(await userManager.CheckPasswordAsync(user!, winnerPassword));
                Assert.False(await userManager.CheckPasswordAsync(user!, loserPassword));
                Assert.False(await userManager.CheckPasswordAsync(user!, originalPassword));

                // The token is spent, not merely rotated: it still verifies for nobody afterwards.
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

        // Nothing in this lane may reach a mail transport or queue a delivery.
        Assert.Equal(0, factory.Sender.SendCount);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            Assert.Equal(0, await db.PasswordResetEmailOutbox.CountAsync());
        }
    }

    private static async Task<string?> ReadSecurityStampAsync(ConcurrentReplayApiFactory factory, Guid userId)
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
    /// Throwaway, file-backed SQLite fixture so the two reset submissions use separate physical
    /// connections rather than one shared in-memory connection. Wiring mirrors the password-recovery
    /// fixtures already on this branch: recovery flag on, no migrations, in-process recording transport,
    /// delivery worker removed so nothing can send.
    /// </summary>
    private sealed class ConcurrentReplayApiFactory : WebApplicationFactory<Program>
    {
        private readonly string databasePath = Path.Combine(
            Path.GetTempPath(),
            $"taslim-concurrent-replay-{Guid.NewGuid():N}.sqlite");

        private readonly string connectionString;
        private readonly RecordingEmailTransport sender = new();
        private readonly DoubleSpendPasswordHasher hasher = new();

        public RecordingEmailTransport Sender => sender;

        public DoubleSpendPasswordHasher Hasher => hasher;

        public ConcurrentReplayApiFactory()
        {
            connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false,
                DefaultTimeout = 30,
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
                services.AddSingleton<IEmailSender>(sender);
                services.AddSingleton(hasher);

                // Decorate, never replace behaviour: the inner hasher is the application's own
                // PasswordHasher<TUser> built from the application's own registered options, so hash
                // format, iteration parameters and verification results are unchanged. The wrapper only
                // synchronizes and records while armed, and is a pure delegation otherwise.
                services.RemoveAll<IPasswordHasher<ApplicationUser>>();
                services.AddSingleton<IPasswordHasher<ApplicationUser>>(provider => hasher.WithInner(
                    ActivatorUtilities.CreateInstance<PasswordHasher<ApplicationUser>>(provider)));

                var worker = services.SingleOrDefault(descriptor =>
                    descriptor.ServiceType == typeof(IHostedService)
                    && descriptor.ImplementationType == typeof(PasswordResetEmailWorker));
                if (worker is not null) services.Remove(worker);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    /// <summary>
    /// Pass-through <see cref="IPasswordHasher{TUser}"/>. While armed for one synthetic account it makes
    /// the two reset submissions meet at the point Identity reaches only after a successful reset-token
    /// check, records the security stamp each was working from, and releases them together so both
    /// persist against the same pre-reset state.
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

            // Both submissions now hold the identical pre-reset stamp; releasing them together is what
            // turns this into a genuine double-spend rather than an accidental sequential replay.
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
