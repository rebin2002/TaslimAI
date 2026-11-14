using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
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
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;
namespace Taslim.Api.Tests;
/// <summary>
/// Browser-boundary acceptance for the password-recovery journey. These tests drive the recovery
/// endpoints the way the deployed Next.js client does: a cross-origin `Origin` plus the CORS
/// preflight the browser issues before any credentialed JSON POST, a real cookie jar carrying the
/// antiforgery pair, and the reset URL exactly as it arrives in the mailbox. The API's only mail
/// transport is an in-test loopback STARTTLS SMTP server, so no real mail service is contacted.
///
/// Why this lane exists: the existing SMTP journey test posts with the default factory client,
/// which sends no `Origin` header and never performs a preflight, and the Playwright journey
/// fulfils the API responses from route fakes. Neither proves that a browser is allowed to reach
/// the endpoints at all, nor that the delivered link targets the origin the web app is served from.
/// </summary>
[Collection(PasswordRecoveryLocalSmtpCollection.Name)]
public sealed class PasswordRecoveryBrowserBoundaryTests
{
    private const string WebOrigin = "http://localhost:3000";
    private const string ApiOrigin = "http://localhost:5000";
    private const string UnlistedOrigin = "http://recovery-foreign.example.test";
    private const string InitialPassword = "StrongPassword!123";
    private const string ResetPasswordValue = "EvenStronger!123";
    private const string SyntheticSmtpUsername = "loopback-browser-test-user";
    private const string SyntheticSmtpPassword = "loopback-browser-test-password-not-a-secret";
    private const string ResetLinkMarker = WebOrigin + "/reset-password#";
    // Nothing is ever delivered through this port: the preflight-only test never queues an
    // email, while the journey test starts a loopback SMTP server on an ephemeral port.
    private const int UnusedSmtpPort = 1;
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Recovery_endpoints_answer_the_browser_preflight_for_the_configured_web_origin_only()
    {
        using var factory = new BrowserRecoveryApiFactory(UnusedSmtpPort, TokenLifetime);

        // Availability is what decides whether login advertises "Forgot password?" at all, so the
        // anonymous cross-origin GET the recovery form makes has to be readable by the browser.
        using (var availabilityClient = factory.CreateBrowserClient())
        using (var availability = await availabilityClient.GetAsync("/api/auth/password-recovery/availability"))
        {
            Assert.Equal(HttpStatusCode.OK, availability.StatusCode);
            Assert.Equal(WebOrigin, Single(availability, "Access-Control-Allow-Origin"));
            Assert.True(
                (await availability.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("available").GetBoolean(),
                "A configured SMTP sender must not keep the recovery entry hidden outside production.");
        }

        foreach (var path in new[] { "/api/auth/password/forgot", "/api/auth/password/reset" })
        {
            using var client = factory.CreateBrowserClient();
            using var preflight = await client.SendAsync(NewPreflight(path));
            Assert.True(
                preflight.IsSuccessStatusCode,
                $"The browser preflight for {path} must not be blocked by authentication, antiforgery, or rate limiting. Status={(int)preflight.StatusCode}.");
            Assert.Equal(WebOrigin, Single(preflight, "Access-Control-Allow-Origin"));
            Assert.Equal("true", Single(preflight, "Access-Control-Allow-Credentials"));
            Assert.False(preflight.Headers.Contains("Set-Cookie"),
                $"A preflight for {path} must not mint an antiforgery cookie.");
        }

        using var foreignClient = factory.CreateBrowserClient(UnlistedOrigin);
        using var foreign = await foreignClient.SendAsync(NewPreflight("/api/auth/password/forgot"));
        Assert.False(foreign.Headers.Contains("Access-Control-Allow-Origin"),
            "An unlisted origin must not be granted access to the anonymous recovery endpoint.");
    }

    [Fact]
    public async Task Browser_shaped_client_completes_forgot_to_reset_over_loopback_smtp_and_reopens_the_account()
    {
        await using var smtp = new LoopbackBrowserSmtpServer(SyntheticSmtpUsername, SyntheticSmtpPassword);
        var previousValidation = ServicePointManager.ServerCertificateValidationCallback;
        ServicePointManager.ServerCertificateValidationCallback = smtp.ValidateServerCertificate;
        try
        {
            using var factory = new BrowserRecoveryApiFactory(smtp.Port, TokenLifetime);
            var email = $"browser-recovery-{Guid.NewGuid():N}@example.test";

            using var registration = factory.CreateBrowserClient();
            using (var response = await PostAsBrowserAsync(registration, "/api/auth/register", new
                     {
                         displayName = "Browser Recovery",
                         email,
                         password = InitialPassword,
                         preferredLanguage = "en",
                     }, includeAuthTransition: true))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Two live sessions, as when the signed-in user opens the mailed link in another tab.
            using var firstSession = factory.CreateBrowserClient();
            using var secondSession = factory.CreateBrowserClient();
            await SignInAsync(firstSession, email, InitialPassword);
            await SignInAsync(secondSession, email, InitialPassword);
            Assert.Equal(HttpStatusCode.OK, (await firstSession.GetAsync("/api/auth/me")).StatusCode);

            // The recovery form runs in its own page context: anonymous, with its own CSRF pair.
            using var browser = factory.CreateBrowserClient();
            using (var forgot = await PostAsBrowserAsync(browser, "/api/auth/password/forgot", new { email }))
                Assert.Equal(HttpStatusCode.Accepted, forgot.StatusCode);

            var message = await smtp.WaitForMessageAsync(0, WaitLimit);
            Assert.Equal(email, message.Recipient, ignoreCase: true);

            // The link the mailbox carries is the real web entry point, and its credentials travel
            // only in the URL fragment, which the browser never sends to the API.
            var resetUrl = ExtractResetUrl(message.RawMessage, email);
            Assert.StartsWith(ResetLinkMarker, resetUrl, StringComparison.Ordinal);
            var fragmentValues = FragmentValues(resetUrl);
            Assert.True(fragmentValues.TryGetValue("token", out var mailedToken) && !string.IsNullOrWhiteSpace(mailedToken));
            Assert.Equal("en", fragmentValues["lang"]);
            Assert.DoesNotContain("token=", new Uri(resetUrl).Query, StringComparison.Ordinal);

            var userId = await FindUserIdAsync(factory, email);
            await WaitForDeliveryAsync(factory, userId);

            // The reset page posts the fragment values back over the same CSRF-protected boundary;
            // without the antiforgery header the anonymous reset is rejected before touching state.
            using (var unguarded = await PostAsBrowserAsync(browser, "/api/auth/password/reset",
                         new { email, token = mailedToken, newPassword = ResetPasswordValue }, includeCsrfHeader: false))
                Assert.Equal("CSRF_VALIDATION_FAILED",
                    (await unguarded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString());

            using (var reset = await PostAsBrowserAsync(browser, "/api/auth/password/reset",
                         new { email, token = mailedToken, newPassword = ResetPasswordValue }))
                Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

            // Every pre-reset session is invalidated, and no session is granted implicitly.
            Assert.Equal(HttpStatusCode.Unauthorized, (await firstSession.GetAsync("/api/auth/me")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await secondSession.GetAsync("/api/auth/me")).StatusCode);

            using var recoveredBrowser = factory.CreateBrowserClient();
            using (var staleSignIn = await PostAsBrowserAsync(recoveredBrowser, "/api/auth/login", new { email, password = InitialPassword }, includeAuthTransition: true))
                Assert.Equal(HttpStatusCode.Unauthorized, staleSignIn.StatusCode);
            using (var freshSignIn = await PostAsBrowserAsync(recoveredBrowser, "/api/auth/login", new { email, password = ResetPasswordValue }, includeAuthTransition: true))
                Assert.Equal(HttpStatusCode.OK, freshSignIn.StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await recoveredBrowser.GetAsync("/api/auth/me")).StatusCode);
        }
        finally
        {
            ServicePointManager.ServerCertificateValidationCallback = previousValidation;
        }
    }

    private static HttpRequestMessage NewPreflight(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "content-type,x-csrf-token");
        return request;
    }

