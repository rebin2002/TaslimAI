using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public interface IMovieStoryService
{
    Task<MovieStoryDto?> GetAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken);
    Task<IReadOnlyList<MovieStoryRevisionSummaryDto>?> ListRevisionsAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken);
    Task<MovieStoryDto?> CreateRevisionAsync(Guid userId, Guid movieProjectId, MovieStoryRevisionRequest request, CancellationToken cancellationToken);
    Task<MovieStoryRevisionDto?> UpdateDraftAsync(Guid userId, Guid movieProjectId, Guid revisionId, MovieStoryRevisionRequest request, CancellationToken cancellationToken);
    Task<MovieStoryRevisionDto?> GetRevisionAsync(Guid userId, Guid movieProjectId, Guid revisionId, CancellationToken cancellationToken);
    Task<MovieStoryRevisionDto?> SubmitAsync(Guid userId, Guid movieProjectId, Guid revisionId, CancellationToken cancellationToken);
    Task<MovieStoryRevisionDto?> ApproveAsync(Guid userId, Guid movieProjectId, Guid revisionId, CancellationToken cancellationToken);
    Task<MovieStoryRevisionDto?> RejectAsync(Guid userId, Guid movieProjectId, Guid revisionId, string reason, CancellationToken cancellationToken);
}

