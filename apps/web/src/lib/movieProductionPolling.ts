import type { MovieClip, MovieProject } from "./api";

const activeExecutionStatuses = new Set(["Pending", "Queued", "Running"]);

/**
 * Movie clip lifecycle states the video worker uses while a provider pass is
 * still running. The worker publishes the private Asset together with the
 * terminal `Ready` status, so a clip in one of these states and without an
 * Asset has no playable short-video output yet.
 */
const activeClipStatuses = new Set(["Queued", "Generating"]);

function isClipInFlight(clip: MovieClip) {
  return activeClipStatuses.has(clip.status) && !clip.assetId;
}

export function hasActiveMovieProductionExecution(project: MovieProject) {
  return project.scenes.some((scene) =>
    (scene.clips ?? []).some(isClipInFlight) ||
    (scene.shots ?? []).some((shot) =>
      (shot.clips ?? []).some(isClipInFlight) ||
      [...(shot.productionVersions ?? []), ...(shot.takes ?? [])].some((item) =>
        activeExecutionStatuses.has(item.execution?.status ?? ""),
      ),
    ),
  );
}

export function nextMovieProductionPollDelay(hasActiveExecution: boolean, retryAttempt = 0) {
  if (!hasActiveExecution) return null;
  return Math.min(1_000 * Math.max(1, retryAttempt + 1), 5_000);
}
