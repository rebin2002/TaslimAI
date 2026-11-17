import type { GenerationJob, PresentationJobResult } from "./api";

const terminalStatuses = new Set<GenerationJob["status"]>(["Succeeded", "Failed", "Cancelled"]);
const activeJobStoragePrefix = "taslim:presentation-generation:";
const draftStoragePrefix = "taslim:presentation-draft:";
const presentationTypes = new Set(["auto", "business", "company_profile", "sales", "investor", "proposal", "training", "project_update", "report", "educational", "general"]);
const presentationLengths = new Set(["short", "standard", "detailed"]);
const presentationTones = new Set(["professional", "formal", "friendly", "persuasive", "neutral"]);
const presentationLanguages = new Set(["auto", "en", "ar", "ku"]);
type StorageLike = Pick<Storage, "getItem" | "setItem" | "removeItem">;
export type PresentationDraftState = {
  projectId: string;
  selected: string[];
  title: string;
  description: string;
  presentationType: string;
  length: string;
  tone: string;
  language: string;
  audience: string;
  brandCompany: string;
  additionalInstructions: string;
  includeAgenda: boolean;
  includeClosingNextSteps: boolean;
};
export function emptyPresentationDraftState(): PresentationDraftState {
  return {
    projectId: "",
    selected: [],
    title: "",
    description: "",
    presentationType: "auto",
    length: "standard",
    tone: "professional",
    language: "auto",
    audience: "",
    brandCompany: "",
    additionalInstructions: "",
    includeAgenda: true,
    includeClosingNextSteps: true,
  };
}
export function presentationDraftStorageKey(userId: string, workspaceId: string) { return `${draftStoragePrefix}${encodeURIComponent(userId)}:${encodeURIComponent(workspaceId)}`; }
function boundedString(value: unknown, maxLength: number) { return typeof value === "string" ? value.slice(0, maxLength) : ""; }
function safeIdentifier(value: unknown) { return typeof value === "string" && value.trim().length > 0 && value.length <= 100 ? value : ""; }
export function parsePresentationDraftState(raw: string | null): PresentationDraftState {
  const fallback = emptyPresentationDraftState();
  if (!raw) return fallback;
  try {
    const parsed: unknown = JSON.parse(raw);
    if (!isRecord(parsed)) return fallback;
    const selected = Array.isArray(parsed.selected) ? [...new Set(parsed.selected.map(safeIdentifier).filter(Boolean))].slice(0, 5) : [];
    return {
      projectId: safeIdentifier(parsed.projectId),
      selected,
      title: boundedString(parsed.title, 160),
      description: boundedString(parsed.description, 8_000),
      presentationType: typeof parsed.presentationType === "string" && presentationTypes.has(parsed.presentationType) ? parsed.presentationType : fallback.presentationType,
      length: typeof parsed.length === "string" && presentationLengths.has(parsed.length) ? parsed.length : fallback.length,
      tone: typeof parsed.tone === "string" && presentationTones.has(parsed.tone) ? parsed.tone : fallback.tone,
      language: typeof parsed.language === "string" && presentationLanguages.has(parsed.language) ? parsed.language : fallback.language,
      audience: boundedString(parsed.audience, 400),
      brandCompany: boundedString(parsed.brandCompany, 160),
      additionalInstructions: boundedString(parsed.additionalInstructions, 3_000),
      includeAgenda: parsed.includeAgenda === false ? false : fallback.includeAgenda,
      includeClosingNextSteps: parsed.includeClosingNextSteps === false ? false : fallback.includeClosingNextSteps,
    };
  } catch {
    return fallback;
  }
}
export function serializePresentationDraftState(state: PresentationDraftState) {
  return JSON.stringify({
    projectId: safeIdentifier(state.projectId),
    selected: [...new Set(state.selected.map(safeIdentifier).filter(Boolean))].slice(0, 5),
    title: boundedString(state.title, 160),
    description: boundedString(state.description, 8_000),
    presentationType: presentationTypes.has(state.presentationType) ? state.presentationType : "auto",
    length: presentationLengths.has(state.length) ? state.length : "standard",
    tone: presentationTones.has(state.tone) ? state.tone : "professional",
    language: presentationLanguages.has(state.language) ? state.language : "auto",
    audience: boundedString(state.audience, 400),
    brandCompany: boundedString(state.brandCompany, 160),
    additionalInstructions: boundedString(state.additionalInstructions, 3_000),
    includeAgenda: state.includeAgenda === true,
    includeClosingNextSteps: state.includeClosingNextSteps === true,
  });
}
function browserStorage(): StorageLike | undefined {
  if (typeof window === "undefined") return undefined;
  try { return window.localStorage; } catch { return undefined; }
}
export function loadPresentationDraftState(userId: string, workspaceId: string, storage: StorageLike | undefined = browserStorage()): PresentationDraftState {
  if (!storage || !userId.trim() || !workspaceId.trim()) return emptyPresentationDraftState();
  try { return parsePresentationDraftState(storage.getItem(presentationDraftStorageKey(userId, workspaceId))); } catch { return emptyPresentationDraftState(); }
}
export function persistPresentationDraftState(userId: string, workspaceId: string, state: PresentationDraftState, storage: StorageLike | undefined = browserStorage()): void {
  if (!storage || !userId.trim() || !workspaceId.trim()) return;
  try { storage.setItem(presentationDraftStorageKey(userId, workspaceId), serializePresentationDraftState(state)); } catch { /* Browser storage can be unavailable or full. */ }
}
export function clearPresentationDraftState(userId: string, workspaceId: string, storage: StorageLike | undefined = browserStorage()): void {
  if (!storage || !userId.trim() || !workspaceId.trim()) return;
  try { storage.removeItem(presentationDraftStorageKey(userId, workspaceId)); } catch { /* Clearing is best effort. */ }
}
export type PresentationStudioState = "compose" | "pending" | "queued" | "running" | "succeeded" | "completed-unavailable" | "failed" | "cancelled";

