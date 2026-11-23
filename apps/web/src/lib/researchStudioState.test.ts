import { afterEach, describe, expect, it, vi } from "vitest";
import type { GenerationJob, ResearchSource } from "./api";
import { clearResearchActiveJobId, displayResearchProgress, hasRenderableResearchResult, isCurrentResearchJob, isResearchSourceReady, isRestorableResearchJob, isResearchJob, isSafeExternalUrl, mergeResearchSources, parseResearchJobResult, persistResearchActiveJobId, readResearchActiveJobId, researchActiveJobStorageKey, researchStudioState } from "./researchStudioState";

const job = (status: GenerationJob["status"], resultJson: string | null = null, progressPercent = 70): GenerationJob => ({ id: "job-1", workspaceId: "workspace-1", projectId: null, jobType: "research.generate", status, title: "Research", progressPercent, resultJson, errorCode: null, errorMessage: null, cancellationRequested: false, createdAt: "2026-01-01", queuedAt: null, startedAt: null, completedAt: null, failedAt: null, cancelledAt: null, outputs: [] });
const source = (citationId: string, title = "Official source"): ResearchSource => ({ citationId, title, domain: "example.gov", url: "https://example.gov/source", publisher: null, publishedAt: null, retrievedAt: "2026-01-01T00:00:00Z", sourceType: "web", snippet: null, searchQuery: null, rank: 1, isSelected: true });
const webSource = (citationId: string, url: string, title = "Official source"): ResearchSource => ({ ...source(citationId, title), url });
const uploadedSource = (citationId: string, title: string): ResearchSource => ({ ...source(citationId, title), url: null, domain: "uploaded file", sourceType: "uploaded" });
const report = (sources: ResearchSource[], citationIds: string[]) => JSON.stringify({ assetId: "asset-1", researchType: "research", title: "Report", sources, keyFindings: [{ type: "key_finding", text: "A finding", citationIds }], sections: [{ heading: "Evidence", blocks: [{ type: "paragraph", text: "Evidence", citationIds }] }] });
afterEach(() => vi.unstubAllGlobals());

