import type { Asset, GenerationJob, SocialJobResult, SocialPost, StoredFile } from "./api";

const terminalStatuses = new Set(["Succeeded", "Failed", "Cancelled"]);
export type SocialStudioState = "compose" | "pending" | "queued" | "running" | "succeeded" | "completed-unavailable" | "failed" | "cancelled";

export function isSocialTerminal(job: GenerationJob | null) { return !!job && terminalStatuses.has(job.status); }
export function shouldPollSocialJob(job: GenerationJob | null) { return !!job && !isSocialTerminal(job); }
export function nextSocialPollDelay(job: GenerationJob | null, retryAttempt = 0) { return shouldPollSocialJob(job) ? Math.min(700 * Math.max(1, retryAttempt + 1), 2_800) : null; }
export function canCancelSocialJob(job: GenerationJob | null) { return !!job && ["Pending", "Queued", "Running"].includes(job.status) && !job.cancellationRequested; }
export function displaySocialProgress(job: GenerationJob | null) { if (!job) return 0; const progress = Math.max(0, Math.min(100, job.progressPercent)); return isSocialTerminal(job) ? progress : Math.min(progress, 99); }
export function socialStudioState(job: GenerationJob | null, result: SocialJobResult | null): SocialStudioState {
  if (!job) return "compose";
  if (job.status === "Pending") return "pending";
  if (job.status === "Queued") return "queued";
  if (job.status === "Running") return "running";
  if (job.status === "Failed") return "failed";
  if (job.status === "Cancelled") return "cancelled";
  return result?.assetId && result.posts?.length ? "succeeded" : "completed-unavailable";
}
function isRecord(value: unknown): value is Record<string, unknown> { return typeof value === "object" && value !== null; }
function nonEmptyString(value: unknown): value is string { return typeof value === "string" && value.trim().length > 0; }
function parsePosts(value: unknown): SocialPost[] {
  if (!Array.isArray(value)) return [];
  return value.flatMap((post) => {
    if (!isRecord(post) || typeof post.order !== "number" || !nonEmptyString(post.hook) || !nonEmptyString(post.body)) return [];
    return [{ order: post.order, hook: post.hook, body: post.body, callToAction: typeof post.callToAction === "string" ? post.callToAction : null, hashtags: Array.isArray(post.hashtags) ? post.hashtags.filter(nonEmptyString).slice(0, 12) : [], altText: typeof post.altText === "string" ? post.altText : null, visualDirection: typeof post.visualDirection === "string" ? post.visualDirection : null, assetRefs: Array.isArray(post.assetRefs) ? post.assetRefs.filter(nonEmptyString).slice(0, 8) : [] }];
  });
}
export function parseSocialJobResult(job: GenerationJob | null): SocialJobResult | null {
  if (!job?.resultJson) return null;
  try {
    const parsed: unknown = JSON.parse(job.resultJson);
    if (!isRecord(parsed)) return null;
    return { assetId: nonEmptyString(parsed.assetId) ? parsed.assetId : undefined, socialType: typeof parsed.socialType === "string" ? parsed.socialType : undefined, platform: typeof parsed.platform === "string" ? parsed.platform : undefined, title: typeof parsed.title === "string" ? parsed.title : undefined, language: typeof parsed.language === "string" ? parsed.language : undefined, postCount: typeof parsed.postCount === "number" ? parsed.postCount : undefined, posts: parsePosts(parsed.posts) };
  } catch { return null; }
}
export function isSocialSourceReady(file: StoredFile) { return [".pdf", ".docx", ".txt", ".md", ".csv", ".xlsx"].includes(file.extension.toLowerCase()) && file.status === "Ready" && file.textExtractionStatus === "Ready"; }
export function isSocialAssetSelectable(asset: Asset) { return asset.status === "Active" && asset.hasFile && ["image", "document", "presentation", "research", "social"].includes(asset.assetType); }
export function formatSocialPostForCopy(post: SocialPost) { return [post.hook, post.body, post.callToAction, post.hashtags?.join(" ")].filter((value) => typeof value === "string" && value.trim()).join("\n\n"); }
