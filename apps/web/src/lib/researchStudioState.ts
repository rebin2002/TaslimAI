import type { GenerationJob, ResearchJobResult, ResearchReportBlock, ResearchSource } from "./api";

const terminalStatuses = new Set<GenerationJob["status"]>(["Succeeded", "Failed", "Cancelled"]);
export type ResearchStudioState = "compose" | "pending" | "queued" | "running" | "succeeded" | "completed-unavailable" | "failed" | "cancelled";

export function isResearchTerminal(job: GenerationJob | null) { return !!job && terminalStatuses.has(job.status); }
export function shouldPollResearchJob(job: GenerationJob | null) { return !!job && !isResearchTerminal(job); }
export function nextResearchPollDelay(job: GenerationJob | null, retryAttempt = 0) { return shouldPollResearchJob(job) ? Math.min(700 * Math.max(1, retryAttempt + 1), 2_800) : null; }
export function canCancelResearchJob(job: GenerationJob | null) { return !!job && ["Pending", "Queued", "Running"].includes(job.status) && !job.cancellationRequested; }
export function displayResearchProgress(job: GenerationJob | null) { if (!job) return 0; const value = Math.max(0, Math.min(100, job.progressPercent)); return isResearchTerminal(job) ? value : Math.min(value, 99); }
export function researchStudioState(job: GenerationJob | null, result: ResearchJobResult | null): ResearchStudioState {
  if (!job) return "compose";
  if (job.status === "Pending") return "pending";
  if (job.status === "Queued") return "queued";
  if (job.status === "Running") return "running";
  if (job.status === "Failed") return "failed";
  if (job.status === "Cancelled") return "cancelled";
  return result?.assetId ? "succeeded" : "completed-unavailable";
}

function isRecord(value: unknown): value is Record<string, unknown> { return typeof value === "object" && value !== null; }
function nonEmpty(value: unknown): value is string { return typeof value === "string" && value.trim().length > 0; }
function parseBlock(value: unknown): ResearchReportBlock | null {
  if (!isRecord(value) || !nonEmpty(value.type)) return null;
  const items = Array.isArray(value.items) ? value.items.filter(nonEmpty) : null;
  const rows = Array.isArray(value.rows) ? value.rows.flatMap((row) => isRecord(row) && Array.isArray(row.cells) ? [{ cells: row.cells.filter(nonEmpty) }] : []) : null;
  const citationIds = Array.isArray(value.citationIds) ? value.citationIds.filter(nonEmpty) : [];
  return { type: value.type, text: typeof value.text === "string" ? value.text : null, items, rows, citationIds };
}
function parseBlocks(value: unknown) { return Array.isArray(value) ? value.flatMap((item) => { const block = parseBlock(item); return block ? [block] : []; }) : []; }
function parseSource(value: unknown): ResearchSource | null {
  if (!isRecord(value) || !nonEmpty(value.citationId) || !nonEmpty(value.title) || !nonEmpty(value.domain)) return null;
  const evidence = Array.isArray(value.evidence) ? value.evidence.flatMap((item) => isRecord(item) && nonEmpty(item.topic) && nonEmpty(item.excerpt) ? [{ topic: item.topic, excerpt: item.excerpt, context: typeof item.context === "string" ? item.context : null, publishedAt: typeof item.publishedAt === "string" ? item.publishedAt : null }] : []) : undefined;
  return { citationId: value.citationId, url: typeof value.url === "string" ? value.url : null, title: value.title, domain: value.domain, publisher: typeof value.publisher === "string" ? value.publisher : null, publishedAt: typeof value.publishedAt === "string" ? value.publishedAt : null, retrievedAt: typeof value.retrievedAt === "string" ? value.retrievedAt : "", sourceType: typeof value.sourceType === "string" ? value.sourceType : "web", snippet: typeof value.snippet === "string" ? value.snippet : null, searchQuery: typeof value.searchQuery === "string" ? value.searchQuery : null, rank: typeof value.rank === "number" ? value.rank : 0, isSelected: value.isSelected === true, evidence };
}
function parseResult(value: unknown): ResearchJobResult | null {
  if (!isRecord(value)) return null;
  const sources = Array.isArray(value.sources) ? value.sources.flatMap((item) => { const source = parseSource(item); return source ? [source] : []; }) : [];
  const sections = Array.isArray(value.sections) ? value.sections.flatMap((section) => isRecord(section) && nonEmpty(section.heading) ? [{ heading: section.heading, blocks: parseBlocks(section.blocks) }] : []) : [];
  return { assetId: nonEmpty(value.assetId) ? value.assetId : undefined, researchType: value.researchType === "research" ? "research" : undefined, title: typeof value.title === "string" ? value.title : undefined, subtitle: typeof value.subtitle === "string" ? value.subtitle : null, language: typeof value.language === "string" ? value.language : undefined, executiveSummary: typeof value.executiveSummary === "string" ? value.executiveSummary : undefined, keyFindings: parseBlocks(value.keyFindings), sections, conclusion: typeof value.conclusion === "string" ? value.conclusion : undefined, sourceCount: typeof value.sourceCount === "number" ? value.sourceCount : sources.length, sources, representations: Array.isArray(value.representations) ? value.representations.flatMap((item) => isRecord(item) && nonEmpty(item.id) && nonEmpty(item.type) && nonEmpty(item.fileName) && nonEmpty(item.contentType) ? [{ id: item.id, type: item.type, fileName: item.fileName, contentType: item.contentType }] : []) : [] };
}
export function parseResearchJobResult(job: GenerationJob | null): ResearchJobResult | null {
  if (!job?.resultJson) return null;
  try { return parseResult(JSON.parse(job.resultJson)); } catch { return null; }
}
export function mergeResearchSources(result: ResearchJobResult | null, sources: ResearchSource[]) { return result ? { ...result, sources, sourceCount: sources.length } : result; }
export function isResearchSourceReady(file: { extension: string; status: string; textExtractionStatus: string }) { return [".pdf", ".docx", ".txt", ".md", ".csv", ".xlsx"].includes(file.extension.toLowerCase()) && file.status === "Ready" && file.textExtractionStatus === "Ready"; }
export function isSafeExternalUrl(value: string | null) { if (!value) return false; try { const url = new URL(value); return url.protocol === "https:" || url.protocol === "http:"; } catch { return false; } }
