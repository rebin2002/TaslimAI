import { describe, expect, it } from "vitest";
import { canCancelDocumentJob, isDocumentSourceReady, parseDocumentJobResult } from "./documentStudioState";
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
  });

  it("parses a safe structured result and rejects malformed polling data", () => {
    const completed = job("Succeeded");
    completed.resultJson = JSON.stringify({ assetId: "asset-1", title: "Report", summary: "Summary", sections: [{ heading: "Overview", blocks: [{ type: "paragraph", text: "Text" }] }] });
    expect(parseDocumentJobResult(completed)?.sections?.[0].heading).toBe("Overview");
    completed.resultJson = "not-json";
    expect(parseDocumentJobResult(completed)).toBeNull();
  });

  it("accepts only ready supported extracted sources", () => {
    expect(isDocumentSourceReady({ extension: ".pdf", status: "Ready", textExtractionStatus: "Ready" })).toBe(true);
    expect(isDocumentSourceReady({ extension: ".png", status: "Ready", textExtractionStatus: "Ready" })).toBe(false);
    expect(isDocumentSourceReady({ extension: ".docx", status: "Ready", textExtractionStatus: "Failed" })).toBe(false);
  });
});
