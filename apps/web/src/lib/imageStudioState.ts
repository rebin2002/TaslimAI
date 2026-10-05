import type { GenerationJob, ImageJobResult } from "./api";

const terminalStatuses = new Set<GenerationJob["status"]>(["Succeeded", "Failed", "Cancelled"]);
const activeJobStoragePrefix = "taslim:image-generation:";

export function imageActiveJobStorageKey(workspaceId: string) {
  return `${activeJobStoragePrefix}${workspaceId}`;
}

export function readImageActiveJobId(workspaceId: string) {
  if (typeof window === "undefined") return null;
  try {
    const jobId = window.sessionStorage.getItem(imageActiveJobStorageKey(workspaceId));
    return jobId?.trim() || null;
  } catch {
    return null;
  }
}

export function persistImageActiveJobId(workspaceId: string, jobId: string) {
  if (typeof window === "undefined" || !jobId.trim()) return;
  try { window.sessionStorage.setItem(imageActiveJobStorageKey(workspaceId), jobId); } catch { /* Storage may be unavailable. */ }
}

export function clearImageActiveJobId(workspaceId: string) {
  if (typeof window === "undefined") return;
  try { window.sessionStorage.removeItem(imageActiveJobStorageKey(workspaceId)); } catch { /* Storage may be unavailable. */ }
}

export function isImageJob(job: GenerationJob | null): boolean {
  return job?.jobType === "image.generate";
}

export function isRestorableImageJob(job: GenerationJob | null, workspaceId: string): boolean {
  return !!job && job.workspaceId === workspaceId && isImageJob(job);
}

export function isImageTerminal(job: GenerationJob | null) {
  return !!job && terminalStatuses.has(job.status);
}

export function shouldPollImageJob(job: GenerationJob | null) {
  return !!job && !isImageTerminal(job);
}

export function nextImagePollDelay(job: GenerationJob | null, retryAttempt = 0) {
  if (!shouldPollImageJob(job)) return null;
  return Math.min(650 * Math.max(1, retryAttempt + 1), 2_800);
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
