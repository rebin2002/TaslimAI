import type { Asset, GenerationJob, VoiceJobResult } from "./api";

const terminalStatuses = new Set<GenerationJob["status"]>(["Succeeded", "Failed", "Cancelled"]);
const activeJobStoragePrefix = "taslim:voice-generation:";
export const voiceJobPollIntervalMs = 2_000;
export const voiceJobPollMaxDelayMs = 8_000;

export function voiceActiveJobStorageKey(workspaceId: string): string {
  return `${activeJobStoragePrefix}${workspaceId}`;
}

export function readVoiceActiveJobId(workspaceId: string): string | null {
  if (typeof window === "undefined") return null;
  try {
    const jobId = window.sessionStorage.getItem(voiceActiveJobStorageKey(workspaceId));
    return jobId?.trim() || null;
  } catch {
    return null;
  }
}

export function persistVoiceActiveJobId(workspaceId: string, jobId: string): void {
  if (typeof window === "undefined" || !jobId.trim()) return;
  try {
    window.sessionStorage.setItem(voiceActiveJobStorageKey(workspaceId), jobId);
  } catch {
    // Storage may be unavailable; in-memory polling remains authoritative.
  }
}

export function clearVoiceActiveJobId(workspaceId: string): void {
  if (typeof window === "undefined") return;
  try {
    window.sessionStorage.removeItem(voiceActiveJobStorageKey(workspaceId));
  } catch {
    // Storage may be unavailable.
  }
}

export function isVoiceJob(job: GenerationJob | null): boolean {
  return job?.jobType.trim().toLowerCase() === "voice.generate";
}

export function isRestorableVoiceJob(job: GenerationJob | null, workspaceId: string): boolean {
  return !!job && job.workspaceId === workspaceId && isVoiceJob(job);
}

export function isVoiceTerminal(job: GenerationJob | null): boolean {
  return !!job && terminalStatuses.has(job.status);
}

export function shouldPollVoiceJob(job: GenerationJob | null): boolean {
  return !!job && !isVoiceTerminal(job);
}

export function nextVoicePollDelay(job: GenerationJob | null, retryAttempt = 0): number | null {
  return shouldPollVoiceJob(job) ? Math.min(voiceJobPollIntervalMs * Math.max(1, retryAttempt + 1), voiceJobPollMaxDelayMs) : null;
}

export function isVoiceAsset(asset: Asset): boolean {
  return asset.assetType === "audio" && (asset.sourceStudio?.toLowerCase() === "voice" || asset.sourceJobTitle?.toLowerCase().includes("voice") === true);
}

export function formatVoiceDuration(milliseconds: number | null | undefined): string {
  if (!milliseconds || milliseconds < 0 || !Number.isFinite(milliseconds)) return "0:00";
  const totalSeconds = Math.floor(milliseconds / 1000);
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return `${minutes}:${String(seconds).padStart(2, "0")}`;
}

export function formatVoiceFileSize(bytes: number | null | undefined): string {
  if (!bytes || bytes < 0 || !Number.isFinite(bytes)) return "";
  if (bytes < 1024) return `${bytes} B`;
  const units = ["KB", "MB", "GB"];
  let value = bytes / 1024;
  let unit = units[0];
  for (let index = 0; value >= 1024 && index < units.length - 1; index += 1) {
    value /= 1024;
    unit = units[index + 1];
  }
  return `${value >= 10 ? value.toFixed(0) : value.toFixed(1)} ${unit}`;
}

export function canCancelVoiceJob(job: GenerationJob | null): boolean {
  return !!job && (job.status === "Pending" || job.status === "Queued" || job.status === "Running") && !job.cancellationRequested;
}

export function parseVoiceJobResult(job: GenerationJob | null): VoiceJobResult | null {
  if (!job?.resultJson) return null;
  try {
    const parsed = JSON.parse(job.resultJson) as VoiceJobResult;
    if (typeof parsed !== "object" || parsed === null || typeof parsed.assetId !== "string" || parsed.assetType !== "audio") return null;
    return {
      assetId: parsed.assetId,
      assetType: "audio",
      contentType: typeof parsed.contentType === "string" ? parsed.contentType : undefined,
      format: typeof parsed.format === "string" ? parsed.format : undefined,
      language: typeof parsed.language === "string" ? parsed.language : undefined,
      voiceStyle: typeof parsed.voiceStyle === "string" ? parsed.voiceStyle : undefined,
      speakingStyle: typeof parsed.speakingStyle === "string" ? parsed.speakingStyle : undefined,
      sizeBytes: typeof parsed.sizeBytes === "number" ? parsed.sizeBytes : undefined,
      durationMilliseconds: typeof parsed.durationMilliseconds === "number" ? parsed.durationMilliseconds : null,
      sampleRateHz: typeof parsed.sampleRateHz === "number" ? parsed.sampleRateHz : null,
    };
  } catch {
    return null;
  }
}

export function safeVoiceJobView(job: GenerationJob | null) {
  if (!job) return null;
  return {
    id: job.id,
    status: job.status,
    progressPercent: job.progressPercent,
    result: parseVoiceJobResult(job),
    errorCode: job.errorCode,
    errorMessage: job.errorMessage,
  };
}
