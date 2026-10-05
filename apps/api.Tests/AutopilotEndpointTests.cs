using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Autopilot;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// HTTP surface tests: the completion bridge intake is signature-only, and the
/// control console is administrator-only. Nothing here can force a release.
/// </summary>
public sealed class AutopilotEndpointTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public AutopilotEndpointTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Intake_endpoint_rejects_unsigned_events()
    {
        using var client = factory.CreateClient();
        using var content = new StringContent(Payload("wave-http-unsigned", "task-1"), Encoding.UTF8, "application/json");
        content.Headers.Add("X-Autopilot-Source", "completion-bridge");
        content.Headers.Add("X-Autopilot-Event-Id", $"evt-{Guid.NewGuid():N}");
        content.Headers.Add("X-Autopilot-Event-Type", AutopilotEventTypes.WaveTaskCompleted);

        var response = await client.PostAsync("/api/autopilot/events", content);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Intake_endpoint_accepts_a_correctly_signed_event()
    {
        const string secret = "integration-test-signing-secret";
        Environment.SetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET", secret);
        try
        {
            using var client = factory.CreateClient();
            var payload = Payload("wave-http-signed", "task-1");
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var signature = Convert.ToHexString(
                new HMACSHA256(Encoding.UTF8.GetBytes(secret))
                    .ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{payload}"))).ToLowerInvariant();

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            content.Headers.Add("X-Autopilot-Source", "completion-bridge");
            content.Headers.Add("X-Autopilot-Event-Id", $"evt-{Guid.NewGuid():N}");
            content.Headers.Add("X-Autopilot-Event-Type", AutopilotEventTypes.WaveTaskCompleted);
            content.Headers.Add("X-Autopilot-Timestamp", timestamp);
            content.Headers.Add("X-Autopilot-Signature", $"sha256={signature}");

            var response = await client.PostAsync("/api/autopilot/events", content);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

            var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
            Assert.Equal(AutopilotIntakeOutcomes.Accepted, body.GetProperty("status").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET", null);
        }
    }

    [Fact]
    public async Task Signed_webhook_does_not_require_csrf_when_an_auth_cookie_is_present()
    {
        const string secret = "integration-test-signing-secret";
        Environment.SetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET", secret);
        try
        {
            using var client = factory.CreateClient();
            await RegisterAdmin(client);

            var payload = Payload("wave-http-authenticated-signed", "task-1");
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var signature = Convert.ToHexString(
                new HMACSHA256(Encoding.UTF8.GetBytes(secret))
                    .ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{payload}"))).ToLowerInvariant();

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            content.Headers.Add("X-Autopilot-Source", "completion-bridge");
            content.Headers.Add("X-Autopilot-Event-Id", $"evt-{Guid.NewGuid():N}");
            content.Headers.Add("X-Autopilot-Event-Type", AutopilotEventTypes.WaveTaskCompleted);
            content.Headers.Add("X-Autopilot-Timestamp", timestamp);
            content.Headers.Add("X-Autopilot-Signature", $"sha256={signature}");

            var response = await client.PostAsync("/api/autopilot/events", content);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET", null);
        }
    }

    [Fact]
    public async Task Replayed_events_are_rejected_with_a_replay_outcome()
    {
        const string secret = "integration-test-signing-secret";
        Environment.SetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET", secret);
        try
        {
            var payload = Payload("wave-http-replay", "task-1");
            var staleTimestamp = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds().ToString();
            var signature = Convert.ToHexString(
                new HMACSHA256(Encoding.UTF8.GetBytes(secret))
                    .ComputeHash(Encoding.UTF8.GetBytes($"{staleTimestamp}.{payload}"))).ToLowerInvariant();

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var options = new AutopilotOptions { RequireSignedEvents = true, SignatureToleranceSeconds = 300 };
            options.Normalize();
            var intake = new AutopilotEventIntake(
                db,
                new AutopilotEventAuthenticator(options),
                new EfAutopilotAuditLog(db),
                options,
                TimeProvider.System);

            var result = await intake.SubmitAsync(new AutopilotIntakeRequest(
                signature,
                staleTimestamp,
                payload,
                "completion-bridge",
                $"evt-replay-{Guid.NewGuid():N}",
                AutopilotEventTypes.WaveTaskCompleted));

            Assert.Equal(AutopilotIntakeOutcomes.RejectedReplay, result.Outcome);
            Assert.Empty(await db.AutopilotEvents
                .Where(item => item.SourceSystem == "completion-bridge" && item.ExternalEventId.StartsWith("evt-replay-"))
                .ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET", null);
        }
    }

    [Fact]
    public async Task Control_console_requires_administrator_access()
    {
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/autopilot/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/autopilot/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/autopilot/decisions")).StatusCode);
    }

    [Fact]
    public async Task Control_console_is_idle_and_safe_by_default()
    {
        using var client = factory.CreateClient();
        var auth = await RegisterAdmin(client);
        await AddAdminRole(auth.User.Email);

        var response = await client.GetAsync("/api/admin/autopilot/overview");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var control = body.GetProperty("control");
        Assert.False(control.GetProperty("featureEnabled").GetBoolean());
        Assert.True(control.GetProperty("dryRun").GetBoolean());
        Assert.False(control.GetProperty("chargingEnabled").GetBoolean());
        Assert.False(control.GetProperty("paidProvidersEnabled").GetBoolean());
        Assert.False(control.GetProperty("killSwitchEngaged").GetBoolean());
        Assert.Equal(20, control.GetProperty("maxConcurrency").GetInt32());
    }

    [Fact]
    public async Task Control_console_exposes_decisions_and_control_semantics()
    {
        using var client = factory.CreateClient();
        var auth = await RegisterAdmin(client);
        await AddAdminRole(auth.User.Email);

        var decisions = JsonSerializer.Deserialize<JsonElement>(
            await client.GetStringAsync("/api/admin/autopilot/decisions"));
        var humanRequired = decisions.GetProperty("humanRequired").EnumerateArray().Select(item => item.GetString()).ToList();
        Assert.Contains(AutopilotHumanDecisions.Pricing, humanRequired);
        Assert.Contains(AutopilotHumanDecisions.EnableCharging, humanRequired);
        Assert.Contains(AutopilotHumanDecisions.ProductionRelease, humanRequired);

        var neverAutomatic = decisions.GetProperty("neverAutomatic").EnumerateArray().Select(item => item.GetString()).ToList();
        Assert.Contains(AutopilotForbiddenActions.ForcePush, neverAutomatic);
        Assert.Contains(AutopilotForbiddenActions.ResetProductionDatabase, neverAutomatic);

        var reasonRequired = await SendWithCsrf(client, HttpMethod.Post, "/api/admin/autopilot/control", new { paused = true });
        Assert.Equal(HttpStatusCode.BadRequest, reasonRequired.StatusCode);

        var pause = await SendWithCsrf(client, HttpMethod.Post, "/api/admin/autopilot/control", new { paused = true, reason = "integration test pause" });
        Assert.Equal(HttpStatusCode.OK, pause.StatusCode);
        var pausedBody = JsonSerializer.Deserialize<JsonElement>(await pause.Content.ReadAsStringAsync());
        Assert.True(pausedBody.GetProperty("paused").GetBoolean());
        Assert.False(pausedBody.GetProperty("killSwitchEngaged").GetBoolean());

        var resume = await SendWithCsrf(client, HttpMethod.Post, "/api/admin/autopilot/control", new { paused = false, reason = "integration test resume" });
        Assert.Equal(HttpStatusCode.OK, resume.StatusCode);

        var reconcile = await SendWithCsrf(client, HttpMethod.Post, "/api/admin/autopilot/reconcile", new { reason = "integration test reconcile" });
        Assert.Equal(HttpStatusCode.OK, reconcile.StatusCode);
    }

    private static string Payload(string waveKey, string taskId) => JsonSerializer.Serialize(new
    {
        waveKey,
        taskId,
        outcome = AutopilotTaskOutcomes.Succeeded,
        branch = "feature/taslim-autopilot-controller",
        baseSha = "8bc3999d8a27f047e253a1f8832bd76e5c6dcb9a",
        candidateSha = "1111111111111111111111111111111111111111",
        checks = new Dictionary<string, bool>
        {
            [AutopilotChecks.Build] = true,
            [AutopilotChecks.UnitTests] = true,
            [AutopilotChecks.Typecheck] = true,
            [AutopilotChecks.BrowserE2E] = true,
            [AutopilotChecks.Migrations] = true,
            [AutopilotChecks.Security] = true,
            [AutopilotChecks.Accounting] = true,
            [AutopilotChecks.IntegrationComplete] = true,
        },
    });

    private async Task<AuthResponse> RegisterAdmin(HttpClient client)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Autopilot Administrator",
            email = $"autopilot-admin-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private async Task AddAdminRole(string email)
    {
        using var scope = factory.Services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        if (!await roleManager.RoleExistsAsync(AdminPolicies.Role))
            Assert.True((await roleManager.CreateAsync(new IdentityRole<Guid>(AdminPolicies.Role))).Succeeded);
        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);
        Assert.True((await userManager.AddToRoleAsync(user!, AdminPolicies.Role)).Succeeded);
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}
