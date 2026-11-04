using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
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
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Email;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

[CollectionDefinition("PasswordRecoveryLocalSmtp", DisableParallelization = true)]
public sealed class PasswordRecoveryLocalSmtpCollection
{
    public const string Name = "PasswordRecoveryLocalSmtp";
}

[Collection(PasswordRecoveryLocalSmtpCollection.Name)]
public sealed class PasswordRecoveryEmailDeliveryEndToEndTests
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);
    private const string InitialPassword = "StrongPassword!123";
    private const string ResetPasswordValue = "EvenStronger!123";
    private const string SyntheticSmtpUsername = "loopback-test-user";
    private const string SyntheticSmtpPassword = "loopback-test-password-not-a-secret";

    [Fact]
    public async Task Captured_loopback_smtp_reset_flow_covers_enumeration_token_supersession_expiry_and_sessions()
    {
        await using var smtp = new LoopbackSmtpCaptureServer(SyntheticSmtpUsername, SyntheticSmtpPassword);
        var previousValidationCallback = ServicePointManager.ServerCertificateValidationCallback;
        ServicePointManager.ServerCertificateValidationCallback = (_, certificate, _, _) =>
            certificate is not null
            && string.Equals(certificate.GetCertHashString(), smtp.CertificateThumbprint, StringComparison.OrdinalIgnoreCase);

        try
        {
            using var factory = new SmtpRecoveryApiFactory(smtp.Port, TokenLifetime);
            var email = $"smtp-e2e-{Guid.NewGuid():N}@example.test";

            using (var registration = factory.CreateClient())
            using (var response = await RegisterAsync(registration, email))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var firstSession = factory.CreateClient();
            using var secondSession = factory.CreateClient();
            using (var response = await LoginAsync(firstSession, email, InitialPassword))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using (var response = await LoginAsync(secondSession, email, InitialPassword))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await firstSession.GetAsync("/api/auth/me")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await secondSession.GetAsync("/api/auth/me")).StatusCode);

            var userId = await FindUserIdAsync(factory, email);

            var firstRequestStartedAt = DateTime.UtcNow;
            using var knownRequestClient = factory.CreateClient();
            using var unknownRequestClient = factory.CreateClient();
            using var knownResponse = await ForgotAsync(knownRequestClient, email);
            using var unknownResponse = await ForgotAsync(unknownRequestClient, "not-present@example.test");
            Assert.Equal(HttpStatusCode.Accepted, knownResponse.StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, unknownResponse.StatusCode);
            var genericAcceptedBody = await knownResponse.Content.ReadAsStringAsync();
            Assert.Equal(genericAcceptedBody, await unknownResponse.Content.ReadAsStringAsync());

            var firstMessage = await smtp.WaitForMessageAsync(0, WaitLimit);
            Assert.Equal(email, firstMessage.Recipient, ignoreCase: true);
            Assert.Contains("From: \"Taslim E2E\" <password-reset@example.test>", firstMessage.RawMessage, StringComparison.OrdinalIgnoreCase);
            var firstToken = ExtractToken(firstMessage.RawMessage, email);
            await WaitForDeliveryAsync(factory, userId, firstRequestStartedAt);
            Assert.Equal(1, smtp.CapturedCount); // Unknown addresses produce no SMTP delivery.

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
                var entry = await db.PasswordResetEmailOutbox.SingleAsync(item => item.UserId == userId);
                // Simulate elapsed account cooldown by changing only the disposable fixture row.
                entry.LastRequestedAt = DateTime.UtcNow.AddMinutes(-2);
                await db.SaveChangesAsync();
            }

            var secondRequestStartedAt = DateTime.UtcNow;
            using var secondRequestClient = factory.CreateClient();
            using var secondRequestResponse = await ForgotAsync(secondRequestClient, email);
            Assert.Equal(HttpStatusCode.Accepted, secondRequestResponse.StatusCode);
            Assert.Equal(genericAcceptedBody, await secondRequestResponse.Content.ReadAsStringAsync());
            var secondMessage = await smtp.WaitForMessageAsync(1, WaitLimit);
            Assert.Equal(email, secondMessage.Recipient, ignoreCase: true);
            var secondToken = ExtractToken(secondMessage.RawMessage, email);
            Assert.NotEqual(firstToken, secondToken);
            await WaitForDeliveryAsync(factory, userId, secondRequestStartedAt);

            Assert.True(await IsResetTokenValidAsync(factory, email, firstToken));
            Assert.True(await IsResetTokenValidAsync(factory, email, secondToken));

            using var resetClient = factory.CreateClient();
            using var reset = await ResetAsync(resetClient, email, firstToken, ResetPasswordValue);
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
            Assert.DoesNotContain(reset.Headers.GetValues("Set-Cookie"),
                cookie => cookie.StartsWith("taslim.auth=", StringComparison.OrdinalIgnoreCase));

            // A successful redemption rotates the Identity security stamp and supersedes
            // every other outstanding reset token, not just replay of the same token.
            using var replayClient = factory.CreateClient();
            using var replay = await ResetAsync(replayClient, email, firstToken, "AnotherPassword!456");
            using var supersededClient = factory.CreateClient();
            using var superseded = await ResetAsync(supersededClient, email, secondToken, "AnotherPassword!456");
            await AssertInvalidResetAsync(replay);
            await AssertInvalidResetAsync(superseded);

            Assert.Equal(HttpStatusCode.Unauthorized, (await firstSession.GetAsync("/api/auth/me")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await secondSession.GetAsync("/api/auth/me")).StatusCode);
            using (var response = await LoginAsync(firstSession, email, ResetPasswordValue))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using (var response = await LoginAsync(secondSession, email, InitialPassword))
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
                var entry = await db.PasswordResetEmailOutbox.SingleAsync(item => item.UserId == userId);
                entry.LastRequestedAt = DateTime.UtcNow.AddMinutes(-2);
                await db.SaveChangesAsync();
            }

            var expiryRequestStartedAt = DateTime.UtcNow;
            using var expiryRequestClient = factory.CreateClient();
            using var expiryRequest = await ForgotAsync(expiryRequestClient, email);
            Assert.Equal(HttpStatusCode.Accepted, expiryRequest.StatusCode);
            var expiryMessage = await smtp.WaitForMessageAsync(2, WaitLimit);
            var expiryToken = ExtractToken(expiryMessage.RawMessage, email);
            Assert.True(await IsResetTokenValidAsync(factory, email, expiryToken));
            await WaitForDeliveryAsync(factory, userId, expiryRequestStartedAt);

            await Task.Delay(TokenLifetime + TimeSpan.FromMilliseconds(500));
            using var expiredClient = factory.CreateClient();
            using var expired = await ResetAsync(expiredClient, email, expiryToken, "ExpiredAttempt!789");
            using var unknownResetClient = factory.CreateClient();
            using var unknownReset = await ResetAsync(unknownResetClient, "not-present@example.test", expiryToken, "ExpiredAttempt!789");
            await AssertInvalidResetAsync(expired);
            Assert.Equal(await unknownReset.Content.ReadAsStringAsync(), await expired.Content.ReadAsStringAsync());

            Assert.Equal(3, smtp.CapturedCount);
            Assert.All(smtp.Messages, message => Assert.Equal(email, message.Recipient, ignoreCase: true));
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

    private static async Task<Guid> FindUserIdAsync(SmtpRecoveryApiFactory factory, string email)
    {
        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);
        return user!.Id;
    }

    private static async Task<bool> IsResetTokenValidAsync(SmtpRecoveryApiFactory factory, string email, string token)
    {
        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);
        return await userManager.VerifyUserTokenAsync(
            user!,
            userManager.Options.Tokens.PasswordResetTokenProvider,
            UserManager<ApplicationUser>.ResetPasswordTokenPurpose,
            token);
    }

    private static async Task WaitForDeliveryAsync(SmtpRecoveryApiFactory factory, Guid userId, DateTime requestedAfter)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < WaitLimit)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var entry = await db.PasswordResetEmailOutbox.AsNoTracking().SingleAsync(item => item.UserId == userId);
            if (entry.Status == PasswordResetDeliveryStates.Delivered && entry.LastRequestedAt >= requestedAfter)
                return;
            await Task.Delay(40);
        }
        using var diagnosticScope = factory.Services.CreateScope();
        var diagnosticDb = diagnosticScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var finalEntry = await diagnosticDb.PasswordResetEmailOutbox.AsNoTracking().SingleAsync(item => item.UserId == userId);
        throw new Xunit.Sdk.XunitException(
            $"Timed out waiting for the synthetic reset-email outbox delivery. Status={finalEntry.Status}; " +
            $"AttemptCount={finalEntry.AttemptCount}; FailureCode={finalEntry.LastFailureCode ?? "none"}; " +
            $"LastRequestedAt={finalEntry.LastRequestedAt:O}; RequestedAfter={requestedAfter:O}; " +
            $"LeaseExpiresAt={finalEntry.LeaseExpiresAt:O}; NextAttemptAt={finalEntry.NextAttemptAt:O}.");
    }

    private static string ExtractToken(string rawMessage, string expectedEmail)
    {
        var headerEnd = rawMessage.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        Assert.True(headerEnd >= 0, "Captured SMTP message lacked MIME headers and body separation.");
        var headers = rawMessage[..headerEnd];
        var body = rawMessage[(headerEnd + 4)..];
        if (headers.Contains("Content-Transfer-Encoding: quoted-printable", StringComparison.OrdinalIgnoreCase))
            body = DecodeQuotedPrintable(body);

        var marker = "http://localhost:3000/reset-password#";
        var start = body.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            var safeBody = System.Text.RegularExpressions.Regex.Replace(body, "(?i)(token=)[^&\\s]+", "$1[REDACTED]");
            safeBody = System.Text.RegularExpressions.Regex.Replace(safeBody, "(?i)(reset-password#)[^\\s]+", "$1[REDACTED]");
            Assert.True(start >= 0, $"Captured SMTP message lacked the expected local reset link. Safe body preview: {safeBody[..Math.Min(safeBody.Length, 240)]}");
        }
        var url = body[start..].Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        var uri = new Uri(url);
        var values = uri.Fragment.TrimStart('#').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]), StringComparer.Ordinal);
        Assert.Equal(expectedEmail, values["email"]);
        Assert.Equal("en", values["lang"]);
        Assert.True(values.TryGetValue("token", out var token) && !string.IsNullOrWhiteSpace(token));
        return token!;
    }

    private static string DecodeQuotedPrintable(string value)
    {
        using var bytes = new MemoryStream();
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '=' && index + 2 < value.Length)
            {
                if (value[index + 1] == '\r' && value[index + 2] == '\n')
                {
                    index += 2;
                    continue;
                }
                if (byte.TryParse(value.AsSpan(index + 1, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var decoded))
                {
                    bytes.WriteByte(decoded);
                    index += 2;
                    continue;
                }
            }

            if (value[index] <= 0x7f)
                bytes.WriteByte((byte)value[index]);
            else
                bytes.Write(Encoding.UTF8.GetBytes(value[index].ToString()));
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static async Task<HttpResponseMessage> RegisterAsync(HttpClient client, string email) =>
        await SendWithCsrfAsync(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "SMTP Recovery E2E",
            email,
            password = InitialPassword,
            preferredLanguage = "en",
        });

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password) =>
        await SendWithCsrfAsync(client, HttpMethod.Post, "/api/auth/login", new { email, password });

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

    private sealed class SmtpRecoveryApiFactory : WebApplicationFactory<Program>
    {
        private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"taslim-smtp-recovery-{Guid.NewGuid():N}.db");
        private readonly string connectionString;
        private readonly int smtpPort;
        private readonly TimeSpan tokenLifetime;

        public SmtpRecoveryApiFactory(int smtpPort, TimeSpan tokenLifetime)
        {
            this.smtpPort = smtpPort;
            this.tokenLifetime = tokenLifetime;
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
                throw new InvalidOperationException("The disposable SMTP E2E SQLite database did not enable WAL mode.");
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
                ["PasswordRecovery:MaxDeliveryAttempts"] = "2",
                ["PasswordRecovery:LeaseSeconds"] = "60",
                ["Email:Provider"] = "Smtp",
                ["Email:Host"] = "127.0.0.1",
                ["Email:Port"] = smtpPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Email:EnableSsl"] = "true",
                ["Email:Username"] = SyntheticSmtpUsername,
                ["Email:Password"] = SyntheticSmtpPassword,
                ["Email:FromAddress"] = "password-reset@example.test",
                ["Email:FromName"] = "Taslim E2E",
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<TaslimDbContext>>();
                services.AddDbContext<TaslimDbContext>(options => options.UseSqlite(connectionString));
                services.PostConfigure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = tokenLifetime);
            });
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

    private sealed record CapturedSmtpMessage(string Recipient, string RawMessage);

    private sealed class LoopbackSmtpCaptureServer : IAsyncDisposable
    {
        private readonly string username;
        private readonly string password;
        private readonly TcpListener listener;
        private readonly CancellationTokenSource stop = new();
        private readonly X509Certificate2 certificate;
        private readonly ConcurrentQueue<CapturedSmtpMessage> messages = new();
        private readonly SemaphoreSlim messageAvailable = new(0);
        private readonly TaskCompletionSource<Exception> failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task acceptLoop;

        public int Port { get; }
        public string CertificateThumbprint => certificate.Thumbprint;
        public int CapturedCount => messages.Count;
        public IReadOnlyList<CapturedSmtpMessage> Messages => messages.ToArray();

        public LoopbackSmtpCaptureServer(string username, string password)
        {
            this.username = username;
            this.password = password;
            certificate = CreateServerCertificate();

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
            listener = started ?? throw new InvalidOperationException("Could not allocate a non-465 loopback SMTP test port.");
            Port = port;
            acceptLoop = AcceptLoopAsync();
        }

        public async Task<CapturedSmtpMessage> WaitForMessageAsync(int index, TimeSpan timeout)
        {
            using var deadline = new CancellationTokenSource(timeout);
            while (messages.Count <= index)
            {
                var signal = messageAvailable.WaitAsync(deadline.Token);
                var completed = await Task.WhenAny(signal, failure.Task);
                if (completed == failure.Task)
                    throw new Xunit.Sdk.XunitException($"Loopback SMTP server failed: {failure.Task.Result.GetType().Name}");
                await signal;
            }
            return messages.ToArray()[index];
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    await HandleClientAsync(client, stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
            catch (SocketException) when (stop.IsCancellationRequested) { }
            catch (Exception ex)
            {
                failure.TrySetResult(ex);
            }
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
            var authenticated = false;
            var recipient = string.Empty;
            try
            {
                await ReplyAsync(writer, "220 localhost ESMTP ready");
                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
                {
                    var upper = line.ToUpperInvariant();
                    if (upper.StartsWith("EHLO ", StringComparison.Ordinal) || upper.StartsWith("HELO ", StringComparison.Ordinal))
                    {
                        await ReplyAsync(writer, "250-localhost");
                        await ReplyAsync(writer, "250-SIZE 10485760");
                        if (!secure) await ReplyAsync(writer, "250-STARTTLS");
                        if (secure) await ReplyAsync(writer, "250-AUTH LOGIN PLAIN");
                        await ReplyAsync(writer, "250 OK");
                    }
                    else if (upper == "STARTTLS")
                    {
                        if (secure)
                        {
                            await ReplyAsync(writer, "454 TLS already active");
                            continue;
                        }
                        await ReplyAsync(writer, "220 2.0.0 Begin TLS");
                        tls = new SslStream(network, leaveInnerStreamOpen: true);
                        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                        {
                            ServerCertificate = certificate,
                            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                            ClientCertificateRequired = false,
                        }, cancellationToken);
                        active = tls;
                        reader = MakeReader(active);
                        writer = MakeWriter(active);
                        secure = true;
                    }
                    else if (upper.StartsWith("AUTH LOGIN", StringComparison.Ordinal))
                    {
                        if (!secure)
                        {
                            await ReplyAsync(writer, "530 5.7.0 STARTTLS required");
                            continue;
                        }
                        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        var user64 = parts.Length >= 3 ? parts[2] : null;
                        if (user64 is null)
                        {
                            await ReplyAsync(writer, "334 VXNlcm5hbWU6");
                            user64 = await reader.ReadLineAsync(cancellationToken) ?? string.Empty;
                        }
                        await ReplyAsync(writer, "334 UGFzc3dvcmQ6");
                        var password64 = await reader.ReadLineAsync(cancellationToken) ?? string.Empty;
                        authenticated = Decode(user64) == username && Decode(password64) == password;
                        await ReplyAsync(writer, authenticated ? "235 2.7.0 Authentication successful" : "535 5.7.8 Authentication failed");
                    }
                    else if (upper.StartsWith("AUTH PLAIN", StringComparison.Ordinal))
                    {
                        if (!secure)
                        {
                            await ReplyAsync(writer, "530 5.7.0 STARTTLS required");
                            continue;
                        }
                        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        var response = parts.Length >= 3 ? parts[2] : string.Empty;
                        if (response.Length == 0)
                        {
                            await ReplyAsync(writer, "334 ");
                            response = await reader.ReadLineAsync(cancellationToken) ?? string.Empty;
                        }
                        var fields = Decode(response).Split('\0');
                        authenticated = fields.Length >= 3 && fields[^2] == username && fields[^1] == password;
                        await ReplyAsync(writer, authenticated ? "235 2.7.0 Authentication successful" : "535 5.7.8 Authentication failed");
                    }
                    else if (upper.StartsWith("MAIL FROM:", StringComparison.Ordinal))
                    {
                        await ReplyAsync(writer, authenticated ? "250 2.1.0 Sender accepted" : "530 5.7.0 Authentication required");
                    }
                    else if (upper.StartsWith("RCPT TO:", StringComparison.Ordinal))
                    {
                        recipient = ExtractAddress(line);
                        await ReplyAsync(writer, authenticated ? "250 2.1.5 Recipient accepted" : "530 5.7.0 Authentication required");
                    }
                    else if (upper == "DATA")
                    {
                        if (!authenticated)
                        {
                            await ReplyAsync(writer, "530 5.7.0 Authentication required");
                            continue;
                        }
                        await ReplyAsync(writer, "354 End data with <CR><LF>.<CR><LF>");
                        var data = new StringBuilder();
                        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null && line != ".")
                        {
                            if (line.StartsWith("..", StringComparison.Ordinal)) line = line[1..];
                            data.Append(line).Append("\r\n");
                        }
                        if (line is null) throw new IOException("SMTP DATA ended unexpectedly.");
                        messages.Enqueue(new CapturedSmtpMessage(recipient, data.ToString()));
                        messageAvailable.Release();
                        await ReplyAsync(writer, "250 2.0.0 Message captured by local test server");
                    }
                    else if (upper == "RSET" || upper == "NOOP")
                    {
                        await ReplyAsync(writer, "250 2.0.0 OK");
                    }
                    else if (upper == "QUIT")
                    {
                        await ReplyAsync(writer, "221 2.0.0 Bye");
                        return;
                    }
                    else
                    {
                        await ReplyAsync(writer, "502 5.5.1 Command not implemented");
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

        private static async Task ReplyAsync(StreamWriter writer, string response)
        {
            await writer.WriteLineAsync(response);
            await writer.FlushAsync();
        }

        private static string Decode(string value)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
            catch (FormatException) { return string.Empty; }
        }

        private static string ExtractAddress(string command)
        {
            var start = command.IndexOf('<');
            var end = command.IndexOf('>', start + 1);
            return start >= 0 && end > start ? command[(start + 1)..end] : string.Empty;
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
            certificate.Dispose();
            stop.Dispose();
            messageAvailable.Dispose();
        }
    }
}
