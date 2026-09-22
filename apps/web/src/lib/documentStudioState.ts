import type { DocumentJobResult, GenerationJob } from "./api";

const terminalStatuses = new Set<GenerationJob["status"]>(["Succeeded", "Failed", "Cancelled"]);

export type DocumentPresentationState =
  | "compose"
  | "pending"
  | "queued"
  | "running"
  | "succeeded"
  | "completed-unavailable"
  | "failed"
  | "cancelled";

export function isDocumentTerminal(job: GenerationJob | null) {
  return !!job && terminalStatuses.has(job.status);
}

export function shouldPollDocumentJob(job: GenerationJob | null) {
  return !!job && !isDocumentTerminal(job);
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

function nonEmptyString(value: unknown): value is string {
  return typeof value === "string" && value.trim().length > 0;
}

function parseSections(value: unknown): NonNullable<DocumentJobResult["sections"]> {
  if (!Array.isArray(value)) return [];
  return value.flatMap((section) => {
    if (!isRecord(section) || !nonEmptyString(section.heading) || !Array.isArray(section.blocks)) return [];
    const blocks = section.blocks.flatMap((block) => {
      if (!isRecord(block) || !nonEmptyString(block.type)) return [];
      const items = Array.isArray(block.items) ? block.items.filter(nonEmptyString) : undefined;
      const rows = Array.isArray(block.rows)
        ? block.rows.flatMap((row) => isRecord(row) && Array.isArray(row.cells)
          ? [{ cells: row.cells.filter(nonEmptyString) }]
          : [])
        : undefined;
      return [{
        type: block.type,
        text: typeof block.text === "string" ? block.text : null,
        items: items?.length ? items : null,
        rows: rows?.length ? rows : null,
      }];
    });
    return [{ heading: section.heading, blocks }];
  });
}

function parseRepresentations(value: unknown): NonNullable<DocumentJobResult["representations"]> {
  if (!Array.isArray(value)) return [];
  return value.flatMap((representation) => {
    if (!isRecord(representation) || !nonEmptyString(representation.id) || !nonEmptyString(representation.type)
      || !nonEmptyString(representation.fileName) || !nonEmptyString(representation.contentType)) return [];
    return [{
      id: representation.id,
      type: representation.type,
      fileName: representation.fileName,
      contentType: representation.contentType,
    }];
  });
}

export function parseDocumentJobResult(job: GenerationJob | null): DocumentJobResult | null {
  if (!job?.resultJson) return null;
  try {
    const parsed: unknown = JSON.parse(job.resultJson);
    if (!isRecord(parsed)) return null;
    return {
      assetId: nonEmptyString(parsed.assetId) ? parsed.assetId : undefined,
      documentType: parsed.documentType === "document" ? "document" : undefined,
      title: typeof parsed.title === "string" ? parsed.title : undefined,
      language: typeof parsed.language === "string" ? parsed.language : undefined,
      summary: typeof parsed.summary === "string" ? parsed.summary : undefined,
      sections: parseSections(parsed.sections),
      representations: parseRepresentations(parsed.representations),
    };
  } catch {
    return null;
  }
}

export function isDocumentSourceReady(file: { extension: string; status: string; textExtractionStatus: string }) {
  return [".pdf", ".docx", ".txt", ".md", ".csv", ".xlsx"].includes(file.extension.toLowerCase()) && file.status === "Ready" && file.textExtractionStatus === "Ready";
}
