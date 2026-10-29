using System.Collections.Concurrent;
using System.Net;
using System.Net.Mail;
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
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Email;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class PasswordRecoveryApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly TimeSpan? resetTokenLifetime;
    private readonly Dictionary<string, string?> recoverySettings = new()
    {
        ["PasswordRecovery:Enabled"] = "true",
        ["PasswordRecovery:PublicBaseUrl"] = "http://localhost:3000",
        ["PasswordRecovery:RequestCooldownMinutes"] = "5",
        ["PasswordRecovery:WorkerPollSeconds"] = "1",
        ["PasswordRecovery:MaxDeliveryAttempts"] = "5",
        ["PasswordRecovery:LeaseSeconds"] = "60",
    };
    public RecordingEmailSender EmailSender { get; } = new();

    public PasswordRecoveryApiFactory(bool recoveryEnabled = true, TimeSpan? resetTokenLifetime = null)
    {
        recoverySettings["PasswordRecovery:Enabled"] = recoveryEnabled ? "true" : "false";
        this.resetTokenLifetime = resetTokenLifetime;
        connection.Open();
        using var db = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(recoverySettings));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<TaslimDbContext>>();
            services.AddSingleton(connection);
            services.AddDbContext<TaslimDbContext>(options => options.UseSqlite(connection));
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(EmailSender);
            if (resetTokenLifetime is { } tokenLifetime)
                services.PostConfigure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = tokenLifetime);
        });
    }

    public void SetRecoveryEnabled(bool enabled) => recoverySettings["PasswordRecovery:Enabled"] = enabled ? "true" : "false";

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) connection.Dispose();
    }
}

public sealed record CapturedEmail(string Recipient, string Subject, string Body);

public sealed class RecordingEmailSender : IEmailSender
{
    private int configured = 1;
    private int failSends;
    public bool IsConfigured => Volatile.Read(ref configured) == 1;
    public ConcurrentQueue<CapturedEmail> Messages { get; } = new();
    public void SetConfigured(bool value) => Volatile.Write(ref configured, value ? 1 : 0);
    public void SetFailure(bool value) => Volatile.Write(ref failSends, value ? 1 : 0);

    public Task SendAsync(string recipient, string subject, string plainTextBody, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref failSends) == 1)
            throw new SmtpException($"Sensitive test-only transport detail for {recipient}: AUTH payload must not escape.");
        Messages.Enqueue(new CapturedEmail(recipient, subject, plainTextBody));
        return Task.CompletedTask;
    }
}

