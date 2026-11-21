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
/// Synthetic contract coverage for the Movie team-review audit trail:
/// the review feed must rank tied audit rows deterministically, and a recorded
/// review decision must survive a stale replay instead of being overwritten.
/// Everything is built from synthetic identities in an isolated in-memory database,
/// with no provider, no charging path, and no production data.
/// </summary>
public sealed class MovieTeamReviewAuditDecisionTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public MovieTeamReviewAuditDecisionTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Recorded_review_decision_survives_a_stale_replay()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Stale Replay Owner");
        var movie = await CreateMovie(owner, ownerAuth.PersonalWorkspace.Id, "Stale replay feature film");
        using var reviewer = factory.CreateClient();
        var reviewerAuth = await Register(reviewer, "Stale Replay Reviewer");
        await AddWorkspaceMember(ownerAuth.PersonalWorkspace.Id, reviewerAuth.User.Id);
        await SendWithCsrf<MovieTeamMemberDto>(owner, HttpMethod.Post, Collaboration(movie) + "/team", new
        {
            userId = reviewerAuth.User.Id,
            role = MovieTeamRoles.Director,
            permissions = Array.Empty<string>(),
        });

        var review = await SendWithCsrf<MovieReviewDto>(owner, HttpMethod.Post, Collaboration(movie) + "/reviews", new
        {
            targetType = MovieCollaborationTargetTypes.Project,
            targetId = movie.Project.Id,
            reviewerUserId = reviewerAuth.User.Id,
            isFinal = false,
            requestNote = "Please sign off the assembled cut.",
        });
        Assert.Equal(MovieReviewStatuses.Pending, review.Status);

        var recorded = await SendWithCsrf<MovieReviewDto>(reviewer, HttpMethod.Post, $"{Collaboration(movie)}/reviews/{review.Id}/decision", new
        {
            status = MovieReviewStatuses.Approved,
            decisionNote = "Approved with a bright final grade.",
        });
        Assert.Equal(MovieReviewStatuses.Approved, recorded.Status);

        // A delayed replay of an abandoned rejection must be refused: the approval
        // already recorded in the audit trail stays intact.
        using var stale = await SendWithCsrfAsync(reviewer, HttpMethod.Post,
            $"{Collaboration(movie)}/reviews/{review.Id}/decision", new
            {
                status = MovieReviewStatuses.Rejected,
                decisionNote = "Scratch the approval.",
            });
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
        var envelope = JsonDocument.Parse(await stale.Content.ReadAsStringAsync());
        Assert.Equal("MOVIE_COLLABORATION_INVALID", envelope.RootElement.GetProperty("error").GetProperty("code").GetString());

        var feed = await owner.GetFromJsonAsync<MovieCollaborationDto>(Collaboration(movie));
        Assert.NotNull(feed);
        var persisted = Assert.Single(feed!.Reviews);
        Assert.Equal(review.Id, persisted.Id);
        Assert.Equal(MovieReviewStatuses.Approved, persisted.Status);
        Assert.Equal("Approved with a bright final grade.", persisted.DecisionNote);
    }

    [Fact]
    public async Task A_replayed_rejection_cannot_reopen_a_decided_review_either()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Reopen Guard Owner");
        var movie = await CreateMovie(owner, ownerAuth.PersonalWorkspace.Id, "Reopen guard feature film");
        using var reviewer = factory.CreateClient();
        var reviewerAuth = await Register(reviewer, "Reopen Guard Reviewer");
        await AddWorkspaceMember(ownerAuth.PersonalWorkspace.Id, reviewerAuth.User.Id);
        await SendWithCsrf<MovieTeamMemberDto>(owner, HttpMethod.Post, Collaboration(movie) + "/team", new
        {
            userId = reviewerAuth.User.Id,
            role = MovieTeamRoles.Editor,
            permissions = Array.Empty<string>(),
        });

        var review = await SendWithCsrf<MovieReviewDto>(owner, HttpMethod.Post, Collaboration(movie) + "/reviews", new
        {
            targetType = MovieCollaborationTargetTypes.Project,
            targetId = movie.Project.Id,
            reviewerUserId = reviewerAuth.User.Id,
            isFinal = false,
        });
        var rejected = await SendWithCsrf<MovieReviewDto>(reviewer, HttpMethod.Post, $"{Collaboration(movie)}/reviews/{review.Id}/decision", new
        {
            status = MovieReviewStatuses.Rejected,
            decisionNote = "The grade collapses in the third act.",
        });
        Assert.Equal(MovieReviewStatuses.Rejected, rejected.Status);
        var recordedAt = rejected.ReviewedAt;

        // An approval must not resurrect a decided rejection; a genuinely different
        // cut earns a freshly requested review instead.
        using var replay = await SendWithCsrfAsync(reviewer, HttpMethod.Post,
            $"{Collaboration(movie)}/reviews/{review.Id}/decision", new { status = MovieReviewStatuses.Approved });
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);

        var feed = await owner.GetFromJsonAsync<MovieCollaborationDto>(Collaboration(movie));
        var persisted = Assert.Single(feed!.Reviews);
        Assert.Equal(MovieReviewStatuses.Rejected, persisted.Status);
        Assert.Equal(recordedAt, persisted.ReviewedAt);
    }

    [Fact]
    public async Task Review_feed_ranks_tied_audit_rows_deterministically()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Tied Feed Owner");
        var movie = await CreateMovie(owner, ownerAuth.PersonalWorkspace.Id, "Tied feed feature film");
        using var reviewer = factory.CreateClient();
        var reviewerAuth = await Register(reviewer, "Tied Feed Reviewer");
        await AddWorkspaceMember(ownerAuth.PersonalWorkspace.Id, reviewerAuth.User.Id);

        // Four synthetic audit rows recorded against one fixed instant (plus one
        // strictly older instant) whose insertion order matches neither the
        // primary timestamp ranking nor any single row-order fallback.
        var tied = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        var older = tied.AddSeconds(-1);
        var idMiddle = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var idLowest = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var idHighest = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var idOldest = Guid.Parse("44444444-4444-4444-4444-444444444444");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.MovieReviews.AddRange(
                new MovieReview { Id = idMiddle, MovieProjectId = movie.Project.Id, TargetType = MovieCollaborationTargetTypes.Project, TargetId = movie.Project.Id, RequestedByUserId = ownerAuth.User.Id, ReviewerUserId = reviewerAuth.User.Id, Status = MovieReviewStatuses.Pending, CreatedAt = tied },
                new MovieReview { Id = idLowest, MovieProjectId = movie.Project.Id, TargetType = MovieCollaborationTargetTypes.Project, TargetId = movie.Project.Id, RequestedByUserId = ownerAuth.User.Id, ReviewerUserId = reviewerAuth.User.Id, Status = MovieReviewStatuses.Pending, CreatedAt = tied },
                new MovieReview { Id = idHighest, MovieProjectId = movie.Project.Id, TargetType = MovieCollaborationTargetTypes.Project, TargetId = movie.Project.Id, RequestedByUserId = ownerAuth.User.Id, ReviewerUserId = reviewerAuth.User.Id, Status = MovieReviewStatuses.Pending, CreatedAt = tied },
                new MovieReview { Id = idOldest, MovieProjectId = movie.Project.Id, TargetType = MovieCollaborationTargetTypes.Project, TargetId = movie.Project.Id, RequestedByUserId = ownerAuth.User.Id, ReviewerUserId = reviewerAuth.User.Id, Status = MovieReviewStatuses.Pending, CreatedAt = older });
            await db.SaveChangesAsync();
        }

        var feed = await owner.GetFromJsonAsync<MovieCollaborationDto>(Collaboration(movie));
        Assert.NotNull(feed);

        // Newest first, and equal instants ranked by descending id - the same rule
        // the operations dashboard audit feed pins - so repeated reads are stable.
        Assert.Equal(new[] { idHighest, idMiddle, idLowest, idOldest }, feed!.Reviews.Select(item => item.Id).ToArray());
    }

    private static string Collaboration(MovieStudioProjectResponse movie) => $"/api/movie-studio/projects/{movie.Project.Id}/collaboration";

    private async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrfAsync(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"movie-review-audit-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private async Task<MovieStudioProjectResponse> CreateMovie(HttpClient client, Guid workspaceId, string title)
    {
        var response = await SendWithCsrfAsync(client, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId,
            mode = MovieProjectModes.Full,
            title,
            description = "A durable review-audit test movie.",
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

    private async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object payload)
    {
        using var response = await SendWithCsrfAsync(client, method, path, payload);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<HttpResponseMessage> SendWithCsrfAsync(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}
