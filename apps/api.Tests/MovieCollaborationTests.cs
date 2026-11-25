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

public sealed class MovieCollaborationTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public MovieCollaborationTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Full_movie_creates_human_owner_and_exposes_collaboration_foundation()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Movie Producer");
        var movie = await CreateMovie(owner, ownerAuth.PersonalWorkspace.Id, "Collab feature film");

        var collaboration = await owner.GetFromJsonAsync<MovieCollaborationDto>($"/api/movie-studio/projects/{movie.Project.Id}/collaboration");

        Assert.NotNull(collaboration);
        var member = Assert.Single(collaboration!.Team);
        Assert.Equal(ownerAuth.User.Id, member.UserId);
        Assert.Equal(MovieTeamRoles.Producer, member.Role);
        Assert.True(member.IsProjectOwner);
        Assert.Contains(MoviePermissions.FinalApproval, member.Permissions);
        Assert.Empty(collaboration.Comments);
        Assert.Empty(collaboration.Credits);
    }

    [Fact]
    public async Task Owner_can_add_member_with_role_and_comment_but_member_cannot_generate_or_manage_team_by_default()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Movie Owner");
        var movie = await CreateMovie(owner, ownerAuth.PersonalWorkspace.Id, "RBAC feature film");
        using var director = factory.CreateClient();
        var directorAuth = await Register(director, "Movie Director");
        await AddWorkspaceMember(ownerAuth.PersonalWorkspace.Id, directorAuth.User.Id);

        var member = await SendWithCsrf<MovieTeamMemberDto>(owner, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/collaboration/team", new
        {
            userId = directorAuth.User.Id,
            role = MovieTeamRoles.Director,
            permissions = Array.Empty<string>(),
        });
        Assert.Equal(MovieTeamRoles.Director, member.Role);
        Assert.Contains(MoviePermissions.Generate, member.Permissions);

        var scene = await SendWithCsrf<MovieSceneDto>(owner, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/scenes", new
        {
            title = "Opening",
            summary = "The film opens in a quiet city.",
        });
        var comment = await SendWithCsrf<MovieCommentDto>(director, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/collaboration/comments", new
        {
            targetType = MovieCollaborationTargetTypes.Scene,
            targetId = scene.Id,
            body = "@Movie Producer please review the opening tone.",
            mentionedUserIds = new[] { ownerAuth.User.Id },
        });
        Assert.Equal(directorAuth.User.Id, comment.AuthorUserId);
        Assert.Single(comment.Mentions);
        Assert.Equal(ownerAuth.User.Id, comment.Mentions[0].UserId);

        var forbiddenTeam = await SendWithCsrf(director, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/collaboration/team", new
        {
            userId = ownerAuth.User.Id,
            role = MovieTeamRoles.Writer,
            permissions = Array.Empty<string>(),
        });
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenTeam.StatusCode);

        var forbiddenAssignment = await SendWithCsrf(director, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/collaboration/assignments", new
        {
            targetType = MovieCollaborationTargetTypes.Scene,
            targetId = scene.Id,
            assigneeUserId = directorAuth.User.Id,
            title = "Draft coverage",
            status = MovieAssignmentStatuses.Open,
        });
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenAssignment.StatusCode);
    }

    [Fact]
    public async Task Review_decision_requires_assigned_reviewer_approval_and_final_approval_is_separate()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Review Owner");
        var movie = await CreateMovie(owner, ownerAuth.PersonalWorkspace.Id, "Review feature film");
        using var editor = factory.CreateClient();
        var editorAuth = await Register(editor, "Review Editor");
        await AddWorkspaceMember(ownerAuth.PersonalWorkspace.Id, editorAuth.User.Id);
        await SendWithCsrf<MovieTeamMemberDto>(owner, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/collaboration/team", new
        {
            userId = editorAuth.User.Id,
            role = MovieTeamRoles.Writer,
            permissions = Array.Empty<string>(),
            permissionOverrides = new[] { new { permission = MoviePermissions.Approve, granted = true } },
        });

        var review = await SendWithCsrf<MovieReviewDto>(owner, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/collaboration/reviews", new
        {
            targetType = MovieCollaborationTargetTypes.Project,
            targetId = movie.Project.Id,
            reviewerUserId = editorAuth.User.Id,
            isFinal = false,
            requestNote = "Please review the cut.",
        });
        Assert.Equal(MovieReviewStatuses.Pending, review.Status);
        var collaboration = await editor.GetFromJsonAsync<MovieCollaborationDto>($"/api/movie-studio/projects/{movie.Project.Id}/collaboration");
        var reviewer = Assert.Single(collaboration!.Team, item => item.UserId == editorAuth.User.Id);
        Assert.Contains(MoviePermissions.Approve, reviewer.Permissions);
        Assert.DoesNotContain(MoviePermissions.Generate, reviewer.Permissions);
        var capabilities = await editor.GetFromJsonAsync<MovieCapabilityResponse>($"/api/movie-studio/projects/{movie.Project.Id}/capabilities");
        Assert.NotNull(capabilities);
        Assert.True(capabilities!.Capabilities[MovieOperationalActions.ReviewsDecision]);
        Assert.False(capabilities.Capabilities[MovieOperationalActions.Generate]);
        Assert.False(capabilities.Capabilities[MovieOperationalActions.TeamManagement]);
        Assert.False(capabilities.Capabilities[MovieOperationalActions.BudgetManagement]);

        var decided = await SendWithCsrf<MovieReviewDto>(editor, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/collaboration/reviews/{review.Id}/decision", new
        {
            status = MovieReviewStatuses.Approved,
            decisionNote = "Approved for the next production stage.",
        });
        Assert.Equal(MovieReviewStatuses.Approved, decided.Status);

        var finalRequest = await SendWithCsrf<MovieReviewDto>(owner, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/collaboration/reviews", new
        {
            targetType = MovieCollaborationTargetTypes.Project,
            targetId = movie.Project.Id,
            reviewerUserId = editorAuth.User.Id,
            isFinal = true,
        });
        var finalDecision = await SendWithCsrf(editor, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/collaboration/reviews/{finalRequest.Id}/decision", new
        {
            status = MovieReviewStatuses.Approved,
        });
        Assert.Equal(HttpStatusCode.Forbidden, finalDecision.StatusCode);
    }

    [Fact]
    public async Task Assignment_assignee_can_update_status_but_unrelated_user_cannot_read_movie()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Assignment Owner");
        var movie = await CreateMovie(owner, ownerAuth.PersonalWorkspace.Id, "Assignment feature film");
        using var writer = factory.CreateClient();
        var writerAuth = await Register(writer, "Assignment Writer");
        await AddWorkspaceMember(ownerAuth.PersonalWorkspace.Id, writerAuth.User.Id);
        await SendWithCsrf<MovieTeamMemberDto>(owner, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/collaboration/team", new
        {
            userId = writerAuth.User.Id,
            role = MovieTeamRoles.Writer,
            permissions = Array.Empty<string>(),
        });
        var assignment = await SendWithCsrf<MovieAssignmentDto>(owner, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/collaboration/assignments", new
        {
            targetType = MovieCollaborationTargetTypes.Project,
            targetId = movie.Project.Id,
            assigneeUserId = writerAuth.User.Id,
            title = "Write the first draft",
            description = "Use the approved guide.",
            status = MovieAssignmentStatuses.Open,
        });
        var updated = await SendWithCsrf<MovieAssignmentDto>(writer, HttpMethod.Patch, $"/api/movie-studio/projects/{movie.Project.Id}/collaboration/assignments/{assignment.Id}", new
        {
            status = MovieAssignmentStatuses.Completed,
            description = "Draft delivered.",
        });
        Assert.Equal(MovieAssignmentStatuses.Completed, updated.Status);

        using var outsider = factory.CreateClient();
        await Register(outsider, "Assignment Outsider");
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/movie-studio/projects/{movie.Project.Id}/collaboration")).StatusCode);
    }

    [Fact]
    public async Task Production_credit_requires_current_human_team_member_and_never_creates_ai_identity()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Credit Producer");
        var movie = await CreateMovie(owner, ownerAuth.PersonalWorkspace.Id, "Credits feature film");
        var credit = await SendWithCsrf<MovieProductionCreditDto>(owner, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/collaboration/credits", new
        {
            userId = ownerAuth.User.Id,
            role = MovieTeamRoles.Producer,
            creditName = "Producer Name",
            sortOrder = 1,
        });
        Assert.Equal(ownerAuth.User.Id, credit.UserId);
        Assert.Equal("Producer Name", credit.CreditName);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.DoesNotContain(await db.Users.ToListAsync(), user => string.Equals(user.Email, "taslim-ai@example.com", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"movie-collab-{Guid.NewGuid():N}@example.com",
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