public sealed class PasswordRecoveryTests(PasswordRecoveryApiFactory factory) : IClassFixture<PasswordRecoveryApiFactory>
{
    [Fact]
    public async Task Availability_reports_only_server_readiness_and_reset_fails_closed_when_disabled()
    {
        using var disabledFactory = new PasswordRecoveryApiFactory(recoveryEnabled: false);
        using var enabledFactory = new PasswordRecoveryApiFactory(recoveryEnabled: true);
        using var unconfiguredFactory = new PasswordRecoveryApiFactory(recoveryEnabled: true);
        unconfiguredFactory.EmailSender.SetConfigured(false);
        using var disabledClient = disabledFactory.CreateClient();
        using var enabledClient = enabledFactory.CreateClient();
        using var unconfiguredClient = unconfiguredFactory.CreateClient();

        var disabledAvailability = await disabledClient.GetAsync("/api/auth/password-recovery/availability");
        var enabledAvailability = await enabledClient.GetAsync("/api/auth/password-recovery/availability");
        var unconfiguredAvailability = await unconfiguredClient.GetAsync("/api/auth/password-recovery/availability");
        Assert.Equal(HttpStatusCode.OK, disabledAvailability.StatusCode);
        Assert.Equal(HttpStatusCode.OK, enabledAvailability.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unconfiguredAvailability.StatusCode);
        Assert.Contains("no-store", disabledAvailability.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-store", enabledAvailability.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var disabledBody = await disabledAvailability.Content.ReadFromJsonAsync<JsonElement>();
        var enabledBody = await enabledAvailability.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "available" }, disabledBody.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.False(disabledBody.GetProperty("available").GetBoolean());
        Assert.True(enabledBody.GetProperty("available").GetBoolean());
        Assert.False((await unconfiguredAvailability.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("available").GetBoolean());

        var reset = await Reset(disabledClient, "disabled@example.test", "unusable-token", "EvenStronger!123");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, reset.StatusCode);
        Assert.Equal("PASSWORD_RECOVERY_UNAVAILABLE", (await reset.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Availability_and_reset_gate_follow_reloaded_recovery_options()
    {
        using var reloadFactory = new PasswordRecoveryApiFactory();
        using var client = reloadFactory.CreateClient();
        var configuration = reloadFactory.Services.GetRequiredService<IConfiguration>() as IConfigurationRoot
            ?? throw new InvalidOperationException("The test configuration root is unavailable.");

        var initiallyReady = await client.GetFromJsonAsync<JsonElement>("/api/auth/password-recovery/availability");
        Assert.True(initiallyReady.GetProperty("available").GetBoolean());

        reloadFactory.SetRecoveryEnabled(false);
        configuration.Reload();
        var unavailable = await client.GetFromJsonAsync<JsonElement>("/api/auth/password-recovery/availability");
        Assert.False(unavailable.GetProperty("available").GetBoolean());
        var resetWhileDisabled = await Reset(client, "disabled@example.test", "unusable-token", "EvenStronger!123");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resetWhileDisabled.StatusCode);

        reloadFactory.SetRecoveryEnabled(true);
        configuration.Reload();
        var availableAgain = await client.GetFromJsonAsync<JsonElement>("/api/auth/password-recovery/availability");
        Assert.True(availableAgain.GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task Actually_expired_identity_reset_token_is_rejected()
    {
        using var expiryFactory = new PasswordRecoveryApiFactory(resetTokenLifetime: TimeSpan.FromSeconds(3));
        var email = $"expired-{Guid.NewGuid():N}@example.test";
        using var registering = expiryFactory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await Register(registering, email)).StatusCode);
        using var requestClient = expiryFactory.CreateClient();
        Assert.Equal(HttpStatusCode.Accepted, (await Forgot(requestClient, email)).StatusCode);

        var sent = await WaitForEmailAsync(expiryFactory.EmailSender, email);
        var fragment = new Uri(ExtractResetUrl(sent.Body)).Fragment.TrimStart('#');
        var token = fragment.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]), StringComparer.Ordinal)["token"];
        using (var scope = expiryFactory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByEmailAsync(email);
            Assert.NotNull(user);
            Assert.True(await userManager.VerifyUserTokenAsync(
                user!,
                userManager.Options.Tokens.PasswordResetTokenProvider,
                UserManager<ApplicationUser>.ResetPasswordTokenPurpose,
                token));
        }
        await Task.Delay(TimeSpan.FromMilliseconds(3_100));

        using var resetClient = expiryFactory.CreateClient();
        var reset = await Reset(resetClient, email, token, "EvenStronger!123");
        Assert.Equal(HttpStatusCode.BadRequest, reset.StatusCode);
        Assert.Equal("PASSWORD_RESET_INVALID", (await reset.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Mail_sender_failures_store_only_a_sanitized_failure_code()
    {
        using var failureFactory = new PasswordRecoveryApiFactory();
        var email = $"failure-{Guid.NewGuid():N}@example.test";
        failureFactory.EmailSender.SetFailure(true);
        using var registering = failureFactory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await Register(registering, email)).StatusCode);
        using var requestClient = failureFactory.CreateClient();
        Assert.Equal(HttpStatusCode.Accepted, (await Forgot(requestClient, email)).StatusCode);

        var entry = await WaitForOutboxFailureAsync(failureFactory);
        Assert.NotNull(entry);
        Assert.Equal("SMTP_DELIVERY_FAILED", entry!.LastFailureCode);
        Assert.DoesNotContain(email, entry.LastFailureCode!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, entry.AttemptCount);
        Assert.Empty(failureFactory.EmailSender.Messages);
    }

