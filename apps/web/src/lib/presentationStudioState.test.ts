import { afterEach, describe, expect, it, vi } from "vitest";
import type { GenerationJob } from "./api";
import { clearPresentationActiveJobId, clearPresentationDraftState, displayPresentationProgress, emptyPresentationDraftState, hasPresentationExport, isPresentationSourceReady, isPresentationJob, isRestorablePresentationJob, loadPresentationDraftState, parsePresentationDraftState, parsePresentationJobResult, persistPresentationActiveJobId, persistPresentationDraftState, presentationActiveJobStorageKey, presentationDraftStorageKey, presentationStudioState, readPresentationActiveJobId, serializePresentationDraftState, shouldResetPresentationWorkspaceState } from "./presentationStudioState";

const job = (status: GenerationJob["status"], resultJson: string | null = null, progressPercent = 70): GenerationJob => ({ id: "job-1", workspaceId: "workspace-1", projectId: null, jobType: "presentation.generate", status, title: "Deck", progressPercent, resultJson, errorCode: null, errorMessage: null, cancellationRequested: false, createdAt: "2026-01-01", queuedAt: null, startedAt: null, completedAt: null, failedAt: null, cancelledAt: null, outputs: [] });
afterEach(() => vi.unstubAllGlobals());

describe("presentationStudioState", () => {
  it("caps nonterminal progress at 99 and honors terminal 100", () => { expect(displayPresentationProgress(job("Running", null, 100))).toBe(99); expect(displayPresentationProgress(job("Succeeded", '{"assetId":"asset-1"}', 100))).toBe(100); });
  it("parses previews defensively and normalizes export metadata for download", () => { const result = parsePresentationJobResult(job("Succeeded", '{"title":"Deck","slideCount":2,"previewSlides":[{"order":1,"type":"title","title":"Welcome","blocks":[{"type":"bullets","items":["One"]}]}],"representations":[{"id":"pptx-1","type":"PPTX","fileName":"deck.pptx","contentType":"application/vnd.openxmlformats-officedocument.presentationml.presentation"},{"id":"pptx-2","fileName":"deck-copy.PPTX","contentType":"application/octet-stream"},{"id":"bad"}]}')); expect(result?.previewSlides?.[0].title).toBe("Welcome"); expect(result?.representations).toHaveLength(2); expect(result?.representations?.map((representation) => representation.type)).toEqual(["pptx", "pptx"]); });
  it("requires a published PPTX representation before exposing a successful export state", () => {
    const withoutExport = parsePresentationJobResult(job("Succeeded", '{"assetId":"asset-1","title":"Deck"}'));
    const withNonPptxExport = parsePresentationJobResult(job("Succeeded", '{"assetId":"asset-1","representations":[{"id":"pdf-1","type":"pdf","fileName":"deck.pdf","contentType":"application/pdf"}]}'));
    const withPptxJson = '{"assetId":"asset-1","representations":[{"id":"pptx-1","type":"pptx","fileName":"deck.pptx","contentType":"application/vnd.openxmlformats-officedocument.presentationml.presentation"}]}';
    const withPptxExport = parsePresentationJobResult(job("Succeeded", withPptxJson));
    expect(hasPresentationExport(withoutExport)).toBe(false);
    expect(hasPresentationExport(withNonPptxExport)).toBe(false);
    expect(hasPresentationExport(withPptxExport)).toBe(true);
    expect(presentationStudioState(job("Succeeded", '{"assetId":"asset-1"}'), withoutExport)).toBe("completed-unavailable");
    expect(presentationStudioState(job("Succeeded", withPptxJson), withPptxExport)).toBe("succeeded");
  });
  it("treats malformed result as completed-unavailable rather than succeeded", () => { const current = job("Succeeded", "not-json", 100); expect(parsePresentationJobResult(current)).toBeNull(); expect(presentationStudioState(current, null)).toBe("completed-unavailable"); });
  it("accepts only ready extracted supported source files", () => { expect(isPresentationSourceReady({ extension: ".xlsx", status: "Ready", textExtractionStatus: "Ready" })).toBe(true); expect(isPresentationSourceReady({ extension: ".png", status: "Ready", textExtractionStatus: "Ready" })).toBe(false); expect(isPresentationSourceReady({ extension: ".pdf", status: "Processing", textExtractionStatus: "Ready" })).toBe(false); });
  it("validates restored jobs and keeps active-job storage workspace-scoped", () => {
    expect(isPresentationJob(job("Running"))).toBe(true);
    expect(isRestorablePresentationJob(job("Running"), "workspace-1")).toBe(true);
    expect(isRestorablePresentationJob({ ...job("Running"), workspaceId: "workspace-2" }, "workspace-1")).toBe(false);
    expect(isPresentationJob({ ...job("Running"), jobType: "research.generate" })).toBe(false);
    expect(shouldResetPresentationWorkspaceState(job("Running"), "workspace-2")).toBe(true);
    expect(shouldResetPresentationWorkspaceState(job("Running"), "workspace-1")).toBe(false);
    expect(shouldResetPresentationWorkspaceState(null, "workspace-2")).toBe(false);
    const values = new Map<string, string>();
    vi.stubGlobal("window", { sessionStorage: { getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => values.set(key, value), removeItem: (key: string) => values.delete(key) } });
    persistPresentationActiveJobId("workspace-1", "presentation-job-1");
    expect(values.get(presentationActiveJobStorageKey("workspace-1"))).toBe("presentation-job-1");
    expect(readPresentationActiveJobId("workspace-1")).toBe("presentation-job-1");
    expect(readPresentationActiveJobId("workspace-2")).toBeNull();
    clearPresentationActiveJobId("workspace-1");
    expect(readPresentationActiveJobId("workspace-1")).toBeNull();
  });
  it("recovers a bounded draft and rejects malformed or unsupported browser data", () => {
    const state = parsePresentationDraftState(JSON.stringify({
      projectId: "project-1",
      selected: ["source-1", "source-1", "source-2", "source-3", "source-4", "source-5", "source-6", 7],
      title: "x".repeat(200),
      description: "brief",
      presentationType: "not-supported",
      length: "detailed",
      tone: "formal",
      language: "ar",
      audience: "audience",
      includeAgenda: false,
      includeClosingNextSteps: "no",
      extra: "ignored",
    }));
    expect(state).toMatchObject({ projectId: "project-1", selected: ["source-1", "source-2", "source-3", "source-4", "source-5"], title: "x".repeat(160), presentationType: "auto", length: "detailed", tone: "formal", language: "ar", includeAgenda: false, includeClosingNextSteps: true });
    expect(parsePresentationDraftState("not-json")).toEqual(emptyPresentationDraftState());
    expect(parsePresentationDraftState(JSON.stringify({ selected: {}, title: 42 }))).toEqual(emptyPresentationDraftState());
  });
  it("persists only the supported draft shape and isolates workspaces", () => {
    const values = new Map<string, string>();
    const browserStorage = {
      getItem: (key: string) => values.get(key) ?? null,
      setItem: (key: string, value: string) => { values.set(key, value); },
      removeItem: (key: string) => { values.delete(key); },
    };
    const first = { ...emptyPresentationDraftState(), title: "Launch", description: "Plan", selected: ["source-1"], includeAgenda: false };
    persistPresentationDraftState("workspace-a", first, browserStorage);
    persistPresentationDraftState("workspace-b", { ...emptyPresentationDraftState(), title: "Other" }, browserStorage);
    expect(presentationDraftStorageKey("workspace-a")).not.toBe(presentationDraftStorageKey("workspace-b"));
    expect(loadPresentationDraftState("workspace-a", browserStorage)).toMatchObject(first);
    expect(loadPresentationDraftState("workspace-b", browserStorage).title).toBe("Other");
    expect(JSON.parse(serializePresentationDraftState({ ...first, extra: "not persisted" } as typeof first & { extra: string })).extra).toBeUndefined();
    clearPresentationDraftState("workspace-a", browserStorage);
    expect(loadPresentationDraftState("workspace-a", browserStorage)).toEqual(emptyPresentationDraftState());
    expect(loadPresentationDraftState("workspace-b", browserStorage).title).toBe("Other");
  });
});
