import type { DocumentJobResult, GenerationJob } from "./api";

export function canCancelDocumentJob(job: GenerationJob | null) {
  return !!job && (job.status === "Pending" || job.status === "Queued" || job.status === "Running") && !job.cancellationRequested;
}

export function parseDocumentJobResult(job: GenerationJob | null): DocumentJobResult | null {
  if (!job?.resultJson) return null;
  try {
    const parsed = JSON.parse(job.resultJson) as DocumentJobResult;
    return parsed && typeof parsed === "object" ? parsed : null;
  } catch {
    return null;
  }
}

export function isDocumentSourceReady(file: { extension: string; status: string; textExtractionStatus: string }) {
  return [".pdf", ".docx", ".txt", ".md", ".csv", ".xlsx"].includes(file.extension.toLowerCase()) && file.status === "Ready" && file.textExtractionStatus === "Ready";
}