    [Fact]
    public void Smtp_configuration_fails_closed_without_authenticated_TLS_sender_details()
    {
        var options = new SmtpEmailOptions
        {
            Provider = "Smtp",
            Host = "smtp.taslim.test",
            Port = 587,
            EnableSsl = true,
            Username = "taslim-smtp-user",
            Password = "not-a-real-secret",
            FromAddress = "noreply@taslim.ai",
            FromName = "Taslim.ai",
        };
        Assert.True(options.IsConfigured);

        options.EnableSsl = false;
        Assert.False(options.IsConfigured);
        options.EnableSsl = true;
        options.Username = string.Empty;
        Assert.False(options.IsConfigured);
        options.Username = "taslim-smtp-user";
        options.Port = 465;
        Assert.False(options.IsConfigured);
        options.Port = 587;
        options.FromAddress = "not-an-address";
        Assert.False(options.IsConfigured);
    }

    [Fact]
    public async Task Request_is_generic_for_existing_and_unknown_accounts_and_fails_closed_when_delivery_is_unconfigured()
    {
        var email = $"recovery-{Guid.NewGuid():N}@example.test";
        using var registering = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await Register(registering, email)).StatusCode);

        factory.EmailSender.SetConfigured(false);
        using var unavailableKnown = factory.CreateClient();
        using var unavailableUnknown = factory.CreateClient();
        var knownUnavailable = await Forgot(unavailableKnown, email);
        var unknownUnavailable = await Forgot(unavailableUnknown, "missing@example.test");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, knownUnavailable.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unknownUnavailable.StatusCode);
        Assert.Equal(await knownUnavailable.Content.ReadAsStringAsync(), await unknownUnavailable.Content.ReadAsStringAsync());
        Assert.DoesNotContain(factory.EmailSender.Messages, message => message.Recipient == email);

        factory.EmailSender.SetConfigured(true);
        using var knownClient = factory.CreateClient();
        using var unknownClient = factory.CreateClient();
        var known = await Forgot(knownClient, email);
        var unknown = await Forgot(unknownClient, "missing@example.test");
        Assert.Equal(HttpStatusCode.Accepted, known.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
        Assert.Equal(await known.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        Assert.Contains("If an account matches", await known.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var sent = await WaitForEmailAsync(email);
        Assert.Contains("http://localhost:3000/reset-password#email=", sent.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("/reset-password?", sent.Body, StringComparison.Ordinal);

        // The per-account cooldown returns the same generic acceptance response
        // and does not enqueue another email, even across separate API clients.
        using var throttledClient = factory.CreateClient();
        var throttled = await Forgot(throttledClient, email);
        Assert.Equal(HttpStatusCode.Accepted, throttled.StatusCode);
        Assert.Equal(await known.Content.ReadAsStringAsync(), await throttled.Content.ReadAsStringAsync());
        Assert.Single(factory.EmailSender.Messages, message => message.Recipient == email);
    }

    [Fact]
    public async Task Reset_requires_csrf_rejects_invalid_tokens_and_consumes_valid_token_once_without_signing_in()
    {
        var tokenOptions = factory.Services.GetRequiredService<IOptions<DataProtectionTokenProviderOptions>>();
        Assert.Equal(TimeSpan.FromMinutes(PasswordRecoveryOptions.PasswordResetTokenLifetimeMinutes), tokenOptions.Value.TokenLifespan);

        var email = $"reset-{Guid.NewGuid():N}@example.test";
        using var firstSession = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await Register(firstSession, email)).StatusCode);
        using var secondSession = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await Login(secondSession, email, "StrongPassword!123")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await secondSession.GetAsync("/api/auth/me")).StatusCode);

        using var noCsrf = factory.CreateClient();
        using var missingCsrfRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/password/reset")
        {
            Content = JsonContent.Create(new { email, token = "invalid", newPassword = "EvenStronger!123" }),
        };
        var missingCsrf = await noCsrf.SendAsync(missingCsrfRequest);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        Assert.Equal("CSRF_VALIDATION_FAILED", (await missingCsrf.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString());

        using var requestMissingUser = factory.CreateClient();
        using var requestInvalidToken = factory.CreateClient();
        var missingUser = await Reset(requestMissingUser, "missing@example.test", "bad-token", "EvenStronger!123");
        var invalidToken = await Reset(requestInvalidToken, email, "bad-token", "EvenStronger!123");
        Assert.Equal(HttpStatusCode.BadRequest, missingUser.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidToken.StatusCode);
        Assert.Equal(await missingUser.Content.ReadAsStringAsync(), await invalidToken.Content.ReadAsStringAsync());

        using var requestClient = factory.CreateClient();
        var requestResponse = await Forgot(requestClient, email);
        Assert.Equal(HttpStatusCode.Accepted, requestResponse.StatusCode);
        var sent = await WaitForEmailAsync(email);
        var fragment = new Uri(ExtractResetUrl(sent.Body)).Fragment.TrimStart('#');
        var values = fragment.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]), StringComparer.Ordinal);
        Assert.Equal(email, values["email"]);
        Assert.False(string.IsNullOrWhiteSpace(values["token"]));

        using var resetClient = factory.CreateClient();
        var newPassword = "EvenStronger!123";
        var reset = await Reset(resetClient, email, values["token"], newPassword);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Contains(reset.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith("taslim.csrf=", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(HttpStatusCode.Unauthorized, (await resetClient.GetAsync("/api/auth/me")).StatusCode);

        using var replayClient = factory.CreateClient();
        var replay = await Reset(replayClient, email, values["token"], "AnotherStrong!123");
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal("PASSWORD_RESET_INVALID", (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.Unauthorized, (await firstSession.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await secondSession.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(firstSession, email, newPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(replayClient, email, "StrongPassword!123")).StatusCode);
    }

    private async Task<CapturedEmail> WaitForEmailAsync(string email)
        => await WaitForEmailAsync(factory.EmailSender, email);

    private static async Task<CapturedEmail> WaitForEmailAsync(RecordingEmailSender sender, string email)
    {
        var end = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < end)
        {
            var message = sender.Messages.FirstOrDefault(candidate => string.Equals(candidate.Recipient, email, StringComparison.OrdinalIgnoreCase));
            if (message is not null) return message;
            await Task.Delay(50);
        }
        throw new Xunit.Sdk.XunitException("Timed out waiting for the test email sender to capture a reset message.");
    }

    private static async Task<PasswordResetEmailOutboxEntry?> WaitForOutboxFailureAsync(PasswordRecoveryApiFactory source)
    {
        var end = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < end)
        {
            await using var scope = source.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var entry = await db.PasswordResetEmailOutbox.AsNoTracking().FirstOrDefaultAsync();
            if (entry?.LastFailureCode is not null) return entry;
            await Task.Delay(50);
        }
        return null;
    }

    private static string ExtractResetUrl(string body) => body
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim())
        .First(line => line.Contains("/reset-password#", StringComparison.Ordinal));

    private static async Task<HttpResponseMessage> Register(HttpClient client, string email) =>
        await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Password Recovery User",
            email,
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });

    private static async Task<HttpResponseMessage> Login(HttpClient client, string email, string password) =>
        await SendWithCsrf(client, HttpMethod.Post, "/api/auth/login", new { email, password });

    private static async Task<HttpResponseMessage> Forgot(HttpClient client, string email) =>
        await SendWithCsrf(client, HttpMethod.Post, "/api/auth/password/forgot", new { email });

    private static async Task<HttpResponseMessage> Reset(HttpClient client, string email, string token, string newPassword) =>
        await SendWithCsrf(client, HttpMethod.Post, "/api/auth/password/reset", new { email, token, newPassword });

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object payload)
    {
        var token = await GetCsrf(client);
        using var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Add("X-CSRF-TOKEN", token);
        return await client.SendAsync(request);
    }

    private static async Task<string> GetCsrf(HttpClient client)
    {
        var response = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        return response.GetProperty("token").GetString()!;
    }
}
