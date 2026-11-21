import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import type { GenerationJob, GenerationJobStatus } from "./api";
import {
  isMovieQuickJobTerminal,
  markMovieQuickJobUnreachable,
  movieQuickPollMaxAttempts,
  movieQuickPollMaxDurationMs,
  movieQuickPollIntervalMs,
  nextMovieQuickPoll,
  shouldSubmitMovieQuickGeneration,
} from "./movieQuickJobPolling";

const pollingSource = readFileSync(new URL("./movieQuickJobPolling.ts", import.meta.url), "utf8");
const studioSource = readFileSync(new URL("../components/MovieStudioView.tsx", import.meta.url), "utf8");

function makeJob(status: GenerationJobStatus): GenerationJob {
  return {
    id: "job-1",
    workspaceId: "ws-1",
    projectId: null,
    jobType: "MovieGeneration",
    status,
    title: "Quick movie",
    progressPercent: 40,
    resultJson: null,
    errorCode: null,
    errorMessage: null,
    cancellationRequested: false,
    createdAt: "2026-11-16T00:00:00.000Z",
    queuedAt: null,
    startedAt: null,
    completedAt: null,
    failedAt: null,
    cancelledAt: null,
    outputs: [],
  };
}

/** Everything the loop needs for a read that came back fine. */
function ok(status: GenerationJobStatus, over: { attempt?: number; elapsedMs?: number; pending?: boolean } = {}) {
  return {
    job: makeJob(status),
    errorStatus: null,
    attempt: over.attempt ?? 1,
    elapsedMs: over.elapsedMs ?? 3_000,
    pending: over.pending ?? false,
  };
}

function failed(errorStatus: number | null, over: { attempt?: number; elapsedMs?: number } = {}) {
  return {
    job: makeJob("Running"),
    errorStatus,
    attempt: over.attempt ?? 1,
    elapsedMs: over.elapsedMs ?? 3_000,
    pending: false,
  };
}

describe("Quick Movie Studio polling stops on a revoked job", () => {
  // The job row is workspace-scoped: a membership change, a transferred workspace or a deleted
  // project makes `/api/generation/jobs/{id}` answer 403 or 404 for a job the view still tracks.
  // None of those become a frame, so continuing to poll is a permanent "Generating" chip.
  it.each([401, 403, 404])("settles the tracked job after HTTP %i instead of polling forever", (errorStatus) => {
    const decision = nextMovieQuickPoll(failed(errorStatus, { attempt: 2, elapsedMs: 12_000 }));
    expect(decision.kind).toBe("stop");
    if (decision.kind !== "stop") return;
    expect(decision.reason).toBe("revoked");
    // An inaccessible job is not going to become accessible by asking harder.
    expect(decision.readBackProject).toBe(false);
  });

  it.each(["Running", "Queued", "Pending"] as const)(
    "turns the tracked %s frame into a terminal one so the chip leaves the in-flight state",
    (status) => {
      const settled = markMovieQuickJobUnreachable(makeJob(status), "revoked");
      expect(settled.status).toBe("Failed");
      expect(isMovieQuickJobTerminal(settled)).toBe(true);
      // The settle re-labels the job the view already has; it never invents a replacement.
      expect(settled.id).toBe("job-1");
      expect(settled.errorCode).toBe("MOVIE_JOB_REVOKED");
    },
  );

  it("keeps the reason code stable and never echoes transport text into the rendered job", () => {
    // The status chip path renders the frame, so a raw HTTP body would reach the DOM unlocalized.
    const revoked = markMovieQuickJobUnreachable(makeJob("Running"), "revoked");
    expect(revoked.errorMessage).toBeNull();
    expect(revoked.errorCode).toBe("MOVIE_JOB_REVOKED");
    const timedOut = markMovieQuickJobUnreachable(makeJob("Running"), "timeout");
    expect(timedOut.errorCode).toBe("MOVIE_JOB_POLL_TIMEOUT");
    expect(timedOut.errorMessage).toBeNull();
  });

  it("stops the loop as soon as a frame is marked unreachable", () => {
    const settled = markMovieQuickJobUnreachable(makeJob("Running"), "revoked");
    const decision = nextMovieQuickPoll({ ...ok("Failed"), job: settled });
    expect(decision.kind).toBe("stop");
  });
});

describe("Quick Movie Studio polling stops on a stalled job", () => {
  it("bounds one tracked job by wall-clock time", () => {
    expect(Number.isFinite(movieQuickPollMaxDurationMs)).toBe(true);
    const decision = nextMovieQuickPoll(failed(null, { attempt: 5, elapsedMs: movieQuickPollMaxDurationMs + 1 }));
    expect(decision.kind).toBe("stop");
    if (decision.kind !== "stop") return;
    expect(decision.reason).toBe("timeout");
  });

  it("bounds one tracked job by detail-read count", () => {
    expect(Number.isFinite(movieQuickPollMaxAttempts)).toBe(true);
    const decision = nextMovieQuickPoll(ok("Running", { attempt: movieQuickPollMaxAttempts, elapsedMs: 1_000 }));
    expect(decision.kind).toBe("stop");
    if (decision.kind !== "stop") return;
    expect(decision.reason).toBe("timeout");
  });

  it("reads the project back once on timeout so a late worker result still surfaces", () => {
    const decision = nextMovieQuickPoll(failed(null, { attempt: 999, elapsedMs: movieQuickPollMaxDurationMs + 1 }));
    expect(decision.kind === "stop" && decision.readBackProject).toBe(true);
  });

  it("keeps polling while both budgets still have room", () => {
    const decision = nextMovieQuickPoll(ok("Running", { attempt: 3, elapsedMs: 9_000, pending: false }));
    expect(decision).toEqual({ kind: "poll", delayMs: movieQuickPollIntervalMs });
  });
});

