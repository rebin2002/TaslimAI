using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Domain;
using Taslim.Api.Email;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

// Deterministic, provider-free failure coverage for the production SmtpEmailSender against
// hostile loopback fake SMTP servers. PR #686's loopback suite only exercises a cooperative
// fake that always accepts delivery; PRs #721 and #739 stay on the in-memory
// RecordingEmailSender. Nothing yet proves the protocol-level fail-closed contract when the
// transport itself refuses the message: no credential or token may ever cross the socket in
// the clear, the outbox must stop in the terminal Failed state with only a sanitized failure
// code, the user must keep receiving the identical generic acceptance, and a later request
// must re-arm delivery. No live mailboxes, providers, or secrets are used.
[Collection(PasswordRecoveryLocalSmtpCollection.Name)]
public sealed class PasswordResetSmtpDeliveryFailureTests
{
    private const string InitialPassword = "StrongPassword!123";
    private const string RefusedSmtpUsername = "loopback-refusal-user";
    private const string RefusedSmtpPassword = "loopback-refusal-password-not-a-secret";

    [Fact]
    public async Task Starttls_refusing_fake_smtp_fails_closed_with_terminal_failed_state_and_generic_user_response()
    {
        await using var factory = new RefusingSmtpApiFactory(RefusalMode.NoStartTlsAdvertisement, maxDeliveryAttempts: 1);
        var email = $"smtp-fail-tls-{Guid.NewGuid():N}@example.test";

        using (var availability = await factory.CreateClient().GetAsync("/api/auth/password-recovery/availability"))
        {
            // The SMTP transport is only probed at delivery time, so an unreachable or
            // non-TLS provider must not flip the user-facing recovery availability signal.
            Assert.Equal(HttpStatusCode.OK, availability.StatusCode);
            Assert.True((await availability.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("available").GetBoolean());
        }

        using (var registration = factory.CreateClient())
        using (var response = await RegisterAsync(registration, email))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var requestClient = factory.CreateClient();
        using var forgot = await ForgotAsync(requestClient, email);
        Assert.Equal(HttpStatusCode.Accepted, forgot.StatusCode);
        Assert.Contains("If an account matches", await forgot.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var entry = await WaitForTerminalStateAsync(factory, email, PasswordResetDeliveryStates.Failed);
        Assert.Equal("SMTP_DELIVERY_FAILED", entry.LastFailureCode);
        Assert.Equal(1, entry.AttemptCount);
        Assert.Null(entry.LeaseExpiresAt);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByEmailAsync(email);
            Assert.NotNull(user);
            var still = await db.PasswordResetEmailOutbox.AsNoTracking().SingleAsync(item => item.UserId == user!.Id);
            // MaxDeliveryAttempts is 1, so the Failed status is terminal: no later poll
            // may resurrect the entry into another SMTP connection.
            Assert.Equal(PasswordResetDeliveryStates.Failed, still.Status);
            Assert.Equal(1, still.AttemptCount);
            Assert.True(await userManager.CheckPasswordAsync(user!, InitialPassword));
        }

        // A refused delivery mints no link: the reset endpoint can only answer with the
        // canonical generic failure, identical for a fabricated token and an unknown account.
        using var bogusReset = factory.CreateClient();
        using var bogus = await ResetAsync(bogusReset, email, "synthetic-unissued-token", "RefusedDelivery!123");
        using var unknownReset = factory.CreateClient();
        using var unknown = await ResetAsync(unknownReset, $"absent-{Guid.NewGuid():N}@example.test", "synthetic-unissued-token", "RefusedDelivery!123");
        await AssertInvalidResetAsync(bogus);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await bogus.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Auth_refusing_fake_smtp_backs_off_then_parks_attempts_exhausted_entry_without_mailable_content()
    {
        await using var factory = new RefusingSmtpApiFactory(RefusalMode.TlsThenAuthDeny, maxDeliveryAttempts: 2);
        var previousValidationCallback = ServicePointManager.ServerCertificateValidationCallback;
        ServicePointManager.ServerCertificateValidationCallback = (_, certificate, _, _) =>
            certificate is not null
            && string.Equals(certificate.GetCertHashString(), factory.Smtp.CertificateThumbprint, StringComparison.OrdinalIgnoreCase);

        try
        {
            var email = $"smtp-fail-auth-{Guid.NewGuid():N}@example.test";
            using (var registration = factory.CreateClient())
            using (var response = await RegisterAsync(registration, email))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var requestClient = factory.CreateClient();
            using var forgot = await ForgotAsync(requestClient, email);
            Assert.Equal(HttpStatusCode.Accepted, forgot.StatusCode);

            // First refusal: the worker keeps the entry Pending with a bounded backoff and a
            // sanitized code that never contains the recipient address or provider text.
            var firstAttempt = await WaitForAttemptAsync(factory, email, attempts: 1, PasswordResetDeliveryStates.Pending);
            Assert.Equal("SMTP_DELIVERY_FAILED", firstAttempt.LastFailureCode);
            Assert.True(firstAttempt.NextAttemptAt > DateTime.UtcNow.AddMinutes(-1),
                "A failed first attempt must stay scheduled for a future backoff instead of spinning.");

            Guid userId;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await userManager.FindByEmailAsync(email);
                Assert.NotNull(user);
                userId = user!.Id;
                var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
                var entry = await db.PasswordResetEmailOutbox.SingleAsync(item => item.UserId == userId);
                // The production backoff is minutes long; advance only this disposable
                // fixture row so the second and final attempt is polled promptly.
                entry.NextAttemptAt = DateTime.UtcNow.AddSeconds(-1);
                await db.SaveChangesAsync();
            }

            var exhausted = await WaitForTerminalStateAsync(factory, email, PasswordResetDeliveryStates.Failed, userId);
            Assert.Equal(2, exhausted.AttemptCount);
            Assert.Equal("SMTP_DELIVERY_FAILED", exhausted.LastFailureCode);
            var terminalNextAttemptAt = exhausted.NextAttemptAt;

            // Both refusals happened before any message content was offered: the fake
            // captured nothing, so no reset link or token ever reached a mailbox.
            // The production sender never proceeds to the DATA phase once the transport
            // refuses the transaction, so the minted reset token never crosses the socket.
            Assert.Equal(0, factory.Smtp.DataOfferedCount);
            Assert.True(factory.Smtp.AllSessionsCompletedTlsHandshake,
                "The production sender must negotiate STARTTLS before offering credentials.");

            // Re-arming with a fresh request is allowed once the account cooldown is
            // elapsed; the terminal Failed row is reset instead of blocking recovery forever.
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
                var entry = await db.PasswordResetEmailOutbox.SingleAsync(item => item.UserId == userId);
                entry.LastRequestedAt = DateTime.UtcNow.AddMinutes(-2);
                await db.SaveChangesAsync();
            }

            using var secondRequestClient = factory.CreateClient();
            using var secondForgot = await ForgotAsync(secondRequestClient, email);
            Assert.Equal(HttpStatusCode.Accepted, secondForgot.StatusCode);
            var rearmed = await WaitForAttemptAsync(factory, email, attempts: 1, PasswordResetDeliveryStates.Pending, userId);
            Assert.Equal(1, rearmed.AttemptCount);
            // The fresh request re-armed the Failed row and the new attempt failed again
            // with the same sanitized code, proving recovery is not permanently bricked.
            Assert.Equal("SMTP_DELIVERY_FAILED", rearmed.LastFailureCode);
            Assert.True(rearmed.NextAttemptAt > terminalNextAttemptAt,
                "A re-armed outbox row must be scheduled for a new delivery window.");
        }
        finally
        {
            ServicePointManager.ServerCertificateValidationCallback = previousValidationCallback;
        }
    }