// Active generation IDs are private to the authenticated principal as well as
// its workspace. Never read legacy workspace-only keys: they can belong to a
// previous account using this browser and would trigger stale-scope GETs.
export function presentationActiveJobStorageKey(userId: string, workspaceId: string) {
  return `${activeJobStoragePrefix}${encodeURIComponent(userId)}:${encodeURIComponent(workspaceId)}`;
}
export function readPresentationActiveJobId(userId: string, workspaceId: string) {
  if (typeof window === "undefined" || !userId.trim() || !workspaceId.trim()) return null;
  try {
    const jobId = window.sessionStorage.getItem(presentationActiveJobStorageKey(userId, workspaceId));
    return jobId?.trim() || null;
  } catch {
    return null;
  }
}
export function persistPresentationActiveJobId(userId: string, workspaceId: string, jobId: string) {
  if (typeof window === "undefined" || !userId.trim() || !workspaceId.trim() || !jobId.trim()) return;
  try { window.sessionStorage.setItem(presentationActiveJobStorageKey(userId, workspaceId), jobId); } catch { /* Storage may be unavailable. */ }
}
export function clearPresentationActiveJobId(userId: string, workspaceId: string) {
  if (typeof window === "undefined" || !userId.trim() || !workspaceId.trim()) return;
  try { window.sessionStorage.removeItem(presentationActiveJobStorageKey(userId, workspaceId)); } catch { /* Storage may be unavailable. */ }
}
export function isPresentationJob(job: GenerationJob | null) {
  return job?.jobType.trim().toLowerCase() === "presentation.generate";
}
export function isRestorablePresentationJob(job: GenerationJob | null, workspaceId: string) {
  return !!job && job.workspaceId === workspaceId && isPresentationJob(job);
}
export function shouldResetPresentationWorkspaceState(job: GenerationJob | null, workspaceId: string | null) {
  return !!job && (!workspaceId || !isRestorablePresentationJob(job, workspaceId));
}
export function hasPresentationExport(result: PresentationJobResult | null) {
  return !!result?.assetId && result.representations?.some((representation) => representation.type === "pptx") === true;
}
export function isPresentationTerminal(job: GenerationJob | null) { return !!job && terminalStatuses.has(job.status); }
export function shouldPollPresentationJob(job: GenerationJob | null) { return !!job && !isPresentationTerminal(job); }
export function nextPresentationPollDelay(job: GenerationJob | null, retryAttempt = 0) { return shouldPollPresentationJob(job) ? Math.min(700 * Math.max(1, retryAttempt + 1), 2_800) : null; }
export function canCancelPresentationJob(job: GenerationJob | null) { return !!job && ["Pending", "Queued", "Running"].includes(job.status) && !job.cancellationRequested; }

