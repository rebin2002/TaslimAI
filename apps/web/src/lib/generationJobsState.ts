import type { GenerationJob, GenerationJobStatus } from "./api";

export type GenerationJobsState = {
  jobs: GenerationJob[];
  selectedJobId: string | null;
  error: string;
};

export const terminalJobStatuses: ReadonlySet<GenerationJobStatus> = new Set(["Succeeded", "Failed", "Cancelled"]);
const activeJobStatuses: ReadonlySet<GenerationJobStatus> = new Set(["Pending", "Queued", "Running"]);

export function isTerminalJob(job: GenerationJob | null | undefined) {
  // Fail closed when a newer server status reaches this older client: do not
  // poll forever or offer cancellation for a state the client cannot interpret.
  return Boolean(job && !activeJobStatuses.has(job.status));
}

export function mergeGenerationJob(state: GenerationJobsState, job: GenerationJob): GenerationJobsState {
  return {
    ...state,
    jobs: [job, ...state.jobs.filter((item) => item.id !== job.id)],
    selectedJobId: job.id,
  };
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
