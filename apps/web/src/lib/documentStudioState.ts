import type { DocumentJobResult, GenerationJob } from "./api";

const terminalStatuses = new Set<GenerationJob["status"]>(["Succeeded", "Failed", "Cancelled"]);
const activeJobStoragePrefix = "taslim:document-generation:";
const draftStoragePrefix = "taslim:document-draft:";
const documentDraftTypes = new Set(["auto", "report", "proposal", "business_letter", "company_profile", "meeting_minutes", "article", "general"]);
const documentDraftLengths = new Set(["short", "standard", "detailed"]);
const documentDraftLanguages = new Set(["auto", "en", "ar", "ku"]);
const documentDraftOutputFormats = new Set(["docx", "pdf", "both"]);
const documentDraftTones = new Set(["professional", "formal", "friendly", "persuasive", "neutral"]);
type StorageLike = Pick<Storage, "getItem" | "setItem" | "removeItem">;

// The compose brief is unsaved work: nothing exists on the server until a
// generation job is submitted. The draft therefore stays browser-local, keyed
// by the workspace that owns it - the same boundary as the active generation
// job - so switching tabs away and back can restore it without ever letting
// another workspace read it.
export type DocumentDraftState = {
  projectId: string;
  selected: string[];
  title: string;
  prompt: string;
  documentType: string;
  length: string;
  audience: string;
  additionalInstructions: string;
  language: string;
  outputFormat: string;
  tone: string;
  includeTableOfContents: boolean;
};

export function emptyDocumentDraftState(): DocumentDraftState {
  return {
    projectId: "",
    selected: [],
    title: "",
    prompt: "",
    documentType: "auto",
    length: "standard",
    audience: "",
    additionalInstructions: "",
    language: "auto",
    outputFormat: "both",
    tone: "professional",
    includeTableOfContents: true,
  };
}

export function documentDraftStorageKey(workspaceId: string) {
  return `${draftStoragePrefix}${encodeURIComponent(workspaceId)}`;
}

// A brief that still matches every default holds nothing the user wrote, so it
// is not a draft: nothing is stored for it, and the stored copy is dropped as
// soon as the editor returns to this state.
export function isDocumentDraftEmpty(state: DocumentDraftState) {
  return serializeDocumentDraftState(state) === serializeDocumentDraftState(emptyDocumentDraftState());
}

function draftIdentifier(value: unknown) {
  return typeof value === "string" && value.trim().length > 0 && value.length <= 100 ? value : "";
}

function boundedDraftText(value: unknown, maxLength: number) {
  return typeof value === "string" ? value.slice(0, maxLength) : "";
}

export function parseDocumentDraftState(raw: string | null): DocumentDraftState {
  const fallback = emptyDocumentDraftState();
  if (!raw) return fallback;
  try {
    const parsed: unknown = JSON.parse(raw);
    if (!isRecord(parsed)) return fallback;
    const selected = Array.isArray(parsed.selected) ? [...new Set(parsed.selected.map(draftIdentifier).filter(Boolean))].slice(0, 5) : [];
    return {
      projectId: draftIdentifier(parsed.projectId),
      selected,
      title: boundedDraftText(parsed.title, 160),
      prompt: boundedDraftText(parsed.prompt, 8_000),
      documentType: typeof parsed.documentType === "string" && documentDraftTypes.has(parsed.documentType) ? parsed.documentType : fallback.documentType,
      length: typeof parsed.length === "string" && documentDraftLengths.has(parsed.length) ? parsed.length : fallback.length,
      audience: boundedDraftText(parsed.audience, 400),
      additionalInstructions: boundedDraftText(parsed.additionalInstructions, 3_000),
      language: typeof parsed.language === "string" && documentDraftLanguages.has(parsed.language) ? parsed.language : fallback.language,
      outputFormat: typeof parsed.outputFormat === "string" && documentDraftOutputFormats.has(parsed.outputFormat) ? parsed.outputFormat : fallback.outputFormat,
      tone: typeof parsed.tone === "string" && documentDraftTones.has(parsed.tone) ? parsed.tone : fallback.tone,
      includeTableOfContents: parsed.includeTableOfContents === false ? false : fallback.includeTableOfContents,
    };
  } catch {
    return fallback;
  }
}

