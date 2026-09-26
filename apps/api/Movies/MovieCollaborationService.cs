using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public sealed class MovieCollaborationForbiddenException(string message = "You do not have permission to perform this action.") : Exception(message);
public sealed class MovieCollaborationValidationException(string message) : Exception(message);

public sealed class MovieCollaborationAccess(TaslimDbContext db, WorkspaceAccessService workspaceAccess)
{
    public async Task<MovieTeamMember?> GetMemberAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken)
    {
        var movie = await db.MovieProjects.AsNoTracking().FirstOrDefaultAsync(item => item.Id == movieProjectId, cancellationToken);
        if (movie is null || !await workspaceAccess.IsMemberAsync(userId, movie.WorkspaceId, cancellationToken)) return null;
        return await db.MovieTeamMembers.Include(item => item.PermissionOverrides)
            .FirstOrDefaultAsync(item => item.MovieProjectId == movieProjectId && item.UserId == userId, cancellationToken);
    }

    public async Task<bool> HasPermissionAsync(Guid userId, Guid movieProjectId, string permission, CancellationToken cancellationToken)
    {
        var member = await GetMemberAsync(userId, movieProjectId, cancellationToken);
        if (member is null) return false;
        return member.IsProjectOwner || HasPermission(member, permission);
    }

    public static bool HasPermission(MovieTeamMember member, string permission)
    {
        var overridePermission = member.PermissionOverrides.FirstOrDefault(item => string.Equals(item.Permission, permission, StringComparison.OrdinalIgnoreCase));
        return overridePermission?.Granted ?? MoviePermissions.ForRole(member.Role).Contains(permission);
    }

    public static IReadOnlyList<string> EffectivePermissions(MovieTeamMember member) =>
        MoviePermissions.Supported.Where(permission => member.IsProjectOwner || HasPermission(member, permission)).OrderBy(permission => permission).ToArray();

    public async Task RequireAsync(Guid userId, Guid movieProjectId, string permission, CancellationToken cancellationToken)
    {
        if (!await HasPermissionAsync(userId, movieProjectId, permission, cancellationToken)) throw new MovieCollaborationForbiddenException();
    }
}

public interface IMovieCollaborationService
{
    Task<MovieCollaborationDto?> GetAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken);
    Task<MovieTeamMemberDto?> AddMemberAsync(Guid userId, Guid movieProjectId, MovieTeamMemberRequest request, CancellationToken cancellationToken);
    Task<MovieTeamMemberDto?> UpdateMemberAsync(Guid userId, Guid movieProjectId, Guid memberId, MovieTeamMemberRequest request, CancellationToken cancellationToken);
    Task<bool> RemoveMemberAsync(Guid userId, Guid movieProjectId, Guid memberId, CancellationToken cancellationToken);
    Task<MovieCommentDto?> AddCommentAsync(Guid userId, Guid movieProjectId, MovieCommentRequest request, CancellationToken cancellationToken);
    Task<MovieCommentDto?> ResolveCommentAsync(Guid userId, Guid movieProjectId, Guid commentId, CancellationToken cancellationToken);
    Task<MovieReviewDto?> RequestReviewAsync(Guid userId, Guid movieProjectId, MovieReviewRequest request, CancellationToken cancellationToken);
    Task<MovieReviewDto?> DecideReviewAsync(Guid userId, Guid movieProjectId, Guid reviewId, MovieReviewDecisionRequest request, CancellationToken cancellationToken);
    Task<MovieAssignmentDto?> AddAssignmentAsync(Guid userId, Guid movieProjectId, MovieAssignmentRequest request, CancellationToken cancellationToken);
    Task<MovieAssignmentDto?> UpdateAssignmentAsync(Guid userId, Guid movieProjectId, Guid assignmentId, MovieAssignmentUpdateRequest request, CancellationToken cancellationToken);
    Task<MovieProductionCreditDto?> AddCreditAsync(Guid userId, Guid movieProjectId, MovieProductionCreditRequest request, CancellationToken cancellationToken);
    Task<bool> RemoveCreditAsync(Guid userId, Guid movieProjectId, Guid creditId, CancellationToken cancellationToken);
}

