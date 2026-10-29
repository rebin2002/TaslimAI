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
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Email;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class PasswordRecoveryApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    public RecordingEmailSender EmailSender { get; } = new();

    public PasswordRecoveryApiFactory()
    {
        connection.Open();
        using var db = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
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
            services.AddSingleton(connection);
            services.AddDbContext<TaslimDbContext>(options => options.UseSqlite(connection));
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(EmailSender);
        });
    }

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
    public bool IsConfigured => Volatile.Read(ref configured) == 1;
    public ConcurrentQueue<CapturedEmail> Messages { get; } = new();
    public void SetConfigured(bool value) => Volatile.Write(ref configured, value ? 1 : 0);

    public Task SendAsync(string recipient, string subject, string plainTextBody, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Messages.Enqueue(new CapturedEmail(recipient, subject, plainTextBody));
        return Task.CompletedTask;
    }
}

public sealed class PasswordRecoveryTests(PasswordRecoveryApiFactory factory) : IClassFixture<PasswordRecoveryApiFactory>
{
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
    {
        var end = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < end)
        {
            var message = factory.EmailSender.Messages.FirstOrDefault(candidate => string.Equals(candidate.Recipient, email, StringComparison.OrdinalIgnoreCase));
            if (message is not null) return message;
            await Task.Delay(50);
        }
        throw new Xunit.Sdk.XunitException("Timed out waiting for the test email sender to capture a reset message.");
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
