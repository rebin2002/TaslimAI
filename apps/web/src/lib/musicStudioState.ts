import type { GenerationJob, MusicJobResult } from "./api";

export function isMusicJob(job: GenerationJob | null): boolean {
  return job?.jobType === "music.generate";
}

export function isMusicProviderUnavailable(job: GenerationJob | null): boolean {
  return job?.status === "Failed" && job.errorCode === "MUSIC_PROVIDER_UNAVAILABLE";
}

// The accepted-but-unconfirmed outcome is the one music failure the customer must NOT be
// invited to repeat: the first attempt may already have reached the provider and be billing,
// and the retry endpoint refuses it (GenerationJobExecution.RetryAsync) until that is
// reconciled. The panel therefore states the unknown outcome and hides the action that would
// only fail. Both codes describe the same unresolved state - the terminal one and the refusal
// returned if an older client posts a retry anyway.
const MUSIC_OUTCOME_UNCONFIRMED_CODES = ["MUSIC_PROVIDER_SUBMISSION_UNCONFIRMED", "MUSIC_SUBMISSION_RETRY_BLOCKED"];

export function isMusicOutcomeUnconfirmed(job: GenerationJob | null): boolean {
  return job?.status === "Failed" && !!job.errorCode && MUSIC_OUTCOME_UNCONFIRMED_CODES.includes(job.errorCode);
}

/**
 * Single source of truth for the music failure panel. `allowRetry` is false only for the
 * unconfirmed outcome, because starting another generation there is a second billable
 * submission, not a recovery. The text must never claim that audio was or was not produced
 * when the provider outcome was never observed.
 */
export function musicStudioFailureCopy(
  job: GenerationJob | null,
  t: (key: string) => string,
): { title: string; text: string; allowRetry: boolean } {
  if (job?.status === "Cancelled") return { title: t("music.cancelledTitle"), text: t("music.cancelledText"), allowRetry: true };
  if (isMusicProviderUnavailable(job)) return { title: t("music.providerUnavailableTitle"), text: t("music.providerUnavailableText"), allowRetry: true };
  if (isMusicOutcomeUnconfirmed(job)) return { title: t("music.unconfirmedTitle"), text: t("music.unconfirmedText"), allowRetry: false };
  return { title: t("music.failureTitle"), text: t("music.failureText"), allowRetry: true };
}

export function canCancelMusicJob(job: GenerationJob | null): boolean {
  return !!job && (job.status === "Pending" || job.status === "Queued" || job.status === "Running") && !job.cancellationRequested;
}

export function parseMusicJobResult(job: GenerationJob | null): MusicJobResult | null {
  if (!job?.resultJson) return null;
  try {
    const parsed = JSON.parse(job.resultJson) as MusicJobResult;
    // A successful provider result without a usable asset identity is malformed;
    // reject it here so no result UI state can render a download for an empty id.
    if (typeof parsed !== "object" || parsed === null || typeof parsed.assetId !== "string" || parsed.assetId.trim() === "") return null;
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
