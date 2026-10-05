import type { GenerationJob, PresentationJobResult } from "./api";

const terminalStatuses = new Set<GenerationJob["status"]>(["Succeeded", "Failed", "Cancelled"]);
const activeJobStoragePrefix = "taslim:presentation-generation:";
export type PresentationStudioState = "compose" | "pending" | "queued" | "running" | "succeeded" | "completed-unavailable" | "failed" | "cancelled";

export function presentationActiveJobStorageKey(workspaceId: string) { return `${activeJobStoragePrefix}${workspaceId}`; }
export function readPresentationActiveJobId(workspaceId: string) {
  if (typeof window === "undefined") return null;
  try {
    const jobId = window.sessionStorage.getItem(presentationActiveJobStorageKey(workspaceId));
    return jobId?.trim() || null;
  } catch {
    return null;
  }
}
export function persistPresentationActiveJobId(workspaceId: string, jobId: string) {
  if (typeof window === "undefined" || !jobId.trim()) return;
  try { window.sessionStorage.setItem(presentationActiveJobStorageKey(workspaceId), jobId); } catch { /* Storage may be unavailable. */ }
}
export function clearPresentationActiveJobId(workspaceId: string) {
  if (typeof window === "undefined") return;
  try { window.sessionStorage.removeItem(presentationActiveJobStorageKey(workspaceId)); } catch { /* Storage may be unavailable. */ }
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
export function isPresentationTerminal(job: GenerationJob | null) { return !!job && terminalStatuses.has(job.status); }
export function shouldPollPresentationJob(job: GenerationJob | null) { return !!job && !isPresentationTerminal(job); }
export function nextPresentationPollDelay(job: GenerationJob | null, retryAttempt = 0) { return shouldPollPresentationJob(job) ? Math.min(700 * Math.max(1, retryAttempt + 1), 2_800) : null; }
export function canCancelPresentationJob(job: GenerationJob | null) { return !!job && ["Pending", "Queued", "Running"].includes(job.status) && !job.cancellationRequested; }
export function displayPresentationProgress(job: GenerationJob | null) { if (!job) return 0; const progress = Math.max(0, Math.min(100, job.progressPercent)); return isPresentationTerminal(job) ? progress : Math.min(progress, 99); }
export function presentationStudioState(job: GenerationJob | null, result: PresentationJobResult | null): PresentationStudioState {
  if (!job) return "compose";
  if (job.status === "Pending") return "pending";
  if (job.status === "Queued") return "queued";
  if (job.status === "Running") return "running";
  if (job.status === "Failed") return "failed";
  if (job.status === "Cancelled") return "cancelled";
  return result?.assetId ? "succeeded" : "completed-unavailable";
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
