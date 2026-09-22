import { describe, expect, it } from "vitest";
import {
  canCancelDocumentJob,
  displayDocumentProgress,
  documentPresentationState,
  isDocumentSourceReady,
  nextDocumentPollDelay,
  parseDocumentJobResult,
  shouldPollDocumentJob,
} from "./documentStudioState";
import type { GenerationJob } from "./api";

const job = (status: GenerationJob["status"], cancellationRequested = false): GenerationJob => ({
  id: "job-1", workspaceId: "workspace-1", projectId: null, jobType: "document.generate", status, title: "Report", progressPercent: 70,
  createdAt: "2026-09-22T00:00:00Z", queuedAt: "2026-09-22T00:00:00Z", startedAt: null, completedAt: null, failedAt: null, cancelledAt: null,
  cancellationRequested, resultJson: null, errorCode: null, errorMessage: null, outputs: [],
});

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

    completed.resultJson = JSON.stringify({ assetId: "asset-1", sections: [{ heading: null, blocks: null }], representations: [{ id: "bad" }] });
    const partial = parseDocumentJobResult(completed);
    expect(partial?.assetId).toBe("asset-1");
    expect(partial?.sections).toEqual([]);
    expect(partial?.representations).toEqual([]);

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
