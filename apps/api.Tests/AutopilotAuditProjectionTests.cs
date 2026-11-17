using System.Net;
using System.Net.Http.Json;
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

public sealed class AutopilotAuditProjectionTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public AutopilotAuditProjectionTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Administrator_audit_projection_exposes_bounded_attribution_and_request_correlation()
    {
        using var client = factory.CreateClient();
        var auth = await RegisterAdmin(client);
        await AddAdminRole(auth.User.Email);

        var auditId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        const string requestId = "trace-admin-autopilot-42";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.AutopilotAuditEvents.Add(new AutopilotAuditEvent
            {
                Id = auditId,
                ActorUserId = auth.User.Id,
                Action = AutopilotAuditActions.ControlChanged,
                TargetType = "autopilot",
                TargetId = targetId,
                Outcome = AutopilotAuditOutcomes.Recorded,
                Reason = "operator pause",
                StatusDetail = "paused=true;killSwitch=false",
                RequestId = requestId,
                WaveKey = "admin-audit-projection-wave",
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync("/api/admin/autopilot/audit?waveKey=admin-audit-projection-wave");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entries = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        var entry = Assert.Single(entries!, item => item.GetProperty("id").GetGuid() == auditId);
        Assert.Equal(auth.User.Id, entry.GetProperty("actorUserId").GetGuid());
        Assert.Equal("autopilot", entry.GetProperty("targetType").GetString());
        Assert.Equal(targetId, entry.GetProperty("targetId").GetGuid());
        Assert.Equal("paused=true;killSwitch=false", entry.GetProperty("statusDetail").GetString());
        Assert.Equal(requestId, entry.GetProperty("requestId").GetString());
    }

    private async Task<AuthResponse> RegisterAdmin(HttpClient client)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Autopilot Audit Administrator",
            email = $"autopilot-audit-admin-{Guid.NewGuid():N}@example.com",
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

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        return await client.SendAsync(request);
    }
}
