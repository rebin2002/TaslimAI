import type { GenerationJob, VoiceJobResult } from "./api";

export function isVoiceJob(job: GenerationJob | null): boolean {
  return job?.jobType === "voice.generate";
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