// Presentation job snapshots can reach the browser out of order: a poll issued
// before the user pressed Cancel can resolve after the cancel request was
// accepted, and a snapshot taken before the worker published the generated file
// can resolve after it. The server keeps a cancelled job cancelled and never
// re-opens a terminal job, so the client merges snapshots monotonically instead
// of overwriting them. Re-applying the same snapshot, or an older one after a
// newer one, is a no-op.
export function resolvePresentationJobSnapshot(current: GenerationJob | null, incoming: GenerationJob | null): GenerationJob | null {
  if (!incoming) return current;
  if (!current || current.id !== incoming.id) return incoming;
  if (isPresentationTerminal(current)) return current.status === incoming.status ? incoming : current;
  if (isPresentationTerminal(incoming)) return incoming;
  return incoming.cancellationRequested || !current.cancellationRequested ? incoming : { ...incoming, cancellationRequested: true };
}

// A cancellation the server accepted stays requested until the job reports its
// terminal outcome, so a pre-cancellation response cannot re-arm the action.
export function markPresentationCancellationRequested(current: GenerationJob | null): GenerationJob | null {
  if (!current || isPresentationTerminal(current) || current.cancellationRequested) return current;
  return { ...current, cancellationRequested: true };
}

// A cancellation belongs to the job that issued it. The marker runs against the
// live state rather than the snapshot captured before the request, so an accepted
// cancellation must never mark a different job as cancelling. A cancel request
// that settles after the state slot changed hands - the job was cleared by a
// 403/404 poll and a new brief started, or a second submission installed its own
// job - would otherwise strand that other job with a sticky cancellation nobody
// asked for: hiding its cancel control, so a separately chargeable generation can
// no longer be stopped, and keeping the monotonic snapshot merge in the
// cancelling path. Server job ids are unique, so binding on the id keeps the
// accepted cancellation on its own job.
export function markPresentationCancellationRequestedForJob(jobId: string) {
  return (current: GenerationJob | null): GenerationJob | null =>
    (current && current.id === jobId ? markPresentationCancellationRequested(current) : current);
}

// Cancelling a job that already settled is an idempotent no-op, not a failed
// cancellation: the caller adopts the authoritative job state instead.
export function isPresentationCancellationNoOp(status: number, code?: string | null) {
  return status === 409 && (!code || code === "JOB_NOT_CANCELLABLE");
}

