import { afterEach, describe, expect, it, vi } from "vitest";
import {
  canCancelDocumentJob,
  clearDocumentActiveJobId,
  documentActiveJobStorageKey,
  displayDocumentProgress,
  documentPresentationState,
  isDocumentJob,
  isDocumentSourceReady,
  isSafeDocumentRepresentation,
  isRestorableDocumentJob,
  nextDocumentPollDelay,
  parseDocumentJobResult,
  persistDocumentActiveJobId,
  readDocumentActiveJobId,
  retainDocumentWorkspaceProjectId,
  shouldResetDocumentWorkspaceState,
  shouldPollDocumentJob,
} from "./documentStudioState";
import type { GenerationJob } from "./api";

const job = (status: GenerationJob["status"], cancellationRequested = false, overrides: Partial<GenerationJob> = {}): GenerationJob => ({
  id: "job-1", workspaceId: "workspace-1", projectId: null, jobType: "document.generate", status, title: "Report", progressPercent: 70,
  createdAt: "2026-09-22T00:00:00Z", queuedAt: "2026-09-22T00:00:00Z", startedAt: null, completedAt: null, failedAt: null, cancelledAt: null,
  cancellationRequested, resultJson: null, errorCode: null, errorMessage: null, outputs: [],
  ...overrides,
});

afterEach(() => vi.unstubAllGlobals());

