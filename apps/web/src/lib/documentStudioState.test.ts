import { afterEach, describe, expect, it, vi } from "vitest";
import {
  canCancelDocumentJob,
  clearDocumentActiveJobId,
  documentActiveJobStorageKey,
  displayDocumentProgress,
  documentPresentationState,
  isDocumentJobUnavailableError,
  isDocumentJob,
  isDocumentSourceReady,
  isSafeDocumentIdentifier,
  isSafeDocumentRepresentation,
  isRestorableDocumentJob,
  nextDocumentPollDelay,
  parseDocumentJobResult,
  persistDocumentActiveJobId,
  readDocumentActiveJobId,
  retainDocumentWorkspaceFileIds,
  retainDocumentWorkspaceProjectId,
  shouldResetDocumentWorkspaceState,
  shouldPollDocumentJob,
} from "./documentStudioState";
import type { GenerationJob } from "./api";

const job = (status: GenerationJob["status"], cancellationRequested = false, overrides: Partial<GenerationJob> = {}): GenerationJob => ({
  id: "00000000-0000-4000-8000-000000000001", workspaceId: "workspace-1", projectId: null, jobType: "document.generate", status, title: "Report", progressPercent: 70,
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

  it("treats missing or inaccessible jobs as unrecoverable polling state", () => {
    expect(isDocumentJobUnavailableError({ status: 403 })).toBe(true);
    expect(isDocumentJobUnavailableError({ status: 404 })).toBe(true);
    expect(isDocumentJobUnavailableError({ status: 408 })).toBe(false);
    expect(isDocumentJobUnavailableError(new Error("network failure"))).toBe(false);
    expect(isDocumentJobUnavailableError(null)).toBe(false);
  });

  it("validates the restored job type and keeps active job storage workspace-scoped", () => {
    expect(isDocumentJob(job("Running"))).toBe(true);
    expect(isDocumentJob(job("Running", false, { jobType: "image.generate" }))).toBe(false);
    expect(isRestorableDocumentJob(job("Running"), "workspace-1")).toBe(true);
    expect(isRestorableDocumentJob(job("Running", false, { id: "00000000-0000-4000-8000-000000000002" }), "workspace-1", "00000000-0000-4000-8000-000000000001")).toBe(false);
    expect(isRestorableDocumentJob(job("Running", false, { workspaceId: "workspace-2" }), "workspace-1")).toBe(false);
    expect(shouldResetDocumentWorkspaceState(job("Running"), "workspace-2")).toBe(true);
    expect(shouldResetDocumentWorkspaceState(job("Running"), "workspace-1")).toBe(false);
    expect(shouldResetDocumentWorkspaceState(null, "workspace-2")).toBe(false);
    expect(retainDocumentWorkspaceProjectId("project-1", [{ id: "project-1" }])).toBe("project-1");
    expect(retainDocumentWorkspaceProjectId("project-from-other-workspace", [{ id: "project-1" }])).toBe("");
    expect(retainDocumentWorkspaceFileIds(["file-1", "file-2"], [
      { id: "file-1", extension: ".pdf", status: "Ready", textExtractionStatus: "Ready" },
      { id: "file-2", extension: ".docx", status: "Failed", textExtractionStatus: "Ready" },
    ])).toEqual(["file-1"]);
    expect(isSafeDocumentIdentifier("00000000-0000-4000-8000-000000000001")).toBe(true);
    expect(isSafeDocumentIdentifier("../../other-job")).toBe(false);

    const values = new Map<string, string>();
    vi.stubGlobal("window", {
      sessionStorage: {
        getItem: (key: string) => values.get(key) ?? null,
        setItem: (key: string, value: string) => values.set(key, value),
        removeItem: (key: string) => values.delete(key),
      },
    });
    persistDocumentActiveJobId("workspace-1", "00000000-0000-4000-8000-000000000001");
    expect(values.get(documentActiveJobStorageKey("workspace-1"))).toBe("00000000-0000-4000-8000-000000000001");
    expect(readDocumentActiveJobId("workspace-1")).toBe("00000000-0000-4000-8000-000000000001");
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
    succeeded.resultJson = JSON.stringify({ assetId: "00000000-0000-4000-8000-000000000010" });
    expect(documentPresentationState(succeeded, parseDocumentJobResult(succeeded))).toBe("succeeded");
    expect(displayDocumentProgress(succeeded)).toBe(100);
  });

  it("parses a safe structured result and rejects malformed polling data", () => {
    const completed = job("Succeeded");
    completed.resultJson = JSON.stringify({
      assetId: "00000000-0000-4000-8000-000000000010",
      title: "Report",
      summary: "Summary",
      sections: [{ heading: "Overview", blocks: [{ type: "paragraph", text: "Text" }, { type: "table", rows: [{ cells: ["A", "B"] }] }] }],
      representations: [{ id: "00000000-0000-4000-8000-000000000011", type: "docx", fileName: "report.docx", contentType: "application/vnd.openxmlformats-officedocument.wordprocessingml.document" }],
    });
    const parsed = parseDocumentJobResult(completed);
    expect(parsed?.sections?.[0].heading).toBe("Overview");
    expect(parsed?.representations).toHaveLength(1);
    expect(isSafeDocumentRepresentation(JSON.parse(completed.resultJson!).representations[0])).toBe(true);

    completed.resultJson = JSON.stringify({ assetId: "../../other-asset", sections: [{ heading: null, blocks: null }], representations: [{ id: "bad" }] });
    const partial = parseDocumentJobResult(completed);
    expect(partial?.assetId).toBeUndefined();
    expect(partial?.sections).toEqual([]);
    expect(partial?.representations).toEqual([]);
    expect(isSafeDocumentRepresentation({ id: "00000000-0000-4000-8000-000000000012", type: "pdf", fileName: "report.docx", contentType: "application/pdf" })).toBe(false);
    expect(isSafeDocumentRepresentation({ id: "00000000-0000-4000-8000-000000000012", type: "pdf", fileName: "../report.pdf", contentType: "application/pdf" })).toBe(false);
    expect(isSafeDocumentRepresentation({ id: "00000000-0000-4000-8000-000000000012", type: "docx", fileName: "report.docx", contentType: "application/pdf" })).toBe(false);

    completed.resultJson = "not-json";
    expect(parseDocumentJobResult(completed)).toBeNull();
  });

  it("reads legacy PascalCase persisted sections while the API writes camelCase", () => {
    const completed = job("Succeeded", false, {
      resultJson: JSON.stringify({
        AssetId: "00000000-0000-4000-8000-000000000010",
        DocumentType: "document",
        Title: "Legacy report",
        Summary: "Legacy summary",
        Sections: [{ Heading: "Overview", Blocks: [{ Type: "paragraph", Text: "Legacy text" }, { Type: "table", Rows: [{ Cells: ["A", "B"] }] }] }],
        Representations: [{ Id: "00000000-0000-4000-8000-000000000011", Type: "docx", FileName: "report.docx", ContentType: "application/vnd.openxmlformats-officedocument.wordprocessingml.document" }],
      }),
    });

    const parsed = parseDocumentJobResult(completed);
    expect(parsed).toMatchObject({ assetId: "00000000-0000-4000-8000-000000000010", title: "Legacy report" });
    expect(parsed?.sections?.[0].blocks[1].rows?.[0].cells).toEqual(["A", "B"]);
    expect(parsed?.representations).toHaveLength(1);
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
