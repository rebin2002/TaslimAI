import type { GenerationJob, GenerationJobStatus } from "./api";

export type GenerationJobsState = {
  jobs: GenerationJob[];
  selectedJobId: string | null;
  error: string;
};

export const terminalJobStatuses: ReadonlySet<GenerationJobStatus> = new Set(["Succeeded", "Failed", "Cancelled"]);

export function isTerminalJob(job: GenerationJob | null | undefined) {
  return Boolean(job && terminalJobStatuses.has(job.status));
}

// The history view reads a single workspace at a time. A response that describes a record from
// another workspace - an in-flight poll, create or cancel that started before the session's
// workspace was reassigned - must not enter the list, because the list is what the user reads as
// the current workspace's history and each row carries the job's result payload. Dropping the
// record keeps paging and the selection consistent with the server-side fence on the list itself.
export function isJobReadableInWorkspace(job: GenerationJob, activeWorkspaceId: string | null) {
  return Boolean(activeWorkspaceId) && job.workspaceId === activeWorkspaceId;
}

export function mergeGenerationJob(
  state: GenerationJobsState,
  job: GenerationJob,
  activeWorkspaceId: string | null,
): GenerationJobsState {
  if (!isJobReadableInWorkspace(job, activeWorkspaceId)) return state;
  return {
    ...state,
    jobs: [job, ...state.jobs.filter((item) => item.id !== job.id)],
    selectedJobId: job.id,
  };
}

export function selectActiveGenerationJob({ current, jobs, activeWorkspaceId }: Readonly<{
  current: GenerationJob | null;
  jobs: GenerationJob[];
  activeWorkspaceId: string | null;
}>): GenerationJob | null {
  if (current && isJobReadableInWorkspace(current, activeWorkspaceId)) return current;
  return jobs.find((item) => isJobReadableInWorkspace(item, activeWorkspaceId)) ?? null;
}

export function canCancelGenerationJob(job: GenerationJob | null | undefined) {
  return Boolean(job && !isTerminalJob(job));
}

export function safeGenerationJobDisplay(job: GenerationJob) {
  return {
    id: job.id,
    status: job.status,
    progressPercent: job.progressPercent,
    resultJson: job.resultJson,
    errorMessage: job.errorMessage,
  };
}
