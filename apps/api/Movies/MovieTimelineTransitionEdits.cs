using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public sealed class MovieTimelineTransitionEdit
{
    public Guid Id { get; set; }
    public Guid MovieTimelineId { get; set; }
    public Guid MovieProjectId { get; set; }
    public Guid DecisionId { get; set; }
    public int BaseTimelineVersion { get; set; }
    public int ResultTimelineVersion { get; set; }
    public string Action { get; set; } = string.Empty;
    public string DecisionJson { get; set; } = string.Empty;
    public string ResultTimelineJson { get; set; } = string.Empty;
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public MovieTimeline Timeline { get; set; } = null!;
    public MovieProject MovieProject { get; set; } = null!;
    public ApplicationUser CreatedByUser { get; set; } = null!;
}

public sealed class MovieTimelineTransitionEditRequest
{
    public MovieCanonicalTimelineContract Timeline { get; set; } = null!;
    public MovieTimelineEditDecisionContract Decision { get; set; } = null!;
}

public sealed record MovieTimelineTransitionEditDto(
    Guid Id,
    Guid MovieProjectId,
    int BaseTimelineVersion,
    int ResultTimelineVersion,
    string Action,
    MovieCanonicalTimelineContract Timeline,
    DateTime CreatedAt);

public interface IMovieTimelineTransitionService
{
    Task<MovieTimelineTransitionEditDto?> GetLatestAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken);
    Task<MovieTimelineTransitionEditDto?> ApplyAsync(Guid userId, Guid movieProjectId, MovieTimelineTransitionEditRequest request, CancellationToken cancellationToken);
}

public sealed class MovieTimelineTransitionService(
    TaslimDbContext db,
    MovieAuthorizationService authorization,
    MovieCollaborationAccess collaboration) : IMovieTimelineTransitionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<MovieTimelineTransitionEditDto?> GetLatestAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken)
    {
        var project = await db.MovieProjects.AsNoTracking().FirstOrDefaultAsync(item => item.Id == movieProjectId, cancellationToken);
        if (project is null || !await authorization.CanPermissionAsync(userId, movieProjectId, MoviePermissions.View, cancellationToken)) return null;
        var record = await db.MovieTimelineTransitionEdits.AsNoTracking()
            .Where(item => item.MovieProjectId == movieProjectId)
            .OrderByDescending(item => item.ResultTimelineVersion)
            .ThenByDescending(item => item.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        return record is null ? null : ToDto(record);
    }

    public async Task<MovieTimelineTransitionEditDto?> ApplyAsync(Guid userId, Guid movieProjectId, MovieTimelineTransitionEditRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await collaboration.RequireAsync(userId, movieProjectId, MoviePermissions.Edit, cancellationToken);
        var timeline = await db.MovieTimelines.FirstOrDefaultAsync(item => item.MovieProjectId == movieProjectId, cancellationToken);
        if (timeline is null) return null;
        if (request.Timeline is null || request.Decision is null || request.Timeline.TimelineId != timeline.Id)
            throw Invalid(MovieTimelineValidationCodes.EditTargetInvalid, "The transition edit must target the current project timeline.");

        var latest = await db.MovieTimelineTransitionEdits.AsNoTracking()
            .Where(item => item.MovieTimelineId == timeline.Id)
            .OrderByDescending(item => item.ResultTimelineVersion)
            .Select(item => (int?)item.ResultTimelineVersion)
            .FirstOrDefaultAsync(cancellationToken);
        var expectedBaseVersion = latest ?? 1;
        if (request.Timeline.Version != expectedBaseVersion)
            throw Invalid(MovieTimelineValidationCodes.TimelineVersionConflict, "The transition edit was created against a stale persisted timeline version.");

        var shotIds = request.Timeline.Clips.Select(item => item.MovieShotId).Distinct().ToArray();
        var projectShotCount = await db.MovieShots.CountAsync(item => shotIds.Contains(item.Id) && item.Scene.MovieProjectId == movieProjectId, cancellationToken);
        if (projectShotCount != shotIds.Length)
            throw Invalid(MovieTimelineValidationCodes.ClipShotIdRequired, "Every transition timeline clip must reference a shot in this movie project.");

        MovieTimelineValidator.ValidateEditDecisionOrThrow(request.Timeline, request.Decision);
        var result = MovieTimelineAuthority.ApplyUserOverride(request.Timeline, request.Decision);
        var now = DateTime.UtcNow;
        var record = new MovieTimelineTransitionEdit
        {
            Id = Guid.NewGuid(),
            MovieTimelineId = timeline.Id,
            MovieProjectId = movieProjectId,
            DecisionId = request.Decision.DecisionId,
            BaseTimelineVersion = request.Timeline.Version,
            ResultTimelineVersion = result.Version,
            Action = request.Decision.Action.Trim().ToLowerInvariant(),
            DecisionJson = JsonSerializer.Serialize(request.Decision, JsonOptions),
            ResultTimelineJson = JsonSerializer.Serialize(result, JsonOptions),
            CreatedByUserId = userId,
            CreatedAt = now,
        };
        db.MovieTimelineTransitionEdits.Add(record);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(record);
    }

    private static MovieTimelineTransitionEditDto ToDto(MovieTimelineTransitionEdit record)
    {
        var timeline = JsonSerializer.Deserialize<MovieCanonicalTimelineContract>(record.ResultTimelineJson, JsonOptions)
            ?? throw Invalid(MovieTimelineValidationCodes.EditTargetInvalid, "The persisted transition timeline is invalid.");
        return new(record.Id, record.MovieProjectId, record.BaseTimelineVersion, record.ResultTimelineVersion, record.Action, timeline, record.CreatedAt);
    }

    private static MovieTimelineValidationException Invalid(string code, string message) => new(code, message);
}
