import type { GenerationJob, MusicJobResult } from "./api";

export function isMusicJob(job: GenerationJob | null): boolean {
  return job?.jobType === "music.generate";
}

export function isMusicProviderUnavailable(job: GenerationJob | null): boolean {
  return job?.status === "Failed" && job.errorCode === "MUSIC_PROVIDER_UNAVAILABLE";
}

export function canCancelMusicJob(job: GenerationJob | null): boolean {
  return !!job && (job.status === "Pending" || job.status === "Queued" || job.status === "Running") && !job.cancellationRequested;
}

export function parseMusicJobResult(job: GenerationJob | null): MusicJobResult | null {
  if (!job?.resultJson) return null;
  try {
    const parsed = JSON.parse(job.resultJson) as MusicJobResult;
    if (typeof parsed !== "object" || parsed === null || typeof parsed.assetId !== "string") return null;
    return {
      assetId: parsed.assetId,
      assetType: parsed.assetType === "music" ? "music" : undefined,
      title: typeof parsed.title === "string" ? parsed.title : undefined,
      format: typeof parsed.format === "string" ? parsed.format : undefined,
      durationSeconds: typeof parsed.durationSeconds === "number" ? parsed.durationSeconds : null,
      vocalPreference: typeof parsed.vocalPreference === "string" ? parsed.vocalPreference : undefined,
      language: typeof parsed.language === "string" ? parsed.language : undefined,
    };
  } catch {
    return null;
  }
}

export function safeMusicJobView(job: GenerationJob | null) {
  if (!job) return null;
  return {
    id: job.id,
    status: job.status,
    progressPercent: job.progressPercent,
    result: parseMusicJobResult(job),
    errorCode: job.errorCode,
    errorMessage: job.errorMessage,
  };
}
