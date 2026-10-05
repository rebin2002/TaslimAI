using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Assets;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public sealed class MovieSoundValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public interface IMovieSoundService
{
    Task<MovieSoundTrackDto?> CreateForSceneAsync(Guid userId, Guid sceneId, MovieSoundTrackRequest request, CancellationToken cancellationToken);
    Task<MovieSoundTrackDto?> CreateForShotAsync(Guid userId, Guid shotId, MovieSoundTrackRequest request, CancellationToken cancellationToken);
    Task<MovieSoundTrackListDto?> GetForSceneAsync(Guid userId, Guid sceneId, CancellationToken cancellationToken);
    Task<MovieSoundTrackListDto?> GetForShotAsync(Guid userId, Guid shotId, CancellationToken cancellationToken);
    Task<MovieSoundTrackDto?> GetAsync(Guid userId, Guid trackId, CancellationToken cancellationToken);
    Task<MovieSoundLibraryDto?> GetLibraryAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken);
    Task<MovieSoundLibraryReferenceDto?> AddLibraryReferenceAsync(Guid userId, Guid movieProjectId, MovieSoundLibraryReferenceRequest request, CancellationToken cancellationToken);
    Task<MovieSoundTrackDto?> ReviewAsync(Guid userId, Guid trackId, MovieSoundReviewRequest request, CancellationToken cancellationToken);
}

