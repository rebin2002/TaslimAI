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

// The Document Studio template preview renders the persisted job result directly into the
// browser DOM. React escapes the values, so markup can never execute, but an oversized or
// control-character-laden result still floods the reader, pollutes the copy-to-clipboard
// brief, and lets bidirectional or zero-width characters spoof what the preview appears to
// say. The preview boundary therefore owns its own sanitization: every rendered string is
// stripped of control and invisible formatting characters, whitespace-collapsed, and hard
// capped, and the preview structure is bounded so one hostile result cannot grow the reader
// without limit. This is deliberately scoped to the preview; editor drafts, downloadable
// representations, and the server renderer keep their own separate boundaries.
export const documentPreviewBounds = {
  titleMax: 240,
  languageMax: 35,
  summaryMax: 1_200,
  headingMax: 240,
  blockTypeMax: 48,
  textMax: 6_000,
  itemMax: 1_000,
  cellMax: 500,
  sectionsMax: 24,
  blocksPerSectionMax: 40,
  itemsPerBlockMax: 60,
  rowsPerBlockMax: 60,
  cellsPerRowMax: 12,
} as const;

// Control characters (newline and tab are handled separately as layout whitespace), soft
// hyphen, zero-width and bidirectional formatting characters, and BOM. Nothing here strips
// ordinary printable punctuation, so legitimate Arabic, Kurdish, and Latin prose is kept
// byte-for-byte.
const previewInvisibleCharacters = /[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f\u00ad\u061c\u180e\u200b-\u200f\u202a-\u202e\u2060-\u2064\u2066-\u206f\ufeff]/g;
// The language chip is matched against the raw value, so a tag carrying any control,
// whitespace, or invisible formatting character is dropped outright rather than silently
// repaired into a different tag ("en" plus a trailing RLO must not render as "en").
const previewLanguageTagPattern = /^[a-z]{2,3}(-([A-Z]{2,3}|[a-z]{3}|[A-Za-z][a-z]{3}|[0-9]{3}))?$/;

export function sanitizeDocumentPreviewText(value: unknown, max: number): string {
  if (typeof value !== "string") return "";
  const stripped = value
    .replace(previewInvisibleCharacters, "")
    // Unicode spaces (en/em, punctuation, narrow no-break, ideographic) render like word
    // separators, so spoofed word joins such as "result\u2008ok" normalize to one real space.
    .replace(/[\u00a0\u1680\u2000-\u200a\u202f\u205f\u3000]/g, " ")
    .replace(/[\t ]+/g, " ")
    .replace(/\s*\n\s*/g, "\n")
    .trim();
  if (stripped.length <= max) return stripped;
  // Leave room for the ellipsis so the returned preview never exceeds the bound.
  return `${stripped.slice(0, Math.max(0, max - 1)).trimEnd()}\u2026`;
}

const documentIdentifierPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

export function isSafeDocumentIdentifier(value: unknown): value is string {
  return typeof value === "string" && documentIdentifierPattern.test(value.trim());
}

function parseSections(value: unknown): NonNullable<DocumentJobResult["sections"]> {
  if (!Array.isArray(value)) return [];
  return value.slice(0, documentPreviewBounds.sectionsMax).flatMap((section) => {
    if (!isRecord(section)) return [];
    const heading = readDocumentProperty(section, "heading", "Heading");
    const blocksValue = readDocumentProperty(section, "blocks", "Blocks");
    if (!nonEmptyString(heading) || !Array.isArray(blocksValue)) return [];
    const blocks = blocksValue.slice(0, documentPreviewBounds.blocksPerSectionMax).flatMap((block) => {
      if (!isRecord(block)) return [];
      const type = readDocumentProperty(block, "type", "Type");
      const text = readDocumentProperty(block, "text", "Text");
      const itemsValue = readDocumentProperty(block, "items", "Items");
      const rowsValue = readDocumentProperty(block, "rows", "Rows");
      if (!nonEmptyString(type)) return [];
      const items = Array.isArray(itemsValue)
        ? itemsValue.filter(nonEmptyString).slice(0, documentPreviewBounds.itemsPerBlockMax).map((item) => sanitizeDocumentPreviewText(item, documentPreviewBounds.itemMax)).filter(nonEmptyString)
        : undefined;
      const rows = Array.isArray(rowsValue)
        ? rowsValue.slice(0, documentPreviewBounds.rowsPerBlockMax).flatMap((row) => {
          if (!isRecord(row)) return [];
          const cells = readDocumentProperty(row, "cells", "Cells");
          if (!Array.isArray(cells)) return [];
          const sanitizedCells = cells.filter(nonEmptyString).slice(0, documentPreviewBounds.cellsPerRowMax).map((cell) => sanitizeDocumentPreviewText(cell, documentPreviewBounds.cellMax)).filter(nonEmptyString);
          return sanitizedCells.length ? [{ cells: sanitizedCells }] : [];
        })
        : undefined;
      const sanitizedType = sanitizeDocumentPreviewText(type, documentPreviewBounds.blockTypeMax);
      if (!sanitizedType) return [];
      return [{
        type: sanitizedType,
        text: typeof text === "string" ? sanitizeDocumentPreviewText(text, documentPreviewBounds.textMax) : null,
        items: items?.length ? items : null,
        rows: rows?.length ? rows : null,
      }];
    });
    const sanitizedHeading = sanitizeDocumentPreviewText(heading, documentPreviewBounds.headingMax);
    if (!sanitizedHeading || blocks.length === 0) return [];
    return [{ heading: sanitizedHeading, blocks }];
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
    const languagePreview = typeof language === "string" && language.length <= documentPreviewBounds.languageMax && previewLanguageTagPattern.test(language)
      ? sanitizeDocumentPreviewText(language, documentPreviewBounds.languageMax)
      : "";
    return {
      assetId: isSafeDocumentIdentifier(assetId) ? assetId.trim() : undefined,
      documentType: documentType === "document" ? "document" : undefined,
      title: typeof title === "string" ? sanitizeDocumentPreviewText(title, documentPreviewBounds.titleMax) : undefined,
      // The language chip only ever shows a short tag; anything with spaces, markup-like
      // characters, or excess length is dropped so the preview renders its fallback label.
      language: languagePreview && previewLanguageTagPattern.test(languagePreview) ? languagePreview : undefined,
      summary: typeof summary === "string" ? sanitizeDocumentPreviewText(summary, documentPreviewBounds.summaryMax) : undefined,
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
