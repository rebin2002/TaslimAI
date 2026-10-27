using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Taslim.Api.Controllers;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieCollaborationControllerPrincipalTests
{
    [Fact]
    public async Task Malformed_authenticated_principal_is_rejected_by_every_collaboration_action()
    {
        var service = new RecordingMovieCollaborationService();
        var controller = CreateController(service);
        var movieProjectId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        var commentId = Guid.NewGuid();
        var reviewId = Guid.NewGuid();
        var assignmentId = Guid.NewGuid();
        var creditId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var results = new IActionResult[]
        {
            await controller.Get(movieProjectId, default),
            await controller.AddMember(movieProjectId, new MovieTeamMemberRequest(userId, "Editor", [], []), default),
            await controller.UpdateMember(movieProjectId, memberId, new MovieTeamMemberRequest(userId, "Editor", [], []), default),
            await controller.RemoveMember(movieProjectId, memberId, default),
            await controller.AddComment(movieProjectId, new MovieCommentRequest("scene", Guid.NewGuid(), "Comment", null, []), default),
            await controller.ResolveComment(movieProjectId, commentId, default),
            await controller.RequestReview(movieProjectId, new MovieReviewRequest("scene", Guid.NewGuid(), userId, false, null), default),
            await controller.DecideReview(movieProjectId, reviewId, new MovieReviewDecisionRequest("Approved", null), default),
            await controller.AddAssignment(movieProjectId, new MovieAssignmentRequest("scene", Guid.NewGuid(), userId, "Review shot", null, "Open", null), default),
            await controller.UpdateAssignment(movieProjectId, assignmentId, new MovieAssignmentUpdateRequest(null, "Open", null), default),
            await controller.AddCredit(movieProjectId, new MovieProductionCreditRequest(userId, "Editor", null, 0), default),
            await controller.RemoveCredit(movieProjectId, creditId, default),
        };

        Assert.All(results, result => Assert.IsType<UnauthorizedResult>(result));
        Assert.Equal(0, service.CallCount);
    }

    private static MovieCollaborationController CreateController(RecordingMovieCollaborationService service)
    {
        return new MovieCollaborationController(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, "not-a-guid")],
                        authenticationType: "test")),
                },
            },
        };
    }

    private sealed class RecordingMovieCollaborationService : IMovieCollaborationService
    {
        public int CallCount { get; private set; }

        public Task<MovieCollaborationDto?> GetAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult<MovieCollaborationDto?>(null);
        }

        public Task<MovieTeamMemberDto?> AddMemberAsync(Guid userId, Guid movieProjectId, MovieTeamMemberRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult<MovieTeamMemberDto?>(null);
        }

        public Task<MovieTeamMemberDto?> UpdateMemberAsync(Guid userId, Guid movieProjectId, Guid memberId, MovieTeamMemberRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult<MovieTeamMemberDto?>(null);
        }

        public Task<bool> RemoveMemberAsync(Guid userId, Guid movieProjectId, Guid memberId, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(false);
        }

        public Task<MovieCommentDto?> AddCommentAsync(Guid userId, Guid movieProjectId, MovieCommentRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult<MovieCommentDto?>(null);
        }

        public Task<MovieCommentDto?> ResolveCommentAsync(Guid userId, Guid movieProjectId, Guid commentId, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult<MovieCommentDto?>(null);
        }

        public Task<MovieReviewDto?> RequestReviewAsync(Guid userId, Guid movieProjectId, MovieReviewRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult<MovieReviewDto?>(null);
        }

        public Task<MovieReviewDto?> DecideReviewAsync(Guid userId, Guid movieProjectId, Guid reviewId, MovieReviewDecisionRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult<MovieReviewDto?>(null);
        }

        public Task<MovieAssignmentDto?> AddAssignmentAsync(Guid userId, Guid movieProjectId, MovieAssignmentRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult<MovieAssignmentDto?>(null);
        }

        public Task<MovieAssignmentDto?> UpdateAssignmentAsync(Guid userId, Guid movieProjectId, Guid assignmentId, MovieAssignmentUpdateRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult<MovieAssignmentDto?>(null);
        }

        public Task<MovieProductionCreditDto?> AddCreditAsync(Guid userId, Guid movieProjectId, MovieProductionCreditRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult<MovieProductionCreditDto?>(null);
        }

        public Task<bool> RemoveCreditAsync(Guid userId, Guid movieProjectId, Guid creditId, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(false);
        }
    }
}
