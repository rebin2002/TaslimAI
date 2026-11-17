import type { GenerationJob, ResearchJobResult, ResearchReportBlock, ResearchSource } from "./api";

const terminalStatuses = new Set<GenerationJob["status"]>(["Succeeded", "Failed", "Cancelled"]);
const activeJobStoragePrefix = "taslim:research-generation:";
export type ResearchStudioState = "compose" | "pending" | "queued" | "running" | "succeeded" | "completed-unavailable" | "failed" | "cancelled";

export function researchActiveJobStorageKey(workspaceId: string) { return `${activeJobStoragePrefix}${workspaceId}`; }
export function readResearchActiveJobId(workspaceId: string) {
  if (typeof window === "undefined") return null;
  try {
    const jobId = window.sessionStorage.getItem(researchActiveJobStorageKey(workspaceId));
    return jobId?.trim() || null;
  } catch {
    return null;
  }
}
export function persistResearchActiveJobId(workspaceId: string, jobId: string) {
  if (typeof window === "undefined" || !jobId.trim()) return;
  try { window.sessionStorage.setItem(researchActiveJobStorageKey(workspaceId), jobId); } catch { /* Storage may be unavailable. */ }
}
export function clearResearchActiveJobId(workspaceId: string) {
  if (typeof window === "undefined") return;
  try { window.sessionStorage.removeItem(researchActiveJobStorageKey(workspaceId)); } catch { /* Storage may be unavailable. */ }
}
export function isResearchJob(job: GenerationJob | null) {
  return job?.jobType.trim().toLowerCase() === "research.generate";
}
export function isRestorableResearchJob(job: GenerationJob | null, workspaceId: string) {
  return !!job && job.workspaceId === workspaceId && isResearchJob(job);
}

export function isCurrentResearchJob(job: GenerationJob | null, workspaceId: string, expectedJobId?: string) {
  return !!job && job.workspaceId === workspaceId && (!expectedJobId || job.id === expectedJobId) && isResearchJob(job);
}

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
function citationKey(value: string) { return value.trim().toLocaleLowerCase("en-US"); }
function uniqueStrings(values: string[]) {
  const seen = new Set<string>();
  return values.flatMap((value) => {
    const trimmed = value.trim();
    const key = citationKey(trimmed);
    if (!trimmed || seen.has(key)) return [];
    seen.add(key);
    return [trimmed];
  });
}
/**
 * Citation identifiers are "S<number>". Ordering them by that number, rather than trusting the order
 * a payload happened to arrive in, is what makes the numbered source list stable: the embedded report
 * manifest and the persisted source details are two different orderings of the same citations.
 */
function citationOrdinal(value: string) {
  const digits = /(\d+)$/.exec(citationKey(value));
  if (!digits) return null;
  const ordinal = Number(digits[1]);
  return Number.isSafeInteger(ordinal) ? ordinal : null;
}
function compareCitationIds(left: string, right: string) {
  const leftOrdinal = citationOrdinal(left);
  const rightOrdinal = citationOrdinal(right);
  if (leftOrdinal !== null && rightOrdinal !== null && leftOrdinal !== rightOrdinal) return leftOrdinal - rightOrdinal;
  if (leftOrdinal !== null && rightOrdinal === null) return -1;
  if (leftOrdinal === null && rightOrdinal !== null) return 1;
  const leftKey = citationKey(left);
  const rightKey = citationKey(right);
  if (leftKey !== rightKey) return leftKey < rightKey ? -1 : 1;
  return left < right ? -1 : left > right ? 1 : 0;
}
/**
 * The same page can be cited twice under two identifiers, so the reader counts one document as two
 * sources and a single finding appears credited to two of them. Rows are collapsed on the locator a
 * reader would recognise as the same page: scheme and host case, a trailing path slash and an in-page
 * anchor carry no separate document, while the query is kept because it selects the document. Rows
 * without a usable link - every uploaded source - are never collapsed, because their title, not their
 * locator, is what tells them apart.
 */
