import { describe, expect, it } from "vitest";
import type { GenerationJob } from "./api";
import { displayPresentationProgress, isPresentationSourceReady, parsePresentationJobResult, presentationStudioState } from "./presentationStudioState";

const job = (status: GenerationJob["status"], resultJson: string | null = null, progressPercent = 70): GenerationJob => ({ id: "job-1", workspaceId: "workspace-1", projectId: null, jobType: "presentation.generate", status, title: "Deck", progressPercent, resultJson, errorCode: null, errorMessage: null, cancellationRequested: false, createdAt: "2026-01-01", queuedAt: null, startedAt: null, completedAt: null, failedAt: null, cancelledAt: null, outputs: [] });

describe("presentationStudioState", () => {
  it("caps nonterminal progress at 99 and honors terminal 100", () => { expect(displayPresentationProgress(job("Running", null, 100))).toBe(99); expect(displayPresentationProgress(job("Succeeded", '{"assetId":"asset-1"}', 100))).toBe(100); });
  it("parses previews defensively and keeps only PPTX download metadata", () => { const result = parsePresentationJobResult(job("Succeeded", '{"title":"Deck","slideCount":2,"previewSlides":[{"order":1,"type":"title","title":"Welcome","blocks":[{"type":"bullets","items":["One"]}]}],"representations":[{"id":"pptx-1","type":"pptx","fileName":"deck.pptx","contentType":"application/vnd.openxmlformats-officedocument.presentationml.presentation"},{"id":"bad"}]}')); expect(result?.previewSlides?.[0].title).toBe("Welcome"); expect(result?.representations).toHaveLength(1); expect(result?.representations?.[0].type).toBe("pptx"); });
  it("treats malformed result as completed-unavailable rather than succeeded", () => { const current = job("Succeeded", "not-json", 100); expect(parsePresentationJobResult(current)).toBeNull(); expect(presentationStudioState(current, null)).toBe("completed-unavailable"); });
  it("accepts only ready extracted supported source files", () => { expect(isPresentationSourceReady({ extension: ".xlsx", status: "Ready", textExtractionStatus: "Ready" })).toBe(true); expect(isPresentationSourceReady({ extension: ".png", status: "Ready", textExtractionStatus: "Ready" })).toBe(false); expect(isPresentationSourceReady({ extension: ".pdf", status: "Processing", textExtractionStatus: "Ready" })).toBe(false); });
});
