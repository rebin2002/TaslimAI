import { describe, expect, it } from "vitest";
import type { GenerationJob } from "./api";
import { canCancelGenerationJob, isTerminalJob, mergeGenerationJob, safeGenerationJobDisplay, type GenerationJobsState } from "./generationJobsState";

function job(overrides: Partial<GenerationJob> = {}): GenerationJob {
  return {
    id: "job-1", workspaceId: "workspace-1", projectId: null, jobType: "system.test", status: "Queued", title: "Test", progressPercent: 0,
    resultJson: null, errorCode: null, errorMessage: null, cancellationRequested: false, createdAt: "2026-01-01T00:00:00Z", queuedAt: "2026-01-01T00:00:00Z",
    startedAt: null, completedAt: null, failedAt: null, cancelledAt: null, outputs: [], ...overrides,
  };
}

describe("generation job UI state", () => {
  it("merges create and polling updates while keeping the selected job", () => {
    let state: GenerationJobsState = { jobs: [], selectedJobId: null, error: "" };
    state = mergeGenerationJob(state, job());
    state = mergeGenerationJob(state, job({ status: "Running", progressPercent: 40 }));
    state = mergeGenerationJob(state, job({ status: "Succeeded", progressPercent: 100, resultJson: '{"message":"done"}' }));
    expect(state.jobs).toHaveLength(1);
    expect(state.selectedJobId).toBe("job-1");
    expect(state.jobs[0]).toMatchObject({ status: "Succeeded", progressPercent: 100, resultJson: '{"message":"done"}' });
  });

  it("allows cancellation only before terminal states", () => {
    expect(canCancelGenerationJob(job({ status: "Queued" }))).toBe(true);
    expect(canCancelGenerationJob(job({ status: "Running" }))).toBe(true);
    expect(canCancelGenerationJob(job({ status: "Cancelled" }))).toBe(false);
    expect(canCancelGenerationJob(job({ status: "Failed" }))).toBe(false);
    expect(isTerminalJob(job({ status: "Succeeded" }))).toBe(true);
  });

  it("preserves safe error display without exposing provider or model fields", () => {
    const failed = job({ status: "Failed", errorCode: "JOB_EXECUTION_FAILED", errorMessage: "The job could not be completed." });
    const display = safeGenerationJobDisplay(failed);
    expect(display).toMatchObject({ status: "Failed", errorMessage: "The job could not be completed." });
    expect(display).not.toHaveProperty("provider");
    expect(display).not.toHaveProperty("providerModel");
  });
});
