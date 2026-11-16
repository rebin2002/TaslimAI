import type { DocumentJobResult, GenerationJob } from "./api";

const terminalStatuses = new Set<GenerationJob["status"]>(["Succeeded", "Failed", "Cancelled"]);
const activeJobStoragePrefix = "taslim:document-generation:";

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
