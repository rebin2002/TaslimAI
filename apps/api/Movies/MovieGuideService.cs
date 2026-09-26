using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public interface IMovieGuideService
{
    Task<MovieGuideRevisionResponse?> CreateRevisionAsync(Guid userId, Guid movieProjectId, MovieGuideRevisionRequest request, CancellationToken cancellationToken);
    Task<MovieGuideHistoryResponse?> GetHistoryAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken);
    Task<MovieGuideRevisionResponse?> LockAsync(Guid userId, Guid movieProjectId, int? revisionNumber, CancellationToken cancellationToken);
    Task<MovieGuideHistoryResponse?> UnlockAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken);
    Task<MovieDirectorContextDto?> GetDirectorContextAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken);
}

public sealed class MovieGuideService(TaslimDbContext db, MovieAuthorizationService authorization) : IMovieGuideService
{
    public async Task<MovieGuideRevisionResponse?> CreateRevisionAsync(Guid userId, Guid movieProjectId, MovieGuideRevisionRequest request, CancellationToken cancellationToken)
    {
        var guide = await LoadGuideAsync(movieProjectId, cancellationToken);
        if (guide is null || !await authorization.CanAsync(userId, movieProjectId, MovieOperationalActions.GuideEdit, cancellationToken)) return null;
        if (guide.LockedRevisionNumber is not null) throw new MovieGuideLockedException();

        ValidateRequest(request);
        var now = DateTime.UtcNow;
        var revision = BuildRevision(guide, userId, request, guide.CurrentRevisionNumber + 1, now);
        guide.CurrentRevisionNumber = revision.RevisionNumber;
        guide.UpdatedAt = now;
        db.MovieGuideRevisions.Add(revision);
        await db.SaveChangesAsync(cancellationToken);
        return new MovieGuideRevisionResponse(ToDto(revision), null);
    }

