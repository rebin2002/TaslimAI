import { afterEach, describe, expect, it, vi } from "vitest";
import type { GenerationJob } from "./api";
import { canCancelImageJob, clearImageActiveJobId, imageActiveJobStorageKey, imageJobPollIntervalMs, imageJobPollMaxDelayMs, imageRestoreMaxAttempts, isImageJob, isImageTerminal, isRestorableImageJob, mergeImageJobUpdate, nextImagePollDelay, nextImageRestoreDelay, parseImageJobResult, persistImageActiveJobId, readImageActiveJobId, safeImageJobView, shouldClearImageJobAfterPollError, shouldPollImageJob, shouldResetImageWorkspaceState, shouldRetryImageRestore } from "./imageStudioState";

function job(overrides: Partial<GenerationJob> = {}): GenerationJob {
  return {
    id: "image-job-1", workspaceId: "workspace-1", projectId: null, jobType: "image.generate", status: "Running", title: "Hero", progressPercent: 42,
    resultJson: null, errorCode: null, errorMessage: null, cancellationRequested: false, createdAt: "2026-01-01T00:00:00Z", queuedAt: "2026-01-01T00:00:00Z",
    startedAt: "2026-01-01T00:00:01Z", completedAt: null, failedAt: null, cancelledAt: null, outputs: [], ...overrides,
  };
}

