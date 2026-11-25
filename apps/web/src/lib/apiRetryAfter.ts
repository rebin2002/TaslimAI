/**
 * Retry-After handling for the browser API client.
 *
 * The API answers a rejected throttle with `429` and a `RATE_LIMITED` envelope that
 * carries an RFC 9110 `Retry-After` hint, and an upstream that could not be reached
 * is reported as `503`. The hint is the server's own answer to "when may this exact
 * request run again?", so a client that ignores it either gives up on a read that was
 * always going to succeed or resubmits a request that already created state.
 *
 * This module keeps the three properties that make a repeat safe:
 *   1. only an idempotent read is repeated, and never with a body;
 *   2. the delay is the advertised one, never an earlier guess;
 *   3. the wait is bounded and cancellable, so a throttled hint cannot strand a view.
 */

/** The longest a hint may be trusted as an instruction, whatever the client does with it. */
export const MAX_ADVERTISED_RETRY_AFTER_SECONDS = 86_400;

/**
 * The longest wait the client is willing to spend inside one read.
 *
 * A hint past this budget is reported to the caller instead of awaited: a pending
 * promise that a view cannot cancel is a worse user state than the error the caller
 * already knows how to render, and the caller's own retry affordance is the right
 * place to act on a delay this long.
 */
export const MAX_THROTTLE_RETRY_WAIT_MS = 10_000;

/** Statuses whose `Retry-After` header means "this request may be repeated later". */
export const throttleRetryStatuses: ReadonlySet<number> = new Set([429, 503]);

const deltaSecondsPattern = /^\d+$/;
// An HTTP-date always opens with the day name in the RFC 9110 IMF-fixdate form, and
// Date.parse accepts far too much else (a bare "0" or "-5" reads as a year).
const httpDatePattern = /^[a-z]{3}, \d{2} [a-z]{3} \d{4} \d{2}:\d{2}:\d{2}/i;

/** Whether this request has no server-visible effect to repeat. */
export function isIdempotentRead(init: RequestInit): boolean {
  if (init.body !== undefined && init.body !== null) return false;
  const method = init.method === undefined || init.method === null ? "GET" : String(init.method).toUpperCase();
  return method === "GET";
}

/** Whether the failure is a throttle the advertised hint applies to. */
export function isThrottleRetryStatus(status: number): boolean {
  return throttleRetryStatuses.has(status);
}

/**
 * Reads a `Retry-After` hint as whole seconds from `nowMs`.
 *
 * Returns null when the header is absent, malformed, or beyond the trusted window:
 * the client must not invent a delay, because a guess is exactly the hammering a
 * throttle exists to prevent.
 */
export function parseRetryAfterSeconds(value: string | null | undefined, nowMs: number): number | null {
  const raw = typeof value === "string" ? value.trim() : "";
  if (raw.length === 0) return null;

  if (deltaSecondsPattern.test(raw)) {
    const seconds = Number(raw);
    return seconds <= MAX_ADVERTISED_RETRY_AFTER_SECONDS ? seconds : null;
  }

  if (!httpDatePattern.test(raw)) return null;
  const targetMs = Date.parse(raw);
  if (Number.isNaN(targetMs)) return null;

  // A date that has already passed is due now, not never.
  const seconds = Math.round((targetMs - nowMs) / 1000);
  if (seconds <= 0) return 0;
  return seconds <= MAX_ADVERTISED_RETRY_AFTER_SECONDS ? seconds : null;
}

/**
 * Waits out an advertised delay, or rejects with the caller's abort reason.
 *
 * A caller that abandons the read (a superseded search, an unmounted view) must not
 * leave a timer behind that later fires another request nobody asked for, so the
 * wait is tied to the same signal that owns the original fetch.
 */
export function waitBeforeRetry(delayMs: number, signal?: AbortSignal | null): Promise<void> {
  if (signal?.aborted) return Promise.reject(signal.reason ?? new DOMException("Aborted", "AbortError"));
  if (delayMs <= 0) return Promise.resolve();

  return new Promise<void>((resolve, reject) => {
    const onAbort = () => {
      clearTimeout(timer);
      reject(signal?.reason ?? new DOMException("Aborted", "AbortError"));
    };
    const timer = setTimeout(() => {
      signal?.removeEventListener("abort", onAbort);
      resolve();
    }, delayMs);
    signal?.addEventListener("abort", onAbort, { once: true });
  });
}
