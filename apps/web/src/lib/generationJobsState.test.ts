import { describe, expect, it } from "vitest";
import type { GenerationJob } from "./api";
import {
  canCancelGenerationJob,
  describeGenerationJobsSurface,
  filterGenerationJobsToWorkspace,
  isCurrentGenerationJobsAlert,
  isCurrentGenerationJobsRead,
  isTerminalJob,
  mergeGenerationJobIntoRead,
  readGenerationJobs,
  resolveGenerationJobsDetail,
  safeGenerationJobDisplay,
  isCurrentGenerationJobsWork,
  type GenerationJobsAlert,
  type GenerationJobsRead,
  type GenerationJobsSelection,
} from "./generationJobsState";
function job(overrides: Partial<GenerationJob> = {}): GenerationJob {
  return {
    id: "job-1", workspaceId: "workspace-1", projectId: null, jobType: "system.test", status: "Queued", title: "Test", progressPercent: 0,
    resultJson: null, errorCode: null, errorMessage: null, cancellationRequested: false, createdAt: "2026-01-01T00:00:00Z", queuedAt: "2026-01-01T00:00:00Z",
    startedAt: null, completedAt: null, failedAt: null, cancelledAt: null, outputs: [], ...overrides,
  };
}
function read(workspaceId: string, epoch: number, jobs: GenerationJob[]): GenerationJobsRead {
  return { workspaceId, epoch, jobs };
}
function oldRowish(overrides: Partial<GenerationJob> = {}) {
  return job({ id: "job-old", workspaceId: "workspace-a", title: "Older page row", progressPercent: 10, ...overrides });
}
describe("generation job UI state", () => {
  it("merges create and polling updates into the settled read and keeps the newest row", () => {
    let settled = read("workspace-1", 1, []);
    settled = mergeGenerationJobIntoRead(settled, job(), "workspace-1", 1)!;
    settled = mergeGenerationJobIntoRead(settled, job({ status: "Running", progressPercent: 40 }), "workspace-1", 1)!;
    const latest = job({ status: "Succeeded", progressPercent: 100, resultJson: '{"message":"done"}' });
    settled = mergeGenerationJobIntoRead(settled, latest, "workspace-1", 1)!;
    expect(settled.jobs).toHaveLength(1);
    const selection: GenerationJobsSelection = { workspaceId: "workspace-1", jobId: "job-1" };
    expect(resolveGenerationJobsDetail({ read: settled, selection, activeWorkspaceId: "workspace-1", attemptEpoch: 1 })).toMatchObject({ status: "Succeeded", progressPercent: 100, resultJson: '{"message":"done"}' });
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
    const settled = read(current, 1, [job({ id: "job-new", workspaceId: current })]);
    const next = mergeGenerationJobIntoRead(settled, stale, current, 1)!;
    expect(next.jobs.map((item) => item.id)).toEqual(["job-new"]);
    expect(JSON.stringify(next.jobs)).not.toContain("from workspace-1");
  });
  it("leaves an empty history untouched when the only response is from the previous workspace", () => {
    const next = mergeGenerationJobIntoRead(read(current, 1, []), stale, current, 1)!;
    expect(next.jobs).toHaveLength(0);
  });
  it("still merges jobs owned by the workspace the view is reading", () => {
    const next = mergeGenerationJobIntoRead(read(current, 1, []), job({ id: "job-new", workspaceId: current }), current, 1)!;
    expect(next.jobs.map((item) => item.id)).toEqual(["job-new"]);
  });
  it("never treats a workspace as readable when the session has no workspace", () => {
    expect(mergeGenerationJobIntoRead(read(current, 1, [job({ id: "job-new", workspaceId: current })]), stale, null, 1)).toEqual(read(current, 1, [job({ id: "job-new", workspaceId: current })]));
    expect(readGenerationJobs(read(current, 1, [job({ id: "job-new", workspaceId: current })]), null, 1)).toEqual([]);
    expect(resolveGenerationJobsDetail({ read: read(current, 1, [job()]), selection: null, activeWorkspaceId: null, attemptEpoch: 1 })).toBeNull();
  });
  it("filters the entire recent-jobs list synchronously when the workspace changes", () => {
    const retained = job({ id: "job-old", workspaceId: "workspace-1", title: "Private old title", resultJson: '{"private":"old-data"}' });
    const active = job({ id: "job-new", workspaceId: current, title: "Current title" });
    const visible = filterGenerationJobsToWorkspace([retained, active], current);
    expect(visible.map(item => item.id)).toEqual(["job-new"]);
    expect(JSON.stringify(visible)).not.toContain("Private old title");
    expect(JSON.stringify(visible)).not.toContain("old-data");
    expect(filterGenerationJobsToWorkspace([retained, active], null)).toEqual([]);
  });
  it("drops a record whose payload carries no workspace identity at all", () => {
    const anonymous = job({ id: "job-anon", workspaceId: undefined as unknown as string });
    const settled = read(current, 1, [job({ id: "job-new", workspaceId: current })]);
    expect(mergeGenerationJobIntoRead(settled, anonymous, current, 1)!.jobs.map((item) => item.id)).toEqual(["job-new"]);
    expect(readGenerationJobs(read(current, 1, [anonymous]), current, 1)).toEqual([]);
  });
});
// A workspace id alone is not enough to prove that a value may be rendered: the session can leave a
// workspace and return to it, and the records it left behind belong to the workspace being read but
// to a read that has already been superseded. The read therefore carries the list attempt that
// produced it, and only the newest settled read for the workspace being read may render.
describe("generation history read freshness", () => {
  const workspace = "workspace-a";
  const oldRow = job({ id: "job-old", workspaceId: workspace, title: "Older page row", progressPercent: 10 });
  const freshRow = job({ id: "job-old", workspaceId: workspace, title: "Newest page row", progressPercent: 90, resultJson: '{"note":"fresh"}' });
  it("treats a read as renderable only for its own workspace and its own list attempt", () => {
    const settled = read(workspace, 2, [freshRow]);
    expect(isCurrentGenerationJobsRead(settled, workspace, 2)).toBe(true);
    expect(isCurrentGenerationJobsRead(settled, "workspace-b", 2)).toBe(false);
    expect(isCurrentGenerationJobsRead(settled, workspace, 3)).toBe(false);
    expect(isCurrentGenerationJobsRead(null, workspace, 2)).toBe(false);
    expect(isCurrentGenerationJobsRead(settled, null, 2)).toBe(false);
  });
  it("renders nothing while a superseded read is the only value held", () => {
    // Left the workspace and came back: the newest list attempt has not settled yet, and the rows
    // in hand are from the read taken before the detour.
    const held = read(workspace, 1, [oldRow]);
    expect(readGenerationJobs(held, workspace, 4)).toEqual([]);
    expect(resolveGenerationJobsDetail({ read: held, selection: { workspaceId: workspace, jobId: "job-old" }, activeWorkspaceId: workspace, attemptEpoch: 4 })).toBeNull();
  });
  it("resolves the inspected job to the newest row the current read holds for it", () => {
    const selection: GenerationJobsSelection = { workspaceId: workspace, jobId: "job-old" };
    const detail = resolveGenerationJobsDetail({ read: read(workspace, 4, [freshRow]), selection, activeWorkspaceId: workspace, attemptEpoch: 4 });
    expect(detail).toMatchObject({ progressPercent: 90, title: "Newest page row" });
    expect(JSON.stringify(detail)).not.toContain("Older page row");
  });
  it("renders no detail at all when the newest page no longer contains the inspected job", () => {
    const selection: GenerationJobsSelection = { workspaceId: workspace, jobId: "job-gone" };
    expect(resolveGenerationJobsDetail({ read: read(workspace, 4, [freshRow]), selection, activeWorkspaceId: workspace, attemptEpoch: 4 })).toBeNull();
  });
  it("seeds a read for a job created before the authoritative page has landed", () => {
    const created = job({ id: "job-new", workspaceId: workspace, status: "Queued" });
    // The list request for attempt 5 is still in flight, so nothing has settled for this workspace.
    expect(mergeGenerationJobIntoRead(null, created, workspace, 5)).toEqual({ workspaceId: workspace, epoch: 5, jobs: [created] });
    // A foreign read is replaced by the active workspace's own seed, never merged into.
    expect(mergeGenerationJobIntoRead(read("workspace-b", 5, []), created, workspace, 5)).toEqual({ workspaceId: workspace, epoch: 5, jobs: [created] });
  });
  it("retires a failure alert together with the read that raised it", () => {
    const alert: GenerationJobsAlert = { workspaceId: workspace, epoch: 2, message: "The history could not be loaded.", retryable: true };
    expect(isCurrentGenerationJobsAlert(alert, workspace, 2)).toBe(true);
    expect(isCurrentGenerationJobsAlert(alert, "workspace-b", 2)).toBe(false);
    expect(isCurrentGenerationJobsAlert(alert, workspace, 3)).toBe(false);
    expect(isCurrentGenerationJobsAlert(null, workspace, 3)).toBe(false);
  });
});
// "No test jobs yet" is a claim about the workspace being read. It may only be made once a read for
// that workspace has settled; while the first page of a newly mounted workspace is still on its way
// the honest surface is the loading placeholder, and a settled failure must not spin forever.
describe("generation history surface claims", () => {
  const failure: GenerationJobsAlert = { workspaceId: "workspace-a", epoch: 3, message: "The history could not be loaded.", retryable: true };
  it("claims a list only when the current read has rows to show", () => {
    expect(describeGenerationJobsSurface({ activeWorkspaceId: "workspace-a", read: read("workspace-a", 3, [oldRowish()]), attemptEpoch: 3, loading: false, alert: null })).toBe("list");
  });
  it("claims loading, never an empty history, while the workspace has no settled read", () => {
    const foreign = read("workspace-b", 2, [oldRowish({ workspaceId: "workspace-b" })]);
    expect(describeGenerationJobsSurface({ activeWorkspaceId: "workspace-a", read: foreign, attemptEpoch: 3, loading: true, alert: null })).toBe("loading");
    // The render between the workspace change and its own load effect: nothing in flight has been
    // flagged yet, and the page still must not claim an empty history for a workspace it has not read.
    expect(describeGenerationJobsSurface({ activeWorkspaceId: "workspace-a", read: foreign, attemptEpoch: 3, loading: false, alert: null })).toBe("loading");
    expect(describeGenerationJobsSurface({ activeWorkspaceId: "workspace-a", read: null, attemptEpoch: 1, loading: false, alert: null })).toBe("loading");
  });
  it("settles on the empty history once the current workspace reports no jobs", () => {
    expect(describeGenerationJobsSurface({ activeWorkspaceId: "workspace-a", read: read("workspace-a", 3, []), attemptEpoch: 3, loading: false, alert: null })).toBe("empty");
  });
  it("stops claiming to load when the read for this attempt has failed", () => {
    expect(describeGenerationJobsSurface({ activeWorkspaceId: "workspace-a", read: read("workspace-b", 2, []), attemptEpoch: 3, loading: false, alert: failure })).toBe("empty");
  });
  it("claims an empty history when the session has no workspace to read", () => {
    expect(describeGenerationJobsSurface({ activeWorkspaceId: null, read: read("workspace-a", 3, [oldRowish()]), attemptEpoch: 4, loading: false, alert: null })).toBe("empty");
  });
});

