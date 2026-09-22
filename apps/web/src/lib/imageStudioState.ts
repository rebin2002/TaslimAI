import type { GenerationJob, ImageJobResult } from "./api";

export function isImageJob(job: GenerationJob | null): boolean {
  return job?.jobType === "image.generate";
}

export function canCancelImageJob(job: GenerationJob | null): boolean {
  return !!job && (job.status === "Pending" || job.status === "Queued" || job.status === "Running") && !job.cancellationRequested;
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
