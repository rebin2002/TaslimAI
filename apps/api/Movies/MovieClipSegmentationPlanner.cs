namespace Taslim.Api.Movies;

/// <summary>
/// A read-only, provider-neutral plan for dividing a long film or planned scene
/// into short clips that respect a selected adapter's declared duration range.
/// This does not approve references, create generation jobs, reserve usage, or
/// imply that the clips can be joined without an independently reviewed edit.
/// </summary>
public sealed record MoviePlannedClipSegment(int Sequence, int StartSeconds, int DurationSeconds);

public static class MovieClipSegmentationPlanner
{
    public const int MaximumFilmDurationSeconds = 7_200;

    /// <summary>
    /// Returns false rather than truncating the requested timeline or inventing
    /// unsupported provider durations. The smallest feasible number of clips
    /// is used and the remainder is distributed without a too-short final clip.
    /// </summary>
    public static bool TryPlan(
        int totalSeconds,
        MovieClipDurationRange? capability,
        out IReadOnlyList<MoviePlannedClipSegment> segments)
    {
        segments = Array.Empty<MoviePlannedClipSegment>();
        if (totalSeconds is < 1 or > MaximumFilmDurationSeconds
            || capability is null
            || capability.MinimumSeconds < 1
            || capability.MaximumSeconds < capability.MinimumSeconds)
            return false;

        // Ceiling division in long arithmetic also remains safe if a provider
        // declares int.MaxValue as its upper bound.
        var count = ((long)totalSeconds + capability.MaximumSeconds - 1) / capability.MaximumSeconds;
        if (count < 1 || count > totalSeconds / capability.MinimumSeconds)
            return false;

        var numberOfSegments = checked((int)count);
        var baseDuration = totalSeconds / numberOfSegments;
        var extraSeconds = totalSeconds % numberOfSegments;
        var planned = new MoviePlannedClipSegment[numberOfSegments];
        var start = 0;
        for (var index = 0; index < numberOfSegments; index++)
        {
            var duration = baseDuration + (index < extraSeconds ? 1 : 0);
            if (!capability.Supports(duration))
                return false;

            planned[index] = new MoviePlannedClipSegment(index + 1, start, duration);
            start += duration;
        }

        if (start != totalSeconds)
            return false;

        segments = planned;
        return true;
    }
}

/// <summary>
/// A duration-only preview, not production preflight, consent, pricing,
/// storyboard approval or authorization for provider execution.
/// </summary>
public sealed record MovieClipSegmentationPreviewDto(
    Guid MovieProjectId,
    int TotalDurationSeconds,
    string ProviderKey,
    bool ProviderDeclaresSceneClipOperation,
    bool CanSplitByDuration,
    int? MinimumClipSeconds,
    int? MaximumClipSeconds,
    IReadOnlyList<MoviePlannedClipSegment> Segments);
