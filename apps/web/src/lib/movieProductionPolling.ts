import type { MovieProject } from "./api";

const activeExecutionStatuses = new Set(["Pending", "Queued", "Running"]);

export function hasActiveMovieProductionExecution(project: MovieProject) {
  return project.scenes.some((scene) => scene.shots.some((shot) =>
    [...(shot.productionVersions ?? []), ...(shot.takes ?? [])].some((item) =>
      activeExecutionStatuses.has(item.execution?.status ?? ""),
    ),
  ));
}

export function nextMovieProductionPollDelay(hasActiveExecution: boolean, retryAttempt = 0) {
  if (!hasActiveExecution) return null;
  return Math.min(1_000 * Math.max(1, retryAttempt + 1), 5_000);
}
