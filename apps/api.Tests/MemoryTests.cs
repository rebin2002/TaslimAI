using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MemoryTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public MemoryTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task User_can_create_list_edit_and_delete_a_manual_memory()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Memory Owner");
        var created = await SendWithCsrf<PersonalMemoryDto>(client, HttpMethod.Post, $"/api/workspaces/{auth.PersonalWorkspace.Id}/memories", new
        {
            category = "Writing", title = "Tone", content = "Use a clear, professional tone.",
        });
        Assert.Equal("Manual", created.Source);
        Assert.True(created.IsActive);

        var listed = await client.GetFromJsonAsync<List<PersonalMemoryDto>>($"/api/workspaces/{auth.PersonalWorkspace.Id}/memories");
        Assert.Contains(listed!, item => item.Id == created.Id);

        var updated = await SendWithCsrf<PersonalMemoryDto>(client, HttpMethod.Patch, $"/api/memories/{created.Id}", new
        {
            category = "Preference", title = "Tone", content = "Use short, professional paragraphs.",
        });
        Assert.Equal("Preference", updated.Category);
        Assert.Equal("Use short, professional paragraphs.", updated.Content);

        var deleted = await SendWithCsrf(client, HttpMethod.Delete, $"/api/memories/{created.Id}", null);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        listed = await client.GetFromJsonAsync<List<PersonalMemoryDto>>($"/api/workspaces/{auth.PersonalWorkspace.Id}/memories");
        Assert.DoesNotContain(listed!, item => item.Id == created.Id);
    }

    [Fact]
    public async Task Memory_category_and_length_are_validated()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Memory Validation Owner");
        var invalidCategory = await SendWithCsrf(client, HttpMethod.Post, $"/api/workspaces/{auth.PersonalWorkspace.Id}/memories", new
        {
            category = "SecretSystem", title = "Hidden", content = "Not accepted.",
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidCategory.StatusCode);
        var body = await invalidCategory.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_ERROR", body.GetProperty("error").GetProperty("code").GetString());

        var invalidLength = await SendWithCsrf(client, HttpMethod.Post, $"/api/workspaces/{auth.PersonalWorkspace.Id}/memories", new
        {
            category = "Other", title = "Too long", content = new string('x', 4001),
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidLength.StatusCode);
    }

    [Fact]
    public async Task Memories_are_isolated_by_workspace_and_user_ownership()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Memory Private Owner");
        var memory = await SendWithCsrf<PersonalMemoryDto>(owner, HttpMethod.Post, $"/api/workspaces/{ownerAuth.PersonalWorkspace.Id}/memories", new
        {
            category = "Personal", title = "Private", content = "Private workspace context.",
        });

        using var other = factory.CreateClient();
        var otherAuth = await Register(other, "Memory Other User");
        var list = await other.GetAsync($"/api/workspaces/{ownerAuth.PersonalWorkspace.Id}/memories");
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        var update = await SendWithCsrf(other, HttpMethod.Patch, $"/api/memories/{memory.Id}", new
        {
            category = "Other", title = "Attempt", content = "Must not change.",
        });
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        var otherList = await other.GetFromJsonAsync<List<PersonalMemoryDto>>($"/api/workspaces/{otherAuth.PersonalWorkspace.Id}/memories");
        Assert.Empty(otherList!);
    }

    private async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"memory-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object payload)
    {
        var response = await SendWithCsrf(client, method, path, payload);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
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
