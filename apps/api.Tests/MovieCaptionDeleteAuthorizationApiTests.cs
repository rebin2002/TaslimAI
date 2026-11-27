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
/// Authorization and privacy boundary for caption cue deletion.
///
/// Deletion is the caption mutation that enforces permission through the fail-closed
/// collaboration guard, so a denial must surface as a deliberate status code rather
/// than an unhandled server error. It is also the mutation that could turn the API
/// into a cue-existence oracle, because the cue identifier is the only locator in the
/// route: an answer that differs between "there is no such cue" and "there is a cue
/// you may not touch" lets any authenticated caller confirm that a cue id is real
/// without ever being granted access to it.
///
/// The contract pinned here therefore mirrors <c>UpdateCueAsync</c>, the sibling
/// mutation of the same resource: a cue outside the caller's visibility and an unknown
/// cue id collapse into one indistinguishable 404, while a 403 stays reserved for a
/// cue the caller can already see (that caller can enumerate the track's cues through
/// the read routes) but is not allowed to edit.
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
    public async Task Outsider_deleting_a_foreign_caption_cue_is_not_found_and_leaves_the_cue_intact()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await RegisterAsync(owner, "Caption Delete Foreign Owner");
        var project = await CreateMovieAsync(owner, ownerAuth.PersonalWorkspace.Id, "Caption delete foreign cue");
        var track = await CreateTrackAsync(owner, project.Project.Id);
        var cue = await CreateCueAsync(owner, track.Id);

        using var outsider = factory.CreateClient();
        await RegisterAsync(outsider, "Caption Delete Foreign Outsider");

        using var response = await SendWithCsrfAsync(outsider, HttpMethod.Delete, $"/api/movie-studio/caption-cues/{cue.Id}", null);

        // A cue the caller cannot see must never be answered differently from a cue
        // that does not exist: 404 (not 403, and never a 500) is the only safe reply.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MOVIE_CAPTION_CUE_NOT_FOUND", body.GetProperty("error").GetProperty("code").GetString());

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

        // Sharing a workspace is not the same as being on the movie team: the cue is
        // outside this caller's visibility, so the answer must be the not-found one.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MOVIE_CAPTION_CUE_NOT_FOUND", body.GetProperty("error").GetProperty("code").GetString());
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

    [Fact]
    public async Task Owner_deleting_an_unknown_caption_cue_id_is_not_found()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await RegisterAsync(owner, "Caption Delete Owner Unknown Id");
        await CreateMovieAsync(owner, ownerAuth.PersonalWorkspace.Id, "Caption delete unknown id");

        using var response = await SendWithCsrfAsync(owner, HttpMethod.Delete, $"/api/movie-studio/caption-cues/{Guid.NewGuid()}", null);

        // The not-found answer stays available to permitted callers, so a typo or a cue
        // deleted in another tab is still reported honestly instead of as 403.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MOVIE_CAPTION_CUE_NOT_FOUND", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Foreign_caption_cue_and_unknown_caption_cue_are_indistinguishable_to_an_outsider()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await RegisterAsync(owner, "Caption Delete Oracle Owner");
        var project = await CreateMovieAsync(owner, ownerAuth.PersonalWorkspace.Id, "Caption delete existence oracle");
        var track = await CreateTrackAsync(owner, project.Project.Id);
        var cue = await CreateCueAsync(owner, track.Id);

        using var outsider = factory.CreateClient();
        await RegisterAsync(outsider, "Caption Delete Oracle Outsider");

        using var existing = await SendWithCsrfAsync(outsider, HttpMethod.Delete, $"/api/movie-studio/caption-cues/{cue.Id}", null);
        var existingBody = await existing.Content.ReadAsStringAsync();

        using var unknown = await SendWithCsrfAsync(outsider, HttpMethod.Delete, $"/api/movie-studio/caption-cues/{Guid.NewGuid()}", null);
        var unknownBody = await unknown.Content.ReadAsStringAsync();

        // Status, error code and message are identical end to end, so repeated deletes
        // of candidate ids cannot rank existing cues against absent ones: the delete
        // route leaks nothing about whether a cue id exists in someone else's project.
        Assert.Equal(unknown.StatusCode, existing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, existing.StatusCode);
        Assert.Equal(existingBody, unknownBody);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.NotNull(await db.MovieCaptionCues.AsNoTracking().FirstOrDefaultAsync(item => item.Id == cue.Id));
    }

    [Fact]
    public async Task Visible_cue_without_edit_permission_is_forbidden_and_stays_intact()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await RegisterAsync(owner, "Caption Delete Reviewer Owner");
        var project = await CreateMovieAsync(owner, ownerAuth.PersonalWorkspace.Id, "Caption delete visible but unpermitted");
        var track = await CreateTrackAsync(owner, project.Project.Id);
        var cue = await CreateCueAsync(owner, track.Id);

        using var reviewer = factory.CreateClient();
        var reviewerAuth = await RegisterAsync(reviewer, "Caption Delete Viewer Reviewer");
        await AddWorkspaceMemberAsync(ownerAuth.PersonalWorkspace.Id, reviewerAuth.User.Id);
        // A movie team member who can see the track but whose edit right is denied: the
        // legitimate permission case, which must stay a deliberate 403 rather than a 500.
        await AddMovieTeamMemberAsync(project.Project.Id, reviewerAuth.User.Id, denyEdit: true);

        using var visible = await SendWithCsrfAsync(reviewer, HttpMethod.Get, $"/api/movie-studio/projects/{project.Project.Id}/caption-tracks", null);
        Assert.True(visible.IsSuccessStatusCode, await visible.Content.ReadAsStringAsync());

        using var response = await SendWithCsrfAsync(reviewer, HttpMethod.Delete, $"/api/movie-studio/caption-cues/{cue.Id}", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MOVIE_PERMISSION_DENIED", body.GetProperty("error").GetProperty("code").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.NotNull(await db.MovieCaptionCues.AsNoTracking().FirstOrDefaultAsync(item => item.Id == cue.Id));
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

    private async Task AddMovieTeamMemberAsync(Guid movieProjectId, Guid userId, bool denyEdit)
    {
        var now = DateTime.UtcNow;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        db.MovieTeamMembers.Add(new MovieTeamMember
        {
            Id = Guid.NewGuid(),
            MovieProjectId = movieProjectId,
            UserId = userId,
            Role = MovieTeamRoles.Writer,
            CreatedAt = now,
            UpdatedAt = now,
            PermissionOverrides = denyEdit
                ? [new MovieTeamMemberPermission { Id = Guid.NewGuid(), Permission = MoviePermissions.Edit, Granted = false }]
                : [],
        });
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
