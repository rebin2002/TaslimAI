import type { GenerationJob, GenerationJobStatus } from "./api";
export const terminalJobStatuses: ReadonlySet<GenerationJobStatus> = new Set(["Succeeded", "Failed", "Cancelled"]);

export function isTerminalJob(job: GenerationJob | null | undefined) {
  return Boolean(job && terminalJobStatuses.has(job.status));
}

// The history view reads a single workspace at a time. A response that describes a record from
// another workspace - an in-flight poll, create or cancel that started before the session's
// workspace was reassigned - must not enter the list, because the list is what the user reads as
// the current workspace's history and each row carries the job's result payload. Dropping the
// record keeps paging and the selection consistent with the server-side fence on the list itself.
// A record with no workspace identity at all (a partial or malformed payload) is unreadable: the
// fence is fail-closed, because it cannot be proven to belong to the workspace being read.
export function isJobReadableInWorkspace(job: GenerationJob, activeWorkspaceId: string | null) {
  return Boolean(activeWorkspaceId) && job.workspaceId === activeWorkspaceId;
}

// A mounted account history page can retain its previous workspace's array for one render
// while AuthProvider switches workspaces. Fencing only the detail selection is not enough:
// the recent-jobs list itself contains titles and can expose old-workspace rows.
// Filter at render time, before the next effect/load has a chance to clear stale state.
export function filterGenerationJobsToWorkspace(jobs: readonly GenerationJob[], activeWorkspaceId: string | null): GenerationJob[] {
  if (!activeWorkspaceId) return [];
  return jobs.filter(job => isJobReadableInWorkspace(job, activeWorkspaceId));
}

// A read of the generation history is only meaningful together with the identity of what it read:
// the workspace it describes and the list attempt that produced it. Bundling both into the value
// the page renders from makes the fence structural instead of a cleanup that can be forgotten:
// nothing that is not the newest settled read of the workspace being read can render, so the page
// can neither paint a retired workspace's rows nor present a superseded page of its own workspace
// as though it were current.
export type GenerationJobsRead = {
  workspaceId: string;
  epoch: number;
  jobs: GenerationJob[];
};

// The inspected row is an identifier bound to a workspace, never a cached job record. The detail
// panel renders the row the current read holds for that id, so it cannot drift ahead of - or
// behind - the history list produced by the same read.
export type GenerationJobsSelection = {
  workspaceId: string;
  jobId: string;
};

// A failure belongs to the read that raised it. An alert from a retired read is a message about a
// workspace the user has already left, so it must not be raised over the page they read now.
export type GenerationJobsAlert = {
  workspaceId: string;
  epoch: number;
  message: string;
  retryable: boolean;
};

// A mutation may settle after the page has switched workspaces. Its pending
// indicator belongs only to the workspace/request that issued it; otherwise
// the next workspace keeps its Create/Cancel controls disabled indefinitely.
export type GenerationJobsWork = { workspaceId: string; request: number };
export function isCurrentGenerationJobsWork(
  work: GenerationJobsWork | null,
  activeWorkspaceId: string | null,
  requestGeneration: number,
) {
  return work !== null && activeWorkspaceId !== null
    && work.workspaceId === activeWorkspaceId && work.request === requestGeneration;
}

export function isCurrentGenerationJobsRead(read: GenerationJobsRead | null, activeWorkspaceId: string | null, attemptEpoch: number) {
  return Boolean(activeWorkspaceId) && read !== null && read.workspaceId === activeWorkspaceId && read.epoch === attemptEpoch;
}

export function isCurrentGenerationJobsAlert(alert: GenerationJobsAlert | null, activeWorkspaceId: string | null, attemptEpoch: number) {
  return Boolean(activeWorkspaceId) && alert !== null && alert.workspaceId === activeWorkspaceId && alert.epoch === attemptEpoch;
}

// Rows the page may render: nothing unless the settled read describes the workspace being read,
// and then only rows that themselves belong to it (defence in depth against a payload carrying a
// foreign or identity-less record).
export function readGenerationJobs(read: GenerationJobsRead | null, activeWorkspaceId: string | null, attemptEpoch: number): GenerationJob[] {
  // Stated inline rather than through isCurrentGenerationJobsRead so the narrowings below hold.
  if (read === null || activeWorkspaceId === null) return [];
  if (read.workspaceId !== activeWorkspaceId || read.epoch !== attemptEpoch) return [];
  return filterGenerationJobsToWorkspace(read.jobs, activeWorkspaceId);
}

// Apply a create/poll/cancel record to the read the page holds. A record from another workspace is
// dropped and the read left untouched; a record for the workspace being read is either merged into
// its newest settled read or seeds one, so a job created or polled before the authoritative list
// page has landed is not silently discarded.
export function mergeGenerationJobIntoRead(
  read: GenerationJobsRead | null,
  job: GenerationJob,
  activeWorkspaceId: string | null,
  attemptEpoch: number,
): GenerationJobsRead | null {
  if (activeWorkspaceId === null || job.workspaceId !== activeWorkspaceId) return read;
  if (read !== null && read.workspaceId === activeWorkspaceId && read.epoch === attemptEpoch) {
    return { ...read, jobs: [job, ...read.jobs.filter((item) => item.id !== job.id)] };
  }
  // No settled read for the workspace being read: seed one for the same attempt, so a job that was
  // created or polled before the authoritative page has landed is not lost, and is replaced by that
  // page as soon as it arrives.
  return { workspaceId: activeWorkspaceId, epoch: attemptEpoch, jobs: [job] };
}

export function resolveGenerationJobsDetail({ read, selection, activeWorkspaceId, attemptEpoch }: Readonly<{
  read: GenerationJobsRead | null;
  selection: GenerationJobsSelection | null;
  activeWorkspaceId: string | null;
  attemptEpoch: number;
}>): GenerationJob | null {
  const rows = readGenerationJobs(read, activeWorkspaceId, attemptEpoch);
  if (rows.length === 0) return null;
  if (selection && selection.workspaceId === activeWorkspaceId) {
    // The newest page decides. A selection the current read cannot vouch for renders nothing,
    // rather than falling back to the record the page remembered from a superseded read.
    return rows.find((item) => item.id === selection.jobId) ?? null;
  }
  // A freshly mounted page synchronises with the server's own newest row for this workspace.
  return rows[0];
}

export type GenerationJobsSurface = "loading" | "empty" | "list";

// What the recent-history card may claim. "No test jobs yet" is a statement about the workspace
// being read, so it is only allowed once a read for that workspace has settled. While the first
// page of a newly mounted workspace is still in flight the honest surface is the loading
// placeholder, not an empty history the server never reported. A settled failure counts as settled:
// the page then offers its retry affordance instead of claiming to load forever.
export function describeGenerationJobsSurface({ activeWorkspaceId, read, attemptEpoch, loading, alert }: Readonly<{
  activeWorkspaceId: string | null;
  read: GenerationJobsRead | null;
  attemptEpoch: number;
  loading: boolean;
  alert: GenerationJobsAlert | null;
}>): GenerationJobsSurface {
  if (!activeWorkspaceId) return "empty";
  if (readGenerationJobs(read, activeWorkspaceId, attemptEpoch).length > 0) return "list";
  const failed = isCurrentGenerationJobsAlert(alert, activeWorkspaceId, attemptEpoch);
  if (loading || (!isCurrentGenerationJobsRead(read, activeWorkspaceId, attemptEpoch) && !failed)) return "loading";
  return "empty";
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
