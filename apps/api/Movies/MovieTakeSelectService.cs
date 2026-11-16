using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public interface IMovieTakeSelectService
{
    Task<IReadOnlyList<MovieTakeSelectDto>?> ListAsync(Guid userId, Guid movieProjectId, Guid takeId, CancellationToken cancellationToken);
    Task<MovieTakeSelectDto?> CreateAsync(Guid userId, Guid movieProjectId, Guid takeId, MovieTakeSelectRequest request, CancellationToken cancellationToken);
    Task<MovieTakeSelectDto?> ReviewAsync(Guid userId, Guid movieProjectId, Guid takeId, Guid selectId, MovieTakeSelectReviewRequest request, CancellationToken cancellationToken);
}

public sealed class MovieTakeSelectService(TaslimDbContext db, MovieCollaborationAccess collaboration) : IMovieTakeSelectService
{
    public async Task<IReadOnlyList<MovieTakeSelectDto>?> ListAsync(Guid userId, Guid movieProjectId, Guid takeId, CancellationToken cancellationToken)
    {
        var take = await OwnedTakeQuery().AsNoTracking().FirstOrDefaultAsync(item => item.Id == takeId && item.MovieShot.Scene.MovieProjectId == movieProjectId, cancellationToken);
        if (take is null || !await collaboration.HasPermissionAsync(userId, movieProjectId, MoviePermissions.View, cancellationToken)) return null;
        return await db.MovieTakeSelects.AsNoTracking()
            .Where(item => item.MovieTakeId == takeId)
            .OrderBy(item => item.SelectNumber)
            .Select(item => ToDto(item))
            .ToListAsync(cancellationToken);
    }

    public async Task<MovieTakeSelectDto?> CreateAsync(Guid userId, Guid movieProjectId, Guid takeId, MovieTakeSelectRequest request, CancellationToken cancellationToken)
    {
        var take = await OwnedTakeQuery().FirstOrDefaultAsync(item => item.Id == takeId && item.MovieShot.Scene.MovieProjectId == movieProjectId, cancellationToken);
        if (take is null || !await collaboration.HasPermissionAsync(userId, movieProjectId, MoviePermissions.Edit, cancellationToken)) return null;

        ValidateText(request.Label, 160, "Select label");
        ValidateText(request.Notes, 4_000, "Select notes");
        var sourceAsset = take.Asset ?? take.MovieClip?.Asset;
        if (sourceAsset is null || sourceAsset.Status != AssetStatus.Active || !sourceAsset.StoredFileId.HasValue
            || (!string.Equals(sourceAsset.AssetType, AssetTypes.Video, StringComparison.OrdinalIgnoreCase)
                && sourceAsset.MimeType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) != true))
            throw SourceInvalid("A select must reference an active generated video asset.");
        var sourceDuration = ResolveTakeDuration(take);
        ValidateRange(request.StartMilliseconds, request.EndMilliseconds, sourceDuration);