describe("Quick Movie Studio polling keeps transient failures transient", () => {
  // A dropped connection or a 5xx from the API is not a revocation: ending the job here would
  // tell the user their generation failed while the worker is still producing it.
  it.each([0, 408, 429, 500, 502, 503, 504])("retries HTTP %i at the normal cadence", (errorStatus) => {
    const decision = nextMovieQuickPoll(failed(errorStatus, { attempt: 2, elapsedMs: 6_000 }));
    expect(decision.kind).toBe("poll");
    if (decision.kind !== "poll") return;
    expect(decision.delayMs).toBe(movieQuickPollIntervalMs);
  });

  it("backs off after a failed read instead of hammering the authenticated endpoint", () => {
    const first = nextMovieQuickPoll(failed(503, { attempt: 1, elapsedMs: 3_000 }));
    const later = nextMovieQuickPoll(failed(503, { attempt: 3, elapsedMs: 9_000 }));
    expect(first.kind).toBe("poll");
    expect(later.kind).toBe("poll");
    if (first.kind !== "poll" || later.kind !== "poll") return;
    expect(later.delayMs).toBeGreaterThan(first.delayMs);
    expect(later.delayMs).toBeLessThanOrEqual(15_000);
  });

  it("does not let the retry backoff outlive the deadline", () => {
    const remaining = 4_000;
    const decision = nextMovieQuickPoll(
      failed(503, { attempt: 12, elapsedMs: movieQuickPollMaxDurationMs - remaining }),
    );
    expect(decision.kind).toBe("poll");
    if (decision.kind !== "poll") return;
    expect(decision.delayMs).toBeLessThanOrEqual(remaining);
  });
});

describe("Quick Movie Studio polling never submits the provider twice", () => {
  it("refuses a second submission while an unsettled job is tracked", () => {
    expect(shouldSubmitMovieQuickGeneration(false, makeJob("Running"))).toBe(false);
    expect(shouldSubmitMovieQuickGeneration(false, makeJob("Queued"))).toBe(false);
    expect(shouldSubmitMovieQuickGeneration(false, makeJob("Pending"))).toBe(false);
  });

  it("refuses a second submission while the first POST is still in flight", () => {
    expect(shouldSubmitMovieQuickGeneration(true, null)).toBe(false);
  });

  it("allows a submission again once the tracked job is terminal or gone", () => {
    expect(shouldSubmitMovieQuickGeneration(false, makeJob("Succeeded"))).toBe(true);
    expect(shouldSubmitMovieQuickGeneration(false, makeJob("Failed"))).toBe(true);
    expect(shouldSubmitMovieQuickGeneration(false, makeJob("Cancelled"))).toBe(true);
    expect(shouldSubmitMovieQuickGeneration(false, null)).toBe(true);
  });

  it("offers no retry action that would re-run the provider", () => {
    // Recovery is a re-read of the same job id, never a new generation. A "submit"/"create"/
    // "retry-generation" decision kind would be the duplicate-provider-submission door.
    expect(pollingSource).not.toMatch(/kind:\s*"(submit|create|resubmit|regenerate)"/);
    expect(pollingSource).not.toContain("createMovieProject");
    // The policy may read the job types but must not hold the client, or it could reach the API
    // on its own instead of only answering what the view asks it.
    expect(pollingSource).toMatch(/^import type \{[^}]*\} from "\.\/api";$/m);
    expect(pollingSource).not.toMatch(/\bapi\.(get|list|create|cancel|delete|patch|post)/);
  });

  it("coalesces reads so a slow response cannot stack a second request for the same job", () => {
    const decision = nextMovieQuickPoll(ok("Running", { attempt: 4, elapsedMs: 12_000, pending: true }));
    expect(decision.kind).toBe("wait");
  });

  it("routes the studio poll through the policy instead of swallowing the rejection", () => {
    expect(studioSource).toContain("nextMovieQuickPoll(");
    expect(studioSource).toContain("markMovieQuickJobUnreachable(");
    expect(studioSource).toContain("shouldSubmitMovieQuickGeneration(");
    // The defect: the job read ended in `.catch(() => undefined)`, so 403/404/5xx were identical
    // to success-with-no-new-frame and the interval never stopped.
    expect(studioSource).not.toMatch(/getGenerationJob[\s\S]{0,400}\.catch\(\(\) => undefined\)/);
  });

  it("stays a pure schedule with no clock or randomness of its own", () => {
    expect(pollingSource).not.toMatch(/Date\.now|performance\.now|Math\.random|setTimeout|setInterval/);
  });
});
