using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public sealed record MovieStoryCastSuggestionDto(
    string Name,
    string? Role,
    string Description,
    string Source,
    string SourceType,
    bool IsEstablished,
    bool IsProposed,
    IReadOnlyList<string> Evidence);

public sealed record MovieStoryCastSuggestionsDto(
    Guid MovieProjectId,
    Guid? StoryRevisionId,
    string StoryRevisionStatus,
    IReadOnlyList<MovieStoryCastSuggestionDto> Suggestions);

public interface IMovieStoryCastService
{
    Task<MovieStoryCastSuggestionsDto?> GetSuggestionsAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Projects screenplay character cues into reviewable Cast suggestions without creating
/// character cards or treating draft/AI material as established continuity facts.
/// </summary>
public sealed class MovieStoryCastService(TaslimDbContext db, MovieCollaborationAccess collaboration) : IMovieStoryCastService
{
    public async Task<MovieStoryCastSuggestionsDto?> GetSuggestionsAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken = default)
    {
        var story = await db.MovieStories.AsNoTracking()
            .Include(item => item.Revisions).ThenInclude(item => item.Scenes).ThenInclude(item => item.Elements)
            .SingleOrDefaultAsync(item => item.MovieProjectId == movieProjectId, cancellationToken);
        if (story is null || !await collaboration.HasPermissionAsync(userId, movieProjectId, MoviePermissions.View, cancellationToken)) return null;

        var revision = story.CurrentRevisionId is Guid currentId
            ? story.Revisions.SingleOrDefault(item => item.Id == currentId)
            : story.ApprovedRevisionId is Guid approvedId
                ? story.Revisions.SingleOrDefault(item => item.Id == approvedId)
                : null;
        if (revision is null) return new(movieProjectId, null, "None", []);

        var suggestions = revision.Scenes
            .OrderBy(scene => scene.Ordinal)
            .SelectMany(scene => scene.Elements
                .Where(element => !string.IsNullOrWhiteSpace(element.CharacterName))
                .Select(element => new { scene, element, name = element.CharacterName!.Trim() }))
            .GroupBy(item => item.name, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                var name = first.name;
                var role = name.Contains("farmer", StringComparison.OrdinalIgnoreCase)
                    ? "Protagonist"
                    : name.Contains("grandfather", StringComparison.OrdinalIgnoreCase)
                        ? "Family / mentor"
                        : "Story character";
                var evidence = group.Take(4).Select(item => $"{item.scene.SceneIdentifier}: {item.element.Content.Trim()}").ToArray();
                var established = string.Equals(revision.Status, MovieStoryRevisionStatuses.Approved, StringComparison.OrdinalIgnoreCase);
                return new MovieStoryCastSuggestionDto(
                    name,
                    role,
                    $"Character cue found in the Story screenplay; create or confirm a durable Cast card only after review.",
                    "Story screenplay",
                    "story_screenplay_character",
                    established,
                    !established,
                    evidence);
            })
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new(movieProjectId, revision.Id, revision.Status, suggestions);
    }
}