    private static async Task AssertInvalidResetAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PASSWORD_RESET_INVALID", body.GetProperty("error").GetProperty("code").GetString());
    }

    private static async Task<PasswordResetEmailOutboxEntry> WaitForTerminalStateAsync(
        RefusingSmtpApiFactory factory,
        string email,
        string status,
        Guid? knownUserId = null)
    {
        var userId = knownUserId ?? await FindUserIdAsync(factory, email);
        return await WaitForEntryAsync(factory, userId,
            entry => entry.Status == status && entry.LeaseExpiresAt is null,
            $"Timed out waiting for the synthetic SMTP failure to park the outbox entry as {status}.");
    }

    private static async Task<PasswordResetEmailOutboxEntry> WaitForAttemptAsync(
        RefusingSmtpApiFactory factory,
        string email,
        int attempts,
        string status,
        Guid? knownUserId = null)
    {
        var userId = knownUserId ?? await FindUserIdAsync(factory, email);
        return await WaitForEntryAsync(factory, userId,
            entry => entry.AttemptCount == attempts && entry.Status == status,
            $"Timed out waiting for attempt {attempts} of the synthetic SMTP failure to settle as {status}.");
    }

    private static async Task<PasswordResetEmailOutboxEntry> WaitForEntryAsync(
        RefusingSmtpApiFactory factory,
        Guid userId,
        Func<PasswordResetEmailOutboxEntry, bool> isReady,
        string timeoutMessage)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(15))
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var entry = await db.PasswordResetEmailOutbox.AsNoTracking().SingleOrDefaultAsync(item => item.UserId == userId);
            if (entry is not null && isReady(entry)) return entry;
            await Task.Delay(50);
        }
        throw new Xunit.Sdk.XunitException(timeoutMessage);
    }

    private static async Task<Guid> FindUserIdAsync(RefusingSmtpApiFactory factory, string email)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);
        return user!.Id;
    }

    private static async Task<HttpResponseMessage> RegisterAsync(HttpClient client, string email) =>
        await SendWithCsrfAsync(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "SMTP Refusal Test User",
            email,
            password = InitialPassword,
            preferredLanguage = "en",
        });

    private static async Task<HttpResponseMessage> ForgotAsync(HttpClient client, string email) =>
        await SendWithCsrfAsync(client, HttpMethod.Post, "/api/auth/password/forgot", new { email });

    private static async Task<HttpResponseMessage> ResetAsync(HttpClient client, string email, string token, string newPassword) =>
        await SendWithCsrfAsync(client, HttpMethod.Post, "/api/auth/password/reset", new { email, token, newPassword });

    private static async Task<HttpResponseMessage> SendWithCsrfAsync(HttpClient client, HttpMethod method, string path, object payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(payload) };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        return await client.SendAsync(request);
    }

    private enum RefusalMode
    {
        // EHLO never advertises STARTTLS, so the production sender must refuse to send
        // credentials over a plaintext transport.
        NoStartTlsAdvertisement,
        // STARTTLS succeeds but every AUTH attempt is denied with 535, so the sender must
        // fail with an authenticated-transport SmtpException before any mail content.
        TlsThenAuthDeny,
    }

    private sealed class RefusingSmtpApiFactory : WebApplicationFactory<Program>, IAsyncDisposable
    {
        private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"taslim-smtp-refusal-{Guid.NewGuid():N}.db");
        private readonly string connectionString;
        public RefusingLoopbackSmtpServer Smtp { get; }

        public RefusingSmtpApiFactory(RefusalMode mode, int maxDeliveryAttempts)
        {
            Smtp = new RefusingLoopbackSmtpServer(mode);
            connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false,
                DefaultTimeout = 30,
            }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var db = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
            db.Database.EnsureCreated();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";
            var journalMode = Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The disposable SMTP refusal SQLite database did not enable WAL mode.");
            MaxDeliveryAttempts = maxDeliveryAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private string MaxDeliveryAttempts { get; }

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
                ["PasswordRecovery:MaxDeliveryAttempts"] = MaxDeliveryAttempts,
                ["PasswordRecovery:LeaseSeconds"] = "60",
                ["Email:Provider"] = "Smtp",
                ["Email:Host"] = "127.0.0.1",
                ["Email:Port"] = Smtp.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Email:EnableSsl"] = "true",
                ["Email:Username"] = RefusedSmtpUsername,
                ["Email:Password"] = RefusedSmtpPassword,
                ["Email:FromAddress"] = "password-reset@example.test",
                ["Email:FromName"] = "Taslim Refusal E2E",
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<TaslimDbContext>>();
                services.AddDbContext<TaslimDbContext>(options => options.UseSqlite(connectionString));
            });
        }

        async ValueTask IAsyncDisposable.DisposeAsync()
        {
            // Explicitly close the host and delete the disposable database first, then
            // stop the refusal server, so no listener survives between suites.
            Dispose();
            await Smtp.DisposeAsync();
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                foreach (var suffix in new[] { "", "-shm", "-wal" })
                    File.Delete(databasePath + suffix);
            }
        }
    }

    private sealed class RefusingLoopbackSmtpServer : IAsyncDisposable
    {
        private readonly RefusalMode mode;
        private readonly TcpListener listener;
        private readonly CancellationTokenSource stop = new();
        private readonly X509Certificate2 certificate;
        private readonly Task acceptLoop;
        private int dataOfferedCount;
        private int sessionCount;
        private int completedTlsHandshakes;

        public int Port { get; }
        public string CertificateThumbprint => certificate.Thumbprint;
        // Counts only DATA commands: the phase where the reset link and token would
        // actually leave the process. Envelope commands carry no token material.
        public int DataOfferedCount => Volatile.Read(ref dataOfferedCount);
        public int SessionCount => Volatile.Read(ref sessionCount);
        public bool AllSessionsCompletedTlsHandshake => Volatile.Read(ref completedTlsHandshakes) == Volatile.Read(ref sessionCount);

        public RefusingLoopbackSmtpServer(RefusalMode mode)
        {
            this.mode = mode;
            certificate = mode == RefusalMode.TlsThenAuthDeny ? CreateServerCertificate() : null!;

            TcpListener? started = null;
            var port = 0;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var candidate = new TcpListener(IPAddress.Loopback, 0);
                candidate.Start();
                var candidatePort = ((IPEndPoint)candidate.LocalEndpoint).Port;
                if (candidatePort == 465)
                {
                    candidate.Stop();
                    continue;
                }
                started = candidate;
                port = candidatePort;
                break;
            }
            listener = started ?? throw new InvalidOperationException("Could not allocate a non-465 loopback SMTP refusal test port.");
            Port = port;
            acceptLoop = AcceptLoopAsync();
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    Interlocked.Increment(ref sessionCount);
                    await HandleClientAsync(client, stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
            catch (SocketException) when (stop.IsCancellationRequested) { }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using var network = client.GetStream();
            Stream active = network;
            SslStream? tls = null;
            var readers = new List<StreamReader>();
            var writers = new List<StreamWriter>();
            StreamReader MakeReader(Stream stream)
            {
                var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                readers.Add(reader);
                return reader;
            }
            StreamWriter MakeWriter(Stream stream)
            {
                var writer = new StreamWriter(stream, Encoding.ASCII, bufferSize: 1024, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
                writers.Add(writer);
                return writer;
            }

            var reader = MakeReader(active);
            var writer = MakeWriter(active);
            var secure = false;
            try
            {
                await writer.WriteLineAsync("220 localhost ESMTP refusing");
                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
                {
                    var upper = line.ToUpperInvariant();
                    if (upper.StartsWith("EHLO ", StringComparison.Ordinal) || upper.StartsWith("HELO ", StringComparison.Ordinal))
                    {
                        await writer.WriteLineAsync("250-localhost");
                        await writer.WriteLineAsync("250-SIZE 10485760");
                        if (mode == RefusalMode.TlsThenAuthDeny && !secure) await writer.WriteLineAsync("250-STARTTLS");
                        if (secure) await writer.WriteLineAsync("250-AUTH LOGIN PLAIN");
                        await writer.WriteLineAsync("250 OK");
                    }
                    else if (upper == "STARTTLS")
                    {
                        if (secure || mode == RefusalMode.NoStartTlsAdvertisement)
                        {
                            await writer.WriteLineAsync("454 4.7.0 TLS not available");
                            continue;
                        }
                        await writer.WriteLineAsync("220 2.0.0 Begin TLS");
                        tls = new SslStream(network, leaveInnerStreamOpen: true);
                        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                        {
                            ServerCertificate = certificate,
                            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                            ClientCertificateRequired = false,
                        }, cancellationToken);
                        reader = MakeReader(active = tls);
                        writer = MakeWriter(active);
                        secure = true;
                        Interlocked.Increment(ref completedTlsHandshakes);
                    }
                    else if (upper.StartsWith("AUTH ", StringComparison.Ordinal))
                    {
                        if (!secure)
                        {
                            await writer.WriteLineAsync("530 5.7.0 STARTTLS required");
                            continue;
                        }
                        if (upper.StartsWith("AUTH LOGIN", StringComparison.Ordinal))
                        {
                            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length < 3)
                            {
                                await writer.WriteLineAsync("334 VXNlcm5hbWU6");
                                await reader.ReadLineAsync(cancellationToken);
                            }
                            await writer.WriteLineAsync("334 UGFzc3dvcmQ6");
                            await reader.ReadLineAsync(cancellationToken);
                        }
                        else
                        {
                            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length < 3)
                            {
                                await writer.WriteLineAsync("334 ");
                                await reader.ReadLineAsync(cancellationToken);
                            }
                        }
                        // The refusal reason is deliberate and constant: credentials are
                        // never accepted, so no message content is ever offered.
                        await writer.WriteLineAsync("535 5.7.8 Authentication failed");
                    }
                    else if (upper.StartsWith("MAIL FROM:", StringComparison.Ordinal) || upper.StartsWith("RCPT TO:", StringComparison.Ordinal))
                    {
                        await writer.WriteLineAsync("554 5.7.1 Relay refused by deterministic fake");
                    }
                    else if (upper == "DATA")
                    {
                        Interlocked.Increment(ref dataOfferedCount);
                        await writer.WriteLineAsync("503 5.5.1 No transaction in progress");
                        // Drain the data terminator so the client can finish cleanly.
                        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null && line != ".") { }
                    }
                    else if (upper == "RSET" || upper == "NOOP")
                    {
                        await writer.WriteLineAsync("250 2.0.0 OK");
                    }
                    else if (upper == "QUIT")
                    {
                        await writer.WriteLineAsync("221 2.0.0 Bye");
                        return;
                    }
                    else
                    {
                        await writer.WriteLineAsync("250 2.0.0 OK");
                    }
                }
            }
            finally
            {
                foreach (var item in writers) await item.DisposeAsync();
                foreach (var item in readers) item.Dispose();
                if (tls is not null) await tls.DisposeAsync();
            }
        }

        private static X509Certificate2 CreateServerCertificate()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
            var usages = new OidCollection { new("1.3.6.1.5.5.7.3.1") };
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, critical: false));
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            listener.Stop();
            try { await acceptLoop; }
            catch (OperationCanceledException) { }
            if (certificate is not null) certificate.Dispose();
            stop.Dispose();
        }
    }
}