function citedPageKey(source: ResearchSource): string | null {
  const locator = source.url?.trim() ?? "";
  if (!locator || !isSafeExternalUrl(locator)) return null;
  const page = new URL(locator);
  const path = page.pathname.replace(/\/+$/u, "");
  return `${page.protocol.toLowerCase()}//${page.host.toLowerCase()}${path}${page.search}`;
}
type CitedSources = { sources: ResearchSource[]; aliases: Map<string, string> };
function parseCitedSources(values: unknown[]): ResearchSource[] {
  const seen = new Set<string>();
  const parsed: ResearchSource[] = [];
  for (const value of values) {
    const item = parseSource(value);
    if (!item) continue;
    const key = citationKey(item.citationId);
    if (seen.has(key)) continue;
    seen.add(key);
    parsed.push(item);
  }
  return parsed.sort((left, right) => compareCitationIds(left.citationId, right.citationId));
}
function collapseCitedSources(sources: ResearchSource[]): CitedSources {
  const aliases = new Map<string, string>();
  const pageOwner = new Map<string, string>();
  const collapsed: ResearchSource[] = [];
  for (const source of sources) {
    const page = citedPageKey(source);
    if (page === null) { collapsed.push(source); continue; }
    const owner = pageOwner.get(page);
    if (owner) { aliases.set(citationKey(source.citationId), owner); continue; }
    pageOwner.set(page, source.citationId);
    collapsed.push(source);
  }
  return { sources: collapsed, aliases };
}
function collectCitedSources(values: unknown[]): CitedSources { return collapseCitedSources(parseCitedSources(values)); }
function parseBlock(value: unknown): ResearchReportBlock | null {
  if (!isRecord(value) || !nonEmpty(value.type)) return null;
  const items = Array.isArray(value.items) ? value.items.filter(nonEmpty).map((item) => item.trim()) : null;
  const rows = Array.isArray(value.rows) ? value.rows.flatMap((row) => isRecord(row) && Array.isArray(row.cells) ? [{ cells: row.cells.filter(nonEmpty).map((cell) => cell.trim()) }] : []) : null;
  const citationIds = Array.isArray(value.citationIds) ? uniqueStrings(value.citationIds.filter(nonEmpty)) : [];
  return { type: value.type.trim(), text: typeof value.text === "string" ? value.text : null, items, rows, citationIds };
}
function parseBlocks(value: unknown) { return Array.isArray(value) ? value.flatMap((item) => { const block = parseBlock(item); return block ? [block] : []; }) : []; }
function parseSource(value: unknown): ResearchSource | null {
  if (!isRecord(value) || !nonEmpty(value.citationId) || !nonEmpty(value.title) || !nonEmpty(value.domain)) return null;
  const evidence = Array.isArray(value.evidence) ? value.evidence.flatMap((item) => isRecord(item) && nonEmpty(item.topic) && nonEmpty(item.excerpt) ? [{ topic: item.topic.trim(), excerpt: item.excerpt.trim(), context: typeof item.context === "string" ? item.context : null, publishedAt: typeof item.publishedAt === "string" ? item.publishedAt : null }] : []) : undefined;
  return { citationId: value.citationId.trim(), url: typeof value.url === "string" ? value.url : null, title: value.title.trim(), domain: value.domain.trim(), publisher: typeof value.publisher === "string" ? value.publisher : null, publishedAt: typeof value.publishedAt === "string" ? value.publishedAt : null, retrievedAt: typeof value.retrievedAt === "string" ? value.retrievedAt : "", sourceType: typeof value.sourceType === "string" ? value.sourceType : "web", snippet: typeof value.snippet === "string" ? value.snippet : null, searchQuery: typeof value.searchQuery === "string" ? value.searchQuery : null, rank: typeof value.rank === "number" ? value.rank : 0, isSelected: value.isSelected === true, evidence };
}
function normalizeBlockCitations(block: ResearchReportBlock, sourceIds: Map<string, string>): ResearchReportBlock {
  // Renumbering two identifiers onto one page can leave the same citation listed twice in a block, so
  // the mapped markers are deduplicated as well.
  return { ...block, citationIds: uniqueStrings((block.citationIds ?? []).flatMap((citation) => { const canonical = sourceIds.get(citationKey(citation)); return canonical ? [canonical] : []; })) };
}
function normalizeBlocks(blocks: ResearchReportBlock[], sourceIds: Map<string, string>) {
  return blocks.map((block) => normalizeBlockCitations(block, sourceIds));
}
function normalizeParsedResult(result: ResearchJobResult, collected: CitedSources): ResearchJobResult {
  const sources = collected.sources;
  const sourceIds = new Map(sources.map((source) => [citationKey(source.citationId), source.citationId]));
  // A collapsed identifier still appears in the report text, so it resolves to the citation that now
  // owns the page instead of being dropped as an unbacked marker.
  for (const [key, owner] of collected.aliases) if (!sourceIds.has(key)) sourceIds.set(key, owner);
  return {
    ...result,
    sourceCount: sources.length,
    sources,
    keyFindings: normalizeBlocks(result.keyFindings ?? [], sourceIds),
    sections: (result.sections ?? []).map((section) => ({ ...section, blocks: normalizeBlocks(section.blocks, sourceIds) })),
  };
}
function parseResult(value: unknown): ResearchJobResult | null {
  if (!isRecord(value)) return null;
  const collected = collectCitedSources(Array.isArray(value.sources) ? value.sources : []);
  const sources = collected.sources;
  const sections = Array.isArray(value.sections) ? value.sections.flatMap((section) => isRecord(section) && nonEmpty(section.heading) ? [{ heading: section.heading.trim(), blocks: parseBlocks(section.blocks) }] : []) : [];
  const result: ResearchJobResult = { assetId: nonEmpty(value.assetId) ? value.assetId : undefined, researchType: value.researchType === "research" ? "research" : undefined, title: typeof value.title === "string" ? value.title : undefined, subtitle: typeof value.subtitle === "string" ? value.subtitle : null, language: typeof value.language === "string" ? value.language : undefined, executiveSummary: typeof value.executiveSummary === "string" ? value.executiveSummary : undefined, keyFindings: parseBlocks(value.keyFindings), sections, conclusion: typeof value.conclusion === "string" ? value.conclusion : undefined, sourceCount: sources.length, sources, representations: Array.isArray(value.representations) ? value.representations.flatMap((item) => isRecord(item) && nonEmpty(item.id) && nonEmpty(item.type) && nonEmpty(item.fileName) && nonEmpty(item.contentType) ? [{ id: item.id, type: item.type, fileName: item.fileName, contentType: item.contentType }] : []) : [] };
  return normalizeParsedResult(result, collected);
}
export function parseResearchJobResult(job: GenerationJob | null): ResearchJobResult | null {
  if (!job?.resultJson) return null;
  try { return parseResult(JSON.parse(job.resultJson)); } catch { return null; }
}
export function mergeResearchSources(result: ResearchJobResult | null, sources: ResearchSource[]) {
  if (!result || sources.length === 0) return result;
  const embeddedSources = collectCitedSources(result.sources ?? []);
  const details = parseCitedSources(sources);
  if (embeddedSources.sources.length === 0) return normalizeParsedResult(result, collapseCitedSources(details));
  const detailByCitation = new Map(details.map((source) => [citationKey(source.citationId), source]));
  const merged = embeddedSources.sources.map((source) => detailByCitation.get(citationKey(source.citationId)) ?? source);
  // A detail row can carry a locator the embedded manifest did not have, so the merged citations are
  // collapsed and renumbered rather than trusting either payload on its own.
  return normalizeParsedResult(result, collapseCitedSources(merged));
}
export function isResearchSourceReady(file: { extension: string; status: string; textExtractionStatus: string }) { return [".pdf", ".docx", ".txt", ".md", ".csv", ".xlsx"].includes(file.extension.toLowerCase()) && file.status === "Ready" && file.textExtractionStatus === "Ready"; }
export function isSafeExternalUrl(value: string | null) { if (!value) return false; try { const url = new URL(value); return url.protocol === "https:" || url.protocol === "http:"; } catch { return false; } }

// The finished report's evidence panel has three distinct zero-source conditions that
// must never render the same message: the source ledger is still being retrieved
// ("loading"), retrieving it failed and a retry is available ("error"), or the run
// genuinely cited nothing ("empty"). A fetch failure that falls through to the empty
// copy would tell the reader the report has no citations when the citations exist but
// the read failed, hiding a retryable fault behind an accurate-looking terminal state.
export type ResearchSourcesLoadStatus = "loading" | "ready" | "error";
export type ResearchSourcesPanelStatus = "populated" | "loading" | "error" | "empty";
export function researchSourcesPanelStatus(load: ResearchSourcesLoadStatus, sourceCount: number): ResearchSourcesPanelStatus {
  if (sourceCount > 0) return "populated";
  if (load === "loading") return "loading";
  if (load === "error") return "error";
  return "empty";
}