public sealed class MovieCollaborationService(TaslimDbContext db, WorkspaceAccessService workspaceAccess, MovieCollaborationAccess access) : IMovieCollaborationService
{
    public async Task<MovieCollaborationDto?> GetAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken)
    {
        if (!await access.HasPermissionAsync(userId, movieProjectId, MoviePermissions.View, cancellationToken)) return null;
        var movie = await db.MovieProjects.AsNoTracking().FirstOrDefaultAsync(item => item.Id == movieProjectId, cancellationToken);
        if (movie is null) return null;
        var team = await db.MovieTeamMembers.AsNoTracking().Include(item => item.User).Include(item => item.PermissionOverrides)
            .Where(item => item.MovieProjectId == movieProjectId).OrderByDescending(item => item.IsProjectOwner).ThenBy(item => item.CreatedAt).ToListAsync(cancellationToken);
        var comments = await db.MovieComments.AsNoTracking().Include(item => item.AuthorUser).Include(item => item.Mentions).ThenInclude(item => item.MentionedUser)
            .Where(item => item.MovieProjectId == movieProjectId).OrderBy(item => item.CreatedAt).ToListAsync(cancellationToken);
        var reviews = await db.MovieReviews.AsNoTracking().Include(item => item.RequestedByUser).Include(item => item.ReviewerUser)
            .Where(item => item.MovieProjectId == movieProjectId).OrderByDescending(item => item.CreatedAt).ToListAsync(cancellationToken);
        var assignments = await db.MovieProductionAssignments.AsNoTracking().Include(item => item.AssigneeUser).Include(item => item.AssignedByUser)
            .Where(item => item.MovieProjectId == movieProjectId).OrderBy(item => item.Status).ThenBy(item => item.DueAt).ToListAsync(cancellationToken);
        var credits = await db.MovieProductionCredits.AsNoTracking().Include(item => item.User).Where(item => item.MovieProjectId == movieProjectId)
            .OrderBy(item => item.SortOrder).ThenBy(item => item.CreatedAt).ToListAsync(cancellationToken);
        var currentMember = team.FirstOrDefault(item => item.UserId == userId);
        return new MovieCollaborationDto(
            movieProjectId,
            team.Select(ToDto).ToArray(),
            comments.Select(ToDto).ToArray(),
            reviews.Select(ToDto).ToArray(),
            assignments.Select(ToDto).ToArray(),
            credits.Select(ToDto).ToArray(),
            currentMember is null ? [] : MovieCollaborationAccess.EffectivePermissions(currentMember));
    }

    public async Task<MovieTeamMemberDto?> AddMemberAsync(Guid userId, Guid movieProjectId, MovieTeamMemberRequest request, CancellationToken cancellationToken)
    {
        await access.RequireAsync(userId, movieProjectId, MoviePermissions.ManageTeam, cancellationToken);
        ValidateRoleAndPermissions(request.Role, request.Permissions ?? [], request.PermissionOverrides ?? []);
        var movie = await db.MovieProjects.AsNoTracking().FirstOrDefaultAsync(item => item.Id == movieProjectId, cancellationToken);
        if (movie is null) return null;
        if (!await workspaceAccess.IsMemberAsync(request.UserId, movie.WorkspaceId, cancellationToken)) throw new MovieCollaborationValidationException("The user must already be a member of the movie workspace.");
        if (await db.MovieTeamMembers.AnyAsync(item => item.MovieProjectId == movieProjectId && item.UserId == request.UserId, cancellationToken))
            throw new MovieCollaborationValidationException("The user is already on this movie team.");
        var now = DateTime.UtcNow;
        var member = new MovieTeamMember { Id = Guid.NewGuid(), MovieProjectId = movieProjectId, UserId = request.UserId, Role = request.Role.Trim(), CreatedAt = now, UpdatedAt = now };
        ApplyOverrides(member, request.Permissions ?? [], request.PermissionOverrides ?? []);
        db.MovieTeamMembers.Add(member);
        await db.SaveChangesAsync(cancellationToken);
        return await LoadMemberDtoAsync(member.Id, cancellationToken);
    }

    public async Task<MovieTeamMemberDto?> UpdateMemberAsync(Guid userId, Guid movieProjectId, Guid memberId, MovieTeamMemberRequest request, CancellationToken cancellationToken)
    {
        await access.RequireAsync(userId, movieProjectId, MoviePermissions.ManageTeam, cancellationToken);
        ValidateRoleAndPermissions(request.Role, request.Permissions ?? [], request.PermissionOverrides ?? []);
        var member = await db.MovieTeamMembers.Include(item => item.PermissionOverrides).FirstOrDefaultAsync(item => item.Id == memberId && item.MovieProjectId == movieProjectId, cancellationToken);
        if (member is null) return null;
        if (member.IsProjectOwner) throw new MovieCollaborationValidationException("The project owner cannot be reassigned or overridden.");
        var movie = await db.MovieProjects.AsNoTracking().FirstAsync(item => item.Id == movieProjectId, cancellationToken);
        if (!await workspaceAccess.IsMemberAsync(member.UserId, movie.WorkspaceId, cancellationToken)) throw new MovieCollaborationValidationException("The user is no longer a member of the movie workspace.");
        member.Role = request.Role.Trim();
        member.UpdatedAt = DateTime.UtcNow;
        db.MovieTeamMemberPermissions.RemoveRange(member.PermissionOverrides);
        member.PermissionOverrides = [];
        ApplyOverrides(member, request.Permissions ?? [], request.PermissionOverrides ?? []);
        await db.SaveChangesAsync(cancellationToken);
        return await LoadMemberDtoAsync(member.Id, cancellationToken);
    }

    public async Task<bool> RemoveMemberAsync(Guid userId, Guid movieProjectId, Guid memberId, CancellationToken cancellationToken)
    {
        await access.RequireAsync(userId, movieProjectId, MoviePermissions.ManageTeam, cancellationToken);
        var member = await db.MovieTeamMembers.FirstOrDefaultAsync(item => item.Id == memberId && item.MovieProjectId == movieProjectId, cancellationToken);
        if (member is null) return false;
        if (member.IsProjectOwner) throw new MovieCollaborationValidationException("The project owner cannot be removed.");
        db.MovieTeamMembers.Remove(member);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<MovieCommentDto?> AddCommentAsync(Guid userId, Guid movieProjectId, MovieCommentRequest request, CancellationToken cancellationToken)
    {
        await access.RequireAsync(userId, movieProjectId, MoviePermissions.Comment, cancellationToken);
        await EnsureTargetAsync(movieProjectId, request.TargetType, request.TargetId, cancellationToken);
        if (string.IsNullOrWhiteSpace(request.Body) || request.Body.Trim().Length > 4000) throw new MovieCollaborationValidationException("Comment body must be between 1 and 4,000 characters.");
        if (request.ParentCommentId.HasValue && !await db.MovieComments.AnyAsync(item => item.Id == request.ParentCommentId && item.MovieProjectId == movieProjectId, cancellationToken))
            throw new MovieCollaborationValidationException("The parent comment is not part of this movie project.");
        var mentionIds = (request.MentionedUserIds ?? []).Distinct().ToArray();
        var teamIds = await db.MovieTeamMembers.Where(item => item.MovieProjectId == movieProjectId && mentionIds.Contains(item.UserId)).Select(item => item.UserId).ToListAsync(cancellationToken);
        if (teamIds.Count != mentionIds.Length) throw new MovieCollaborationValidationException("Mentions must target current human movie-team members.");
        var now = DateTime.UtcNow;
        var comment = new MovieComment { Id = Guid.NewGuid(), MovieProjectId = movieProjectId, TargetType = request.TargetType.Trim().ToLowerInvariant(), TargetId = request.TargetId, AuthorUserId = userId, Body = request.Body.Trim(), ParentCommentId = request.ParentCommentId, CreatedAt = now, UpdatedAt = now };
        comment.Mentions = mentionIds.Select(mentionedUserId => new MovieCommentMention { Id = Guid.NewGuid(), CommentId = comment.Id, MentionedUserId = mentionedUserId, CreatedAt = now }).ToList();
        db.MovieComments.Add(comment);
        await db.SaveChangesAsync(cancellationToken);
        return await LoadCommentDtoAsync(comment.Id, cancellationToken);
    }

    public async Task<MovieCommentDto?> ResolveCommentAsync(Guid userId, Guid movieProjectId, Guid commentId, CancellationToken cancellationToken)
    {
        await access.RequireAsync(userId, movieProjectId, MoviePermissions.Comment, cancellationToken);
        var comment = await db.MovieComments.FirstOrDefaultAsync(item => item.Id == commentId && item.MovieProjectId == movieProjectId, cancellationToken);
        if (comment is null) return null;
        comment.ResolvedAt ??= DateTime.UtcNow;
        comment.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await LoadCommentDtoAsync(comment.Id, cancellationToken);
    }

    public async Task<MovieReviewDto?> RequestReviewAsync(Guid userId, Guid movieProjectId, MovieReviewRequest request, CancellationToken cancellationToken)
    {
        await access.RequireAsync(userId, movieProjectId, MoviePermissions.Comment, cancellationToken);
        await EnsureTargetAsync(movieProjectId, request.TargetType, request.TargetId, cancellationToken);
        if (request.ReviewerUserId == userId || !await db.MovieTeamMembers.AnyAsync(item => item.MovieProjectId == movieProjectId && item.UserId == request.ReviewerUserId, cancellationToken))
            throw new MovieCollaborationValidationException("The reviewer must be a different current human movie-team member.");
        if (request.IsFinal && !await access.HasPermissionAsync(userId, movieProjectId, MoviePermissions.FinalApproval, cancellationToken)) throw new MovieCollaborationForbiddenException("Final approval requests require final-approval authority.");
        if (!string.IsNullOrWhiteSpace(request.RequestNote) && request.RequestNote.Trim().Length > 4000) throw new MovieCollaborationValidationException("Review note cannot exceed 4,000 characters.");
        var review = new MovieReview { Id = Guid.NewGuid(), MovieProjectId = movieProjectId, TargetType = request.TargetType.Trim().ToLowerInvariant(), TargetId = request.TargetId, RequestedByUserId = userId, ReviewerUserId = request.ReviewerUserId, IsFinal = request.IsFinal, RequestNote = MovieStudioHelpers.Clean(request.RequestNote), Status = MovieReviewStatuses.Pending, CreatedAt = DateTime.UtcNow };
        db.MovieReviews.Add(review);
        await db.SaveChangesAsync(cancellationToken);
        return await LoadReviewDtoAsync(review.Id, cancellationToken);
    }

    public async Task<MovieReviewDto?> DecideReviewAsync(Guid userId, Guid movieProjectId, Guid reviewId, MovieReviewDecisionRequest request, CancellationToken cancellationToken)
    {
        var review = await db.MovieReviews.FirstOrDefaultAsync(item => item.Id == reviewId && item.MovieProjectId == movieProjectId, cancellationToken);
        if (review is null) return null;
        if (review.ReviewerUserId != userId) throw new MovieCollaborationForbiddenException("Only the assigned reviewer can decide this review.");
        await access.RequireAsync(userId, movieProjectId, review.IsFinal ? MoviePermissions.FinalApproval : MoviePermissions.Approve, cancellationToken);
        if (!MovieReviewStatuses.Decisions.Contains(request.Status)) throw new MovieCollaborationValidationException("Choose Approved, ChangesRequested, or Rejected.");
        if (!string.IsNullOrWhiteSpace(request.DecisionNote) && request.DecisionNote.Trim().Length > 4000) throw new MovieCollaborationValidationException("Decision note cannot exceed 4,000 characters.");
        review.Status = request.Status.Trim();
        review.DecisionNote = MovieStudioHelpers.Clean(request.DecisionNote);
        review.ReviewedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await LoadReviewDtoAsync(review.Id, cancellationToken);
    }

    public async Task<MovieAssignmentDto?> AddAssignmentAsync(Guid userId, Guid movieProjectId, MovieAssignmentRequest request, CancellationToken cancellationToken)
    {
        await access.RequireAsync(userId, movieProjectId, MoviePermissions.ManageTeam, cancellationToken);
        await EnsureTargetAsync(movieProjectId, request.TargetType, request.TargetId, cancellationToken);
        await EnsureTeamUserAsync(movieProjectId, request.AssigneeUserId, cancellationToken);
        ValidateAssignment(request.Title, request.Description, request.Status);
        var now = DateTime.UtcNow;
        var assignment = new MovieProductionAssignment { Id = Guid.NewGuid(), MovieProjectId = movieProjectId, AssigneeUserId = request.AssigneeUserId, AssignedByUserId = userId, TargetType = request.TargetType.Trim().ToLowerInvariant(), TargetId = request.TargetId, Title = request.Title.Trim(), Description = MovieStudioHelpers.Clean(request.Description), Status = request.Status.Trim(), DueAt = request.DueAt, CreatedAt = now, UpdatedAt = now };
        db.MovieProductionAssignments.Add(assignment);
        await db.SaveChangesAsync(cancellationToken);
        return await LoadAssignmentDtoAsync(assignment.Id, cancellationToken);
    }

    public async Task<MovieAssignmentDto?> UpdateAssignmentAsync(Guid userId, Guid movieProjectId, Guid assignmentId, MovieAssignmentUpdateRequest request, CancellationToken cancellationToken)
    {
        var assignment = await db.MovieProductionAssignments.FirstOrDefaultAsync(item => item.Id == assignmentId && item.MovieProjectId == movieProjectId, cancellationToken);
        if (assignment is null) return null;
        if (assignment.AssigneeUserId != userId) await access.RequireAsync(userId, movieProjectId, MoviePermissions.ManageTeam, cancellationToken);
        ValidateAssignment(assignment.Title, request.Description, request.Status);
        assignment.Status = request.Status.Trim();
        assignment.Description = MovieStudioHelpers.Clean(request.Description);
        assignment.DueAt = request.DueAt;
        assignment.UpdatedAt = DateTime.UtcNow;
        assignment.CompletedAt = string.Equals(assignment.Status, MovieAssignmentStatuses.Completed, StringComparison.OrdinalIgnoreCase) ? DateTime.UtcNow : null;
        await db.SaveChangesAsync(cancellationToken);
        return await LoadAssignmentDtoAsync(assignment.Id, cancellationToken);
    }

    public async Task<MovieProductionCreditDto?> AddCreditAsync(Guid userId, Guid movieProjectId, MovieProductionCreditRequest request, CancellationToken cancellationToken)
    {
        await access.RequireAsync(userId, movieProjectId, MoviePermissions.ManageTeam, cancellationToken);
        if (!MovieTeamRoles.Supported.Contains(request.Role)) throw new MovieCollaborationValidationException("Choose a supported production role.");
        await EnsureTeamUserAsync(movieProjectId, request.UserId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(request.CreditName) && request.CreditName.Trim().Length > 160) throw new MovieCollaborationValidationException("Credit name cannot exceed 160 characters.");
        if (await db.MovieProductionCredits.AnyAsync(item => item.MovieProjectId == movieProjectId && item.UserId == request.UserId && item.Role == request.Role.Trim(), cancellationToken)) throw new MovieCollaborationValidationException("That production credit already exists.");
        var credit = new MovieProductionCredit { Id = Guid.NewGuid(), MovieProjectId = movieProjectId, UserId = request.UserId, Role = request.Role.Trim(), CreditName = MovieStudioHelpers.Clean(request.CreditName), SortOrder = request.SortOrder, CreatedAt = DateTime.UtcNow };
        db.MovieProductionCredits.Add(credit);
        await db.SaveChangesAsync(cancellationToken);
        return await LoadCreditDtoAsync(credit.Id, cancellationToken);
    }

    public async Task<bool> RemoveCreditAsync(Guid userId, Guid movieProjectId, Guid creditId, CancellationToken cancellationToken)
    {
        await access.RequireAsync(userId, movieProjectId, MoviePermissions.ManageTeam, cancellationToken);
        var credit = await db.MovieProductionCredits.FirstOrDefaultAsync(item => item.Id == creditId && item.MovieProjectId == movieProjectId, cancellationToken);
        if (credit is null) return false;
        db.MovieProductionCredits.Remove(credit);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task EnsureTargetAsync(Guid movieProjectId, string targetType, Guid targetId, CancellationToken cancellationToken)
    {
        var normalized = targetType.Trim().ToLowerInvariant();
        if (!MovieCollaborationTargetTypes.Supported.Contains(normalized)) throw new MovieCollaborationValidationException("Choose a supported movie object target.");
        var valid = normalized switch
        {
            MovieCollaborationTargetTypes.Project => await db.MovieProjects.AnyAsync(item => item.Id == movieProjectId && item.Id == targetId, cancellationToken),
            MovieCollaborationTargetTypes.Guide => await db.MovieContinuityGuides.AnyAsync(item => item.Id == targetId && item.MovieProjectId == movieProjectId, cancellationToken),
            MovieCollaborationTargetTypes.Scene => await db.MovieScenes.AnyAsync(item => item.Id == targetId && item.MovieProjectId == movieProjectId, cancellationToken),
            MovieCollaborationTargetTypes.Character => await db.MovieCharacters.AnyAsync(item => item.Id == targetId && item.MovieProjectId == movieProjectId, cancellationToken),
            MovieCollaborationTargetTypes.Location => await db.MovieLocations.AnyAsync(item => item.Id == targetId && item.MovieProjectId == movieProjectId, cancellationToken),
            MovieCollaborationTargetTypes.Shot => await db.MovieShots.AnyAsync(item => item.Id == targetId && item.Scene.MovieProjectId == movieProjectId, cancellationToken),
            MovieCollaborationTargetTypes.Clip => await db.MovieClips.AnyAsync(item => item.Id == targetId && item.MovieProjectId == movieProjectId, cancellationToken),
            MovieCollaborationTargetTypes.Assembly => await db.MovieAssemblies.AnyAsync(item => item.Id == targetId && item.MovieProjectId == movieProjectId, cancellationToken),
            _ => false,
        };
        if (!valid) throw new MovieCollaborationValidationException("The target object is not part of this movie project.");
    }

    private async Task EnsureTeamUserAsync(Guid movieProjectId, Guid userId, CancellationToken cancellationToken)
    {
        if (!await db.MovieTeamMembers.AnyAsync(item => item.MovieProjectId == movieProjectId && item.UserId == userId, cancellationToken)) throw new MovieCollaborationValidationException("The user must be a current movie-team member.");
    }

    private static void ValidateRoleAndPermissions(string role, IReadOnlyCollection<string> permissions, IReadOnlyCollection<MoviePermissionOverrideRequest> overrides)
    {
        if (!MovieTeamRoles.Supported.Contains(role)) throw new MovieCollaborationValidationException("Choose a supported production role.");
        if (permissions.Any(permission => !MoviePermissions.Supported.Contains(permission)) || overrides.Any(item => !MoviePermissions.Supported.Contains(item.Permission))) throw new MovieCollaborationValidationException("One or more permissions are not supported.");
        if (permissions.Concat(overrides.Select(item => item.Permission)).GroupBy(item => item, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)) throw new MovieCollaborationValidationException("A permission may only be specified once.");
    }

    private static void ApplyOverrides(MovieTeamMember member, IReadOnlyCollection<string> permissions, IReadOnlyCollection<MoviePermissionOverrideRequest> overrides)
    {
        member.PermissionOverrides = permissions.Select(permission => new MovieTeamMemberPermission { Id = Guid.NewGuid(), MovieTeamMemberId = member.Id, Permission = permission.Trim(), Granted = true })
            .Concat(overrides.Select(item => new MovieTeamMemberPermission { Id = Guid.NewGuid(), MovieTeamMemberId = member.Id, Permission = item.Permission.Trim(), Granted = item.Granted }))
            .ToList();
    }

    private static void ValidateAssignment(string title, string? description, string status)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 200) throw new MovieCollaborationValidationException("Assignment title is required and must be at most 200 characters.");
        if (!string.IsNullOrWhiteSpace(description) && description.Trim().Length > 4000) throw new MovieCollaborationValidationException("Assignment description cannot exceed 4,000 characters.");
        if (!MovieAssignmentStatuses.Supported.Contains(status)) throw new MovieCollaborationValidationException("Choose a supported assignment status.");
    }

    private async Task<MovieTeamMemberDto?> LoadMemberDtoAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = await db.MovieTeamMembers.AsNoTracking().Include(member => member.User).Include(member => member.PermissionOverrides).FirstOrDefaultAsync(member => member.Id == id, cancellationToken);
        return item is null ? null : ToDto(item);
    }

    private async Task<MovieCommentDto?> LoadCommentDtoAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = await db.MovieComments.AsNoTracking().Include(comment => comment.AuthorUser).Include(comment => comment.Mentions).ThenInclude(mention => mention.MentionedUser).FirstOrDefaultAsync(comment => comment.Id == id, cancellationToken);
        return item is null ? null : ToDto(item);
    }

    private async Task<MovieReviewDto?> LoadReviewDtoAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = await db.MovieReviews.AsNoTracking().Include(review => review.RequestedByUser).Include(review => review.ReviewerUser).FirstOrDefaultAsync(review => review.Id == id, cancellationToken);
        return item is null ? null : ToDto(item);
    }

    private async Task<MovieAssignmentDto?> LoadAssignmentDtoAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = await db.MovieProductionAssignments.AsNoTracking().Include(assignment => assignment.AssigneeUser).Include(assignment => assignment.AssignedByUser).FirstOrDefaultAsync(assignment => assignment.Id == id, cancellationToken);
        return item is null ? null : ToDto(item);
    }

    private async Task<MovieProductionCreditDto?> LoadCreditDtoAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = await db.MovieProductionCredits.AsNoTracking().Include(credit => credit.User).FirstOrDefaultAsync(credit => credit.Id == id, cancellationToken);
        return item is null ? null : ToDto(item);
    }

    private static MovieTeamMemberDto ToDto(MovieTeamMember item) => new(item.Id, item.UserId, item.User.DisplayName, item.Role, item.IsProjectOwner, MovieCollaborationAccess.EffectivePermissions(item), item.CreatedAt, item.UpdatedAt);
    private static MovieCommentDto ToDto(MovieComment item) => new(item.Id, item.TargetType, item.TargetId, item.AuthorUserId, item.AuthorUser.DisplayName, item.Body, item.ParentCommentId, item.Mentions.Select(mention => new MovieMentionDto(mention.MentionedUserId, mention.MentionedUser.DisplayName)).ToArray(), item.CreatedAt, item.UpdatedAt, item.ResolvedAt);
    private static MovieReviewDto ToDto(MovieReview item) => new(item.Id, item.TargetType, item.TargetId, item.RequestedByUserId, item.RequestedByUser.DisplayName, item.ReviewerUserId, item.ReviewerUser.DisplayName, item.IsFinal, item.Status, item.RequestNote, item.DecisionNote, item.CreatedAt, item.ReviewedAt);
    private static MovieAssignmentDto ToDto(MovieProductionAssignment item) => new(item.Id, item.TargetType, item.TargetId, item.AssigneeUserId, item.AssigneeUser.DisplayName, item.AssignedByUserId, item.AssignedByUser.DisplayName, item.Title, item.Description, item.Status, item.DueAt, item.CreatedAt, item.UpdatedAt, item.CompletedAt);
    private static MovieProductionCreditDto ToDto(MovieProductionCredit item) => new(item.Id, item.UserId, item.User.DisplayName, item.Role, item.CreditName, item.SortOrder, item.CreatedAt);
}