export function serializeDocumentDraftState(state: DocumentDraftState) {
  return JSON.stringify({
    projectId: draftIdentifier(state.projectId),
    selected: [...new Set(state.selected.map(draftIdentifier).filter(Boolean))].slice(0, 5),
    title: boundedDraftText(state.title, 160),
    prompt: boundedDraftText(state.prompt, 8_000),
    documentType: documentDraftTypes.has(state.documentType) ? state.documentType : "auto",
    length: documentDraftLengths.has(state.length) ? state.length : "standard",
    audience: boundedDraftText(state.audience, 400),
    additionalInstructions: boundedDraftText(state.additionalInstructions, 3_000),
    language: documentDraftLanguages.has(state.language) ? state.language : "auto",
    outputFormat: documentDraftOutputFormats.has(state.outputFormat) ? state.outputFormat : "both",
    tone: documentDraftTones.has(state.tone) ? state.tone : "professional",
    includeTableOfContents: state.includeTableOfContents === true,
  });
}

function browserSessionStorage(): StorageLike | undefined {
  if (typeof window === "undefined") return undefined;
  try {
    return window.sessionStorage;
  } catch {
    return undefined;
  }
}

export function readDocumentDraftState(workspaceId: string, storage: StorageLike | undefined = browserSessionStorage()): DocumentDraftState {
  if (!storage || !workspaceId.trim()) return emptyDocumentDraftState();
  try {
    const key = documentDraftStorageKey(workspaceId);
    const raw = storage.getItem(key);
    if (raw === null) return emptyDocumentDraftState();
    const draft = parseDocumentDraftState(raw);
    // A stored value that yields nothing restorable - unreadable, or a brief
    // that has since been emptied - is dropped instead of being kept as trash
    // another mount might read back.
    if (isDocumentDraftEmpty(draft)) storage.removeItem(key);
    return draft;
  } catch {
    return emptyDocumentDraftState();
  }
}

export function persistDocumentDraftState(workspaceId: string, state: DocumentDraftState, storage: StorageLike | undefined = browserSessionStorage()): void {
  if (!storage || !workspaceId.trim()) return;
  try {
    // A brief that still holds no user work is not a draft: it is never stored,
    // and the stored copy goes away as soon as the editor is emptied again.
    if (isDocumentDraftEmpty(state)) {
      storage.removeItem(documentDraftStorageKey(workspaceId));
      return;
    }
    storage.setItem(documentDraftStorageKey(workspaceId), serializeDocumentDraftState(state));
  } catch {
    // Storage may be unavailable or full; the in-memory brief stays usable.
  }
}

export function clearDocumentDraftState(workspaceId: string, storage: StorageLike | undefined = browserSessionStorage()): void {
  if (!storage || !workspaceId.trim()) return;
  try {
    storage.removeItem(documentDraftStorageKey(workspaceId));
  } catch {
    // Clearing is best effort.
  }
}

export type DocumentPresentationState =
  | "compose"
  | "pending"
  | "queued"
  | "running"
  | "succeeded"
  | "completed-unavailable"
  | "failed"
  | "cancelled";

export function documentActiveJobStorageKey(workspaceId: string) {
  return `${activeJobStoragePrefix}${workspaceId}`;
}

export function readDocumentActiveJobId(workspaceId: string) {
  if (typeof window === "undefined") return null;
  try {
    const jobId = window.sessionStorage.getItem(documentActiveJobStorageKey(workspaceId));
    const normalized = jobId?.trim() || "";
    return isSafeDocumentIdentifier(normalized) ? normalized : null;
  } catch {
    return null;
  }
}

export function persistDocumentActiveJobId(workspaceId: string, jobId: string) {
  if (typeof window === "undefined" || !isSafeDocumentIdentifier(jobId)) return;
  try {
    window.sessionStorage.setItem(documentActiveJobStorageKey(workspaceId), jobId);
  } catch {
    // Storage may be unavailable; the in-memory polling path remains authoritative.
  }
}

export function clearDocumentActiveJobId(workspaceId: string) {
  if (typeof window === "undefined") return;
  try {
    window.sessionStorage.removeItem(documentActiveJobStorageKey(workspaceId));
  } catch {
    // Storage may be unavailable.
  }
}

