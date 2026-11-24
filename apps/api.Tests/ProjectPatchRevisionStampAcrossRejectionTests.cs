using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Persistence;
using Taslim.Api.Projects;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Gap pin for the #929 lane (draft-only, test-only, one file, touches no owned file):
/// the existing tests prove the committed <em>fields</em> survive a rejected stale
/// <c>If-Match</c> and that the fenced store-level update touches zero rows, but no
/// assertion pins that the <em>revision stamp itself</em> is immutable across the
/// rejection. A regression that stamped <c>UpdatedAt</c> before returning the 409 —
/// burning an unrelated client's still-valid <c>If-Match</c> base — would pass every
/// current test. The interleaving is declared through revisions, so this is fully
/// deterministic and needs no external provider (in-memory store only).
/// Stamps are compared by ticks through <see cref="ProjectRevision.TryParseETag"/>,
/// the same idiom the lane's discoverability test uses, so the pin is independent
/// of the clock-kind the serving store hands back.
/// </summary>
public sealed class ProjectPatchRevisionStampAcrossRejectionTests : IClassFixture<TaslimApiFactory>
{
    private const string Password = "StrongPassword!234";
    private readonly TaslimApiFactory factory;

    public ProjectPatchRevisionStampAcrossRejectionTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task StaleIfMatch_Rejection_AdvancesNoStamp_HandsBackTheStoresCurrentStamp_AndKeepsItUsable()
    {
        var (editorA, editorB, project) = await CreateProjectWithTwoTabsAsync();
        var baseStamp = await ReadRevisionAsync(editorA, project.Id);
        var baseToken = FormatIfMatchToken(baseStamp);

        // Editor B's write lands first and legitimately advances the stamp.
        var bSave = await PatchAsync(editorB, project.Id, baseToken, Form(project, description: "B's synopsis edit"));
        Assert.Equal(HttpStatusCode.OK, bSave.StatusCode);
        var postBStamp = await ReadRevisionAsync(editorA, project.Id);
        Assert.True(postBStamp.Ticks > baseStamp.Ticks, "the accepted write advances the stamp");

        // Editor A fires from the pre-B base: rejected before anything is written.
        var rejected = await PatchAsync(editorA, project.Id, baseToken, Form(project, name: "A's renamed project"));
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        var envelope = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PROJECT_REVISION_CONFLICT",
            envelope.GetProperty("error").GetProperty("code").GetString());

        // Pin 1 — the 409 hands back exactly the post-B stamp, not a fresh one.
        Assert.NotNull(rejected.Headers.ETag);
        Assert.True(ProjectRevision.TryParseETag(rejected.Headers.ETag!.ToString(), out var conflictStamp));
        Assert.Equal(postBStamp.Ticks, conflictStamp.Ticks);

        // Pin 2 — the rejection itself advanced nothing: the store still carries
        // the post-B stamp, so B's If-Match base (and any third reader's) survives.
        using var get = await editorA.GetAsync($"/api/projects/{project.Id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.True(ProjectRevision.TryParseETag(get.Headers.ETag!.ToString(), out var afterRejectionStamp));
        Assert.Equal(postBStamp.Ticks, afterRejectionStamp.Ticks);
        var after = await get.Content.ReadFromJsonAsync<ProjectDto>();
        Assert.NotNull(after);
        Assert.Equal(afterRejectionStamp.Ticks, after.UpdatedAt.Ticks);

        // Pin 3 — the stamp the rejection left behind is still a usable base:
        // re-declaring it with B's committed content lands and only that
        // accepted write advances the stamp further.
        var freshWrite = await PatchAsync(editorB, project.Id, get.Headers.ETag!.ToString(), Form(after!, description: "B's synopsis edit"));
        Assert.Equal(HttpStatusCode.OK, freshWrite.StatusCode);
        var finalStamp = await ReadRevisionAsync(editorA, project.Id);
        Assert.True(finalStamp.Ticks > postBStamp.Ticks,
            "only the accepted write, not the rejected one, advances the stamp");
    }

    private static object Form(ProjectDto project, string? name = null, string? description = null) => new
    {
        name = name ?? project.Name,
        description = description ?? project.Description,
        instructions = project.Instructions,
        contextNotes = project.ContextNotes,
        type = project.Type,
    };

    private async Task<(HttpClient EditorA, HttpClient EditorB, ProjectDto Project)> CreateProjectWithTwoTabsAsync()
    {
        var email = $"stamp-across-rejection-{Guid.NewGuid():N}@example.com";
        using var registrar = factory.CreateClient();
        var registration = await SendWithCsrfAsync(registrar, HttpMethod.Post, "/api/auth/register",
            new { displayName = "Stamp Owner", email, password = Password, preferredLanguage = "en" });
        Assert.True(registration.IsSuccessStatusCode, await registration.Content.ReadAsStringAsync());
        var auth = await registration.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(auth);
        var workspaceId = auth.PersonalWorkspace.Id;

        var editorA = factory.CreateClient();
        await LoginAsync(editorA, email);
        var createToken = await GetCsrfAsync(editorA);
        var project = await SendWithTokenAsync<ProjectDto>(editorA, HttpMethod.Post,
            $"/api/workspaces/{workspaceId}/projects", createToken,
            new { name = "Stamped Campaign", description = "Original synopsis", instructions = "Original notes", contextNotes = "Original context", type = "Business" });

        var editorB = factory.CreateClient();
        await LoginAsync(editorB, email);
        return (editorA, editorB, project);
    }

    private static async Task<DateTime> ReadRevisionAsync(HttpClient client, Guid projectId)
    {
        var project = await client.GetFromJsonAsync<ProjectDto>($"/api/projects/{projectId}");
        Assert.NotNull(project);
        return project.UpdatedAt;
    }

    private static string FormatIfMatchToken(DateTime revision) =>
        "\"" + revision.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "\"";

    private static async Task<HttpResponseMessage> PatchAsync(HttpClient client, Guid projectId, string ifMatch, object body)
    {
        var token = await GetCsrfAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/projects/{projectId}");
        request.Headers.Add("X-CSRF-TOKEN", token);
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    private async Task LoginAsync(HttpClient client, string email)
    {
        var response = await SendWithCsrfAsync(client, HttpMethod.Post, "/api/auth/login", new { email, password = Password });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> GetCsrfAsync(HttpClient client)
    {
        var response = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        return response.GetProperty("token").GetString()!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrfAsync(HttpClient client, HttpMethod method, string path, object payload)
    {
        var token = await GetCsrfAsync(client);
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", token);
        request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private static async Task<T> SendWithTokenAsync<T>(HttpClient client, HttpMethod method, string path, string token, object payload)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", token);
        request.Content = JsonContent.Create(payload);
        var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
