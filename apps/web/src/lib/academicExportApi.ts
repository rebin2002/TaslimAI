/**
 * Issue #1267 (template ↔ saved-report export slice) — API client.
 *
 * A deliberately separate module: `api.ts` keeps its request/CSRF helpers private,
 * editing it would collide with other open lanes, and the export surface must stay
 * small enough to audit. It talks to exactly three endpoints:
 *
 *   GET  /api/generation/jobs?...jobType=research.generate&status=Succeeded
 *   GET  /api/academic-export/jobs/{jobId}/report
 *   POST /api/academic-export/jobs/{jobId}/export
 *
 * Nothing here can start content generation: the endpoints read an already-saved
 * report and render it under a versioned template configuration. This module never
 * reads, requests or displays provider, model, routing or billing data.
 */
import { API_URL } from "./apiBase";
import type { GenerationJob, GenerationJobList } from "./api";

/** One source row of the saved report, exactly as the summary endpoint returns it. */
export type AcademicExportSource = {
  citationId: string;
  title: string;
  url: string | null;
  publishedYear: string | null;
  sourceType: string;
  contentAccessed: boolean;
  accessNote: string;
};

/** Honest, content-free summary of what an export would contain. */
export type AcademicReportSummary = {
  jobId: string;
  title: string;
  subtitle: string | null;
  language: string;
  savedAtUtc: string;
  sectionCount: number;
  sourceCount: number;
  accessedSourceCount: number;
  metadataOnlySourceCount: number;
  sourceContentHash: string;
  sources: AcademicExportSource[];
};

export type AcademicExportFormat = "docx" | "pdf";

export type AcademicExportRequestPayload = {
  configurationId: string;
  format: AcademicExportFormat;
  previousSnapshotJson?: string | null;
  studentOverrides?: Record<string, string> | null;
  aiDisclosureText?: string | null;
};

export type AcademicExportResult = {
  blob: Blob;
  fileName: string;
  /** Server-truth export mode; null when the browser hides the header (CORS). */
  mode: string | null;
  reason: string | null;
  /** Snapshot JSON to echo on the next export of the same report. */
  snapshot: string | null;
};

/** Error carrying the API's machine-readable code so copy can stay honest per case. */
export class AcademicExportApiError extends Error {
  readonly code: string;
  readonly status: number;
  constructor(code: string, message: string, status: number) {
    super(message);
    this.name = "AcademicExportApiError";
    this.code = code;
    this.status = status;
  }
}

/** Jobs the export surface offers: completed research runs whose payload is saved. */
export type SavedReportOption = {
  jobId: string;
  title: string;
  savedAt: string | null;
};

async function parseError(response: Response): Promise<never> {
  let code = "ACADEMIC_EXPORT_NETWORK";
  let message = `Request failed with status ${response.status}.`;
  try {
    const body = (await response.json()) as { error?: { code?: string; message?: string } };
    if (body?.error?.code) code = body.error.code;
    if (body?.error?.message) message = body.error.message;
  } catch {
    // Non-JSON error bodies keep the generic message.
  }
  throw new AcademicExportApiError(code, message, response.status);
}

async function getJson<T>(path: string): Promise<T> {
  const response = await fetch(`${API_URL}${path}`, {
    method: "GET",
    credentials: "include",
    headers: { accept: "application/json" },
  });
  if (!response.ok) await parseError(response);
  return (await response.json()) as T;
}

let csrfToken: string | null = null;

async function getCsrfToken(forceRefresh = false): Promise<string> {
  if (csrfToken && !forceRefresh) return csrfToken;
  const body = await getJson<{ token: string }>("/api/auth/csrf");
  csrfToken = body.token;
  return csrfToken;
}

/** Completed research reports saved in this workspace, newest page first. */
export async function listSavedResearchReports(
  workspaceId: string,
  options: { page?: number; pageSize?: number } = {},
): Promise<SavedReportOption[]> {
  const params = new URLSearchParams({
    workspaceId,
    page: String(options.page ?? 1),
    pageSize: String(options.pageSize ?? 20),
    jobType: "research.generate",
    status: "Succeeded",
  });
  const list = await getJson<GenerationJobList>(`/api/generation/jobs?${params.toString()}`);
  return list.items
    .filter((job: GenerationJob) => job.jobType === "research.generate" && job.status === "Succeeded" && job.resultJson)
    .map((job) => ({
      jobId: job.id,
      title: job.title?.trim() || "Untitled research report",
      savedAt: job.completedAt,
    }));
}

/** What an export of this saved report would contain, with honest access labels. */
export async function getAcademicReportSummary(jobId: string): Promise<AcademicReportSummary> {
  return getJson<AcademicReportSummary>(`/api/academic-export/jobs/${encodeURIComponent(jobId)}/report`);
}

/**
 * Render the saved report under a template configuration and download the bytes.
 * A CSRF-problem 400 refreshes the token once and retries; every other failure is
 * surfaced unchanged so the panel can tell the truth about what happened.
 */
export async function exportAcademicReport(
  jobId: string,
  payload: AcademicExportRequestPayload,
): Promise<AcademicExportResult> {
  const send = async (token: string) =>
    fetch(`${API_URL}/api/academic-export/jobs/${encodeURIComponent(jobId)}/export`, {
      method: "POST",
      credentials: "include",
      headers: { "content-type": "application/json", "X-CSRF-TOKEN": token },
      body: JSON.stringify(payload),
    });
  let response = await send(await getCsrfToken());
  if (response.status === 400) {
    // Only a rejected anti-forgery token is worth retrying; re-read to inspect.
    const text = await response.text();
    if (/csrf|antiforgery/i.test(text)) {
      csrfToken = null;
      response = await send(await getCsrfToken(true));
    } else {
      throw await errorFromText(response, text);
    }
  }
  if (!response.ok) await parseError(response);
  const blob = await response.blob();
  const disposition = response.headers.get("content-disposition") ?? "";
  const fileNameMatch = /filename="?([^";]+)"?/i.exec(disposition);
  return {
    blob,
    fileName: fileNameMatch?.[1] ?? `academic-report.${payload.format}`,
    mode: response.headers.get("X-Academic-Export-Mode"),
    reason: response.headers.get("X-Academic-Export-Reason"),
    snapshot: response.headers.get("X-Academic-Export-Snapshot"),
  };
}

async function errorFromText(response: Response, text: string): Promise<AcademicExportApiError> {
  try {
    const body = JSON.parse(text) as { error?: { code?: string; message?: string } };
    return new AcademicExportApiError(
      body?.error?.code ?? "ACADEMIC_EXPORT_REQUEST_INVALID",
      body?.error?.message ?? `Request failed with status ${response.status}.`,
      response.status,
    );
  } catch {
    return new AcademicExportApiError(
      "ACADEMIC_EXPORT_REQUEST_INVALID",
      `Request failed with status ${response.status}.`,
      response.status,
    );
  }
}