export function isDocumentJob(job: GenerationJob | null) {
  return job?.jobType.trim().toLowerCase() === "document.generate";
}

export function isRestorableDocumentJob(job: GenerationJob | null, workspaceId: string, expectedJobId?: string) {
  return !!job && isSafeDocumentIdentifier(job.id) && job.workspaceId === workspaceId && (!expectedJobId || job.id === expectedJobId) && isDocumentJob(job);
}

export function shouldResetDocumentWorkspaceState(job: GenerationJob | null, workspaceId: string | null) {
  return !!job && (!workspaceId || !isRestorableDocumentJob(job, workspaceId));
}

export function retainDocumentWorkspaceProjectId(projectId: string, projects: ReadonlyArray<{ id: string }>) {
  return projectId && projects.some((project) => project.id === projectId) ? projectId : "";
}

export function retainDocumentWorkspaceFileIds(
  selectedIds: ReadonlyArray<string>,
  files: ReadonlyArray<{ id: string; extension: string; status: string; textExtractionStatus: string }>,
) {
  const readyIds = new Set(files.filter(isDocumentSourceReady).map((file) => file.id));
  return selectedIds.filter((id) => readyIds.has(id));
}

export function isDocumentTerminal(job: GenerationJob | null) {
  return !!job && terminalStatuses.has(job.status);
}

export function shouldPollDocumentJob(job: GenerationJob | null) {
  return !!job && !isDocumentTerminal(job);
}

export function isDocumentJobUnavailableError(error: unknown) {
  if (typeof error !== "object" || error === null || !("status" in error)) return false;
  const status = (error as { status?: unknown }).status;
  return status === 403 || status === 404;
}

export function nextDocumentPollDelay(job: GenerationJob | null, retryAttempt = 0) {
  if (!shouldPollDocumentJob(job)) return null;
  return Math.min(700 * Math.max(1, retryAttempt + 1), 2_800);
}

export function canCancelDocumentJob(job: GenerationJob | null) {
  return !!job && (job.status === "Pending" || job.status === "Queued" || job.status === "Running") && !job.cancellationRequested;
}

export function documentPresentationState(job: GenerationJob | null, result: DocumentJobResult | null): DocumentPresentationState {
  if (!job) return "compose";
  if (job.status === "Pending") return "pending";
  if (job.status === "Queued") return "queued";
  if (job.status === "Running") return "running";
  if (job.status === "Failed") return "failed";
  if (job.status === "Cancelled") return "cancelled";
  return result?.assetId ? "succeeded" : "completed-unavailable";
}

export function displayDocumentProgress(job: GenerationJob | null) {
  if (!job) return 0;
  const progress = Math.max(0, Math.min(100, job.progressPercent));
  return isDocumentTerminal(job) ? progress : Math.min(progress, 99);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}

function readDocumentProperty(record: Record<string, unknown>, camelCase: string, pascalCase: string) {
  return record[camelCase] ?? record[pascalCase];
}

function nonEmptyString(value: unknown): value is string {
  return typeof value === "string" && value.trim().length > 0;
}

const documentIdentifierPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

export function isSafeDocumentIdentifier(value: unknown): value is string {
  return typeof value === "string" && documentIdentifierPattern.test(value.trim());
}

function parseSections(value: unknown): NonNullable<DocumentJobResult["sections"]> {
  if (!Array.isArray(value)) return [];
  return value.flatMap((section) => {
    if (!isRecord(section)) return [];
    const heading = readDocumentProperty(section, "heading", "Heading");
    const blocksValue = readDocumentProperty(section, "blocks", "Blocks");
    if (!nonEmptyString(heading) || !Array.isArray(blocksValue)) return [];
    const blocks = blocksValue.flatMap((block) => {
      if (!isRecord(block)) return [];
      const type = readDocumentProperty(block, "type", "Type");
      const text = readDocumentProperty(block, "text", "Text");
      const itemsValue = readDocumentProperty(block, "items", "Items");
      const rowsValue = readDocumentProperty(block, "rows", "Rows");
      if (!nonEmptyString(type)) return [];
      const items = Array.isArray(itemsValue) ? itemsValue.filter(nonEmptyString) : undefined;
      const rows = Array.isArray(rowsValue)
        ? rowsValue.flatMap((row) => {
          if (!isRecord(row)) return [];
          const cells = readDocumentProperty(row, "cells", "Cells");
          return Array.isArray(cells) ? [{ cells: cells.filter(nonEmptyString) }] : [];
        })
        : undefined;
      return [{
        type,
        text: typeof text === "string" ? text : null,
        items: items?.length ? items : null,
        rows: rows?.length ? rows : null,
      }];
    });
    return [{ heading, blocks }];
  });
}