// Starting a create/cancel action in workspace A must never leave workspace B
// disabled if A's network response loses its generation fence on re-assignment.
describe("generation history mutation busy-state fencing", () => {
  const pending = { workspaceId: "workspace-a", request: 12 };
  it("is busy only for the issuing workspace and exact request", () => {
    expect(isCurrentGenerationJobsWork(pending, "workspace-a", 12)).toBe(true);
    expect(isCurrentGenerationJobsWork(pending, "workspace-b", 12)).toBe(false);
    expect(isCurrentGenerationJobsWork(pending, null, 12)).toBe(false);
    expect(isCurrentGenerationJobsWork(pending, "workspace-a", 13)).toBe(false);
    expect(isCurrentGenerationJobsWork(null, "workspace-a", 12)).toBe(false);
  });
  it("does not resurrect a stale pending action after switching away and back", () => {
    // A -> B -> A retires attempt 12 even when the old promise has not settled.
    expect(isCurrentGenerationJobsWork(pending, "workspace-b", 13)).toBe(false);
    expect(isCurrentGenerationJobsWork(pending, "workspace-a", 14)).toBe(false);
    expect(isCurrentGenerationJobsWork({ workspaceId: "workspace-a", request: 14 }, "workspace-a", 14)).toBe(true);
  });
});
