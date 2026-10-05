import type { Asset, GenerationJob, SocialJobResult, SocialPost, StoredFile } from "./api";

const terminalStatuses = new Set(["Succeeded", "Failed", "Cancelled"]);
const socialPlatforms = new Set(["instagram", "facebook", "linkedin", "x", "tiktok", "multi"]);
const socialTypes = new Set(["auto", "announcement", "product_launch", "promotion", "educational", "thought_leadership", "company_update", "event", "community", "general"]);
const socialTones = new Set(["professional", "friendly", "persuasive", "educational", "playful", "concise", "thoughtful"]);
const socialLanguages = new Set(["auto", "en", "ar", "ku"]);
const activeJobStoragePrefix = "taslim:social-generation:";
const draftStoragePrefix = "taslim:social-draft:";
export type SocialStudioState = "compose" | "pending" | "queued" | "running" | "succeeded" | "completed-unavailable" | "failed" | "cancelled";
export type SocialPreviewPlatform = "instagram" | "facebook" | "linkedin" | "x" | "tiktok" | "multi";
export type SocialDraftSocialType = "auto" | "announcement" | "product_launch" | "promotion" | "educational" | "thought_leadership" | "company_update" | "event" | "community" | "general";
export type SocialDraftTone = "professional" | "friendly" | "persuasive" | "educational" | "playful" | "concise" | "thoughtful";
export type SocialDraftLanguage = "auto" | "en" | "ar" | "ku";

export type SocialComposeDraft = {
  projectId: string;
  selectedFiles: string[];
  selectedAssets: string[];
  prompt: string;
  socialType: SocialDraftSocialType;
  platform: SocialPreviewPlatform;
  tone: SocialDraftTone;
  language: SocialDraftLanguage;
  audience: string;
  brandVoice: string;
  callToAction: string;
  includeHashtags: boolean;
  includeEmojis: boolean;
  generateVariants: boolean;
};

export function socialActiveJobStorageKey(workspaceId: string) { return `${activeJobStoragePrefix}${workspaceId}`; }
export function socialDraftStorageKey(workspaceId: string) { return `${draftStoragePrefix}${workspaceId}`; }

export function readSocialActiveJobId(workspaceId: string) {
  if (typeof window === "undefined") return null;
  try {
    const jobId = window.sessionStorage.getItem(socialActiveJobStorageKey(workspaceId));
    return jobId?.trim() || null;
  } catch {
    return null;
  }
}
export function persistSocialActiveJobId(workspaceId: string, jobId: string) {
  if (typeof window === "undefined" || !jobId.trim()) return;
  try { window.sessionStorage.setItem(socialActiveJobStorageKey(workspaceId), jobId); } catch { /* Storage may be unavailable. */ }
}
export function clearSocialActiveJobId(workspaceId: string) {
  if (typeof window === "undefined") return;
  try { window.sessionStorage.removeItem(socialActiveJobStorageKey(workspaceId)); } catch { /* Storage may be unavailable. */ }
}

function isRecord(value: unknown): value is Record<string, unknown> { return typeof value === "object" && value !== null; }
function nonEmptyString(value: unknown): value is string { return typeof value === "string" && value.trim().length > 0; }
function readDraftText(value: unknown, maxLength: number) { return typeof value === "string" ? value.trim().slice(0, maxLength) : ""; }
function readDraftIds(value: unknown, maxItems: number) { return Array.isArray(value) ? value.filter(nonEmptyString).map((item) => item.trim()).slice(0, maxItems) : []; }
function readAllowed<T extends string>(value: unknown, allowed: Set<string>, fallback: T) { return typeof value === "string" && allowed.has(value.trim().toLowerCase()) ? value.trim().toLowerCase() as T : fallback; }

export function readSocialDraft(workspaceId: string): SocialComposeDraft | null {
  if (typeof window === "undefined") return null;
  try {
    const raw = window.sessionStorage.getItem(socialDraftStorageKey(workspaceId));
    if (!raw) return null;
    const parsed: unknown = JSON.parse(raw);
    if (!isRecord(parsed)) return null;
    return {
      projectId: readDraftText(parsed.projectId, 200),
      selectedFiles: readDraftIds(parsed.selectedFiles, 5),
      selectedAssets: readDraftIds(parsed.selectedAssets, 8),
      prompt: readDraftText(parsed.prompt, 6_000),
      socialType: readAllowed(parsed.socialType, socialTypes, "auto"),
      platform: readAllowed(parsed.platform, socialPlatforms, "multi"),
      tone: readAllowed(parsed.tone, socialTones, "professional"),
      language: readAllowed(parsed.language, socialLanguages, "auto"),
      audience: readDraftText(parsed.audience, 400),
      brandVoice: readDraftText(parsed.brandVoice, 1_000),
      callToAction: readDraftText(parsed.callToAction, 400),
      includeHashtags: parsed.includeHashtags !== false,
      includeEmojis: parsed.includeEmojis === true,
      generateVariants: parsed.generateVariants === true,
    };
  } catch {
    return null;
  }
}

export function persistSocialDraft(workspaceId: string, draft: SocialComposeDraft) {
  if (typeof window === "undefined") return;
  try { window.sessionStorage.setItem(socialDraftStorageKey(workspaceId), JSON.stringify(draft)); } catch { /* Storage may be unavailable. */ }
}
export function clearSocialDraft(workspaceId: string) {
  if (typeof window === "undefined") return;
  try { window.sessionStorage.removeItem(socialDraftStorageKey(workspaceId)); } catch { /* Storage may be unavailable. */ }
}

export function isSocialTerminal(job: GenerationJob | null) { return !!job && terminalStatuses.has(job.status); }
export function shouldPollSocialJob(job: GenerationJob | null) { return !!job && !isSocialTerminal(job); }
export function nextSocialPollDelay(job: GenerationJob | null, retryAttempt = 0) { return shouldPollSocialJob(job) ? Math.min(700 * Math.max(1, retryAttempt + 1), 2_800) : null; }
export function canCancelSocialJob(job: GenerationJob | null) { return !!job && ["Pending", "Queued", "Running"].includes(job.status) && !job.cancellationRequested; }
export function displaySocialProgress(job: GenerationJob | null) { if (!job) return 0; const progress = Math.max(0, Math.min(100, job.progressPercent)); return isSocialTerminal(job) ? progress : Math.min(progress, 99); }
export function normalizeSocialPreviewPlatform(platform: string | undefined): SocialPreviewPlatform {
  const normalized = platform?.trim().toLowerCase() ?? "";
  return socialPlatforms.has(normalized) ? normalized as SocialPreviewPlatform : "multi";
}
export function socialStudioState(job: GenerationJob | null, result: SocialJobResult | null): SocialStudioState {
  if (!job) return "compose";
  if (job.status === "Pending") return "pending";
  if (job.status === "Queued") return "queued";
  if (job.status === "Running") return "running";
  if (job.status === "Failed") return "failed";
  if (job.status === "Cancelled") return "cancelled";
  return result?.assetId && result.posts?.length ? "succeeded" : "completed-unavailable";
}
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
