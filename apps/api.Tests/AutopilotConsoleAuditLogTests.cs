using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Autopilot;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Contract tests for the administrator Autopilot console audit log.
/// The audit feed is windowed by a clamped limit and consumed newest-first,
/// so a burst of events sharing one timestamp must keep a deterministic
/// order: newest first, ties broken by descending id - the same rule the
/// operations dashboard audit feed already pins. Without that tiebreak the
/// order of tied rows is decided by the storage plan, so the cut of a short
/// window can keep older rows in place of newer ones and the caller's pages
/// can drop or duplicate entries. Each burst is committed newest id first,
/// one row at a time, so the physical rowid order is the exact reverse of
/// the required reading order: any window whose tie order leaks storage
/// placement fails the assertions below instead of passing by accident.
/// With the explicit tiebreak the result is plan-independent. The HTTP
/// surface is also pinned to be administrator-only and to serialise no
/// column outside the provider-neutral DTO.
/// </summary>
public sealed class AutopilotConsoleAuditLogTests : IClassFixture<TaslimApiFactory>
{
    private static readonly DateTime BurstStamp = new(2026, 11, 16, 9, 30, 0, DateTimeKind.Utc);

    private readonly TaslimApiFactory factory;

    public AutopilotConsoleAuditLogTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    // A rank-encoded Guid orders identically under .NET Guid comparison and
    // under the raw BLOB comparison the database applies to ORDER BY, so the
    // expected sequences below are engine-independent. The trailing salt byte
    // keeps ids unique across tests that share the fixture database without
    // changing the rank-dominant ordering.
    private static Guid RankGuid(int rank, byte salt) => new(rank, 0, 0, new byte[] { 0, 0, 0, 0, 0, 0, 0, salt });

    private static int RankOf(Guid id) => id.ToByteArray()[0];

    private async Task SeedTiedBurstAsync(
        string waveKey,
        int count,
        byte salt,
        string? statusDetail = null,
        string? requestId = null,
        Guid? actorUserId = null,
        Guid? targetId = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        // Insert newest id first, saving each row individually so the
        // physical order is exactly insertion order; the contract order is
        // then the reverse of it, which only an explicit tiebreak fixes.
        for (var rank = count; rank >= 1; rank--)
        {
            db.AutopilotAuditEvents.Add(new AutopilotAuditEvent
            {
                Id = RankGuid(rank, salt),
                ActorUserId = actorUserId,
                Action = rank % 2 == 0 ? "backlog.approved" : "cycle.evaluated",
                TargetType = "autopilot",
                TargetId = targetId,
                Outcome = "succeeded",
                Reason = $"window-boundary reason {rank}",
                StatusDetail = statusDetail,
                WaveKey = waveKey,
                RequestId = requestId,
                Attempt = rank,
                DryRun = true,
                CreatedAt = BurstStamp,
            });
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Audit_window_keeps_the_newest_events_when_tied_timestamps_straddle_the_cut()
    {
        var waveKey = $"wave-audit-tie-{Guid.NewGuid():N}";
        await SeedTiedBurstAsync(waveKey, 6, salt: 1);
        using var scope = factory.Services.CreateScope();
        var console = scope.ServiceProvider.GetRequiredService<AutopilotConsoleService>();

        // Full window: newest first, and inside the tied burst descending id.
        var full = await console.ListAuditAsync(waveKey, 6);
        Assert.Equal(new[] { 6, 5, 4, 3, 2, 1 }, full.Select(item => RankOf(item.Id)).ToArray());

        // A shorter window must never keep an older tied record in place of a
        // newer one: the cut keeps ranks 6..3, dropping only 2 and 1.
        var window = await console.ListAuditAsync(waveKey, 4);
        Assert.Equal(new[] { 6, 5, 4, 3 }, window.Select(item => RankOf(item.Id)).ToArray());
        Assert.Equal(full.Select(item => item.Id).Take(4).ToArray(), window.Select(item => item.Id).ToArray());
    }

    [Fact]
    public async Task Audit_window_is_stable_and_complete_across_repeated_reads()
    {
        var waveKey = $"wave-audit-stable-{Guid.NewGuid():N}";
        await SeedTiedBurstAsync(waveKey, 5, salt: 2);
        using var scope = factory.Services.CreateScope();
        var console = scope.ServiceProvider.GetRequiredService<AutopilotConsoleService>();

        var first = await console.ListAuditAsync(waveKey, 5);
        var second = await console.ListAuditAsync(waveKey, 5);

        // Two consecutive reads of the same window must return the same rows
        // in the same order, and that order must be the contract order.
        Assert.Equal(first.Select(item => item.Id).ToArray(), second.Select(item => item.Id).ToArray());
        Assert.Equal(new[] { 5, 4, 3, 2, 1 }, first.Select(item => RankOf(item.Id)).ToArray());
    }

    [Fact]
    public async Task Audit_limit_is_clamped_to_the_documented_window_bounds()
    {
        var waveKey = $"wave-audit-clamp-{Guid.NewGuid():N}";
        await SeedTiedBurstAsync(waveKey, 3, salt: 3);
        using var scope = factory.Services.CreateScope();
        var console = scope.ServiceProvider.GetRequiredService<AutopilotConsoleService>();

        var low = await console.ListAuditAsync(waveKey, 0);
        Assert.Single(low);
        // Even the one-row window must show the newest event of the burst.
        Assert.Equal(3, RankOf(low[0].Id));

        var high = await console.ListAuditAsync(waveKey, int.MaxValue);
        Assert.Equal(3, high.Count);
    }

    [Fact]
    public async Task Audit_endpoint_refuses_an_authenticated_non_administrator()
    {
        using var client = factory.CreateClient();
        await RegisterUser(client, $"audit-plain-{Guid.NewGuid():N}@example.com");
        var response = await client.GetAsync("/api/admin/autopilot/audit");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Audit_endpoint_serialises_only_the_provider_neutral_dto_columns()
    {
        var waveKey = $"wave-audit-redaction-{Guid.NewGuid():N}";
        Guid actorUserId;
        using (var register = factory.CreateClient())
        {
            var actor = await RegisterUser(register, $"audit-actor-{Guid.NewGuid():N}@example.com");
            actorUserId = actor.User.Id;
        }
        await SeedTiedBurstAsync(
            waveKey,
            1,
            salt: 4,
            statusDetail: "status-detail-must-not-escape",
            requestId: "request-id-must-not-escape",
            actorUserId: actorUserId,
            targetId: Guid.NewGuid());

        using var client = factory.CreateClient();
        var auth = await RegisterUser(client, $"audit-admin-{Guid.NewGuid():N}@example.com");
        await AddAdminRole(auth.User.Email);
        var body = await client.GetStringAsync($"/api/admin/autopilot/audit?waveKey={waveKey}&limit=10");

        // The DTO columns arrive...
        Assert.Contains(waveKey, body);
        Assert.Contains("window-boundary reason 1", body);
        // ...and every non-DTO column stays inside the persistence boundary.
        Assert.DoesNotContain("status-detail-must-not-escape", body);
        Assert.DoesNotContain("request-id-must-not-escape", body);
        Assert.DoesNotContain(actorUserId.ToString(), body);
    }

    private static async Task<AuthResponse> RegisterUser(HttpClient client, string email)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Audit Console Test User",
            email,
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
        var csrf = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}