describe("image studio state", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("recognizes image jobs and allows cancellation before terminal states", () => {
    expect(isImageJob(job())).toBe(true);
    expect(isRestorableImageJob(job(), "workspace-1")).toBe(true);
    expect(isRestorableImageJob(job({ workspaceId: "workspace-2" }), "workspace-1")).toBe(false);
    expect(isRestorableImageJob(job({ jobType: "social.generate" }), "workspace-1")).toBe(false);
    expect(canCancelImageJob(job({ status: "Queued" }))).toBe(true);
    expect(canCancelImageJob(job({ status: "Succeeded" }))).toBe(false);
    expect(canCancelImageJob(job({ cancellationRequested: true }))).toBe(false);
  });

  it("resets mounted state when the active workspace no longer owns the image job", () => {
    expect(shouldResetImageWorkspaceState(job(), "workspace-1")).toBe(false);
    expect(shouldResetImageWorkspaceState(job({ workspaceId: "workspace-2" }), "workspace-1")).toBe(true);
    expect(shouldResetImageWorkspaceState(job(), null)).toBe(true);
  });

  it("retries transient polling failures with bounded backoff and stops at terminal states", () => {
    expect(isImageTerminal(job({ status: "Running" }))).toBe(false);
    expect(shouldPollImageJob(job({ status: "Running" }))).toBe(true);
    expect(imageJobPollIntervalMs).toBe(2_000);
    expect(imageJobPollMaxDelayMs).toBe(8_000);
    expect(nextImagePollDelay(job({ status: "Running" }), 0)).toBe(2_000);
    expect(nextImagePollDelay(job({ status: "Running" }), 2)).toBe(6_000);
    expect(nextImagePollDelay(job({ status: "Running" }), 99)).toBe(8_000);
    expect(isImageTerminal(job({ status: "Succeeded" }))).toBe(true);
    expect(shouldPollImageJob(job({ status: "Succeeded" }))).toBe(false);
    expect(nextImagePollDelay(job({ status: "Succeeded" }), 0)).toBeNull();
  });

  it("treats inaccessible active jobs as orphaned instead of retrying forever", () => {
    expect(shouldClearImageJobAfterPollError(401)).toBe(true);
    expect(shouldClearImageJobAfterPollError(403)).toBe(true);
    expect(shouldClearImageJobAfterPollError(404)).toBe(true);
    expect(shouldClearImageJobAfterPollError(408)).toBe(false);
    expect(shouldClearImageJobAfterPollError(500)).toBe(false);
    expect(shouldClearImageJobAfterPollError(null)).toBe(false);
  });

  // The studio writes the active job from three independent sources: the poll timer, the
  // read-back that follows a cancellation request, and the restore that adopts a persisted job.
  // Their responses can settle in any order, so the merge below is what keeps the visible job
  // state monotonic instead of letting a late frame undo a newer decision.
  it("never lets a late non-terminal frame resurrect a settled job", () => {
    const settled = job({ status: "Cancelled", progressPercent: 100, cancelledAt: "2026-01-01T00:00:09Z" });
    const stalePoll = job({ status: "Running", progressPercent: 42 });
    expect(mergeImageJobUpdate(settled, stalePoll)).toBe(settled);
    expect(isImageTerminal(mergeImageJobUpdate(settled, stalePoll))).toBe(true);
    // A cancellation that settled immediately also settles the persisted pointer's job.
    expect(mergeImageJobUpdate(job({ status: "Succeeded", progressPercent: 100 }), stalePoll).status).toBe("Succeeded");
    expect(mergeImageJobUpdate(job({ status: "Failed", errorCode: "IMAGE_GENERATION_FAILED" }), stalePoll).status).toBe("Failed");
  });

  it("treats the first settled terminal frame as durable against a contradictory late frame", () => {
    const success = job({ status: "Succeeded", progressPercent: 100, resultJson: '{"assetId":"asset-1"}' });
    // A stored result must not be yanked away by a late Failed frame for the same job id.
    expect(mergeImageJobUpdate(success, job({ status: "Failed", errorCode: "IMAGE_GENERATION_FAILED" }))).toBe(success);
    // Re-reading the same settled snapshot keeps the rendered result stable.
    expect(mergeImageJobUpdate(success, job({ status: "Succeeded", progressPercent: 100, resultJson: '{"assetId":"asset-1"}' }))).toBe(success);
    // The same job can still move forward while it is active.
    expect(mergeImageJobUpdate(job({ status: "Queued", progressPercent: 0 }), job({ status: "Running", progressPercent: 10 })).status).toBe("Running");
  });

  it("keeps a requested cancellation across a stale frame without blocking its settlement", () => {
    const requested = job({ status: "Running", cancellationRequested: true });
    const beforeRequest = job({ status: "Running", cancellationRequested: false, progressPercent: 30 });
    // The worker has not drained yet: the request must stay visible so the cancel control and
    // its announcement do not flicker back to a plain in-progress job.
    expect(mergeImageJobUpdate(requested, beforeRequest)).toBe(requested);
    expect(canCancelImageJob(mergeImageJobUpdate(requested, beforeRequest))).toBe(false);
    // The API's immediate path flips a Pending/Queued job straight to Cancelled and leaves the
    // request flag unset, so the authoritative terminal frame must always win.
    expect(mergeImageJobUpdate(requested, job({ status: "Cancelled", cancelledAt: "2026-01-01T00:00:09Z" })).status).toBe("Cancelled");
  });

  it("replaces the snapshot when the studio adopts a different job", () => {
    const previous = job({ id: "image-job-1", status: "Succeeded", resultJson: '{"assetId":"asset-1"}' });
    const retry = job({ id: "image-job-2", status: "Queued", progressPercent: 0, resultJson: null });
    expect(mergeImageJobUpdate(previous, retry)).toBe(retry);
    expect(mergeImageJobUpdate(null, retry)).toBe(retry);
  });

  it("bounds restore retries on transient failures and never retries an orphaned pointer", () => {
    // A persisted active-job pointer is adopted with one plain read today: a transport or 5xx
    // failure leaves the in-progress generation unexplained with no retry and no announced error.
    expect(shouldRetryImageRestore(500, 0)).toBe(true);
    expect(shouldRetryImageRestore(503, 0)).toBe(true);
    expect(shouldRetryImageRestore(408, 0)).toBe(true);
    expect(shouldRetryImageRestore(null, 0)).toBe(true);
    // Orphaned and revoked pointers are settled by the #268 policy instead of being retried.
    expect(shouldRetryImageRestore(401, 0)).toBe(false);
    expect(shouldRetryImageRestore(403, 0)).toBe(false);
    expect(shouldRetryImageRestore(404, 0)).toBe(false);
    // The retry budget is finite and its backoff stays inside the polling ceiling.
    expect(shouldRetryImageRestore(500, imageRestoreMaxAttempts - 1)).toBe(false);
    expect(nextImageRestoreDelay(0)).toBe(imageJobPollIntervalMs);
    expect(nextImageRestoreDelay(imageRestoreMaxAttempts - 1)).toBeLessThanOrEqual(imageJobPollMaxDelayMs);
  });

  it("parses only the safe Asset result fields", () => {
    const result = parseImageJobResult(job({ status: "Succeeded", progressPercent: 100, resultJson: '{"assetId":"asset-1","format":"png","width":1024,"height":1024,"provider":"openai","providerModel":"secret"}' }));
    expect(result).toMatchObject({ assetId: "asset-1", format: "png", width: 1024, height: 1024 });
    expect(result).not.toHaveProperty("provider");
    expect(result).not.toHaveProperty("providerModel");
  });

  it("exposes a safe failed-job view and ignores malformed result JSON", () => {
    const failed = job({ status: "Failed", errorCode: "IMAGE_PROVIDER_UNAVAILABLE", errorMessage: "Image generation is temporarily unavailable." });
    expect(safeImageJobView(failed)).toMatchObject({ status: "Failed", errorCode: "IMAGE_PROVIDER_UNAVAILABLE" });
    expect(parseImageJobResult(job({ status: "Succeeded", resultJson: "not-json" }))).toBeNull();
  });

  it("isolates active jobs by user and workspace without importing legacy pointers", () => {
    const values = new Map<string, string>();
    vi.stubGlobal("window", { sessionStorage: { getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => values.set(key, value), removeItem: (key: string) => values.delete(key) } });
    persistImageActiveJobId("user-A", "workspace-1", "image-job-1");
    expect(values.get(imageActiveJobStorageKey("user-A", "workspace-1"))).toBe("image-job-1");
    expect(readImageActiveJobId("user-A", "workspace-1")).toBe("image-job-1");
    expect(readImageActiveJobId("user-B", "workspace-1")).toBeNull();
    expect(readImageActiveJobId("user-A", "workspace-2")).toBeNull();
    expect(readImageActiveJobId("", "workspace-1")).toBeNull();
    values.set("taslim:image-generation:workspace-1", "legacy-job");
    expect(readImageActiveJobId("user-B", "workspace-1")).toBeNull();
    persistImageActiveJobId("", "workspace-1", "invalid");
    expect(values.has(imageActiveJobStorageKey("", "workspace-1"))).toBe(false);
    clearImageActiveJobId("user-A", "workspace-1");
    expect(readImageActiveJobId("user-A", "workspace-1")).toBeNull();
  });
});