public sealed class MovieStoryService(TaslimDbContext db, MovieCollaborationAccess collaboration) : IMovieStoryService
{
    public async Task<MovieStoryDto?> GetAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken)
    {
        var story = await Query().SingleOrDefaultAsync(item => item.MovieProjectId == movieProjectId, cancellationToken);
        return story is null || !await collaboration.HasPermissionAsync(userId, movieProjectId, MoviePermissions.View, cancellationToken)
            ? null
            : ToDto(story, await collaboration.HasPermissionAsync(userId, movieProjectId, MoviePermissions.Edit, cancellationToken), await collaboration.HasPermissionAsync(userId, movieProjectId, MoviePermissions.Approve, cancellationToken));
    }

    public async Task<IReadOnlyList<MovieStoryRevisionSummaryDto>?> ListRevisionsAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken)
    {
        var story = await db.MovieStories.AsNoTracking().SingleOrDefaultAsync(item => item.MovieProjectId == movieProjectId, cancellationToken);
        if (story is null) return null;
        if (!await collaboration.HasPermissionAsync(userId, movieProjectId, MoviePermissions.View, cancellationToken)) return null;
        return await db.MovieStoryRevisions.AsNoTracking()
            .Where(item => item.MovieStoryId == story.Id)
            .OrderByDescending(item => item.RevisionNumber)
            .Select(item => new MovieStoryRevisionSummaryDto(item.Id, item.RevisionNumber, item.Status, item.Authorship, item.ChangeSummary, item.CreatedAt, item.SubmittedAt, item.ApprovedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<MovieStoryDto?> CreateRevisionAsync(Guid userId, Guid movieProjectId, MovieStoryRevisionRequest request, CancellationToken cancellationToken)
    {
        Validate(request);
        var movie = await db.MovieProjects.AsNoTracking().SingleOrDefaultAsync(item => item.Id == movieProjectId, cancellationToken);
        if (movie is null) return null;
        await collaboration.RequireAsync(userId, movieProjectId, MoviePermissions.Edit, cancellationToken);
        await ValidateSceneLinksAsync(movieProjectId, request.Scenes, cancellationToken);

        var story = await db.MovieStories.Include(item => item.Revisions).SingleOrDefaultAsync(item => item.MovieProjectId == movieProjectId, cancellationToken);
        var now = DateTime.UtcNow;
        if (story is null)
        {
            story = new MovieStory
            {
                Id = Guid.NewGuid(), MovieProjectId = movieProjectId, WorkspaceId = movie.WorkspaceId, CreatedByUserId = userId,
                CreatedAt = now, UpdatedAt = now,
            };
            db.MovieStories.Add(story);
        }

        var previousCurrent = story.CurrentRevisionId.HasValue
            ? story.Revisions.SingleOrDefault(item => item.Id == story.CurrentRevisionId.Value)
            : null;
        if (previousCurrent is not null && previousCurrent.Status == MovieStoryRevisionStatuses.Draft)
            previousCurrent.Status = MovieStoryRevisionStatuses.Superseded;

        var revision = new MovieStoryRevision
        {
            Id = Guid.NewGuid(), MovieStoryId = story.Id, ParentRevisionId = request.ParentRevisionId ?? story.CurrentRevisionId,
            CreatedByUserId = userId, RevisionNumber = story.Revisions.Count == 0 ? 1 : story.Revisions.Max(item => item.RevisionNumber) + 1,
            Premise = request.Premise.Trim(), Logline = request.Logline.Trim(), Synopsis = request.Synopsis.Trim(), Treatment = request.Treatment.Trim(),
            Status = MovieStoryRevisionStatuses.Draft, Authorship = request.Authorship.Trim(), ChangeSummary = Clean(request.ChangeSummary), CreatedAt = now, UpdatedAt = now,
        };
        AddScenes(revision, request.Scenes, now);
        db.MovieStoryRevisions.Add(revision);
        story.Premise = revision.Premise;
        story.Logline = revision.Logline;
        story.Synopsis = revision.Synopsis;
        story.Treatment = revision.Treatment;
        story.CurrentRevisionId = revision.Id;
        story.ApprovalState = MovieStoryApprovalStates.Draft;
        story.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(userId, movieProjectId, cancellationToken);
    }

    public async Task<MovieStoryRevisionDto?> UpdateDraftAsync(Guid userId, Guid movieProjectId, Guid revisionId, MovieStoryRevisionRequest request, CancellationToken cancellationToken)
    {
        Validate(request);
        var revision = await db.MovieStoryRevisions.Include(item => item.MovieStory).Include(item => item.Scenes).ThenInclude(item => item.Elements)
            .SingleOrDefaultAsync(item => item.Id == revisionId && item.MovieStory.MovieProjectId == movieProjectId, cancellationToken);
        if (revision is null) return null;
        await collaboration.RequireAsync(userId, movieProjectId, MoviePermissions.Edit, cancellationToken);
        if (revision.Status != MovieStoryRevisionStatuses.Draft)
            throw new MovieStoryWorkflowException("MOVIE_STORY_REVISION_IMMUTABLE", "Only draft revisions can be edited.");
        await ValidateSceneLinksAsync(movieProjectId, request.Scenes, cancellationToken);
        var now = DateTime.UtcNow;
        revision.Premise = request.Premise.Trim();
        revision.Logline = request.Logline.Trim();
        revision.Synopsis = request.Synopsis.Trim();
        revision.Treatment = request.Treatment.Trim();
        revision.Authorship = request.Authorship.Trim();
        revision.ChangeSummary = Clean(request.ChangeSummary);
        revision.UpdatedAt = now;
        db.MovieScreenplayScenes.RemoveRange(revision.Scenes);
        revision.Scenes = [];
        AddScenes(revision, request.Scenes, now);
        revision.MovieStory.Premise = revision.Premise;
        revision.MovieStory.Logline = revision.Logline;
        revision.MovieStory.Synopsis = revision.Synopsis;
        revision.MovieStory.Treatment = revision.Treatment;
        revision.MovieStory.CurrentRevisionId = revision.Id;
        revision.MovieStory.ApprovalState = MovieStoryApprovalStates.Draft;
        revision.MovieStory.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return await GetRevisionAsync(userId, movieProjectId, revisionId, cancellationToken);
    }

    public async Task<MovieStoryRevisionDto?> GetRevisionAsync(Guid userId, Guid movieProjectId, Guid revisionId, CancellationToken cancellationToken)
    {
        var revision = await QueryRevisions().SingleOrDefaultAsync(item => item.Id == revisionId && item.MovieStory.MovieProjectId == movieProjectId, cancellationToken);
        return revision is null || !await collaboration.HasPermissionAsync(userId, movieProjectId, MoviePermissions.View, cancellationToken) ? null : ToDto(revision);
    }

    public Task<MovieStoryRevisionDto?> SubmitAsync(Guid userId, Guid movieProjectId, Guid revisionId, CancellationToken cancellationToken) =>
        TransitionAsync(userId, movieProjectId, revisionId, MovieStoryRevisionStatuses.Submitted, null, cancellationToken);

    public Task<MovieStoryRevisionDto?> ApproveAsync(Guid userId, Guid movieProjectId, Guid revisionId, CancellationToken cancellationToken) =>
        TransitionAsync(userId, movieProjectId, revisionId, MovieStoryRevisionStatuses.Approved, null, cancellationToken);

    public Task<MovieStoryRevisionDto?> RejectAsync(Guid userId, Guid movieProjectId, Guid revisionId, string reason, CancellationToken cancellationToken) =>
        TransitionAsync(userId, movieProjectId, revisionId, MovieStoryRevisionStatuses.Rejected, reason, cancellationToken);

    private async Task<MovieStoryRevisionDto?> TransitionAsync(Guid userId, Guid movieProjectId, Guid revisionId, string targetStatus, string? reason, CancellationToken cancellationToken)
    {
        var revision = await db.MovieStoryRevisions.Include(item => item.MovieStory).SingleOrDefaultAsync(item => item.Id == revisionId && item.MovieStory.MovieProjectId == movieProjectId, cancellationToken);
        if (revision is null || !await collaboration.HasPermissionAsync(userId, movieProjectId, MoviePermissions.View, cancellationToken)) return null;
        await collaboration.RequireAsync(userId, movieProjectId, targetStatus == MovieStoryRevisionStatuses.Approved ? MoviePermissions.Approve : MoviePermissions.Edit, cancellationToken);
        if (revision.Status is MovieStoryRevisionStatuses.Approved or MovieStoryRevisionStatuses.Rejected or MovieStoryRevisionStatuses.Superseded)
            throw new MovieStoryWorkflowException("MOVIE_STORY_REVISION_IMMUTABLE", "This revision is immutable.");
        var now = DateTime.UtcNow;
        if (targetStatus == MovieStoryRevisionStatuses.Submitted)
        {
            revision.Status = targetStatus;
            revision.SubmittedAt = now;
            revision.MovieStory.ApprovalState = MovieStoryApprovalStates.InReview;
        }
        else if (targetStatus == MovieStoryRevisionStatuses.Approved)
        {
            revision.Status = targetStatus;
            revision.ApprovedAt = now;
            revision.ApprovedByUserId = userId;
            revision.MovieStory.ApprovedRevisionId = revision.Id;
            revision.MovieStory.ApprovalState = MovieStoryApprovalStates.Approved;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 2_000)
                throw new MovieStoryWorkflowException("MOVIE_STORY_REJECTION_INVALID", "A rejection reason between 1 and 2,000 characters is required.");
            revision.Status = targetStatus;
            revision.RejectionReason = reason.Trim();
            if (revision.MovieStory.CurrentRevisionId == revision.Id)
                revision.MovieStory.ApprovalState = MovieStoryApprovalStates.Draft;
        }
        revision.UpdatedAt = now;
        revision.MovieStory.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return await GetRevisionAsync(userId, movieProjectId, revisionId, cancellationToken);
    }

    private async Task ValidateSceneLinksAsync(Guid movieProjectId, IReadOnlyList<MovieStorySceneRequest> scenes, CancellationToken cancellationToken)
    {
        var ids = scenes.Where(item => item.MovieSceneId.HasValue).Select(item => item.MovieSceneId!.Value).Distinct().ToArray();
        if (ids.Length == 0) return;
        var validCount = await db.MovieScenes.CountAsync(item => item.MovieProjectId == movieProjectId && ids.Contains(item.Id), cancellationToken);
        if (validCount != ids.Length) throw new MovieStoryValidationException("One or more screenplay scenes are linked to another movie.");
    }

    private static void Validate(MovieStoryRevisionRequest request)
    {
        if (Length(request.Premise, 1, 8_000) is not null) throw new MovieStoryValidationException("Premise is required and must be 8,000 characters or fewer.");
        if (Length(request.Logline, 1, 2_000) is not null) throw new MovieStoryValidationException("Logline is required and must be 2,000 characters or fewer.");
        if (Length(request.Synopsis, 1, 20_000) is not null) throw new MovieStoryValidationException("Synopsis is required and must be 20,000 characters or fewer.");
        if (Length(request.Treatment, 1, 40_000) is not null) throw new MovieStoryValidationException("Treatment is required and must be 40,000 characters or fewer.");
        if (!MovieStoryAuthorship.Supported.Contains(request.Authorship)) throw new MovieStoryValidationException("Choose Human, AiSuggested, or HumanEdited authorship.");
        if (request.Scenes.Count > 500) throw new MovieStoryValidationException("A revision can contain at most 500 screenplay scenes.");
        var sceneIdentifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scene in request.Scenes)
        {
            if (Length(scene.SceneIdentifier, 1, 80) is not null || !sceneIdentifiers.Add(scene.SceneIdentifier.Trim())) throw new MovieStoryValidationException("Each screenplay scene needs a unique scene identifier.");
            if (Length(scene.Slugline, 1, 500) is not null) throw new MovieStoryValidationException("Each screenplay scene needs a slugline of 500 characters or fewer.");
            if (scene.ActNumber is < 1 or > 20 || scene.SequenceNumber is < 1 or > 9999) throw new MovieStoryValidationException("Act and sequence identifiers must be positive and bounded.");
            if (scene.Elements.Count > 500) throw new MovieStoryValidationException("A screenplay scene can contain at most 500 elements.");
            foreach (var element in scene.Elements)
            {
                if (!MovieScreenplayElementTypes.Supported.Contains(element.ElementType)) throw new MovieStoryValidationException("Choose a supported screenplay element type.");
                if (Length(element.Content, 1, 20_000) is not null) throw new MovieStoryValidationException("Screenplay element content is required and must be 20,000 characters or fewer.");
                if (element.ElementType.Equals(MovieScreenplayElementTypes.Dialogue, StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(element.CharacterName))
                    throw new MovieStoryValidationException("Dialogue elements require a character name.");
            }
        }
    }

    private IQueryable<MovieStory> Query() => db.MovieStories.AsNoTracking()
        .Include(item => item.Revisions).ThenInclude(item => item.Scenes).ThenInclude(item => item.Elements);

    private IQueryable<MovieStoryRevision> QueryRevisions() => db.MovieStoryRevisions.AsNoTracking()
        .Include(item => item.MovieStory)
        .Include(item => item.Scenes).ThenInclude(item => item.Elements);

    private static MovieStoryDto ToDto(MovieStory story, bool canEdit, bool canApprove)
    {
        var revisions = story.Revisions.OrderByDescending(item => item.RevisionNumber).ToArray();
        var current = story.CurrentRevisionId.HasValue ? revisions.SingleOrDefault(item => item.Id == story.CurrentRevisionId.Value) : null;
        var approved = story.ApprovedRevisionId.HasValue ? revisions.SingleOrDefault(item => item.Id == story.ApprovedRevisionId.Value) : null;
        return new MovieStoryDto(story.Id, story.MovieProjectId, story.WorkspaceId, story.Premise, story.Logline, story.Synopsis, story.Treatment, story.ApprovalState, story.CurrentRevisionId, story.ApprovedRevisionId, story.CreatedAt, story.UpdatedAt, current is null ? null : ToDto(current), approved is null ? null : ToDto(approved), revisions.Select(item => new MovieStoryRevisionSummaryDto(item.Id, item.RevisionNumber, item.Status, item.Authorship, item.ChangeSummary, item.CreatedAt, item.SubmittedAt, item.ApprovedAt)).ToArray(), canEdit, canApprove);
    }

    private static void AddScenes(MovieStoryRevision revision, IReadOnlyList<MovieStorySceneRequest> sceneRequests, DateTime now)
    {
        foreach (var (sceneRequest, sceneIndex) in sceneRequests.Select((item, index) => (item, index)))
        {
            var scene = new MovieScreenplayScene
            {
                Id = Guid.NewGuid(), MovieStoryRevisionId = revision.Id, MovieSceneId = sceneRequest.MovieSceneId,
                Ordinal = sceneIndex + 1, SceneIdentifier = sceneRequest.SceneIdentifier.Trim(), ActNumber = sceneRequest.ActNumber,
                SequenceNumber = sceneRequest.SequenceNumber, Slugline = sceneRequest.Slugline.Trim(), Synopsis = Clean(sceneRequest.Synopsis), CreatedAt = now,
            };
            foreach (var (elementRequest, elementIndex) in sceneRequest.Elements.Select((item, index) => (item, index)))
            {
                scene.Elements.Add(new MovieScreenplayElement
                {
                    Id = Guid.NewGuid(), MovieScreenplaySceneId = scene.Id, Ordinal = elementIndex + 1,
                    ElementType = elementRequest.ElementType.Trim(), Content = elementRequest.Content.Trim(),
                    CharacterName = Clean(elementRequest.CharacterName), Parenthetical = Clean(elementRequest.Parenthetical), CreatedAt = now,
                });
            }
            revision.Scenes.Add(scene);
        }
    }

    private static MovieStoryRevisionDto ToDto(MovieStoryRevision revision) => new(revision.Id, revision.RevisionNumber, revision.ParentRevisionId, revision.CreatedByUserId, revision.Premise, revision.Logline, revision.Synopsis, revision.Treatment, revision.Status, revision.Authorship, revision.ChangeSummary, revision.RejectionReason, revision.CreatedAt, revision.UpdatedAt, revision.SubmittedAt, revision.ApprovedAt, revision.ApprovedByUserId, revision.Scenes.OrderBy(item => item.Ordinal).Select(scene => new MovieScreenplaySceneDto(scene.Id, scene.Ordinal, scene.SceneIdentifier, scene.ActNumber, scene.SequenceNumber, scene.MovieSceneId, scene.Slugline, scene.Synopsis, scene.Elements.OrderBy(item => item.Ordinal).Select(element => new MovieScreenplayElementDto(element.Id, element.Ordinal, element.ElementType, element.Content, element.CharacterName, element.Parenthetical)).ToArray())).ToArray());

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? Length(string? value, int min, int max) => string.IsNullOrWhiteSpace(value) || value.Trim().Length < min || value.Trim().Length > max ? value : null;
}

public sealed class MovieStoryValidationException(string message) : Exception(message);
public sealed class MovieStoryWorkflowException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
