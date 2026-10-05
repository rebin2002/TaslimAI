import { afterEach, describe, expect, it, vi } from "vitest";
import type { GenerationJob } from "./api";
import { canCancelImageJob, clearImageActiveJobId, imageActiveJobStorageKey, isImageJob, isImageTerminal, isRestorableImageJob, nextImagePollDelay, parseImageJobResult, persistImageActiveJobId, readImageActiveJobId, safeImageJobView, shouldPollImageJob } from "./imageStudioState";

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

  it("retries transient polling failures with bounded backoff and stops at terminal states", () => {
    expect(isImageTerminal(job({ status: "Running" }))).toBe(false);
    expect(shouldPollImageJob(job({ status: "Running" }))).toBe(true);
    expect(nextImagePollDelay(job({ status: "Running" }), 0)).toBe(650);
    expect(nextImagePollDelay(job({ status: "Running" }), 2)).toBe(1_950);
    expect(nextImagePollDelay(job({ status: "Running" }), 99)).toBe(2_800);
    expect(isImageTerminal(job({ status: "Succeeded" }))).toBe(true);
    expect(shouldPollImageJob(job({ status: "Succeeded" }))).toBe(false);
    expect(nextImagePollDelay(job({ status: "Succeeded" }), 0)).toBeNull();
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

  it("persists the active job by workspace and clears stale pointers", () => {
    const values = new Map<string, string>();
    vi.stubGlobal("window", { sessionStorage: { getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => values.set(key, value), removeItem: (key: string) => values.delete(key) } });
    persistImageActiveJobId("workspace-1", "image-job-1");
    expect(values.get(imageActiveJobStorageKey("workspace-1"))).toBe("image-job-1");
    expect(readImageActiveJobId("workspace-1")).toBe("image-job-1");
    expect(readImageActiveJobId("workspace-2")).toBeNull();
    clearImageActiveJobId("workspace-1");
    expect(readImageActiveJobId("workspace-1")).toBeNull();
  });
});
