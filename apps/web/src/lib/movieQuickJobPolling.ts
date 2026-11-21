import type { GenerationJob, GenerationJobStatus } from "./api";

/**
 * Polling policy for the Quick Movie Studio result screen.
 *
 * The quick-result view tracks a single generation job: the brief is submitted once and the browser
 * then reads `/api/generation/jobs/{id}` until the job settles. Every input this module needs is
 * passed in by the caller, so the schedule is a pure function of (last frame, transport outcome,
 * attempt count, elapsed time, in-flight flag) and can be driven from a unit test without timers,
 * a fake clock, or a render harness.
 *
 * Three outcomes end a poll, and only three:
 *   1. the job reported a terminal status;
 *   2. the job can no longer report anything, because the detail read answers as if the caller lost
 *      access to it (`revoked`);
 *   3. the job ran out of its time or read budget (`timeout`).
 *
 * Anything else keeps the schedule running. None of them ever re-submits the brief: re-submitting
 * would POST a second project and enqueue a second provider pass for the same creative work, which
 * is both a duplicate charge and a duplicate generation the user did not ask for.
 */

/** Cadence while frames keep arriving. A detail read is an authenticated database round trip. */
export const movieQuickPollIntervalMs = 3_000;

/** Ceiling on the retry backoff, so a struggling API still gets re-checked rather than dropped. */
export const movieQuickPollMaxDelayMs = 15_000;

/** Ceiling on how long one tracked job may keep the browser reading. */
export const movieQuickPollMaxDurationMs = 180_000;

/** Ceiling on detail reads for one tracked job. */
export const movieQuickPollMaxAttempts = 60;

/**
 * Detail-read statuses that mean the job will never produce another frame for this browser.
 *
 * A job row is workspace-scoped, so a membership change, a transferred or deleted workspace, or a
 * project that went out of scope turns `/api/generation/jobs/{id}` into 403 or 404 for a job the
 * view still holds. 401 belongs here too: the session is gone, so the retry would not be
 * authenticated either.
 */
const revokedJobStatuses: ReadonlySet<number> = new Set([401, 403, 404]);

/** A generation job status that still expects another frame from the worker. */
const inFlightStatuses: ReadonlySet<GenerationJobStatus> = new Set(["Pending", "Queued", "Running"]);

/** A generation job status the worker will never move on from. */
const terminalStatuses: ReadonlySet<GenerationJobStatus> = new Set(["Succeeded", "Failed", "Cancelled"]);

/** Stable client-side reason codes; rendered copy comes from the locale catalogs, never from these. */
export const movieQuickUnreachableCodes = {
  revoked: "MOVIE_JOB_REVOKED",
  timeout: "MOVIE_JOB_POLL_TIMEOUT",
} as const;

export function isMovieQuickJobInFlight(job: GenerationJob | null | undefined) {
  return Boolean(job && inFlightStatuses.has(job.status));
}

export function isMovieQuickJobTerminal(job: GenerationJob | null | undefined) {
  return Boolean(job && terminalStatuses.has(job.status));
}

export function isMovieQuickJobRevokedStatus(status: number | null | undefined) {
  return typeof status === "number" && revokedJobStatuses.has(status);
}

export type MovieQuickPollStopReason = "terminal" | "revoked" | "timeout";

export type MovieQuickPollDecision =
  /** Issue the next detail read after `delayMs`. */
  | { kind: "poll"; delayMs: number }
  /** A read is already in flight and owns the next schedule; issue nothing. */
  | { kind: "wait"; delayMs: number }
  /** Stop polling. `readBackProject` says whether the one-off project re-read is still worth it. */
  | { kind: "stop"; reason: MovieQuickPollStopReason; readBackProject: boolean };

export type MovieQuickPollInput = {
  /** The frame the view is currently holding, or null before the first one arrives. */
  job: GenerationJob | null;
  /** HTTP status of the failed detail read, or null when the last read succeeded. */
  errorStatus: number | null;
  /** Number of detail reads already issued for this job. */
  attempt: number;
  /** Milliseconds since the view started tracking this job. */
  elapsedMs: number;
  /** True while a detail read for this job is still in flight. */
  pending: boolean;
};

/**
 * Decides what the quick-result poll does next.
 *
 * The order of the checks is the contract. An access verdict about the job itself outranks the
 * budgets, because a revoked job is settled information rather than a job that ran too long; a
 * budget only speaks for itself once the read is known to be retryable.
 */
export function nextMovieQuickPoll(input: MovieQuickPollInput): MovieQuickPollDecision {
  if (isMovieQuickJobTerminal(input.job)) return { kind: "stop", reason: "terminal", readBackProject: true };
  if (isMovieQuickJobRevokedStatus(input.errorStatus)) return { kind: "stop", reason: "revoked", readBackProject: false };

  if (input.attempt >= movieQuickPollMaxAttempts || input.elapsedMs >= movieQuickPollMaxDurationMs) {
    // The worker may still be finishing while the browser stops asking, so one project read can
    // still recover a result that arrived late.
    return { kind: "stop", reason: "timeout", readBackProject: true };
  }

  if (input.pending) return { kind: "wait", delayMs: movieQuickPollIntervalMs };

  if (input.errorStatus === null) return { kind: "poll", delayMs: movieQuickPollIntervalMs };

  // Every other transport outcome is retryable: a dropped connection (status 0), a 408/429, or a
  // 5xx from the API. Ending the job here would tell the user their generation failed while the
  // worker is still producing it, so the loop keeps going, backed off. Scaling the wait with how
  // long this job has already been polling keeps pressure off an API that is struggling without
  // ever abandoning the job.
  const backoff = movieQuickPollIntervalMs * Math.max(1, input.attempt - 1);
  const delayMs = Math.min(backoff, movieQuickPollMaxDelayMs);
  // Never schedule past the deadline: a tick that lands after it is a read the budget disallows.
  return { kind: "poll", delayMs: Math.min(delayMs, movieQuickPollMaxDurationMs - input.elapsedMs) };
}

/**
 * Settles the tracked view once a detail read can no longer produce a frame.
 *
 * The view renders the frame it holds, so stopping the poll without settling it would leave the
 * status chip on "Generating" for as long as the tab is open, and the only affordance left would be
 * "Create another" — a second provider pass for a brief that may already be rendering. Re-labelling
 * the frame the user already has moves the screen to its existing failure copy and releases the
 * submit guard.
 *
 * The job keeps its identity and only carries a stable reason code: the HTTP status text belongs to
 * the transport, not to a frame that gets rendered, so nothing from the response reaches the DOM.
 */
export function markMovieQuickJobUnreachable(
  job: GenerationJob,
  reason: Exclude<MovieQuickPollStopReason, "terminal">,
): GenerationJob {
  return { ...job, status: "Failed", errorCode: movieQuickUnreachableCodes[reason], errorMessage: null };
}

/**
 * Whether a fresh brief may be submitted.
 *
 * A submission POSTs a new project and enqueues a provider pass, so the answer has to be no while
 * an earlier brief for this view is still generating. Recovery from a revoked or stalled poll is a
 * re-read of the same job id, never a second generation.
 */
export function shouldSubmitMovieQuickGeneration(working: boolean, activeJob: GenerationJob | null) {
  return !working && !isMovieQuickJobInFlight(activeJob);
}