describe("researchStudioState", () => {
  it("caps nonterminal progress and honors terminal completion", () => { expect(displayResearchProgress(job("Running", null, 100))).toBe(99); expect(displayResearchProgress(job("Succeeded", '{"assetId":"asset-1"}', 100))).toBe(100); });
  it("parses cited report blocks and rejects malformed result safely", () => { const result = parseResearchJobResult(job("Succeeded", '{"researchType":"research","assetId":"asset-1","title":"Report","sources":[{"citationId":"S1","title":"Official source","domain":"example.gov","url":"https://example.gov","sourceType":"web","isSelected":true}],"keyFindings":[{"type":"key_finding","text":"A finding [S1]","citationIds":["S1"]}],"representations":[{"id":"docx-1","type":"docx","fileName":"report.docx","contentType":"application/vnd.openxmlformats-officedocument.wordprocessingml.document"}]}')); expect(result?.sources?.[0].citationId).toBe("S1"); expect(result?.keyFindings?.[0].citationIds).toEqual(["S1"]); expect(result?.representations).toHaveLength(1); expect(parseResearchJobResult(job("Succeeded", "not-json"))).toBeNull(); });
  it("drops duplicate sources and unbacked citation references at the render boundary", () => {
    const result = parseResearchJobResult(job("Succeeded", JSON.stringify({
      assetId: "asset-1",
      sourceCount: 99,
      sources: [source("S1"), source(" s1 ", "Duplicate source")],
      keyFindings: [{ type: "key_finding", text: "A finding [S1]", citationIds: [" s1 ", "S99", "S1"] }],
      sections: [{ heading: "Evidence", blocks: [{ type: "paragraph", text: "Evidence [S1]", citationIds: ["s1", "unknown"] }] }],
    })));
    expect(result?.sourceCount).toBe(1);
    expect(result?.sources?.map((item) => item.citationId)).toEqual(["S1"]);
    expect(result?.keyFindings?.[0].citationIds).toEqual(["S1"]);
    expect(result?.sections?.[0].blocks[0].citationIds).toEqual(["S1"]);
  });
  it("uses completed-unavailable when a successful job has only an asset fallback", () => {
    const fallback = parseResearchJobResult(job("Succeeded", '{"assetId":"asset-1"}'));
    expect(hasRenderableResearchResult(fallback)).toBe(false);
    expect(researchStudioState(job("Succeeded", '{"assetId":"asset-1"}'), fallback)).toBe("completed-unavailable");
  });
  it("shows success only when the report and downloads are complete", () => {
    const result = parseResearchJobResult(job("Succeeded", JSON.stringify({ assetId: "asset-1", title: "Report", executiveSummary: "Summary", sections: [{ heading: "Evidence", blocks: [{ type: "paragraph", text: "Evidence" }] }], representations: [{ id: "docx-1", type: "docx", fileName: "report.docx", contentType: "application/vnd.openxmlformats-officedocument.wordprocessingml.document" }] })));
    expect(hasRenderableResearchResult(result)).toBe(true);
    expect(researchStudioState(job("Succeeded"), result)).toBe("succeeded");
  });
  it("merges only persisted source details that match the embedded citation manifest", () => {
    const result = parseResearchJobResult(job("Succeeded", JSON.stringify({ assetId: "asset-1", sources: [source("S1")] })));
    const merged = mergeResearchSources(result, [source("S1", "Enriched source"), source("S2", "Unrelated source"), source("s1", "Duplicate detail")]);
    expect(merged?.sources?.map((item) => item.title)).toEqual(["Enriched source"]);
    expect(merged?.sourceCount).toBe(1);
  });
  it("keeps embedded sources when protected source details are unavailable", () => { const result = parseResearchJobResult(job("Succeeded", '{"assetId":"asset-1","title":"Report","sources":[{"citationId":"S1","title":"Official source","domain":"example.gov","url":"https://example.gov","sourceType":"web"}]}')); const merged = mergeResearchSources(result, []); expect(merged?.sources?.[0].citationId).toBe("S1"); expect(merged?.sourceCount).toBe(1); });
  it("accepts only ready extracted source files and safe HTTP links", () => { expect(isResearchSourceReady({ extension: ".xlsx", status: "Ready", textExtractionStatus: "Ready" })).toBe(true); expect(isResearchSourceReady({ extension: ".png", status: "Ready", textExtractionStatus: "Ready" })).toBe(false); expect(isSafeExternalUrl("https://example.gov/source")).toBe(true); expect(isSafeExternalUrl("javascript:alert(1)")).toBe(false); });
  it("validates restored jobs and keeps active-job storage workspace-scoped", () => {
    expect(isResearchJob(job("Running"))).toBe(true);
    expect(isRestorableResearchJob(job("Running"), "workspace-1")).toBe(true);
    expect(isCurrentResearchJob(job("Running"), "workspace-1", "job-1")).toBe(true);
    expect(isCurrentResearchJob({ ...job("Running"), id: "job-2" }, "workspace-1", "job-1")).toBe(false);
    expect(isCurrentResearchJob({ ...job("Running"), workspaceId: "workspace-2" }, "workspace-1", "job-1")).toBe(false);
    expect(isRestorableResearchJob({ ...job("Running"), workspaceId: "workspace-2" }, "workspace-1")).toBe(false);
    expect(isResearchJob({ ...job("Running"), jobType: "presentation.generate" })).toBe(false);
    const values = new Map<string, string>();
    vi.stubGlobal("window", { sessionStorage: { getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => values.set(key, value), removeItem: (key: string) => values.delete(key) } });
    persistResearchActiveJobId("workspace-1", "research-job-1");
    expect(values.get(researchActiveJobStorageKey("workspace-1"))).toBe("research-job-1");
    expect(readResearchActiveJobId("workspace-1")).toBe("research-job-1");
    expect(readResearchActiveJobId("workspace-2")).toBeNull();
    clearResearchActiveJobId("workspace-1");
    expect(readResearchActiveJobId("workspace-1")).toBeNull();
  });

  describe("citation source deduplication and stable numbering", () => {
    it("collapses two citation ids that resolve to the same page into one numbered source", () => {
      const result = parseResearchJobResult(job("Succeeded", report([
        webSource("S1", "https://example.gov/report", "Ministry report"),
        webSource("S2", "https://example.gov/report", "Ministry report (duplicate)"),
      ], ["S1", "S2"])));
      expect(result?.sources?.map((item) => item.citationId)).toEqual(["S1"]);
      expect(result?.sourceCount).toBe(1);
      expect(result?.keyFindings?.[0].citationIds).toEqual(["S1"]);
      expect(result?.sections?.[0].blocks[0].citationIds).toEqual(["S1"]);
    });

    it("treats an in-page anchor as the same cited page", () => {
      const result = parseResearchJobResult(job("Succeeded", report([
        webSource("S1", "https://example.gov/report#section-2", "Anchored view"),
        webSource("S9", "https://example.gov/report", "Full report"),
      ], ["S9"])));
      expect(result?.sources?.map((item) => item.citationId)).toEqual(["S1"]);
      expect(result?.sourceCount).toBe(1);
      expect(result?.keyFindings?.[0].citationIds).toEqual(["S1"]);
    });

    it("keeps distinct query locators as separate cited pages", () => {
      const result = parseResearchJobResult(job("Succeeded", report([
        webSource("S1", "https://example.gov/search?q=solar"),
        webSource("S2", "https://example.gov/search?q=wind"),
      ], ["S1", "S2"])));
      expect(result?.sources?.map((item) => item.citationId)).toEqual(["S1", "S2"]);
      expect(result?.sourceCount).toBe(2);
    });

    it("never collapses uploaded sources because they carry no locator", () => {
      const result = parseResearchJobResult(job("Succeeded", report([
        uploadedSource("S1", "budget.xlsx"),
        uploadedSource("S2", "minutes.pdf"),
      ], ["S1", "S2"])));
      expect(result?.sources?.map((item) => item.citationId)).toEqual(["S1", "S2"]);
      expect(result?.sourceCount).toBe(2);
      expect(result?.keyFindings?.[0].citationIds).toEqual(["S1", "S2"]);
    });

    it("numbers the cited source list in ascending citation order whatever the payload order", () => {
      const result = parseResearchJobResult(job("Succeeded", report([
        webSource("S2", "https://example.gov/b"),
        webSource("S10", "https://example.gov/j"),
        webSource("S1", "https://example.gov/a"),
        webSource("S3", "https://example.gov/c"),
      ], ["S1", "S2", "S3", "S10"])));
      expect(result?.sources?.map((item) => item.citationId)).toEqual(["S1", "S2", "S3", "S10"]);
    });

    it("renumbers the surviving citation to the lowest citation id of the collapsed page", () => {
      const result = parseResearchJobResult(job("Succeeded", report([
        webSource("S7", "https://example.gov/report"),
        webSource("S3", "https://example.gov/report"),
      ], ["S7"])));
      expect(result?.sources?.map((item) => item.citationId)).toEqual(["S3"]);
      expect(result?.keyFindings?.[0].citationIds).toEqual(["S3"]);
    });

    it("collapses the same cited page surfaced through persisted source details", () => {
      const result = parseResearchJobResult(job("Succeeded", report([
        webSource("S1", "https://example.gov/report", "Embedded"),
        webSource("S4", "https://example.gov/report", "Embedded duplicate"),
      ], ["S1", "S4"])));
      const merged = mergeResearchSources(result, [
        webSource("S1", "https://example.gov/report", "Persisted"),
        webSource("S4", "https://example.gov/report", "Persisted duplicate"),
      ]);
      expect(merged?.sources?.map((item) => item.citationId)).toEqual(["S1"]);
      expect(merged?.sourceCount).toBe(1);
      expect(merged?.keyFindings?.[0].citationIds).toEqual(["S1"]);
    });

    it("keeps citation markers pointing at a rendered source after a detail merge reorders rows", () => {
      const result = parseResearchJobResult(job("Succeeded", report([
        webSource("S2", "https://example.gov/b", "Second"),
        webSource("S1", "https://example.gov/a", "First"),
      ], ["S1", "S2"])));
      const merged = mergeResearchSources(result, [
        webSource("S1", "https://example.gov/a", "First detail"),
        webSource("S2", "https://example.gov/b", "Second detail"),
      ]);
      expect(merged?.sources?.map((item) => item.citationId)).toEqual(["S1", "S2"]);
      expect(merged?.keyFindings?.[0].citationIds).toEqual(["S1", "S2"]);
    });
  });
});
