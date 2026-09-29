using Microsoft.EntityFrameworkCore;
using Taslim.Api.Contracts;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public sealed record MovieShotImportanceDto(
    Guid ShotId,
    string Classification,
    string DirectorClassification,
    string? UserOverride,
    string Source,
    string Reasoning,
    decimal Confidence,
    IReadOnlyList<MovieShotImportanceEvidenceDto> Evidence);

public sealed class MovieShotImportanceOverrideRequest
{
    public string? Classification { get; set; }
}

public sealed class MovieShotImportanceService(
    TaslimDbContext db,
    MovieCollaborationAccess collaboration,
    MovieShotImportanceClassifier classifier)
{
    public async Task<MovieShotImportanceDto?> GetAsync(Guid userId, Guid shotId, CancellationToken cancellationToken = default)
    {
        var shot = await LoadShotAsync(shotId, cancellationToken);
        if (shot is null || !await collaboration.HasPermissionAsync(userId, shot.Scene.MovieProjectId, MoviePermissions.View, cancellationToken)) return null;
        return await AssessAsync(shot, cancellationToken);
    }

    public async Task<MovieShotImportanceDto?> SetOverrideAsync(Guid userId, Guid shotId, MovieShotImportanceOverrideRequest request, CancellationToken cancellationToken = default)
    {
        var shot = await db.MovieShots.Include(item => item.Scene).FirstOrDefaultAsync(item => item.Id == shotId, cancellationToken);
        if (shot is null || !await collaboration.HasPermissionAsync(userId, shot.Scene.MovieProjectId, MoviePermissions.Edit, cancellationToken)) return null;

        var normalized = MovieShotImportanceLevels.Normalize(request.Classification);
        if (!string.IsNullOrWhiteSpace(request.Classification) && normalized is null)
            throw new MovieStudioValidationException($"Importance classification must be one of: {string.Join(", ", MovieShotImportanceLevels.Supported.OrderBy(item => item, StringComparer.Ordinal))}.");

        shot.ImportanceOverride = normalized;
        shot.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var refreshed = await LoadShotAsync(shotId, cancellationToken);
        return refreshed is null ? null : await AssessAsync(refreshed, cancellationToken);
    }

    private async Task<MovieShot?> LoadShotAsync(Guid shotId, CancellationToken cancellationToken)
    {
        return await db.MovieShots.AsNoTracking()
            .Include(item => item.Scene).ThenInclude(scene => scene.MovieProject).ThenInclude(project => project.Guide).ThenInclude(guide => guide.Revisions)
            .FirstOrDefaultAsync(item => item.Id == shotId, cancellationToken);
    }

    private async Task<MovieShotImportanceDto> AssessAsync(MovieShot shot, CancellationToken cancellationToken)
    {
        var story = await db.MovieStories.AsNoTracking()
            .Include(item => item.Revisions).ThenInclude(revision => revision.Scenes).ThenInclude(scene => scene.Elements)
            .FirstOrDefaultAsync(item => item.MovieProjectId == shot.Scene.MovieProjectId, cancellationToken);
        var approvedRevision = story?.ApprovedRevisionId is Guid approvedRevisionId
            ? story.Revisions.FirstOrDefault(item => item.Id == approvedRevisionId && item.Status == MovieStoryRevisionStatuses.Approved)
            : null;
        var screenplayScene = approvedRevision?.Scenes.FirstOrDefault(item => item.MovieSceneId == shot.MovieSceneId);
        var guide = shot.Scene.MovieProject.Guide;
        var guideRevision = guide.Revisions
            .Where(item => item.RevisionNumber == guide.LockedRevisionNumber || item.RevisionNumber == guide.CurrentRevisionNumber)
            .OrderByDescending(item => item.RevisionNumber)
            .FirstOrDefault();

        var context = new MovieShotImportanceContext(
            shot.Description,
            shot.Purpose,
            shot.Subjects,
            shot.ProductionRequirements,
            shot.ContinuityReferences,
            shot.CameraAndFraming,
            shot.CameraMotion,
            shot.CinematographyJson,
            shot.Narration,
            shot.Dialogue,
            shot.VisualContinuityNotes,
            new MovieShotImportanceSceneContext(
                shot.Scene.Title,
                shot.Scene.Summary,
                shot.Scene.ContinuityNotes,
                shot.Scene.Narration,
                shot.Scene.Dialogue,
                screenplayScene?.Slugline,
                screenplayScene?.Synopsis,
                screenplayScene is null ? null : string.Join(" ", screenplayScene.Elements.OrderBy(item => item.Ordinal).Select(item => item.Content))),
            approvedRevision is null
                ? null
                : new MovieShotImportanceStoryContext(
                    approvedRevision.Premise,
                    approvedRevision.Logline,
                    approvedRevision.Synopsis,
                    approvedRevision.Treatment,
                    screenplayScene?.Slugline,
                    screenplayScene?.Synopsis,
                    screenplayScene is null ? null : string.Join(" ", screenplayScene.Elements.OrderBy(item => item.Ordinal).Select(item => item.Content))),
            new MovieShotImportanceGuideContext(
                guide.VisualLanguage,
                guide.CameraLanguage,
                guide.ColorAndLighting,
                guide.SoundAndNarration,
                guide.ContinuityRules,
                guideRevision?.StoryBibleJson,
                guideRevision?.VisualBibleJson,
                guideRevision?.CinematographyBibleJson,
                guideRevision?.AudioBibleJson,
                guideRevision?.ContinuityBibleJson,
                guideRevision?.RevisionNumber,
                guideRevision?.Status == MovieGuideRevisionStatuses.Locked));

        var director = classifier.Classify(context);
        var effective = shot.ImportanceOverride ?? director.Classification;
        var source = shot.ImportanceOverride is null ? "director" : "user_override";
        var reasoning = shot.ImportanceOverride is null
            ? director.Reasoning
            : $"User override selected '{shot.ImportanceOverride}'. Director assessment was '{director.Classification}': {director.Reasoning}";
        return new(shot.Id, effective, director.Classification, shot.ImportanceOverride, source, reasoning, director.Confidence, director.Evidence);
    }
}
