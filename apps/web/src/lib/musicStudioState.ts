import type { GenerationJob, MusicJobResult } from "./api";

export function isMusicJob(job: GenerationJob | null): boolean {
  return job?.jobType === "music.generate";
}

export function isMusicProviderUnavailable(job: GenerationJob | null): boolean {
  return job?.status === "Failed" && job.errorCode === "MUSIC_PROVIDER_UNAVAILABLE";
}

export function isMusicRetrievalFailure(job: GenerationJob | null): boolean {
  return (job?.status === "Failed" && ["MUSIC_OUTPUT_RETRIEVAL_FAILED", "MUSIC_RETRIEVAL_RETRY_BLOCKED"].includes(job.errorCode ?? ""))
    || (job?.status === "Cancelled" && job.errorCode === "MUSIC_CANCELLED_UNDELIVERED_OUTPUT");
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
// F-36: a cancellation is only a safe restart when it happened BEFORE the provider accepted the paid
// generation. MUSIC_CANCELLED is that case and keeps the retry affordance. The two codes below record a
// cancellation of an ACCEPTED submission, where nothing on the provider side was cancelled: either the
// first attempt may still be running and billing (MUSIC_CANCELLED_UNCONFIRMED_SUBMISSION) or it finished
// and only delivery was lost (MUSIC_CANCELLED_UNDELIVERED_OUTPUT). The retry endpoint refuses both (see
// GenerationJobExecution.RetryAsync), so the panel must not offer an action that can only fail.
const MUSIC_CANCELLED_UNRESOLVED_CODES = ["MUSIC_CANCELLED_UNCONFIRMED_SUBMISSION", "MUSIC_CANCELLED_UNDELIVERED_OUTPUT"];
export function isMusicCancellationUnresolved(job: GenerationJob | null): boolean {
  return job?.status === "Cancelled" && !!job.errorCode && MUSIC_CANCELLED_UNRESOLVED_CODES.includes(job.errorCode);
}

/**
 * Single source of truth for the music failure panel. `allowRetry` is false for any
 * unresolved outcome - an accepted submission whose result was never observed, whether polling lost it
 * (F-35) or the member cancelled it after acceptance (F-36) - because starting another generation is a second billable
 * submission, not a recovery. Retrieval failures also keep the generic new-generation retry hidden, but
 * offer a recheck of the same job. The text must never claim an unobserved outcome.
 */
export function musicStudioFailureCopy(
  job: GenerationJob | null,
  t: (key: string) => string,
): { title: string; text: string; allowRetry: boolean; allowRecheck: boolean } {
  if (job?.status === "Cancelled") {
    if (job.errorCode === "MUSIC_CANCELLED_UNCONFIRMED_SUBMISSION") return { title: t("music.cancelledUnconfirmedTitle"), text: t("music.cancelledUnconfirmedText"), allowRetry: false, allowRecheck: true };
    if (job.errorCode === "MUSIC_CANCELLED_UNDELIVERED_OUTPUT") return { title: t("music.cancelledUndeliveredTitle"), text: t("music.cancelledUndeliveredText"), allowRetry: false, allowRecheck: true };
    return { title: t("music.cancelledTitle"), text: t("music.cancelledText"), allowRetry: true, allowRecheck: false };
  }
  if (isMusicRetrievalFailure(job)) return {
    title: t("music.failureTitle"),
    text: job?.errorMessage?.trim() || "The finished track could not be delivered. Recheck this original job or contact support; starting a new generation may create a duplicate.",
    allowRetry: false,
    allowRecheck: true,
  };
  if (isMusicProviderUnavailable(job)) return { title: t("music.providerUnavailableTitle"), text: t("music.providerUnavailableText"), allowRetry: true, allowRecheck: false };
  if (isMusicOutcomeUnconfirmed(job)) return { title: t("music.unconfirmedTitle"), text: t("music.unconfirmedText"), allowRetry: false, allowRecheck: true };
  return { title: t("music.failureTitle"), text: t("music.failureText"), allowRetry: true, allowRecheck: false };
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