export function displayPresentationProgress(job: GenerationJob | null) { if (!job) return 0; const progress = Math.max(0, Math.min(100, job.progressPercent)); return isPresentationTerminal(job) ? progress : Math.min(progress, 99); }
export function presentationStudioState(job: GenerationJob | null, result: PresentationJobResult | null): PresentationStudioState {
  if (!job) return "compose";
  if (job.status === "Pending") return "pending";
  if (job.status === "Queued") return "queued";
  if (job.status === "Running") return "running";
  if (job.status === "Failed") return "failed";
  if (job.status === "Cancelled") return "cancelled";
  return hasPresentationExport(result) ? "succeeded" : "completed-unavailable";
}
function isRecord(value: unknown): value is Record<string, unknown> { return typeof value === "object" && value !== null; }
function nonEmptyString(value: unknown): value is string { return typeof value === "string" && value.trim().length > 0; }
function normalizedRepresentationType(type: string | null, fileName: string, contentType: string): string | null {
  const candidates = [type, fileName.split(".").pop() ?? null, contentType].filter((value): value is string => !!value).map((value) => value.trim().toLowerCase());
  for (const candidate of candidates) {
    if (candidate === "pptx" || candidate === "application/vnd.openxmlformats-officedocument.presentationml.presentation") return "pptx";
    if (candidate === "pdf" || candidate === "application/pdf") return "pdf";
    if (candidate === "docx" || candidate === "application/vnd.openxmlformats-officedocument.wordprocessingml.document") return "docx";
  }
  const fallback = candidates[0];
  return fallback && /^[a-z0-9][a-z0-9._-]{0,29}$/.test(fallback) ? fallback : null;
}
function parseBlocks(value: unknown): NonNullable<NonNullable<PresentationJobResult["previewSlides"]>[number]["blocks"]> {
  if (!Array.isArray(value)) return [];
  return value.flatMap((block) => {
    if (!isRecord(block) || !nonEmptyString(block.type)) return [];
    const text = typeof block.text === "string" ? block.text : null;
    const items = Array.isArray(block.items) ? block.items.filter(nonEmptyString) : undefined;
    const columns = Array.isArray(block.columns) ? block.columns.flatMap((column) => isRecord(column) && nonEmptyString(column.heading) && Array.isArray(column.items) ? [{ heading: column.heading, items: column.items.filter(nonEmptyString) }] : []) : undefined;
    const rows = Array.isArray(block.rows) ? block.rows.flatMap((row) => isRecord(row) && Array.isArray(row.cells) ? [{ cells: row.cells.filter(nonEmptyString) }] : []) : undefined;
    const metrics = Array.isArray(block.metrics) ? block.metrics.flatMap((metric) => isRecord(metric) && nonEmptyString(metric.label) && nonEmptyString(metric.value) ? [{ label: metric.label, value: metric.value, detail: typeof metric.detail === "string" ? metric.detail : null }] : []) : undefined;
    return [{ type: block.type, text, items, columns, rows, metrics, label: typeof block.label === "string" ? block.label : null, value: typeof block.value === "string" ? block.value : null }];
  });
}
function parseRepresentations(value: unknown): NonNullable<PresentationJobResult["representations"]> {
  if (!Array.isArray(value)) return [];
  return value.flatMap((representation) => {
    if (!isRecord(representation) || !nonEmptyString(representation.id) || !nonEmptyString(representation.fileName) || !nonEmptyString(representation.contentType)) return [];
    const type = normalizedRepresentationType(typeof representation.type === "string" ? representation.type : null, representation.fileName, representation.contentType);
    return type ? [{ id: representation.id, type, fileName: representation.fileName, contentType: representation.contentType }] : [];
  });
}
export function parsePresentationJobResult(job: GenerationJob | null): PresentationJobResult | null {
  if (!job?.resultJson) return null;
  try {
    const parsed: unknown = JSON.parse(job.resultJson);
    if (!isRecord(parsed)) return null;
    const previewSlides = Array.isArray(parsed.previewSlides) ? parsed.previewSlides.flatMap((slide) => isRecord(slide) && typeof slide.order === "number" && nonEmptyString(slide.type) && nonEmptyString(slide.title) ? [{ order: slide.order, type: slide.type, title: slide.title, subtitle: typeof slide.subtitle === "string" ? slide.subtitle : null, blocks: parseBlocks(slide.blocks) }] : []) : [];
    return { assetId: nonEmptyString(parsed.assetId) ? parsed.assetId : undefined, presentationType: typeof parsed.presentationType === "string" ? parsed.presentationType : undefined, title: typeof parsed.title === "string" ? parsed.title : undefined, subtitle: typeof parsed.subtitle === "string" ? parsed.subtitle : null, language: typeof parsed.language === "string" ? parsed.language : undefined, slideCount: typeof parsed.slideCount === "number" ? parsed.slideCount : undefined, previewSlides, representations: parseRepresentations(parsed.representations) };
  } catch { return null; }
}
export function isPresentationSourceReady(file: { extension: string; status: string; textExtractionStatus: string }) { return [".pdf", ".docx", ".txt", ".md", ".csv", ".xlsx"].includes(file.extension.toLowerCase()) && file.status === "Ready" && file.textExtractionStatus === "Ready"; }