        var now = DateTime.UtcNow;
        var select = new MovieTakeSelect
        {
            Id = Guid.NewGuid(), MovieTakeId = take.Id,
            SelectNumber = (await db.MovieTakeSelects.Where(item => item.MovieTakeId == take.Id).MaxAsync(item => (int?)item.SelectNumber, cancellationToken) ?? 0) + 1,
            Label = request.Label.Trim(), Status = MovieTakeSelectStatuses.Draft,
            StartMilliseconds = request.StartMilliseconds, EndMilliseconds = request.EndMilliseconds,
            Notes = Clean(request.Notes), ProvenanceJson = MovieTakeSelectProvenance.Create(take, sourceAsset.Id, request.StartMilliseconds, request.EndMilliseconds, now),
            CreatedByUserId = userId, CreatedAt = now, UpdatedAt = now,
        };
        db.MovieTakeSelects.Add(select);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(select);
    }

    public async Task<MovieTakeSelectDto?> ReviewAsync(Guid userId, Guid movieProjectId, Guid takeId, Guid selectId, MovieTakeSelectReviewRequest request, CancellationToken cancellationToken)
    {
        var select = await db.MovieTakeSelects
            .Include(item => item.MovieTake).ThenInclude(item => item.MovieShot).ThenInclude(item => item.Scene)
            .FirstOrDefaultAsync(item => item.Id == selectId && item.MovieTakeId == takeId && item.MovieTake.MovieShot.Scene.MovieProjectId == movieProjectId, cancellationToken);
        if (select is null || !await collaboration.HasPermissionAsync(userId, movieProjectId, MoviePermissions.Approve, cancellationToken)) return null;

        var decision = NormalizeDecision(request.Decision);
        ValidateText(request.Comment, 4_000, "Select review comment");
        if (select.Status == MovieTakeSelectStatuses.Archived)
            throw Invalid("Archived selects cannot be reviewed.");

        var now = DateTime.UtcNow;
        select.Status = decision;
        select.ReviewedByUserId = userId;
        select.ReviewedAt = now;
        select.ReviewNote = Clean(request.Comment);
        select.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(select);
    }

    private IQueryable<MovieTake> OwnedTakeQuery() => db.MovieTakes
        .Include(item => item.MovieShot).ThenInclude(item => item.Scene)
        .Include(item => item.Asset)
        .Include(item => item.MovieClip).ThenInclude(item => item!.Asset);

    private static int ResolveTakeDuration(MovieTake take)
    {
        if (MovieTimelineSourceRules.TryReadDurationMilliseconds(take.Asset, out var assetDuration)) return assetDuration;
        if (take.MovieClip?.DurationSeconds is > 0) return checked(take.MovieClip.DurationSeconds.Value * 1000);
        if (take.MetadataJson is not null && TryReadDuration(take.MetadataJson, out var takeDuration)) return takeDuration;
        if (take.MovieShot.DurationSeconds is > 0) return checked(take.MovieShot.DurationSeconds.Value * 1000);
        throw SourceInvalid("The generated take has no trusted duration metadata.");
    }

    private static bool TryReadDuration(string json, out int duration)
    {
        duration = 0;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("durationMilliseconds", out var milliseconds) && milliseconds.TryGetInt32(out duration) && duration > 0) return true;
            if (root.TryGetProperty("durationSeconds", out var seconds) && seconds.TryGetDouble(out var value) && value > 0 && value * 1000 <= int.MaxValue)
            {
                duration = checked((int)Math.Round(value * 1000, MidpointRounding.AwayFromZero));
                return duration > 0;
            }
        }
        catch (JsonException) { }
        return false;
    }

    private static void ValidateRange(int startMilliseconds, int endMilliseconds, int sourceDuration)
    {
        if (startMilliseconds < 0 || endMilliseconds <= startMilliseconds)
            throw Invalid("Select start and end points must be non-negative and ordered.");
        if (endMilliseconds > sourceDuration)
            throw Invalid("The select must fit inside the generated take duration.");
    }

    private static string NormalizeDecision(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !MovieTakeSelectStatuses.Supported.Contains(value.Trim())
            || value.Trim().Equals(MovieTakeSelectStatuses.Draft, StringComparison.OrdinalIgnoreCase)
            || value.Trim().Equals(MovieTakeSelectStatuses.Archived, StringComparison.OrdinalIgnoreCase))
            throw Invalid("Select review decision must be Approved or Rejected.");
        return value.Trim().Equals(MovieTakeSelectStatuses.Approved, StringComparison.OrdinalIgnoreCase)
            ? MovieTakeSelectStatuses.Approved
            : MovieTakeSelectStatuses.Rejected;
    }

    private static MovieTakeSelectDto ToDto(MovieTakeSelect select) => new(
        select.Id, select.MovieTakeId, select.SelectNumber, select.Label, select.Status,
        select.StartMilliseconds, select.EndMilliseconds, select.DurationMilliseconds,
        select.Notes, select.ProvenanceJson, select.CreatedByUserId, select.CreatedAt, select.UpdatedAt,
        select.ReviewedByUserId, select.ReviewedAt, select.ReviewNote);

    private static void ValidateText(string? value, int maximum, string field)
    {
        if (string.IsNullOrWhiteSpace(value) && field == "Select label") throw Invalid("Select label is required.");
        if (value?.Trim().Length > maximum) throw Invalid($"{field} must be {maximum} characters or fewer.");
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static MovieTakeSelectValidationException Invalid(string message) => new(MovieTakeSelectErrors.Invalid, message);
    private static MovieTakeSelectValidationException SourceInvalid(string message) => new(MovieTakeSelectErrors.SourceInvalid, message);
}

public sealed class MovieTakeSelectValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