public static partial class MovieMentionParser
{
    [GeneratedRegex(@"(?<![A-Za-z0-9_])@([A-Za-z0-9_.-]{1,80})")]
    private static partial Regex MentionRegex();

    public static IReadOnlyList<string> ExtractHandles(string body) => MentionRegex().Matches(body).Select(item => item.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}

public sealed record MovieTeamMemberDto(Guid Id, Guid UserId, string DisplayName, string Role, bool IsProjectOwner, IReadOnlyList<string> Permissions, DateTime CreatedAt, DateTime UpdatedAt);
public sealed record MovieMentionDto(Guid UserId, string DisplayName);
public sealed record MovieCommentDto(Guid Id, string TargetType, Guid TargetId, Guid AuthorUserId, string AuthorDisplayName, string Body, Guid? ParentCommentId, IReadOnlyList<MovieMentionDto> Mentions, DateTime CreatedAt, DateTime UpdatedAt, DateTime? ResolvedAt);
public sealed record MovieReviewDto(Guid Id, string TargetType, Guid TargetId, Guid RequestedByUserId, string RequestedByDisplayName, Guid ReviewerUserId, string ReviewerDisplayName, bool IsFinal, string Status, string? RequestNote, string? DecisionNote, DateTime CreatedAt, DateTime? ReviewedAt);
public sealed record MovieAssignmentDto(Guid Id, string TargetType, Guid TargetId, Guid AssigneeUserId, string AssigneeDisplayName, Guid AssignedByUserId, string AssignedByDisplayName, string Title, string? Description, string Status, DateTime? DueAt, DateTime CreatedAt, DateTime UpdatedAt, DateTime? CompletedAt);
public sealed record MovieProductionCreditDto(Guid Id, Guid UserId, string DisplayName, string Role, string? CreditName, int SortOrder, DateTime CreatedAt);
public sealed record MovieCollaborationDto(Guid MovieProjectId, IReadOnlyList<MovieTeamMemberDto> Team, IReadOnlyList<MovieCommentDto> Comments, IReadOnlyList<MovieReviewDto> Reviews, IReadOnlyList<MovieAssignmentDto> Assignments, IReadOnlyList<MovieProductionCreditDto> Credits, IReadOnlyList<string> CurrentUserPermissions);
public sealed record MovieTeamMemberRequest(Guid UserId, string Role, IReadOnlyList<string>? Permissions, IReadOnlyList<MoviePermissionOverrideRequest>? PermissionOverrides = null);
public sealed record MoviePermissionOverrideRequest(string Permission, bool Granted);
public sealed record MovieCommentRequest(string TargetType, Guid TargetId, string Body, Guid? ParentCommentId, IReadOnlyList<Guid>? MentionedUserIds);
public sealed record MovieReviewRequest(string TargetType, Guid TargetId, Guid ReviewerUserId, bool IsFinal, string? RequestNote);
public sealed record MovieReviewDecisionRequest(string Status, string? DecisionNote);
public sealed record MovieAssignmentRequest(string TargetType, Guid TargetId, Guid AssigneeUserId, string Title, string? Description, string Status, DateTime? DueAt);
public sealed record MovieAssignmentUpdateRequest(string? Description, string Status, DateTime? DueAt);
public sealed record MovieProductionCreditRequest(Guid UserId, string Role, string? CreditName, int SortOrder);
