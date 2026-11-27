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
/// Synthetic in-process API regression for the fresh-permissions boundary that follows a
/// team permission revocation. A previously authorized project team member (Writer role:
/// the role grants Edit) has that EDIT privilege revoked by a persisted deny override,
/// and every SUBSEQUENT protected READ and protected EDIT request over HTTP must be
/// evaluated against the fresh persisted permission state rather than the authorization
/// snapshot the member established while authorized: the View-gated reads keep answering
/// (the revocation is scoped to Edit, and scoped correctly only if it is re-read from the
/// database), while the Edit-gated mutations the member demonstrably succeeded with before
/// the revocation must now be refused.
///
/// Lane boundaries (checked before writing):
/// - #915 owns the PATCH-team override reconciliation fix itself and its review-decision
///   boundary (`MovieTeamOverrideRevocationTests`). This file never sends `PATCH team`:
///   the revocation is supplied as persisted state, so the boundary observed here is
///   independent of #915's unmerged service change and runs against main as-is.
/// - #1114/#1060/#959 own storyboard scene reorder; untouched here.
/// - #606 owns the caption cue DELETE authorization envelope (the RequireAsync leg);
///   this file deliberately asserts only the HasPermissionAsync null paths, not delete.
/// - #692 pins a seeded deny override at the service level without HTTP; this file is the
///   previously-authorized-then-revoked transition observed through fresh API requests.
/// - #1099 owns timeline transition-edit revocation shape; different surface, untouched.
/// </summary>
public sealed class MovieCaptionEditRevocationBoundaryTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public MovieCaptionEditRevocationBoundaryTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Revoked_edit_flips_edit_gated_legs_while_view_gated_reads_keep_answering_with_fresh_persisted_permissions()
    {
        using var owner = factory.CreateClient();
        using var memberClient = factory.CreateClient();
        var (movieId, memberUserId, trackId, cueId) = await ArrangeAuthorizedEditorWithCueAsync(owner, memberClient, "Fresh-perm member");

        // Revoke EDIT for this member as persisted state (the decision team management
        // would record; the reconciliation of the team PATCH write itself belongs to #915).
        await PersistEditDenyOverrideAsync(movieId, memberUserId);

        // The View-gated protected reads keep answering - not because a stale grant is
        // remembered, but because only Edit was revoked. Each call below is a separate
        // HTTP request served from a fresh DI scope, so success proves the surviving View
        // grant was re-read from the persisted state.
        var readTrack = await memberClient.GetAsync($"/api/movie-studio/caption-tracks/{trackId}");
        var readTracks = await memberClient.GetAsync($"/api/movie-studio/projects/{movieId}/caption-tracks");
        var readTimeline = await memberClient.GetAsync($"/api/movie-studio/projects/{movieId}/caption-timeline");
        var readCollaboration = await memberClient.GetAsync($"/api/movie-studio/projects/{movieId}/collaboration");
        Assert.True(readTrack.IsSuccessStatusCode, await readTrack.Content.ReadAsStringAsync());
        Assert.True(readTracks.IsSuccessStatusCode, await readTracks.Content.ReadAsStringAsync());
        Assert.True(readTimeline.IsSuccessStatusCode, await readTimeline.Content.ReadAsStringAsync());
        Assert.True(readCollaboration.IsSuccessStatusCode, await readCollaboration.Content.ReadAsStringAsync());

        // The Edit-gated mutations the member demonstrably succeeded with during
        // arrangement must now be refused by the persisted denial.
        var deniedEditPost = await SendWithCsrf(memberClient, HttpMethod.Post, $"/api/movie-studio/caption-tracks/{trackId}/cues", new
        {
            sequence = 2,
            startTimecode = "00:00:05.000",
            endTimecode = "00:00:07.000",
            text = "Written after the revocation",
        });
        var deniedEditPatch = await SendWithCsrf(memberClient, HttpMethod.Patch, $"/api/movie-studio/caption-cues/{cueId}", new
        {
            sequence = 1,
            startTimecode = "00:00:01.000",
            endTimecode = "00:00:03.000",
            text = "Rewritten after the revocation",
        });
        var deniedCreate = await SendWithCsrf(memberClient, HttpMethod.Post, $"/api/movie-studio/projects/{movieId}/caption-tracks", new
        {
            name = "Track after the revocation",
            language = "en",
            trackType = "Subtitle",
        });
        Assert.Equal(HttpStatusCode.NotFound, deniedEditPost.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deniedEditPatch.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deniedCreate.StatusCode);

        // The capability surface agrees with the enforcement, and it agrees repeatedly:
        // a second round of requests cannot flip the decision back through any cache.
        for (var round = 0; round < 2; round++)
        {
            var capabilities = await memberClient.GetFromJsonAsync<MovieCapabilityResponse>($"/api/movie-studio/projects/{movieId}/capabilities");
            Assert.NotNull(capabilities);
            Assert.False(capabilities!.Capabilities[MovieOperationalActions.CaptionsEdit]);
            Assert.False(capabilities.Capabilities[MovieOperationalActions.StoryEdit]);
            Assert.False(capabilities.Capabilities[MovieOperationalActions.SceneEdit]);
            Assert.False(capabilities.Capabilities[MovieOperationalActions.ShotEdit]);
            Assert.True(capabilities.Capabilities[MovieOperationalActions.CaptionsView]);
            Assert.True(capabilities.Capabilities[MovieOperationalActions.Comments]);
            Assert.DoesNotContain(MoviePermissions.Edit, capabilities.Permissions);
            Assert.Contains(MoviePermissions.View, capabilities.Permissions);
            Assert.Contains(MoviePermissions.Comment, capabilities.Permissions);

            var repeatedDeniedEdit = await SendWithCsrf(memberClient, HttpMethod.Patch, $"/api/movie-studio/caption-cues/{cueId}", new
            {
                sequence = 1,
                startTimecode = "00:00:01.000",
                endTimecode = "00:00:03.000",
                text = $"Retry {round}",
            });
            Assert.Equal(HttpStatusCode.NotFound, repeatedDeniedEdit.StatusCode);
            var repeatedRead = await memberClient.GetAsync($"/api/movie-studio/caption-tracks/{trackId}");
            Assert.True(repeatedRead.IsSuccessStatusCode, await repeatedRead.Content.ReadAsStringAsync());
        }

        // The collaboration read model reports the same fresh effective permissions.
        var collaboration = await memberClient.GetFromJsonAsync<MovieCollaborationDto>($"/api/movie-studio/projects/{movieId}/collaboration");
        Assert.NotNull(collaboration);
        var self = Assert.Single(collaboration!.Team, item => item.UserId == memberUserId);
        Assert.DoesNotContain(MoviePermissions.Edit, self.Permissions);
        Assert.Contains(MoviePermissions.View, self.Permissions);

        // And the denied mutations left nothing behind.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(1, await db.MovieCaptionCues.CountAsync(item => item.MovieCaptionTrackId == trackId));
        var cue = await db.MovieCaptionCues.AsNoTracking().SingleAsync(item => item.Id == cueId);
        Assert.Equal("Authorized first light", cue.Text);
    }

    [Fact]
    public async Task Revoked_edit_denial_is_per_user_and_leaves_persisted_cue_untouched()
    {
        using var owner = factory.CreateClient();
        using var memberClient = factory.CreateClient();
        var (movieId, memberUserId, trackId, cueId) = await ArrangeAuthorizedEditorWithCueAsync(owner, memberClient, "Per-user member");

        await PersistEditDenyOverrideAsync(movieId, memberUserId);

        // The denied caller attempts a silent rewrite; it must not reach the row.
        var deniedPatch = await SendWithCsrf(memberClient, HttpMethod.Patch, $"/api/movie-studio/caption-cues/{cueId}", new
        {
            sequence = 1,
            startTimecode = "00:00:01.000",
            endTimecode = "00:00:03.000",
            text = "Silently rewritten by the denied caller",
        });
        Assert.Equal(HttpStatusCode.NotFound, deniedPatch.StatusCode);

        // The owner is untouched by the per-user revocation and proves the row survived.
        var ownerTrack = await owner.GetFromJsonAsync<MovieCaptionTrackDto>($"/api/movie-studio/caption-tracks/{trackId}");
        Assert.NotNull(ownerTrack);
        var stored = Assert.Single(ownerTrack!.Cues);
        Assert.Equal("Authorized first light", stored.Text);
        Assert.Equal(1, stored.Sequence);

        var ownerCapabilities = await owner.GetFromJsonAsync<MovieCapabilityResponse>($"/api/movie-studio/projects/{movieId}/capabilities");
        Assert.NotNull(ownerCapabilities);
        Assert.True(ownerCapabilities!.Capabilities[MovieOperationalActions.CaptionsEdit]);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var deniedMember = await db.MovieTeamMembers.AsNoTracking()
            .Include(item => item.PermissionOverrides)
            .SingleAsync(item => item.MovieProjectId == movieId && item.UserId == memberUserId);
        var denial = Assert.Single(deniedMember.PermissionOverrides);
        Assert.Equal(MoviePermissions.Edit, denial.Permission);
        Assert.False(denial.Granted);
    }

    /// <summary>
    /// Establishes a member who is genuinely authorized for the Edit-gated boundary over
    /// the API before the revocation: role Writer (View+Comment+Edit), one successful
    /// Edit-gated READ, one successful Edit-gated mutation, and a capability map that
    /// advertises Edit. The caller owns the two clients' lifetimes.
    /// </summary>
    private async Task<(Guid MovieId, Guid MemberUserId, Guid TrackId, Guid CueId)> ArrangeAuthorizedEditorWithCueAsync(HttpClient owner, HttpClient memberClient, string memberName)
    {
        var ownerAuth = await Register(owner, $"{memberName} producer");
        var movie = await CreateMovie(owner, ownerAuth.PersonalWorkspace.Id, $"{memberName} movie");
        var movieId = movie.Project.Id;

        var memberAuth = await Register(memberClient, memberName);
        await AddWorkspaceMember(ownerAuth.PersonalWorkspace.Id, memberAuth.User.Id);

        await SendWithCsrf<MovieTeamMemberDto>(owner, HttpMethod.Post, $"/api/movie-studio/projects/{movieId}/collaboration/team", new
        {
            userId = memberAuth.User.Id,
            role = MovieTeamRoles.Writer,
            permissions = Array.Empty<string>(),
        });

        var track = await SendWithCsrf<MovieCaptionTrackDto>(owner, HttpMethod.Post, $"/api/movie-studio/projects/{movieId}/caption-tracks", new
        {
            name = "Revanilla subs",
            language = "en",
            trackType = "Subtitle",
        });

        var capabilities = await memberClient.GetFromJsonAsync<MovieCapabilityResponse>($"/api/movie-studio/projects/{movieId}/capabilities");
        Assert.NotNull(capabilities);
        Assert.True(capabilities!.Capabilities[MovieOperationalActions.CaptionsEdit]);
        Assert.True(capabilities.Capabilities[MovieOperationalActions.SceneEdit]);

        var authorizedRead = await memberClient.GetAsync($"/api/movie-studio/caption-tracks/{track.Id}");
        Assert.True(authorizedRead.IsSuccessStatusCode, await authorizedRead.Content.ReadAsStringAsync());

        var cue = await SendWithCsrf<MovieCaptionCueDto>(memberClient, HttpMethod.Post, $"/api/movie-studio/caption-tracks/{track.Id}/cues", new
        {
            sequence = 1,
            startTimecode = "00:00:01.000",
            endTimecode = "00:00:03.000",
            text = "Authorized first light",
        });

        return (movieId, memberAuth.User.Id, track.Id, cue.Id);
    }

    /// <summary>
    /// Supplies the team-management decision as persisted state: the member's role keeps
    /// granting Edit, and only the explicit deny override revokes the privilege. The
    /// write path exercised here is a fresh insert of the denial row - not the PATCH-team
    /// reconciliation fixed by #915 - so this file observes the downstream boundary
    /// against the same database the authorization gate reads.
    /// </summary>
    private async Task PersistEditDenyOverrideAsync(Guid movieProjectId, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var member = await db.MovieTeamMembers.SingleAsync(item => item.MovieProjectId == movieProjectId && item.UserId == userId);
        db.MovieTeamMemberPermissions.Add(new MovieTeamMemberPermission
        {
            Id = Guid.NewGuid(),
            MovieTeamMemberId = member.Id,
            Permission = MoviePermissions.Edit,
            Granted = false,
        });
        await db.SaveChangesAsync();
    }

    private async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"caption-revocation-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<MovieStudioProjectResponse> CreateMovie(HttpClient client, Guid workspaceId, string title)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId,
            mode = MovieProjectModes.Full,
            title,
            description = "A durable caption edit-revocation test movie.",
            durationSeconds = 120,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "en",
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<MovieStudioProjectResponse>())!;
    }

    private async Task AddWorkspaceMember(Guid workspaceId, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Member, JoinedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object payload)
    {
        using var response = await SendWithCsrf(client, method, path, payload);
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