    public async Task<MovieGuideHistoryResponse?> GetHistoryAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken)
    {
        var guide = await LoadGuideAsync(movieProjectId, cancellationToken);
        if (guide is null || !await authorization.CanPermissionAsync(userId, movieProjectId, MoviePermissions.View, cancellationToken)) return null;
        return ToHistory(guide);
    }

    public async Task<MovieGuideRevisionResponse?> LockAsync(Guid userId, Guid movieProjectId, int? revisionNumber, CancellationToken cancellationToken)
    {
        var guide = await LoadGuideAsync(movieProjectId, cancellationToken);
        if (guide is null || !await authorization.CanAsync(userId, movieProjectId, MovieOperationalActions.GuideApproval, cancellationToken)) return null;
        if (guide.LockedRevisionNumber is not null) throw new MovieGuideLockedException();

        var targetNumber = revisionNumber ?? guide.CurrentRevisionNumber;
        if (targetNumber != guide.CurrentRevisionNumber) throw new MovieGuideValidationException("Only the current Movie Guide revision can be locked.");
        var revision = guide.Revisions.SingleOrDefault(item => item.RevisionNumber == targetNumber);
        if (revision is null) throw new MovieGuideValidationException("Movie Guide revision was not found.");

        var now = DateTime.UtcNow;
        revision.Status = MovieGuideRevisionStatuses.Locked;
        revision.LockedAt = now;
        revision.LockedByUserId = userId;
        guide.LockedRevisionNumber = revision.RevisionNumber;
        guide.LockedAt = now;
        guide.LockedByUserId = userId;
        guide.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return new MovieGuideRevisionResponse(ToDto(revision), ToDirectorContext(guide.MovieProjectId, guide, revision));
    }

    public async Task<MovieGuideHistoryResponse?> UnlockAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken)
    {
        var guide = await LoadGuideAsync(movieProjectId, cancellationToken);
        if (guide is null || !await authorization.CanAsync(userId, movieProjectId, MovieOperationalActions.GuideApproval, cancellationToken)) return null;
        if (guide.LockedRevisionNumber is null) throw new MovieGuideValidationException("Movie Guide is already unlocked.");

        var locked = guide.Revisions.Single(item => item.RevisionNumber == guide.LockedRevisionNumber.Value);
        locked.Status = MovieGuideRevisionStatuses.Draft;
        locked.LockedAt = null;
        locked.LockedByUserId = null;
        guide.LockedRevisionNumber = null;
        guide.LockedAt = null;
        guide.LockedByUserId = null;
        guide.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return ToHistory(guide);
    }

    public async Task<MovieDirectorContextDto?> GetDirectorContextAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken)
    {
        var guide = await LoadGuideAsync(movieProjectId, cancellationToken);
        if (guide is null || !await authorization.CanPermissionAsync(userId, movieProjectId, MoviePermissions.View, cancellationToken)) return null;
        if (guide.LockedRevisionNumber is not int lockedRevisionNumber) throw new MovieGuideNotLockedException();
        var revision = guide.Revisions.Single(item => item.RevisionNumber == lockedRevisionNumber);
        return ToDirectorContext(movieProjectId, guide, revision);
    }

    internal static MovieGuideRevision CreateInitialRevision(MovieContinuityGuide guide, Guid userId, DateTime now, MovieStudioCreateRequest request)
    {
        var revision = BuildRevision(guide, userId, new MovieGuideRevisionRequest
        {
            StoryBibleJson = "{}",
            CharacterBibleReferencesJson = "[]",
            WorldBibleReferencesJson = "[]",
            VisualBibleJson = JsonSerializer.Serialize(new { visualLanguage = request.VisualLanguage?.Trim() ?? string.Empty, colorAndLighting = request.ColorAndLighting?.Trim() ?? string.Empty }),
            CinematographyBibleJson = JsonSerializer.Serialize(new { cameraLanguage = request.CameraLanguage?.Trim() ?? string.Empty }),
            AudioBibleJson = JsonSerializer.Serialize(new { soundAndNarration = request.SoundAndNarration?.Trim() ?? string.Empty }),
            ContinuityBibleJson = JsonSerializer.Serialize(new { continuityRules = request.ContinuityRules?.Trim() ?? string.Empty }),
        }, 1, now);
        return revision;
    }

    internal static MovieGuideRevision BuildRevision(MovieContinuityGuide guide, Guid userId, MovieGuideRevisionRequest request, int revisionNumber, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        MovieContinuityGuideId = guide.Id,
        RevisionNumber = revisionNumber,
        Status = MovieGuideRevisionStatuses.Draft,
        StoryBibleJson = request.StoryBibleJson.Trim(),
        CharacterBibleReferencesJson = request.CharacterBibleReferencesJson.Trim(),
        WorldBibleReferencesJson = request.WorldBibleReferencesJson.Trim(),
        VisualBibleJson = request.VisualBibleJson.Trim(),
        CinematographyBibleJson = request.CinematographyBibleJson.Trim(),
        AudioBibleJson = request.AudioBibleJson.Trim(),
        ContinuityBibleJson = request.ContinuityBibleJson.Trim(),
        CreatedByUserId = userId,
        CreatedAt = now,
    };

    private async Task<MovieContinuityGuide?> LoadGuideAsync(Guid movieProjectId, CancellationToken cancellationToken) =>
        await db.MovieContinuityGuides
            .Include(item => item.MovieProject)
            .Include(item => item.Revisions.OrderBy(revision => revision.RevisionNumber))
            .SingleOrDefaultAsync(item => item.MovieProjectId == movieProjectId, cancellationToken);

    private static void ValidateRequest(MovieGuideRevisionRequest request)
    {
        var sections = new (string Name, string Json, bool Array)[]
        {
            (MovieGuideSectionTypes.StoryBible, request.StoryBibleJson, false),
            (MovieGuideSectionTypes.CharacterBibleReferences, request.CharacterBibleReferencesJson, true),
            (MovieGuideSectionTypes.WorldBibleReferences, request.WorldBibleReferencesJson, true),
            (MovieGuideSectionTypes.VisualBible, request.VisualBibleJson, false),
            (MovieGuideSectionTypes.CinematographyBible, request.CinematographyBibleJson, false),
            (MovieGuideSectionTypes.AudioBible, request.AudioBibleJson, false),
            (MovieGuideSectionTypes.ContinuityBible, request.ContinuityBibleJson, false),
        };
        foreach (var section in sections)
        {
            if (string.IsNullOrWhiteSpace(section.Json) || section.Json.Length > 50_000)
                throw new MovieGuideValidationException($"{section.Name} must be valid JSON under 50,000 characters.");
            try
            {
                using var document = JsonDocument.Parse(section.Json);
                var isArray = document.RootElement.ValueKind == JsonValueKind.Array;
                var isObject = document.RootElement.ValueKind == JsonValueKind.Object;
                if (section.Array ? !isArray : !isObject)
                    throw new MovieGuideValidationException($"{section.Name} must be a JSON {(section.Array ? "array" : "object")}.");
            }
            catch (JsonException)
            {
                throw new MovieGuideValidationException($"{section.Name} must be valid JSON.");
            }
        }
    }

    private static MovieGuideHistoryResponse ToHistory(MovieContinuityGuide guide) =>
        new(guide.Id, guide.CurrentRevisionNumber, guide.LockedRevisionNumber, guide.Revisions.OrderByDescending(item => item.RevisionNumber).Select(ToDto).ToArray());

    private static MovieGuideRevisionDto ToDto(MovieGuideRevision revision) =>
        new(revision.Id, revision.RevisionNumber, revision.Status, Sections(revision), revision.CreatedByUserId, revision.CreatedAt, revision.LockedAt, revision.LockedByUserId);

    private static MovieDirectorContextDto ToDirectorContext(Guid movieProjectId, MovieContinuityGuide guide, MovieGuideRevision revision) =>
        new(movieProjectId, guide.Id, revision.Status == MovieGuideRevisionStatuses.Locked && guide.LockedRevisionNumber == revision.RevisionNumber, revision.RevisionNumber, revision.LockedAt, Sections(revision));

    private static IReadOnlyList<MovieGuideSectionDto> Sections(MovieGuideRevision revision) =>
    [
        new(MovieGuideSectionTypes.StoryBible, revision.StoryBibleJson),
        new(MovieGuideSectionTypes.CharacterBibleReferences, revision.CharacterBibleReferencesJson),
        new(MovieGuideSectionTypes.WorldBibleReferences, revision.WorldBibleReferencesJson),
        new(MovieGuideSectionTypes.VisualBible, revision.VisualBibleJson),
        new(MovieGuideSectionTypes.CinematographyBible, revision.CinematographyBibleJson),
        new(MovieGuideSectionTypes.AudioBible, revision.AudioBibleJson),
        new(MovieGuideSectionTypes.ContinuityBible, revision.ContinuityBibleJson),
    ];
}

public sealed class MovieGuideLockedException() : Exception("The Movie Guide is locked. Unlock it before creating a new revision.");
public sealed class MovieGuideNotLockedException() : Exception("Lock a Movie Guide revision before reading Director context.");
public sealed class MovieGuideValidationException(string message) : Exception(message);