public sealed class MovieSoundService(
    TaslimDbContext db,
    MovieCollaborationAccess collaboration,
    IGenerationJobService jobs) : IMovieSoundService
{
    public Task<MovieSoundTrackDto?> CreateForSceneAsync(Guid userId, Guid sceneId, MovieSoundTrackRequest request, CancellationToken cancellationToken) =>
        CreateAsync(userId, sceneId, null, request, cancellationToken);

    public Task<MovieSoundTrackDto?> CreateForShotAsync(Guid userId, Guid shotId, MovieSoundTrackRequest request, CancellationToken cancellationToken) =>
        CreateAsync(userId, null, shotId, request, cancellationToken);

    public async Task<MovieSoundTrackListDto?> GetForSceneAsync(Guid userId, Guid sceneId, CancellationToken cancellationToken)
    {
        var scene = await db.MovieScenes.AsNoTracking().FirstOrDefaultAsync(item => item.Id == sceneId, cancellationToken);
        if (scene is null || !await collaboration.HasPermissionAsync(userId, scene.MovieProjectId, MoviePermissions.View, cancellationToken)) return null;
        var tracks = await QueryTracks().Where(item => item.MovieSceneId == sceneId).OrderBy(item => item.StartMilliseconds).ThenBy(item => item.Layer).ToListAsync(cancellationToken);
        return new MovieSoundTrackListDto(sceneId, "scene", tracks.Select(MovieSoundTrackProjection.ToDto).ToArray());
    }

    public async Task<MovieSoundTrackListDto?> GetForShotAsync(Guid userId, Guid shotId, CancellationToken cancellationToken)
    {
        var shot = await db.MovieShots.AsNoTracking().Include(item => item.Scene).FirstOrDefaultAsync(item => item.Id == shotId, cancellationToken);
        if (shot is null || !await collaboration.HasPermissionAsync(userId, shot.Scene.MovieProjectId, MoviePermissions.View, cancellationToken)) return null;
        var tracks = await QueryTracks().Where(item => item.MovieShotId == shotId).OrderBy(item => item.StartMilliseconds).ThenBy(item => item.Layer).ToListAsync(cancellationToken);
        return new MovieSoundTrackListDto(shotId, "shot", tracks.Select(MovieSoundTrackProjection.ToDto).ToArray());
    }

    public async Task<MovieSoundTrackDto?> GetAsync(Guid userId, Guid trackId, CancellationToken cancellationToken)
    {
        var track = await QueryTracks().FirstOrDefaultAsync(item => item.Id == trackId, cancellationToken);
        if (track is null || !await collaboration.HasPermissionAsync(userId, track.MovieProjectId, MoviePermissions.View, cancellationToken)) return null;
        return MovieSoundTrackProjection.ToDto(track);
    }

    public async Task<MovieSoundLibraryDto?> GetLibraryAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken)
    {
        if (!await collaboration.HasPermissionAsync(userId, movieProjectId, MoviePermissions.View, cancellationToken)) return null;
        var movie = await db.MovieProjects.AsNoTracking().FirstOrDefaultAsync(item => item.Id == movieProjectId, cancellationToken);
        if (movie is null) return null;
        var references = await db.MovieSoundLibraryReferences.AsNoTracking()
            .Include(item => item.Asset).ThenInclude(item => item!.StoredFile)
            .Where(item => item.MovieProjectId == movieProjectId && item.Asset.Status == AssetStatus.Active)
            .OrderBy(item => item.Label)
            .ThenBy(item => item.CreatedAt)
            .ToListAsync(cancellationToken);
        references = references.Where(item => IsEligibleAudioAsset(item.Asset, movie)).ToList();
        return new MovieSoundLibraryDto(movieProjectId, references.Select(MovieSoundTrackProjection.ToLibraryDto).ToArray());
    }

    public async Task<MovieSoundLibraryReferenceDto?> AddLibraryReferenceAsync(Guid userId, Guid movieProjectId, MovieSoundLibraryReferenceRequest request, CancellationToken cancellationToken)
    {
        if (!await collaboration.HasPermissionAsync(userId, movieProjectId, MoviePermissions.Edit, cancellationToken)) return null;
        var movie = await db.MovieProjects.AsNoTracking().FirstOrDefaultAsync(item => item.Id == movieProjectId, cancellationToken);
        if (movie is null) return null;
        var asset = await FindAudioAssetAsync(request.AssetId, movie, cancellationToken);
        if (asset is null) throw new MovieSoundValidationException(GenerationJobErrorCodes.MovieSoundAssetInvalid, "Choose an audio asset from this movie workspace.");
        var existing = await db.MovieSoundLibraryReferences.Include(item => item.Asset)
            .FirstOrDefaultAsync(item => item.MovieProjectId == movieProjectId && item.AssetId == asset.Id, cancellationToken);
        if (existing is not null) return MovieSoundTrackProjection.ToLibraryDto(existing);
        var now = DateTime.UtcNow;
        var reference = new MovieSoundLibraryReference
        {
            Id = Guid.NewGuid(),
            MovieProjectId = movieProjectId,
            AssetId = asset.Id,
            CreatedByUserId = userId,
            Label = NormalizeLabel(request.Label, asset.Name),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.MovieSoundLibraryReferences.Add(reference);
        await db.SaveChangesAsync(cancellationToken);
        reference.Asset = asset;
        return MovieSoundTrackProjection.ToLibraryDto(reference);
    }

    public async Task<MovieSoundTrackDto?> ReviewAsync(Guid userId, Guid trackId, MovieSoundReviewRequest request, CancellationToken cancellationToken)
    {
        var track = await QueryTracks(tracking: true).FirstOrDefaultAsync(item => item.Id == trackId, cancellationToken);
        if (track is null || !await collaboration.HasPermissionAsync(userId, track.MovieProjectId, MoviePermissions.Approve, cancellationToken)) return null;
        var asset = track.Asset ?? track.GenerationJob?.Assets.OrderByDescending(item => item.CreatedAt).FirstOrDefault();
        if (request.Approve && asset is null)
            throw new MovieSoundValidationException(GenerationJobErrorCodes.MovieSoundNotReady, "Only a sound track with a completed audio asset can be approved.");
        if (request.Approve && !IsEligibleAudioAsset(asset!, track.MovieProject))
            throw new MovieSoundValidationException(GenerationJobErrorCodes.MovieSoundNotReady, "The sound track asset must still be active, project-scoped, and backed by a ready private audio file.");
        if (request.Approve && (track.Status is MovieSoundStatuses.Archived or MovieSoundStatuses.Draft))
            throw new MovieSoundValidationException(GenerationJobErrorCodes.MovieSoundNotReady, "The sound track is not ready for approval.");
        var decision = request.Approve ? MovieSoundStatuses.Approved : MovieSoundStatuses.Rejected;
        var now = DateTime.UtcNow;
        track.Status = decision;
        track.AssetId = asset?.Id ?? track.AssetId;
        track.ApprovedByUserId = request.Approve ? userId : null;
        track.ApprovedAt = request.Approve ? now : null;
        track.UpdatedAt = now;
        db.MovieSoundApprovals.Add(new MovieSoundApproval
        {
            Id = Guid.NewGuid(),
            MovieSoundTrackId = track.Id,
            ReviewerUserId = userId,
            Decision = decision,
            Comment = Normalize(request.Comment, 2_000),
            CreatedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);
        return MovieSoundTrackProjection.ToDto(track);
    }

    private async Task<MovieSoundTrackDto?> CreateAsync(Guid userId, Guid? sceneId, Guid? shotId, MovieSoundTrackRequest request, CancellationToken cancellationToken)
    {
        if (sceneId is null && shotId is null) throw new ArgumentException("A scene or shot target is required.");
        var target = await ResolveTargetAsync(sceneId, shotId, cancellationToken);
        if (target is null || !await collaboration.HasPermissionAsync(userId, target.MovieProjectId, MoviePermissions.Edit, cancellationToken)) return null;
        ValidateCue(request, target.DurationSeconds, sceneId.HasValue ? "scene" : "shot");
        if (request.Generate && (request.AssetId.HasValue || request.LibraryReferenceId.HasValue))
            throw new MovieSoundValidationException(GenerationJobErrorCodes.MovieSoundRequestInvalid, "Choose generation or an existing audio asset, not both.");

        var movie = await db.MovieProjects.AsNoTracking().FirstAsync(item => item.Id == target.MovieProjectId, cancellationToken);
        Asset? asset = null;
        Guid? libraryReferenceId = request.LibraryReferenceId;
        string sourceKind = MovieSoundSourceKinds.Imported;
        if (libraryReferenceId.HasValue)
        {
            var reference = await db.MovieSoundLibraryReferences.AsNoTracking().FirstOrDefaultAsync(item => item.Id == libraryReferenceId && item.MovieProjectId == target.MovieProjectId, cancellationToken);
            if (reference is null) throw new MovieSoundValidationException(GenerationJobErrorCodes.MovieSoundAssetInvalid, "The sound library reference is not part of this movie.");
            asset = await FindAudioAssetAsync(reference.AssetId, movie, cancellationToken);
            sourceKind = MovieSoundSourceKinds.Library;
        }
        else if (request.AssetId.HasValue)
        {
            asset = await FindAudioAssetAsync(request.AssetId.Value, movie, cancellationToken);
        }
        if (request.AssetId.HasValue && asset is null)
            throw new MovieSoundValidationException(GenerationJobErrorCodes.MovieSoundAssetInvalid, "Choose an audio asset from this movie workspace.");
        if (asset is not null && !libraryReferenceId.HasValue)
        {
            var reference = await EnsureLibraryReferenceAsync(target.MovieProjectId, asset, userId, request.Name, cancellationToken);
            libraryReferenceId = reference.Id;
            sourceKind = MovieSoundSourceKinds.Library;
        }
        if (request.Generate) sourceKind = MovieSoundSourceKinds.Generated;
        var now = DateTime.UtcNow;
        var track = new MovieSoundTrack
        {
            Id = Guid.NewGuid(),
            MovieProjectId = target.MovieProjectId,
            MovieSceneId = sceneId ?? target.SceneId,
            MovieShotId = shotId ?? target.ShotId,
            CreatedByUserId = userId,
            AssetId = asset?.Id,
            LibraryReferenceId = libraryReferenceId,
            Kind = request.Kind.Trim().ToLowerInvariant(),
            Layer = request.Layer.Trim().ToLowerInvariant(),
            Name = request.Name.Trim(),
            Description = request.Description.Trim(),
            StartMilliseconds = request.StartMilliseconds,
            EndMilliseconds = request.EndMilliseconds,
            FadeInMilliseconds = request.FadeInMilliseconds,
            FadeOutMilliseconds = request.FadeOutMilliseconds,
            GainDb = request.GainDb,
            Status = request.Generate ? MovieSoundStatuses.Queued : asset is null ? MovieSoundStatuses.Draft : MovieSoundStatuses.ReadyForReview,
            SourceKind = sourceKind,
            ProvenanceJson = MovieSoundTrackProjection.Provenance(sourceKind, asset?.Id, null, userId),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.MovieSoundTracks.Add(track);
        await db.SaveChangesAsync(cancellationToken);

        if (request.Generate)
        {
            var input = new MovieSoundGenerationInput(track.MovieProjectId, track.Id, track.MovieSceneId, track.MovieShotId, track.Kind, track.Layer, track.Description, track.EndMilliseconds - track.StartMilliseconds, Normalize(request.AdditionalInstructions, 2_000));
            try
            {
                var job = await jobs.CreateAsync(userId, new CreateGenerationJobRequest
                {
                    WorkspaceId = movie.WorkspaceId,
                    ProjectId = movie.ProjectId,
                    JobType = GenerationJobTypes.MovieSoundGenerate,
                    Title = track.Name,
                    InputJson = JsonSerializer.Serialize(input),
                }, cancellationToken);
                track.GenerationJobId = job.Id;
                track.ProvenanceJson = MovieSoundTrackProjection.Provenance(sourceKind, null, job.Id, userId);
                await db.SaveChangesAsync(cancellationToken);
            }
            catch
            {
                track.Status = MovieSoundStatuses.Failed;
                track.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                throw;
            }
        }
        return MovieSoundTrackProjection.ToDto(await QueryTracks().FirstAsync(item => item.Id == track.Id, cancellationToken));
    }

    private IQueryable<MovieSoundTrack> QueryTracks(bool tracking = false)
    {
        var query = db.MovieSoundTracks
            .Include(item => item.MovieProject)
            .Include(item => item.Approvals)
            .Include(item => item.Asset).ThenInclude(item => item!.StoredFile)
            .Include(item => item.GenerationJob!).ThenInclude(item => item!.Assets).ThenInclude(item => item.StoredFile)
            .AsQueryable();
        return tracking ? query : query.AsNoTracking();
    }

    private async Task<MovieSoundLibraryReference> EnsureLibraryReferenceAsync(Guid movieProjectId, Asset asset, Guid userId, string label, CancellationToken cancellationToken)
    {
        var existing = await db.MovieSoundLibraryReferences.FirstOrDefaultAsync(item => item.MovieProjectId == movieProjectId && item.AssetId == asset.Id, cancellationToken);
        if (existing is not null) return existing;
        var now = DateTime.UtcNow;
        var reference = new MovieSoundLibraryReference
        {
            Id = Guid.NewGuid(), MovieProjectId = movieProjectId, AssetId = asset.Id, CreatedByUserId = userId,
            Label = NormalizeLabel(label, asset.Name), CreatedAt = now, UpdatedAt = now,
        };
        db.MovieSoundLibraryReferences.Add(reference);
        await db.SaveChangesAsync(cancellationToken);
        return reference;
    }

    private async Task<Asset?> FindAudioAssetAsync(Guid assetId, MovieProject movie, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.Include(item => item.StoredFile)
            .FirstOrDefaultAsync(item => item.Id == assetId && item.WorkspaceId == movie.WorkspaceId, cancellationToken);
        return asset is not null && IsEligibleAudioAsset(asset, movie) ? asset : null;
    }

    private static bool IsEligibleAudioAsset(Asset? asset, MovieProject movie) =>
        asset is not null
        && asset.Status == AssetStatus.Active
        && asset.WorkspaceId == movie.WorkspaceId
        && (asset.ProjectId is null || asset.ProjectId == movie.ProjectId)
        && asset.StoredFileId.HasValue
        && asset.StoredFile is { Status: StoredFileStatus.Ready, ConversationId: null } file
        && file.WorkspaceId == movie.WorkspaceId
        && (file.ProjectId is null || file.ProjectId == movie.ProjectId)
        && (string.Equals(asset.AssetType, AssetTypes.Audio, StringComparison.OrdinalIgnoreCase)
            || string.Equals(asset.AssetType, AssetTypes.Music, StringComparison.OrdinalIgnoreCase)
            || asset.MimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true)
        && file.ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);

    private async Task<SoundTarget?> ResolveTargetAsync(Guid? sceneId, Guid? shotId, CancellationToken cancellationToken)
    {
        if (sceneId.HasValue)
        {
            var scene = await db.MovieScenes.AsNoTracking().FirstOrDefaultAsync(item => item.Id == sceneId.Value, cancellationToken);
            return scene is null ? null : new SoundTarget(scene.MovieProjectId, scene.Id, null, scene.DurationSeconds);
        }
        var shot = await db.MovieShots.AsNoTracking().Include(item => item.Scene).FirstOrDefaultAsync(item => item.Id == shotId!.Value, cancellationToken);
        return shot is null ? null : new SoundTarget(shot.Scene.MovieProjectId, shot.MovieSceneId, shot.Id, shot.DurationSeconds);
    }

    private static void ValidateCue(MovieSoundTrackRequest request, int? targetDurationSeconds, string targetType)
    {
        if (!MovieSoundKinds.Supported.Contains(request.Kind?.Trim() ?? string.Empty))
            throw new MovieSoundValidationException(GenerationJobErrorCodes.MovieSoundKindInvalid, "Choose a supported sound-effect or ambience kind.");
        if (!MovieSoundLayers.Supported.Contains(request.Layer?.Trim() ?? string.Empty))
            throw new MovieSoundValidationException(GenerationJobErrorCodes.MovieSoundLayerInvalid, "Choose a supported sound layer.");
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 160 || string.IsNullOrWhiteSpace(request.Description) || request.Description.Trim().Length > 4_000)
            throw new MovieSoundValidationException(GenerationJobErrorCodes.MovieSoundRequestInvalid, "A sound name and description are required.");
        if (request.StartMilliseconds < 0 || request.EndMilliseconds <= request.StartMilliseconds)
            throw new MovieSoundValidationException(GenerationJobErrorCodes.MovieSoundCueInvalid, "Sound cue timing must have a positive duration.");
        var duration = request.EndMilliseconds - request.StartMilliseconds;
        if (request.FadeInMilliseconds < 0 || request.FadeOutMilliseconds < 0 || request.FadeInMilliseconds + request.FadeOutMilliseconds > duration)
            throw new MovieSoundValidationException(GenerationJobErrorCodes.MovieSoundCueInvalid, "Fade timing must fit inside the sound cue.");
        if (targetDurationSeconds.HasValue && request.EndMilliseconds > targetDurationSeconds.Value * 1_000)
            throw new MovieSoundValidationException(GenerationJobErrorCodes.MovieSoundCueInvalid, $"The sound cue must fit inside the {targetType} duration.");
        if (request.AdditionalInstructions?.Length > 2_000)
            throw new MovieSoundValidationException(GenerationJobErrorCodes.MovieSoundRequestInvalid, "The additional sound instructions are too long.");
    }

    private static string NormalizeLabel(string? label, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(label) ? fallback : label.Trim();
        return value.Length <= 160 ? value : value[..160];
    }

    private static string? Normalize(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private sealed record SoundTarget(Guid MovieProjectId, Guid SceneId, Guid? ShotId, int? DurationSeconds);
}
