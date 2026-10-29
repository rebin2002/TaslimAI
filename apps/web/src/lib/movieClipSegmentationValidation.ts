import type { MovieClipSegmentationPreview } from "./api";

/**
 * A duration-only preview must cover the stated timeline exactly. Never show
 * an apparently viable two-hour split if a stale or malformed API response
 * drops, overlaps, reorders or invents provider clip durations.
 *
 * This is presentation validation, NOT provider admission, price approval,
 * shot planning, or permission to generate any of these clips.
 */
export function isConsistentMovieClipSegmentation(preview: MovieClipSegmentationPreview): boolean {
  const min = preview.minimumClipSeconds;
  const max = preview.maximumClipSeconds;
  if (!preview.providerDeclaresSceneClipOperation || !preview.canSplitByDuration
      || !Number.isSafeInteger(preview.totalDurationSeconds)
      || preview.totalDurationSeconds < 1 || preview.totalDurationSeconds > 7_200
      || min == null || max == null
      || !Number.isSafeInteger(min) || !Number.isSafeInteger(max)
      || min < 1 || max < min
      || !Array.isArray(preview.segments)
      || preview.segments.length < 1
      || preview.segments.length > preview.totalDurationSeconds) {
    return false;
  }

  let cursor = 0;
  for (const [index, segment] of preview.segments.entries()) {
    if (!Number.isSafeInteger(segment.sequence)
        || !Number.isSafeInteger(segment.startSeconds)
        || !Number.isSafeInteger(segment.durationSeconds)
        || segment.sequence !== index + 1
        || segment.startSeconds !== cursor
        || segment.durationSeconds < min
        || segment.durationSeconds > max) {
      return false;
    }
    cursor += segment.durationSeconds;
    if (cursor > preview.totalDurationSeconds) return false;
  }
  return cursor === preview.totalDurationSeconds;
}