describe("Document Studio state", () => {
  it("allows cancellation only for eligible nonterminal jobs", () => {
    expect(canCancelDocumentJob(job("Pending"))).toBe(true);
    expect(canCancelDocumentJob(job("Queued"))).toBe(true);
    expect(canCancelDocumentJob(job("Running"))).toBe(true);
    expect(canCancelDocumentJob(job("Running", true))).toBe(false);
    expect(canCancelDocumentJob(job("Succeeded"))).toBe(false);
    expect(canCancelDocumentJob(null)).toBe(false);
    expect(shouldPollDocumentJob(job("Running"))).toBe(true);
    expect(shouldPollDocumentJob(job("Succeeded"))).toBe(false);
    expect(shouldPollDocumentJob(job("Failed"))).toBe(false);
    expect(shouldPollDocumentJob(job("Cancelled"))).toBe(false);
    expect(nextDocumentPollDelay(job("Running"), 0)).toBe(700);
    expect(nextDocumentPollDelay(job("Running"), 1)).toBe(1400);
    expect(nextDocumentPollDelay(job("Running"), 10)).toBe(2800);
    expect(nextDocumentPollDelay(job("Succeeded"), 0)).toBeNull();
  });

  it("validates the restored job type and keeps active job storage workspace-scoped", () => {
    expect(isDocumentJob(job("Running"))).toBe(true);
    expect(isDocumentJob(job("Running", false, { jobType: "image.generate" }))).toBe(false);
    expect(isRestorableDocumentJob(job("Running"), "workspace-1")).toBe(true);
    expect(isRestorableDocumentJob(job("Running", false, { id: "job-2" }), "workspace-1", "job-1")).toBe(false);
    expect(isRestorableDocumentJob(job("Running", false, { workspaceId: "workspace-2" }), "workspace-1")).toBe(false);
    expect(shouldResetDocumentWorkspaceState(job("Running"), "workspace-2")).toBe(true);
    expect(shouldResetDocumentWorkspaceState(job("Running"), "workspace-1")).toBe(false);
    expect(shouldResetDocumentWorkspaceState(null, "workspace-2")).toBe(false);
    expect(retainDocumentWorkspaceProjectId("project-1", [{ id: "project-1" }])).toBe("project-1");
    expect(retainDocumentWorkspaceProjectId("project-from-other-workspace", [{ id: "project-1" }])).toBe("");

    const values = new Map<string, string>();
    vi.stubGlobal("window", {
      sessionStorage: {
        getItem: (key: string) => values.get(key) ?? null,
        setItem: (key: string, value: string) => values.set(key, value),
        removeItem: (key: string) => values.delete(key),
      },
    });
    persistDocumentActiveJobId("workspace-1", "job-1");
    expect(values.get(documentActiveJobStorageKey("workspace-1"))).toBe("job-1");
    expect(readDocumentActiveJobId("workspace-1")).toBe("job-1");
    expect(readDocumentActiveJobId("workspace-2")).toBeNull();
    clearDocumentActiveJobId("workspace-1");
    expect(readDocumentActiveJobId("workspace-1")).toBeNull();
  });

  it("uses status, not 100% progress, to determine completion", () => {
    const running = job("Running");
    running.progressPercent = 100;
    expect(documentPresentationState(running, null)).toBe("running");
    expect(displayDocumentProgress(running)).toBe(99);

    const succeeded = job("Succeeded");
    succeeded.progressPercent = 100;
    succeeded.resultJson = JSON.stringify({ assetId: "asset-1" });
    expect(documentPresentationState(succeeded, parseDocumentJobResult(succeeded))).toBe("succeeded");
    expect(displayDocumentProgress(succeeded)).toBe(100);
  });

  it("parses a safe structured result and rejects malformed polling data", () => {
    const completed = job("Succeeded");
    completed.resultJson = JSON.stringify({
      assetId: "asset-1",
      title: "Report",
      summary: "Summary",
      sections: [{ heading: "Overview", blocks: [{ type: "paragraph", text: "Text" }, { type: "table", rows: [{ cells: ["A", "B"] }] }] }],
      representations: [{ id: "docx-1", type: "docx", fileName: "report.docx", contentType: "application/vnd.openxmlformats-officedocument.wordprocessingml.document" }],
    });
    const parsed = parseDocumentJobResult(completed);
    expect(parsed?.sections?.[0].heading).toBe("Overview");
    expect(parsed?.representations).toHaveLength(1);
    expect(isSafeDocumentRepresentation(JSON.parse(completed.resultJson!).representations[0])).toBe(true);

    completed.resultJson = JSON.stringify({ assetId: "asset-1", sections: [{ heading: null, blocks: null }], representations: [{ id: "bad" }] });
    const partial = parseDocumentJobResult(completed);
    expect(partial?.assetId).toBe("asset-1");
    expect(partial?.sections).toEqual([]);
    expect(partial?.representations).toEqual([]);
    expect(isSafeDocumentRepresentation({ id: "representation-1", type: "pdf", fileName: "report.docx", contentType: "application/pdf" })).toBe(false);
    expect(isSafeDocumentRepresentation({ id: "representation-1", type: "pdf", fileName: "../report.pdf", contentType: "application/pdf" })).toBe(false);
    expect(isSafeDocumentRepresentation({ id: "representation-1", type: "docx", fileName: "report.docx", contentType: "application/pdf" })).toBe(false);

    completed.resultJson = "not-json";
    expect(parseDocumentJobResult(completed)).toBeNull();
  });

  it("represents a succeeded job without an optional result as recoverable", () => {
    const completed = job("Succeeded");
    expect(documentPresentationState(completed, null)).toBe("completed-unavailable");
    expect(canCancelDocumentJob(completed)).toBe(false);
  });

  it("keeps failed and cancelled jobs in safe terminal states", () => {
    const failed = job("Failed");
    failed.errorMessage = "DOCUMENT_RENDER_FAILED";
    const cancelled = job("Cancelled");
    expect(documentPresentationState(failed, null)).toBe("failed");
    expect(documentPresentationState(cancelled, null)).toBe("cancelled");
    expect(canCancelDocumentJob(failed)).toBe(false);
    expect(canCancelDocumentJob(cancelled)).toBe(false);
  });

  it("accepts only ready supported extracted sources", () => {
    expect(isDocumentSourceReady({ extension: ".pdf", status: "Ready", textExtractionStatus: "Ready" })).toBe(true);
    expect(isDocumentSourceReady({ extension: ".png", status: "Ready", textExtractionStatus: "Ready" })).toBe(false);
    expect(isDocumentSourceReady({ extension: ".docx", status: "Ready", textExtractionStatus: "Failed" })).toBe(false);
  });
});