    private static string Single(HttpResponseMessage response, string header)
    {
        Assert.True(response.Headers.TryGetValues(header, out var values), $"Response lacked the {header} header.");
        return Assert.Single(values.ToArray());
    }

    private static async Task SignInAsync(HttpClient client, string email, string password)
    {
        using var response = await PostAsBrowserAsync(client, "/api/auth/login", new { email, password }, includeAuthTransition: true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Posts the way the web client does: same API origin, browser `Origin` header, the antiforgery
    /// cookie from the jar, and the token in `X-CSRF-TOKEN`.
    /// </summary>
    private static async Task<HttpResponseMessage> PostAsBrowserAsync(
        HttpClient client,
        string path,
        object payload,
        bool includeCsrfHeader = true,
        bool includeAuthTransition = false)
    {
        var token = await RefreshCsrfAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(payload) };
        if (includeCsrfHeader)
            request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", token);
        var response = await client.SendAsync(request);
        if (includeAuthTransition)
        {
            // A completed auth transition rotates the antiforgery pair before the next mutation.
            await RefreshCsrfAsync(client);
        }
        return response;
    }

    private static async Task<string> RefreshCsrfAsync(HttpClient client)
    {
        var tokens = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        var token = tokens.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        return token!;
    }

    private static string ExtractResetUrl(string rawMessage, string expectedEmail)
    {
        var headerEnd = rawMessage.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        Assert.True(headerEnd >= 0, "The captured SMTP message lacked MIME headers and body separation.");
        var body = rawMessage[(headerEnd + 4)..];
        if (rawMessage[..headerEnd].Contains("Content-Transfer-Encoding: quoted-printable", StringComparison.OrdinalIgnoreCase))
            body = DecodeQuotedPrintable(body);
        var start = body.IndexOf(ResetLinkMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, "The captured message contained no reset link for the configured web origin.");
        var url = body[start..].Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        Assert.Equal(expectedEmail, FragmentValues(url)["email"]);
        return url;
    }

    private static Dictionary<string, string> FragmentValues(string url) =>
        new Uri(url).Fragment.TrimStart('#')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0]),
                pair => Uri.UnescapeDataString(pair.Length > 1 ? pair[1] : string.Empty),
                StringComparer.Ordinal);

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
                if (byte.TryParse(value.AsSpan(index + 1, 2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var decoded))
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

    private static async Task<Guid> FindUserIdAsync(BrowserRecoveryApiFactory factory, string email)
    {
        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);
        return user!.Id;
    }

    private static async Task WaitForDeliveryAsync(BrowserRecoveryApiFactory factory, Guid userId)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < WaitLimit)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var entry = await db.PasswordResetEmailOutbox.AsNoTracking().SingleAsync(item => item.UserId == userId);
            if (entry.Status == PasswordResetDeliveryStates.Delivered)
                return;
            await Task.Delay(40);
        }
        using var diagnosticScope = factory.Services.CreateScope();
        var diagnosticDb = diagnosticScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var finalEntry = await diagnosticDb.PasswordResetEmailOutbox.AsNoTracking().SingleAsync(item => item.UserId == userId);
        throw new Xunit.Sdk.XunitException(
            $"Timed out waiting for the synthetic browser-boundary delivery. Status={finalEntry.Status}; " +
            $"AttemptCount={finalEntry.AttemptCount}; FailureCode={finalEntry.LastFailureCode ?? "none"}.");
    }

    /// <summary>
    /// A test API configured the way a local deployment of the web app reaches it: the Next.js
    /// origin listed in `AllowedOrigins`, recovery enabled, and the loopback STARTTLS server as the
    /// only mail transport.
    /// </summary>
    private sealed class BrowserRecoveryApiFactory : WebApplicationFactory<Program>
    {
        private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"taslim-recovery-browser-{Guid.NewGuid():N}.db");
        private readonly string connectionString;
        private readonly int smtpPort;
        private readonly TimeSpan tokenLifetime;

        public BrowserRecoveryApiFactory(int smtpPort, TimeSpan tokenLifetime)
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
            var journalMode = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The disposable browser-boundary SQLite database did not enable WAL mode.");
        }

        /// <summary>An HTTP client shaped like the deployed browser: its own cookie jar on the API origin.</summary>
        public HttpClient CreateBrowserClient(string origin = WebOrigin)
        {
            // The factory client owns one cookie jar per instance and keeps `Set-Cookie` visible
            // to assertions, which is exactly the pairing an antiforgery-protected POST needs.
            var client = CreateClient();
            client.BaseAddress = new Uri(ApiOrigin);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", origin);
            return client;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ApplyMigrations"] = "false",
                ["AllowedOrigins:0"] = WebOrigin,
                ["PasswordRecovery:Enabled"] = "true",
                ["PasswordRecovery:PublicBaseUrl"] = WebOrigin,
                ["PasswordRecovery:RequestCooldownMinutes"] = "1",
                ["PasswordRecovery:WorkerPollSeconds"] = "1",
                ["PasswordRecovery:MaxDeliveryAttempts"] = "2",
                ["PasswordRecovery:LeaseSeconds"] = "60",
                ["Email:Provider"] = "Smtp",
                ["Email:Host"] = "127.0.0.1",
                ["Email:Port"] = smtpPort.ToString(CultureInfo.InvariantCulture),
                ["Email:EnableSsl"] = "true",
                ["Email:Username"] = SyntheticSmtpUsername,
                ["Email:Password"] = SyntheticSmtpPassword,
                ["Email:FromAddress"] = "password-reset@example.test",
                ["Email:FromName"] = "Taslim Browser E2E",
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

    private sealed record CapturedMessage(string Recipient, string RawMessage);

    /// <summary>Minimal loopback STARTTLS SMTP server: authenticates, captures, and never relays.</summary>
    private sealed class LoopbackBrowserSmtpServer : IAsyncDisposable
    {
        private readonly string username;
        private readonly string password;
        private readonly TcpListener listener;
        private readonly CancellationTokenSource stop = new();
        private readonly X509Certificate2 certificate;
        private readonly ConcurrentQueue<CapturedMessage> messages = new();
        private readonly SemaphoreSlim messageAvailable = new(0);
        private readonly TaskCompletionSource<Exception> failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task acceptLoop;

        public int Port { get; }

        public LoopbackBrowserSmtpServer(string username, string password)
        {
            this.username = username;
            this.password = password;
            certificate = CreateServerCertificate();
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            acceptLoop = Task.Run(AcceptLoopAsync);
        }

        /// <summary>Trusts only this in-test certificate; the synthetic credential is not a secret.</summary>
        public bool ValidateServerCertificate(object sender, X509Certificate? serverCertificate, X509Chain? chain, SslPolicyErrors errors) =>
            serverCertificate is not null
            && string.Equals(serverCertificate.GetCertHashString(), certificate.Thumbprint, StringComparison.OrdinalIgnoreCase);

        public async Task<CapturedMessage> WaitForMessageAsync(int index, TimeSpan timeout)
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
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // Expected once the owning test disposes the server.
            }
            catch (Exception exception)
            {
                failure.TrySetResult(exception);
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
                        await ReplyAsync(writer, secure ? "250-AUTH LOGIN PLAIN" : "250-STARTTLS");
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
                        messages.Enqueue(new CapturedMessage(recipient, data.ToString()));
                        messageAvailable.Release();
                        await ReplyAsync(writer, "250 2.0.0 Message captured by the local test server");
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
