using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class WorkspaceMembershipAuthorizationTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public WorkspaceMembershipAuthorizationTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Workspace_detail_is_forbidden_to_nonmember_and_private_workspace_is_absent_from_list()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Workspace Authorization Owner", $"workspace-owner-{Guid.NewGuid():N}@example.com");

        using var outsider = factory.CreateClient();
        await Register(outsider, "Workspace Authorization Outsider", $"workspace-outsider-{Guid.NewGuid():N}@example.com");

        var detail = await outsider.GetAsync($"/api/workspaces/{ownerAuth.PersonalWorkspace.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, detail.StatusCode);

        var list = await outsider.GetFromJsonAsync<List<WorkspaceSummaryDto>>("/api/workspaces");
        Assert.NotNull(list);
        Assert.DoesNotContain(list!, workspace => workspace.Id == ownerAuth.PersonalWorkspace.Id);
    }

    [Fact]
    public async Task Anonymous_workspace_detail_is_unauthorized_without_workspace_metadata()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Workspace Anonymous Owner", $"workspace-anon-{Guid.NewGuid():N}@example.com");
        using var anonymous = factory.CreateClient();

        var response = await anonymous.GetAsync($"/api/workspaces/{ownerAuth.PersonalWorkspace.Id}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(ownerAuth.PersonalWorkspace.Id.ToString(), await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName, string email)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = JsonContent.Create(new
            {
                displayName,
                email,
                password = "StrongPassword!123",
                preferredLanguage = "en",
            }),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }
}
