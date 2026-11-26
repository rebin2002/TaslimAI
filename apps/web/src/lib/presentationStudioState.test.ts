import { afterEach, describe, expect, it, vi } from "vitest";
import type { GenerationJob } from "./api";
import { canCancelPresentationJob, clearPresentationActiveJobId, clearPresentationDraftState, displayPresentationProgress, emptyPresentationDraftState, hasPresentationExport, isPresentationCancellationNoOp, isPresentationJob, isPresentationSourceReady, isRestorablePresentationJob, loadPresentationDraftState, markPresentationCancellationRequested, markPresentationCancellationRequestedForJob, parsePresentationDraftState, parsePresentationJobResult, persistPresentationActiveJobId, persistPresentationDraftState, presentationActiveJobStorageKey, presentationDraftStorageKey, presentationStudioState, readPresentationActiveJobId, resolvePresentationJobSnapshot, serializePresentationDraftState, shouldResetPresentationWorkspaceState } from "./presentationStudioState";

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
    // A previous account's same-workspace job must never trigger a protected
    // GET during a new user's restore, even if an older workspace-only key exists.
    values.set("taslim:presentation-generation:workspace-1", "legacy-user-job");
    persistPresentationActiveJobId("user-a", "workspace-1", "presentation-job-1");
    expect(values.get(presentationActiveJobStorageKey("user-a", "workspace-1"))).toBe("presentation-job-1");
    expect(readPresentationActiveJobId("user-a", "workspace-1")).toBe("presentation-job-1");
    expect(readPresentationActiveJobId("user-a", "workspace-2")).toBeNull();
    expect(readPresentationActiveJobId("user-b", "workspace-1")).toBeNull();
    expect(readPresentationActiveJobId("", "workspace-1")).toBeNull();
    expect(readPresentationActiveJobId("user-a", "")).toBeNull();
    persistPresentationActiveJobId("user-b", "workspace-1", "user-b-job");
    expect(readPresentationActiveJobId("user-b", "workspace-1")).toBe("user-b-job");
    clearPresentationActiveJobId("user-a", "workspace-1");
    expect(readPresentationActiveJobId("user-a", "workspace-1")).toBeNull();
    expect(readPresentationActiveJobId("user-b", "workspace-1")).toBe("user-b-job");
    expect(values.get("taslim:presentation-generation:workspace-1")).toBe("legacy-user-job");
    persistPresentationActiveJobId("", "workspace-1", "ignored");
    expect(readPresentationActiveJobId("", "workspace-1")).toBeNull();
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
  it("persists only supported draft data and isolates both users and workspaces", () => {
    const values = new Map<string, string>();
    const browserStorage = {
      getItem: (key: string) => values.get(key) ?? null,
      setItem: (key: string, value: string) => { values.set(key, value); },
      removeItem: (key: string) => { values.delete(key); },
    };
    const first = { ...emptyPresentationDraftState(), title: "Launch", description: "Plan", selected: ["source-1"], includeAgenda: false };
    persistPresentationDraftState("user-a", "workspace-a", first, browserStorage);
    persistPresentationDraftState("user-a", "workspace-b", { ...emptyPresentationDraftState(), title: "Other" }, browserStorage);
    persistPresentationDraftState("user-b", "workspace-a", { ...emptyPresentationDraftState(), title: "Private B" }, browserStorage);
    expect(presentationDraftStorageKey("user-a", "workspace-a")).not.toBe(presentationDraftStorageKey("user-a", "workspace-b"));
    expect(presentationDraftStorageKey("user-a", "workspace-a")).not.toBe(presentationDraftStorageKey("user-b", "workspace-a"));
    expect(loadPresentationDraftState("user-a", "workspace-a", browserStorage)).toMatchObject(first);
    expect(loadPresentationDraftState("user-a", "workspace-b", browserStorage).title).toBe("Other");
    expect(loadPresentationDraftState("user-b", "workspace-a", browserStorage).title).toBe("Private B");
    expect(loadPresentationDraftState("", "workspace-a", browserStorage)).toEqual(emptyPresentationDraftState());
    expect(loadPresentationDraftState("user-a", "", browserStorage)).toEqual(emptyPresentationDraftState());
    persistPresentationDraftState("", "workspace-a", first, browserStorage);
    expect(loadPresentationDraftState("user-a", "workspace-a", browserStorage)).toMatchObject(first);
    expect(JSON.parse(serializePresentationDraftState({ ...first, extra: "not persisted" } as typeof first & { extra: string })).extra).toBeUndefined();
    clearPresentationDraftState("user-a", "workspace-a", browserStorage);
    expect(loadPresentationDraftState("user-a", "workspace-a", browserStorage)).toEqual(emptyPresentationDraftState());
    expect(loadPresentationDraftState("user-b", "workspace-a", browserStorage).title).toBe("Private B");
    expect(loadPresentationDraftState("user-a", "workspace-b", browserStorage).title).toBe("Other");
  });
  describe("cancellation against generated-file publication", () => {
    const publishedJson = '{"assetId":"asset-1","representations":[{"id":"pptx-1","type":"pptx","fileName":"deck.pptx","contentType":"application/vnd.openxmlformats-officedocument.presentationml.presentation"}]}';

    it("never lets a pre-cancellation snapshot undo a terminal job", () => {
      const cancelled = markPresentationCancellationRequested(job("Cancelled"));
      const staleRunning = job("Running");
      const stalePublished = job("Succeeded", publishedJson);

      expect(resolvePresentationJobSnapshot(cancelled, staleRunning)).toBe(cancelled);
      expect(resolvePresentationJobSnapshot(cancelled, stalePublished)).toBe(cancelled);
      expect(presentationStudioState(resolvePresentationJobSnapshot(cancelled, stalePublished), parsePresentationJobResult(stalePublished))).toBe("cancelled");
      // The first committed terminal outcome wins: the server never re-opens a job.
      expect(resolvePresentationJobSnapshot(stalePublished, cancelled)).toBe(stalePublished);
    });

    it("refreshes fields when the server repeats the same terminal status", () => {
      const degraded = job("Succeeded", "not-json");
      const repaired = job("Succeeded", publishedJson);
      const merged = resolvePresentationJobSnapshot(degraded, repaired);

      expect(merged).toBe(repaired);
      expect(presentationStudioState(merged, parsePresentationJobResult(merged))).toBe("succeeded");
    });

    it("keeps an accepted cancellation sticky until the job settles", () => {
      const cancelling = markPresentationCancellationRequested(job("Running"));
      const staleRunning = job("Running");

      expect(cancelling?.cancellationRequested).toBe(true);
      expect(canCancelPresentationJob(cancelling)).toBe(false);
      expect(resolvePresentationJobSnapshot(cancelling, staleRunning)).toEqual({ ...staleRunning, cancellationRequested: true });
      expect(resolvePresentationJobSnapshot(cancelling, job("Cancelled"))?.status).toBe("Cancelled");
      expect(markPresentationCancellationRequested(cancelling)).toBe(cancelling);
      expect(markPresentationCancellationRequested(null)).toBeNull();
    });

    it("binds an accepted cancellation to the job that issued it", () => {
      const markIssuedCancellation = markPresentationCancellationRequestedForJob("job-1");
      const running = job("Running");
      const otherSessionJob = { ...job("Running"), id: "job-2" };

      // The issuing job keeps the sticky cancellation and loses its cancel control.
      expect(markIssuedCancellation(running)?.cancellationRequested).toBe(true);
      expect(canCancelPresentationJob(markIssuedCancellation(running))).toBe(false);
      // A different job now occupying the same state slot is left untouched, so the
      // user can still cancel the generation they actually started.
      expect(markIssuedCancellation(otherSessionJob)).toBe(otherSessionJob);
      expect(canCancelPresentationJob(otherSessionJob)).toBe(true);
      expect(resolvePresentationJobSnapshot(markIssuedCancellation(otherSessionJob), running)).toBe(running);
      // A settled job is never re-opened by a late cancellation, and no job is null-safe.
      const published = job("Succeeded", publishedJson);
      expect(markIssuedCancellation(published)).toBe(published);
      expect(markIssuedCancellation(null)).toBeNull();
    });

    it("is idempotent for repeated snapshots and never merges across jobs", () => {
      const running = job("Running");
      const cancelling = { ...running, cancellationRequested: true };
      const once = resolvePresentationJobSnapshot(cancelling, running);

      expect(resolvePresentationJobSnapshot(once, running)).toEqual(once);
      expect(resolvePresentationJobSnapshot(once, once)).toBe(once);
      expect(resolvePresentationJobSnapshot(running, running)).toBe(running);
      const otherJob = { ...job("Pending"), id: "job-2" };
      expect(resolvePresentationJobSnapshot(cancelling, otherJob)).toBe(otherJob);
      expect(resolvePresentationJobSnapshot(cancelling, null)).toBe(cancelling);
      expect(resolvePresentationJobSnapshot(null, otherJob)).toBe(otherJob);
    });

    it("treats a cancellation that raced completion as a no-op rather than a failure", () => {
      expect(isPresentationCancellationNoOp(409, "JOB_NOT_CANCELLABLE")).toBe(true);
      expect(isPresentationCancellationNoOp(409)).toBe(true);
      expect(isPresentationCancellationNoOp(409, "PRESENTATION_LOCKED")).toBe(false);
      expect(isPresentationCancellationNoOp(404, "JOB_NOT_FOUND")).toBe(false);
      expect(isPresentationCancellationNoOp(500)).toBe(false);
    });
  });
});
