import type { GenerationJob, ImageJobResult } from "./api";

const terminalStatuses = new Set<GenerationJob["status"]>(["Succeeded", "Failed", "Cancelled"]);
const activeJobStoragePrefix = "taslim:image-generation:";
export const imageJobPollIntervalMs = 2_000;
export const imageJobPollMaxDelayMs = 8_000;
// A persisted active-job pointer is adopted with a single read today. The generation it names is
// usually still running server-side when that read fails, so the read gets its own small, bounded
// budget instead of silently dropping the job from view.
export const imageRestoreMaxAttempts = 3;

export function imageActiveJobStorageKey(userId: string, workspaceId: string) {
  return `${activeJobStoragePrefix}${encodeURIComponent(userId)}:${encodeURIComponent(workspaceId)}`;
}

export function readImageActiveJobId(userId: string, workspaceId: string) {
  if (typeof window === "undefined" || !userId.trim() || !workspaceId.trim()) return null;
  try {
    const jobId = window.sessionStorage.getItem(imageActiveJobStorageKey(userId, workspaceId));
    return jobId?.trim() || null;
  } catch {
    return null;
  }
}

export function persistImageActiveJobId(userId: string, workspaceId: string, jobId: string) {
  if (typeof window === "undefined" || !userId.trim() || !workspaceId.trim() || !jobId.trim()) return;
  try { window.sessionStorage.setItem(imageActiveJobStorageKey(userId, workspaceId), jobId); } catch { /* Storage may be unavailable. */ }
}

export function clearImageActiveJobId(userId: string, workspaceId: string) {
  if (typeof window === "undefined" || !userId.trim() || !workspaceId.trim()) return;
  try { window.sessionStorage.removeItem(imageActiveJobStorageKey(userId, workspaceId)); } catch { /* Storage may be unavailable. */ }
}

export function isImageJob(job: GenerationJob | null): boolean {
  return job?.jobType === "image.generate";
}

export function isRestorableImageJob(job: GenerationJob | null, workspaceId: string): boolean {
  return !!job && job.workspaceId === workspaceId && isImageJob(job);
}

export function shouldResetImageWorkspaceState(job: GenerationJob | null, workspaceId: string | null): boolean {
  return !!job && (!workspaceId || !isRestorableImageJob(job, workspaceId));
}

export function isImageTerminal(job: GenerationJob | null) {
  return !!job && terminalStatuses.has(job.status);
}

export function shouldPollImageJob(job: GenerationJob | null) {
  return !!job && !isImageTerminal(job);
}

export function shouldClearImageJobAfterPollError(status: number | null | undefined) {
  return status === 401 || status === 403 || status === 404;
}

/**
 * Reconciles a freshly read job snapshot with the one the studio already renders.
 *
 * Three independent readers write the active job: the bounded poll, the read-back that follows a
 * cancellation request, and the restore that adopts a persisted pointer. Their responses settle in
 * no particular order, so a late frame must never undo a newer decision — that would hand the user
 * back an in-progress canvas for a generation they already cancelled, and quietly drop the terminal
 * failure state together with the only accessible retry affordance for it.
 */
export function mergeImageJobUpdate(previous: GenerationJob | null, incoming: GenerationJob): GenerationJob {
  if (!previous || previous.id !== incoming.id) return incoming;
  // The first settled frame is the durable boundary for this job: a contradictory or merely
  // older read must not remove a rendered result or the failure canvas that replaces it.
  if (isImageTerminal(previous)) return previous;
  // Only a non-terminal frame can lose the cancellation the user already requested. The API's
  // immediate path settles a Pending/Queued job straight to Cancelled and never sets the flag,
  // so the authoritative terminal read always wins over the request.
  if (previous.cancellationRequested && !incoming.cancellationRequested && !isImageTerminal(incoming)) return previous;
  return incoming;
}

export function nextImageRestoreDelay(failedAttempt: number): number {
  return Math.min(imageJobPollIntervalMs * Math.max(1, failedAttempt + 1), imageJobPollMaxDelayMs);
}

/**
 * Retries a restore only while the pointer is still credible. An inaccessible job stays on the
 * orphaned-pointer path, which clears the pointer instead of asking for it again.
 */
export function shouldRetryImageRestore(status: number | null | undefined, failedAttempt: number): boolean {
  if (shouldClearImageJobAfterPollError(status)) return false;
  return failedAttempt + 1 < imageRestoreMaxAttempts;
}

export function nextImagePollDelay(job: GenerationJob | null, retryAttempt = 0) {
  if (!shouldPollImageJob(job)) return null;
  return Math.min(imageJobPollIntervalMs * Math.max(1, retryAttempt + 1), imageJobPollMaxDelayMs);
}

export function canCancelImageJob(job: GenerationJob | null): boolean {
  return !!job && !isImageTerminal(job) && !job.cancellationRequested;
}

export function parseImageJobResult(job: GenerationJob | null): ImageJobResult | null {
  if (!job?.resultJson) return null;
  try {
    const parsed = JSON.parse(job.resultJson) as ImageJobResult;
    if (typeof parsed !== "object" || parsed === null || typeof parsed.assetId !== "string") return null;
    return {
      assetId: parsed.assetId,
      assetType: parsed.assetType === "image" ? "image" : undefined,
      format: typeof parsed.format === "string" ? parsed.format : undefined,
      width: typeof parsed.width === "number" ? parsed.width : null,
      height: typeof parsed.height === "number" ? parsed.height : null,
      aspectRatio: typeof parsed.aspectRatio === "string" ? parsed.aspectRatio : undefined,
      quality: typeof parsed.quality === "string" ? parsed.quality : undefined,
    };
  } catch {
    return null;
  }
}

export function safeImageJobView(job: GenerationJob | null) {
  if (!job) return null;
  return {
    id: job.id,
    status: job.status,
    progressPercent: job.progressPercent,
    result: parseImageJobResult(job),
    errorCode: job.errorCode,
    errorMessage: job.errorMessage,
  };
}
