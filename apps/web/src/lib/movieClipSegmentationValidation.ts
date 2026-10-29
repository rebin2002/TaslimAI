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
  if (preview.providerDeclaresSceneClipOperation !== true
      || preview.canSplitByDuration !== true
      || typeof preview.providerKey !== "string" || preview.providerKey.trim().length === 0
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
    if (segment == null || typeof segment !== "object"
        || !Number.isSafeInteger(segment.sequence)
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

/**
 * Scene-aligned slots are advisory and must not be advertised when they
 * cross scene boundaries, omit runtime or contain malformed API data.
 * Older API responses may omit these optional fields entirely.
 */
export function isConsistentMovieSceneClipSegmentation(preview: MovieClipSegmentationPreview): boolean {
  const slots = preview.sceneSegments;
  if (preview.canSplitByScene === undefined || preview.canSplitByScene === false) {
    return (preview.canSplitByScene === undefined || typeof preview.canSplitByScene === "boolean")
      && (slots == null || (Array.isArray(slots) && slots.length === 0));
  }
  if (preview.canSplitByScene !== true
      || !isConsistentMovieClipSegmentation(preview)
      || !Array.isArray(slots)
      || slots.length === 0
      || slots.length > preview.totalDurationSeconds) return false;

  const min = preview.minimumClipSeconds;
  const max = preview.maximumClipSeconds;
  if (min == null || max == null) return false;
  const seenScenes = new Set<string>();
  let cursor = 0;
  let currentSceneId = "";
  let currentSceneSequence = 0;
  let sceneCursor = 0;
  for (const [index, slot] of slots.entries()) {
    if (slot == null || typeof slot !== "object"
        || typeof slot.sceneId !== "string"
        || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(slot.sceneId)
        || !Number.isSafeInteger(slot.sceneSequence)
        || !Number.isSafeInteger(slot.clipSequence)
        || !Number.isSafeInteger(slot.timelineStartSeconds)
        || !Number.isSafeInteger(slot.sceneOffsetSeconds)
        || !Number.isSafeInteger(slot.durationSeconds)
        || slot.sceneSequence < 1
        || slot.clipSequence !== index + 1
        || slot.timelineStartSeconds !== cursor
        || slot.durationSeconds < min || slot.durationSeconds > max) return false;

    if (slot.sceneId !== currentSceneId) {
      if (seenScenes.has(slot.sceneId)
          || slot.sceneSequence <= currentSceneSequence
          || slot.sceneOffsetSeconds !== 0) return false;
      seenScenes.add(slot.sceneId);
      currentSceneId = slot.sceneId;
      currentSceneSequence = slot.sceneSequence;
      sceneCursor = 0;
    } else if (slot.sceneSequence !== currentSceneSequence) return false;

    if (slot.sceneOffsetSeconds !== sceneCursor) return false;
    cursor += slot.durationSeconds;
    sceneCursor += slot.durationSeconds;
    if (cursor > preview.totalDurationSeconds) return false;
  }
  return cursor === preview.totalDurationSeconds;
}