type DocumentRepresentation = NonNullable<DocumentJobResult["representations"]>[number];
const documentRepresentationContentTypes: Readonly<Record<string, string>> = {
  docx: "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
  pdf: "application/pdf",
};

export function isSafeDocumentRepresentation(value: unknown): value is DocumentRepresentation {
  if (!isRecord(value)) return false;
  const id = readDocumentProperty(value, "id", "Id");
  const typeValue = readDocumentProperty(value, "type", "Type");
  const fileNameValue = readDocumentProperty(value, "fileName", "FileName");
  const contentTypeValue = readDocumentProperty(value, "contentType", "ContentType");
  if (!isSafeDocumentIdentifier(id) || !nonEmptyString(typeValue)
    || !nonEmptyString(fileNameValue) || !nonEmptyString(contentTypeValue)) return false;
  const type = typeValue.trim().toLowerCase();
  const fileName = fileNameValue.trim();
  const contentType = contentTypeValue.trim().toLowerCase();
  const expectedContentType = documentRepresentationContentTypes[type];
  return !!expectedContentType
    && fileName.length > type.length + 1
    && fileName.length <= 255
    && !(/[\\/\u0000-\u001f\u007f]/.test(fileName))
    && fileName.toLowerCase().endsWith(`.${type}`)
    && contentType === expectedContentType;
}

function parseRepresentations(value: unknown): NonNullable<DocumentJobResult["representations"]> {
  if (!Array.isArray(value)) return [];
  return value.flatMap((representation) => {
    if (!isSafeDocumentRepresentation(representation)) return [];
    const record = representation as unknown as Record<string, unknown>;
    const id = readDocumentProperty(record, "id", "Id");
    const type = readDocumentProperty(record, "type", "Type");
    const fileName = readDocumentProperty(record, "fileName", "FileName");
    const contentType = readDocumentProperty(record, "contentType", "ContentType");
    return [{
      id: (id as string).trim(),
      type: (type as string).trim().toLowerCase(),
      fileName: (fileName as string).trim(),
      contentType: (contentType as string).trim().toLowerCase(),
    }];
  });
}

export function parseDocumentJobResult(job: GenerationJob | null): DocumentJobResult | null {
  if (!job?.resultJson) return null;
  try {
    const parsed: unknown = JSON.parse(job.resultJson);
    if (!isRecord(parsed)) return null;
    const assetId = readDocumentProperty(parsed, "assetId", "AssetId");
    const documentType = readDocumentProperty(parsed, "documentType", "DocumentType");
    const title = readDocumentProperty(parsed, "title", "Title");
    const language = readDocumentProperty(parsed, "language", "Language");
    const summary = readDocumentProperty(parsed, "summary", "Summary");
    const sections = readDocumentProperty(parsed, "sections", "Sections");
    const representations = readDocumentProperty(parsed, "representations", "Representations");
    return {
      assetId: isSafeDocumentIdentifier(assetId) ? assetId.trim() : undefined,
      documentType: documentType === "document" ? "document" : undefined,
      title: typeof title === "string" ? title : undefined,
      language: typeof language === "string" ? language : undefined,
      summary: typeof summary === "string" ? summary : undefined,
      sections: parseSections(sections),
      representations: parseRepresentations(representations),
    };
  } catch {
    return null;
  }
}

export function isDocumentSourceReady(file: { extension: string; status: string; textExtractionStatus: string }) {
  return [".pdf", ".docx", ".txt", ".md", ".csv", ".xlsx"].includes(file.extension.toLowerCase()) && file.status === "Ready" && file.textExtractionStatus === "Ready";
}
