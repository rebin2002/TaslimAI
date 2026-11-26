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
/// Synthetic contract coverage for the Movie team authorization mutation surface:
/// role downgrade, permission revocation, replay, and the project-owner guard.
/// Everything is built from synthetic identities in an isolated in-memory database,
/// with no provider, no charging path, no production data and no migration.
/// </summary>
public sealed class MovieTeamOverrideRevocationTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public MovieTeamOverrideRevocationTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Role_downgrade_with_a_surviving_override_persists_the_new_authority()
    {
        var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Downgrade Producer");
        var movie = await CreateMovie(owner, ownerAuth.PersonalWorkspace.Id, "Downgrade feature film");
        var memberClient = factory.CreateClient();
        var memberAuth = await Register(memberClient, "Downgraded Director");
        await AddWorkspaceMember(ownerAuth.PersonalWorkspace.Id, memberAuth.User.Id);

        var member = await SendWithCsrf<MovieTeamMemberDto>(owner, HttpMethod.Post, Team(movie), new
        {
            userId = memberAuth.User.Id,
            role = MovieTeamRoles.Director,
            permissions = Array.Empty<string>(),
            permissionOverrides = new[] { new { permission = MoviePermissions.Approve, granted = true } },
        });

        var downgraded = await SendWithCsrf(owner, HttpMethod.Patch, $"{Team(movie)}/{member.Id}", new
        {
            userId = memberAuth.User.Id,
            role = MovieTeamRoles.Writer,
            permissions = Array.Empty<string>(),
            permissionOverrides = new[] { new { permission = MoviePermissions.Approve, granted = true } },
        });
        Assert.Equal(HttpStatusCode.OK, downgraded.StatusCode);
        var dto = await downgraded.Content.ReadFromJsonAsync<MovieTeamMemberDto>();
        Assert.NotNull(dto);
        Assert.Equal(MovieTeamRoles.Writer, dto!.Role);
        Assert.Contains(MoviePermissions.Approve, dto.Permissions);
        Assert.DoesNotContain(MoviePermissions.Generate, dto.Permissions);

        Assert.Equal(MovieTeamRoles.Writer, await RoleOf(member.Id));
        Assert.Equal(new[] { "Approve:True" }, await OverridesOf(member.Id));
    }

    [Fact]
    public async Task Denied_override_is_revoked_before_the_next_review_mutation()
    {
        var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Revoke Producer");
        var movie = await CreateMovie(owner, ownerAuth.PersonalWorkspace.Id, "Revoke feature film");
        var memberClient = factory.CreateClient();
        var memberAuth = await Register(memberClient, "Revoked Director");
        await AddWorkspaceMember(ownerAuth.PersonalWorkspace.Id, memberAuth.User.Id);

        var member = await SendWithCsrf<MovieTeamMemberDto>(owner, HttpMethod.Post, Team(movie), new
        {
            userId = memberAuth.User.Id,
            role = MovieTeamRoles.Director,
            permissions = Array.Empty<string>(),
        });
        var scene = await SendWithCsrf<MovieSceneDto>(owner, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/scenes", new
        {
            title = "Opening",
            summary = "The film opens in a quiet city.",
        });

        var before = await SendWithCsrf<MovieReviewDto>(owner, HttpMethod.Post, $"{Collab(movie)}/reviews", new
        {
            targetType = MovieCollaborationTargetTypes.Scene,
            targetId = scene.Id,
            reviewerUserId = memberAuth.User.Id,
            isFinal = false,
        });
        var grantedBefore = await SendWithCsrf<MovieReviewDto>(memberClient, HttpMethod.Post, $"{Collab(movie)}/reviews/{before.Id}/decision", new
        {
            status = MovieReviewStatuses.Approved,
            decisionNote = "Approved before revocation.",
        });
        Assert.Equal(MovieReviewStatuses.Approved, grantedBefore.Status);

        var capabilities = await memberClient.GetFromJsonAsync<MovieCapabilityResponse>($"/api/movie-studio/projects/{movie.Project.Id}/capabilities");
        Assert.NotNull(capabilities);
        Assert.True(capabilities!.Capabilities[MovieOperationalActions.ReviewsDecision]);

        // Revocation must be persisted before the next mutation is attempted.
        var revoke = await SendWithCsrf(owner, HttpMethod.Patch, $"{Team(movie)}/{member.Id}", new
        {
            userId = memberAuth.User.Id,
            role = MovieTeamRoles.Director,
            permissions = Array.Empty<string>(),
            permissionOverrides = new[] { new { permission = MoviePermissions.Approve, granted = false } },
        });
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.Equal(new[] { "Approve:False" }, await OverridesOf(member.Id));

        var after = await memberClient.GetFromJsonAsync<MovieCapabilityResponse>($"/api/movie-studio/projects/{movie.Project.Id}/capabilities");
        Assert.NotNull(after);
        Assert.False(after!.Capabilities[MovieOperationalActions.ReviewsDecision]);

        var next = await SendWithCsrf<MovieReviewDto>(owner, HttpMethod.Post, $"{Collab(movie)}/reviews", new
        {
            targetType = MovieCollaborationTargetTypes.Scene,
            targetId = scene.Id,
            reviewerUserId = memberAuth.User.Id,
            isFinal = false,
        });
        var mutation = await SendWithCsrf(memberClient, HttpMethod.Post, $"{Collab(movie)}/reviews/{next.Id}/decision", new
        {
            status = MovieReviewStatuses.Approved,
        });
        Assert.Equal(HttpStatusCode.Forbidden, mutation.StatusCode);
        Assert.Equal(MovieReviewStatuses.Pending, (await LoadReview(owner, movie, next.Id)).Status);
    }

    [Fact]
    public async Task Replayed_identical_team_update_stays_idempotent()
    {
        var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Replay Producer");
        var movie = await CreateMovie(owner, ownerAuth.PersonalWorkspace.Id, "Replay feature film");
        var memberClient = factory.CreateClient();
        var memberAuth = await Register(memberClient, "Replay Writer");
        await AddWorkspaceMember(ownerAuth.PersonalWorkspace.Id, memberAuth.User.Id);

        var member = await SendWithCsrf<MovieTeamMemberDto>(owner, HttpMethod.Post, Team(movie), new
        {
            userId = memberAuth.User.Id,
            role = MovieTeamRoles.Writer,
            permissions = Array.Empty<string>(),
        });

        var payload = new
        {
            userId = memberAuth.User.Id,
            role = MovieTeamRoles.Director,
            permissions = Array.Empty<string>(),
            permissionOverrides = new[] { new { permission = MoviePermissions.Generate, granted = false } },
        };

        var first = await SendWithCsrf(owner, HttpMethod.Patch, $"{Team(movie)}/{member.Id}", payload);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var replay = await SendWithCsrf(owner, HttpMethod.Patch, $"{Team(movie)}/{member.Id}", payload);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        Assert.Equal(MovieTeamRoles.Director, await RoleOf(member.Id));
        Assert.Equal(new[] { "Generate:False" }, await OverridesOf(member.Id));
    }

    [Fact]
    public async Task Override_added_to_a_member_without_overrides_is_persisted()
    {
        var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Grant Producer");
        var movie = await CreateMovie(owner, ownerAuth.PersonalWorkspace.Id, "Grant feature film");
        var memberClient = factory.CreateClient();
        var memberAuth = await Register(memberClient, "Granted Writer");
        await AddWorkspaceMember(ownerAuth.PersonalWorkspace.Id, memberAuth.User.Id);

        var member = await SendWithCsrf<MovieTeamMemberDto>(owner, HttpMethod.Post, Team(movie), new
        {
            userId = memberAuth.User.Id,
            role = MovieTeamRoles.Writer,
            permissions = Array.Empty<string>(),
        });

        var grant = await SendWithCsrf(owner, HttpMethod.Patch, $"{Team(movie)}/{member.Id}", new
        {
            userId = memberAuth.User.Id,
            role = MovieTeamRoles.Writer,
            permissions = Array.Empty<string>(),
            permissionOverrides = new[] { new { permission = MoviePermissions.Generate, granted = true } },
        });
        Assert.Equal(HttpStatusCode.OK, grant.StatusCode);
        Assert.Equal(new[] { "Generate:True" }, await OverridesOf(member.Id));

        var capabilities = await memberClient.GetFromJsonAsync<MovieCapabilityResponse>($"/api/movie-studio/projects/{movie.Project.Id}/capabilities");
        Assert.NotNull(capabilities);
        Assert.True(capabilities!.Capabilities[MovieOperationalActions.Generate]);
    }

    [Fact]
    public async Task Cleared_overrides_are_removed_before_the_downgraded_member_can_mutate()
    {
        var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Clear Producer");
        var movie = await CreateMovie(owner, ownerAuth.PersonalWorkspace.Id, "Clear feature film");
        var memberClient = factory.CreateClient();
        var memberAuth = await Register(memberClient, "Cleared Director");
        await AddWorkspaceMember(ownerAuth.PersonalWorkspace.Id, memberAuth.User.Id);

        var member = await SendWithCsrf<MovieTeamMemberDto>(owner, HttpMethod.Post, Team(movie), new
        {
            userId = memberAuth.User.Id,
            role = MovieTeamRoles.Director,
            permissions = Array.Empty<string>(),
            permissionOverrides = new[] { new { permission = MoviePermissions.Approve, granted = true } },
        });
        var scene = await SendWithCsrf<MovieSceneDto>(owner, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/scenes", new
        {
            title = "Closing",
            summary = "The city sleeps.",
        });
        var review = await SendWithCsrf<MovieReviewDto>(owner, HttpMethod.Post, $"{Collab(movie)}/reviews", new
        {
            targetType = MovieCollaborationTargetTypes.Scene,
            targetId = scene.Id,
            reviewerUserId = memberAuth.User.Id,
            isFinal = false,
        });

        var clear = await SendWithCsrf(owner, HttpMethod.Patch, $"{Team(movie)}/{member.Id}", new
        {
            userId = memberAuth.User.Id,
            role = MovieTeamRoles.Writer,
            permissions = Array.Empty<string>(),
            permissionOverrides = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        Assert.Equal(MovieTeamRoles.Writer, await RoleOf(member.Id));
        Assert.Empty(await OverridesOf(member.Id));

        var mutation = await SendWithCsrf(memberClient, HttpMethod.Post, $"{Collab(movie)}/reviews/{review.Id}/decision", new
        {
            status = MovieReviewStatuses.Approved,
        });
        Assert.Equal(HttpStatusCode.Forbidden, mutation.StatusCode);
    }

    [Fact]
    public async Task Project_owner_membership_cannot_be_downgraded_or_removed()
    {
        var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Owner Guard Producer");
        var movie = await CreateMovie(owner, ownerAuth.PersonalWorkspace.Id, "Owner guard feature film");
        var collaboration = await owner.GetFromJsonAsync<MovieCollaborationDto>($"/api/movie-studio/projects/{movie.Project.Id}/collaboration");
        Assert.NotNull(collaboration);
        var ownerRow = Assert.Single(collaboration!.Team, item => item.IsProjectOwner);

        var downgrade = await SendWithCsrf(owner, HttpMethod.Patch, $"{Team(movie)}/{ownerRow.Id}", new
        {
            userId = ownerAuth.User.Id,
            role = MovieTeamRoles.Writer,
            permissions = Array.Empty<string>(),
        });
        Assert.Equal(HttpStatusCode.BadRequest, downgrade.StatusCode);

        var remove = await SendWithCsrf(owner, HttpMethod.Delete, $"{Team(movie)}/{ownerRow.Id}", null);
        Assert.Equal(HttpStatusCode.BadRequest, remove.StatusCode);

        var after = await owner.GetFromJsonAsync<MovieCollaborationDto>($"/api/movie-studio/projects/{movie.Project.Id}/collaboration");
        Assert.NotNull(after);
        var stillOwner = Assert.Single(after!.Team, item => item.IsProjectOwner);
        Assert.Equal(MovieTeamRoles.Producer, stillOwner.Role);
        Assert.Contains(MoviePermissions.FinalApproval, stillOwner.Permissions);
    }

    private async Task<MovieReviewDto> LoadReview(HttpClient owner, MovieStudioProjectResponse movie, Guid reviewId)
    {
        var collaboration = await owner.GetFromJsonAsync<MovieCollaborationDto>($"/api/movie-studio/projects/{movie.Project.Id}/collaboration");
        Assert.NotNull(collaboration);
        return collaboration!.Reviews.Single(item => item.Id == reviewId);
    }

    private async Task<string> RoleOf(Guid memberId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var member = await db.MovieTeamMembers.AsNoTracking().FirstAsync(item => item.Id == memberId);
        return member.Role;
    }

    private async Task<string[]> OverridesOf(Guid memberId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var rows = await db.MovieTeamMemberPermissions.AsNoTracking()
            .Where(item => item.MovieTeamMemberId == memberId)
            .Select(item => new { item.Permission, item.Granted })
            .ToListAsync();
        return rows.OrderBy(item => item.Permission).Select(item => $"{item.Permission}:{item.Granted}").ToArray();
    }

    private static string Team(MovieStudioProjectResponse movie) => $"{Collab(movie)}/team";

    private static string Collab(MovieStudioProjectResponse movie) => $"/api/movie-studio/projects/{movie.Project.Id}/collaboration";

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"movie-team-revoke-{Guid.NewGuid():N}@example.com",
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
            description = "A durable collaboration test movie.",
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
