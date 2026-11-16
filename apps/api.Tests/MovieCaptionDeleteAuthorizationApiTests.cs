using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Authorization boundary for caption cue deletion. Deletion is the one caption
/// mutation that enforces permission through the fail-closed collaboration
/// guard, so an outsider request must surface as a 403 permission denial rather
/// than an unhandled server error, and it must never mutate the owner's cue.
/// </summary>
public sealed class MovieCaptionDeleteAuthorizationApiTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public MovieCaptionDeleteAuthorizationApiTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Outsider_caption_cue_delete_is_forbidden_and_leaves_the_cue_intact()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await RegisterAsync(owner, "Caption Delete Owner");
        var project = await CreateMovieAsync(owner, ownerAuth.PersonalWorkspace.Id, "Caption delete authorization");
        var track = await CreateTrackAsync(owner, project.Project.Id);
        var cue = await CreateCueAsync(owner, track.Id);

        using var outsider = factory.CreateClient();
        await RegisterAsync(outsider, "Caption Delete Outsider");

        using var response = await SendWithCsrfAsync(outsider, HttpMethod.Delete, $"/api/movie-studio/caption-cues/{cue.Id}", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MOVIE_PERMISSION_DENIED", body.GetProperty("error").GetProperty("code").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.NotNull(await db.MovieCaptionCues.AsNoTracking().FirstOrDefaultAsync(item => item.Id == cue.Id));
        Assert.True(await db.MovieCaptionTracks.AsNoTracking().AnyAsync(item => item.Id == track.Id && item.MovieProjectId == project.Project.Id));
    }

    [Fact]
    public async Task Workspace_member_without_movie_edit_permission_cannot_delete_a_caption_cue()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await RegisterAsync(owner, "Caption Delete Producer");
        var project = await CreateMovieAsync(owner, ownerAuth.PersonalWorkspace.Id, "Caption delete workspace member");
        var track = await CreateTrackAsync(owner, project.Project.Id);
        var cue = await CreateCueAsync(owner, track.Id);

        using var member = factory.CreateClient();
        var memberAuth = await RegisterAsync(member, "Caption Delete Workspace Member");
        await AddWorkspaceMemberAsync(ownerAuth.PersonalWorkspace.Id, memberAuth.User.Id);

        using var response = await SendWithCsrfAsync(member, HttpMethod.Delete, $"/api/movie-studio/caption-cues/{cue.Id}", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.NotNull(await db.MovieCaptionCues.AsNoTracking().FirstOrDefaultAsync(item => item.Id == cue.Id));
    }

    [Fact]
    public async Task Owner_caption_cue_delete_succeeds_and_removes_only_that_cue()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await RegisterAsync(owner, "Caption Delete Owner Success");
        var project = await CreateMovieAsync(owner, ownerAuth.PersonalWorkspace.Id, "Caption delete success path");
        var track = await CreateTrackAsync(owner, project.Project.Id);
        var first = await CreateCueAsync(owner, track.Id, 1, "00:00:01.000", "00:00:02.000", "First cue");
        var second = await CreateCueAsync(owner, track.Id, 2, "00:00:03.000", "00:00:04.000", "Second cue");

        using var response = await SendWithCsrfAsync(owner, HttpMethod.Delete, $"/api/movie-studio/caption-cues/{first.Id}", null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Null(await db.MovieCaptionCues.AsNoTracking().FirstOrDefaultAsync(item => item.Id == first.Id));
        Assert.NotNull(await db.MovieCaptionCues.AsNoTracking().FirstOrDefaultAsync(item => item.Id == second.Id));
    }

    private async Task<AuthResponse> RegisterAsync(HttpClient client, string displayName)
    {
        using var response = await SendWithCsrfAsync(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"caption-delete-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<MovieStudioProjectResponse> CreateMovieAsync(HttpClient client, Guid workspaceId, string title)
    {
        using var response = await SendWithCsrfAsync(client, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId,
            mode = MovieProjectModes.Full,
            title,
            description = "Provider-free caption deletion authorization test.",
            durationSeconds = 120,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "en",
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<MovieStudioProjectResponse>())!;
    }

    private static async Task<MovieCaptionTrackDto> CreateTrackAsync(HttpClient client, Guid projectId)
    {
        using var response = await SendWithCsrfAsync(client, HttpMethod.Post, $"/api/movie-studio/projects/{projectId}/caption-tracks", new
        {
            name = "English subtitles",
            trackType = MovieCaptionTrackTypes.Subtitle,
            language = "en",
            status = MovieCaptionTrackStatuses.Draft,
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<MovieCaptionTrackDto>())!;
    }

    private static Task<MovieCaptionCueDto> CreateCueAsync(HttpClient client, Guid trackId) =>
        CreateCueAsync(client, trackId, 1, "00:00:01.000", "00:00:02.000", "Private cue");

    private static async Task<MovieCaptionCueDto> CreateCueAsync(HttpClient client, Guid trackId, int sequence, string start, string end, string text)
    {
        using var response = await SendWithCsrfAsync(client, HttpMethod.Post, $"/api/movie-studio/caption-tracks/{trackId}/cues", new
        {
            sequence,
            startTimecode = start,
            endTimecode = end,
            text,
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<MovieCaptionCueDto>())!;
    }

    private async Task AddWorkspaceMemberAsync(Guid workspaceId, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Member, JoinedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    private static async Task<HttpResponseMessage> SendWithCsrfAsync(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}
