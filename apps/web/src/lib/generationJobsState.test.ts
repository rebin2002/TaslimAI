import { describe, expect, it } from "vitest";
import type { GenerationJob } from "./api";
import { canCancelGenerationJob, isTerminalJob, mergeGenerationJob, safeGenerationJobDisplay, selectActiveGenerationJob, type GenerationJobsState } from "./generationJobsState";

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
    state = mergeGenerationJob(state, job(), "workspace-1");
    state = mergeGenerationJob(state, job({ status: "Running", progressPercent: 40 }), "workspace-1");
    state = mergeGenerationJob(state, job({ status: "Succeeded", progressPercent: 100, resultJson: '{"message":"done"}' }), "workspace-1");
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

// The history list at /account/generation-jobs reads one workspace at a time, but the session's
// personal workspace can be reassigned while the page stays mounted (AuthProvider.refresh()
// publishes a different workspace id). In-flight create/poll/cancel responses still describe the
// previous workspace, so merging them unconditionally writes another workspace's record -
// including its resultJson - into the list the user is now reading.
describe("generation history workspace reassignment fencing", () => {
  const current = "workspace-2";
  const stale = job({ id: "job-old", workspaceId: "workspace-1", title: "Previous workspace job", resultJson: '{"secret":"from workspace-1"}' });

  it("drops a merged job that belongs to a different workspace than the one being read", () => {
    const state: GenerationJobsState = { jobs: [job({ id: "job-new", workspaceId: current })], selectedJobId: "job-new", error: "" };
    const next = mergeGenerationJob(state, stale, current);
    expect(next.jobs.map((item) => item.id)).toEqual(["job-new"]);
    expect(next.selectedJobId).toBe("job-new");
    expect(JSON.stringify(next.jobs)).not.toContain("from workspace-1");
  });

  it("leaves an empty history untouched when the only response is from the previous workspace", () => {
    const state: GenerationJobsState = { jobs: [], selectedJobId: null, error: "" };
    const next = mergeGenerationJob(state, stale, current);
    expect(next.jobs).toHaveLength(0);
    expect(next.selectedJobId).toBeNull();
  });

  it("still merges jobs owned by the workspace the view is reading", () => {
    const state: GenerationJobsState = { jobs: [], selectedJobId: null, error: "" };
    const next = mergeGenerationJob(state, job({ id: "job-new", workspaceId: current }), current);
    expect(next.jobs.map((item) => item.id)).toEqual(["job-new"]);
    expect(next.selectedJobId).toBe("job-new");
  });

  it("never treats a workspace as readable when the session has no workspace", () => {
    const state: GenerationJobsState = { jobs: [], selectedJobId: null, error: "" };
    expect(mergeGenerationJob(state, stale, null).jobs).toHaveLength(0);
  });

  it("selects the current job only while it belongs to the workspace being read", () => {
    const scoped = job({ id: "job-new", workspaceId: current });
    expect(selectActiveGenerationJob({ current: scoped, jobs: [scoped], activeWorkspaceId: current })).toBe(scoped);
    expect(selectActiveGenerationJob({ current: stale, jobs: [scoped], activeWorkspaceId: current })).toBe(scoped);
    expect(selectActiveGenerationJob({ current: stale, jobs: [], activeWorkspaceId: current })).toBeNull();
    expect(selectActiveGenerationJob({ current: scoped, jobs: [scoped], activeWorkspaceId: null })).toBeNull();
  });

  it("filters the rendered history to the workspace being read", () => {
    const scoped = job({ id: "job-new", workspaceId: current });
    const active = selectActiveGenerationJob({ current: null, jobs: [stale, scoped], activeWorkspaceId: current });
    expect(active?.id).toBe("job-new");
  });
});
