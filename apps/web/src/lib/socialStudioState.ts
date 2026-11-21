import type { Asset, GenerationJob, SocialJobResult, SocialPost, StoredFile } from "./api";
import {
  clearSessionDraft,
  defaultSessionDraftStore,
  purgeSessionDraftsForUser,
  readSessionDraft,
  retireLegacyWorkspaceDraft,
  sessionDraftStorageKey,
  writeSessionDraft,
  type DraftPrincipal,
  type LegacyDraftDisposition,
} from "./sessionDraftScope";

const terminalStatuses = new Set(["Succeeded", "Failed", "Cancelled"]);
const socialPlatforms = new Set(["instagram", "facebook", "linkedin", "x", "tiktok", "multi"]);
const socialTypes = new Set(["auto", "announcement", "product_launch", "promotion", "educational", "thought_leadership", "company_update", "event", "community", "general"]);
const socialTones = new Set(["professional", "friendly", "persuasive", "educational", "playful", "concise", "thoughtful"]);
const socialLanguages = new Set(["auto", "en", "ar", "ku"]);
const activeJobStoragePrefix = "taslim:social-generation:";
const socialDraftNamespace = "social-draft";
const draftStoragePrefix = "taslim:social-draft:";
export type SocialStudioState = "compose" | "pending" | "queued" | "running" | "succeeded" | "completed-unavailable" | "failed" | "cancelled";
export type SocialPreviewPlatform = "instagram" | "facebook" | "linkedin" | "x" | "tiktok" | "multi";
export type SocialStudioDraft = {
  projectId: string;
  selectedFiles: string[];
  selectedAssets: string[];
  prompt: string;
  socialType: string;
  platform: string;
  tone: string;
  language: string;
  audience: string;
  brandVoice: string;
  callToAction: string;
  includeHashtags: boolean;
  includeEmojis: boolean;
  generateVariants: boolean;
};

export function socialActiveJobStorageKey(workspaceId: string) { return `${activeJobStoragePrefix}${workspaceId}`; }
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

export function isSocialJobForWorkspace(job: GenerationJob | null, workspaceId: string | null | undefined) {
  return !!job && !!workspaceId && job.workspaceId === workspaceId && typeof job.jobType === "string" && job.jobType.trim().toLowerCase() === "social.generate";
}
/**
 * The principal a persisted Social draft is attributed to (issue #688).
 *
 * Before this change a draft was stored under `taslim:social-draft:<workspaceId>`, so the workspace
 * identifier was the only boundary an unsent private draft respected. Two accounts that share one
 * browser tab share one `sessionStorage`, so signing out of Account A and signing in as Account B in
 * the same tab handed B A's prompt, brand-voice / audience / CTA copy and selected file and asset
 * identifiers. A workspace id is an authorisation scope, not an authentication result, so persisted
 * drafts are now bound to the authenticated identity *and* the workspace.
 */
export type SocialDraftPrincipal = { userId: string | null | undefined; workspaceId: string | null | undefined };
/**
 * A draft is addressed either by an identity+workspace principal or by the legacy workspace-only
 * string form. The string form cannot prove who the owner is, therefore it resolves to no address at
 * all: a read returns nothing, a write stores nothing, a discard removes nothing. It stays in the
 * signature so an un-migrated call site fails closed instead of silently re-creating a shared
 * address, and so a bare workspace id can never be smuggled in as an identity.
 */
export type SocialDraftTarget = SocialDraftPrincipal | string | null | undefined;

function toSocialDraftPrincipal(target: SocialDraftTarget): DraftPrincipal {
  if (typeof target === "string") return { userId: null, workspaceId: target };
  return target ?? { userId: null, workspaceId: null };
}

/** Identity-bound storage address, or null when no authenticated identity backs the request. */
export function socialDraftStorageKey(target: SocialDraftTarget): string | null {
  return sessionDraftStorageKey(socialDraftNamespace, toSocialDraftPrincipal(target));
}

/**
 * The pre-#688 workspace-only address. Exported so the quarantine path can FIND and REMOVE entries
 * left by older builds; nothing may read their payload into any identity.
 */
export function legacySocialDraftStorageKey(workspaceId: string) { return `${draftStoragePrefix}${workspaceId}`; }

export function readSocialDraft(target: SocialDraftTarget): SocialStudioDraft | null {
  // The owner-bound envelope is the only source of truth and there is deliberately no workspace-only
  // fallback: an entry that is not addressed to this exact user + workspace is never surfaced.
  return readSessionDraft(defaultSessionDraftStore(), socialDraftNamespace, toSocialDraftPrincipal(target), (value) => (isRecord(value) ? normalizeSocialDraft(value) : null));
}

/** Persists the draft for this identity + workspace only. Returns false when nothing was written. */
export function persistSocialDraft(target: SocialDraftTarget, draft: SocialStudioDraft): boolean {
  return writeSessionDraft(defaultSessionDraftStore(), socialDraftNamespace, toSocialDraftPrincipal(target), normalizeSocialDraft(draft));
}

/** Discards exactly this identity + workspace draft. An unattributed target removes nothing. */
export function clearSocialDraft(target: SocialDraftTarget): boolean {
  return clearSessionDraft(defaultSessionDraftStore(), socialDraftNamespace, toSocialDraftPrincipal(target));
}

export type SocialDraftScope = { userId: string; workspaceId: string };
export type LegacySocialDraftDisposition = LegacyDraftDisposition;

/**
 * Resolves the (identity, workspace) pair a draft session is bound to, or null while either half is
 * missing or anonymous. The studio uses this as its hydration token so that a re-render for a
 * different account is provably a different scope and can never be read as "same workspace, so keep
 * showing what is already on screen" — that is the no-flash half of #688, while the identity-bound
 * address above is the cannot-be-read half.
 */
export function resolveSocialDraftScope(target: SocialDraftTarget): SocialDraftScope | null {
  const principal = toSocialDraftPrincipal(target);
  if (!socialDraftStorageKey(target)) return null;
  const userId = typeof principal.userId === "string" ? principal.userId.trim() : "";
  const workspaceId = typeof principal.workspaceId === "string" ? principal.workspaceId.trim() : "";
  if (!userId || !workspaceId) return null;
  return { userId, workspaceId };
}

/** Stable identity+workspace token for effect dependency lists; null while unauthenticated. */
export function socialDraftScopeToken(target: SocialDraftTarget): string | null {
  const scope = resolveSocialDraftScope(target);
  // JSON encoding keeps the token collision-free for ids that themselves contain separators.
  return scope ? JSON.stringify([scope.userId, scope.workspaceId]) : null;
}

/**
 * Removes a pre-#688 workspace-only draft from this tab WITHOUT ever surfacing its content to the
 * identity that happens to arrive first: ownership of those entries cannot be proven, so they are
 * quarantined (removed) rather than migrated. The one exception the policy makes is an entry that
 * already carries this exact owner's envelope, which is promoted to the scoped address. The return
 * value is a disposition label only; it never contains draft content.
 */
export function retireLegacySocialDraft(target: SocialDraftTarget): LegacySocialDraftDisposition {
  const scope = resolveSocialDraftScope(target);
  const store = defaultSessionDraftStore();
  if (!scope || !store) return "storage-unavailable";
  return retireLegacyWorkspaceDraft(store, legacySocialDraftStorageKey(scope.workspaceId), socialDraftNamespace, scope);
}

/**
 * Account-boundary scrub for #688: removes the departing identity's persisted Social drafts from
 * this tab and nothing else. It refuses to touch a single entry when the identity is not proven, so
 * a half-resolved session can never wipe another account's state, and it never enumerates payloads.
 */
export function purgeSocialDraftsOnAccountBoundary(userId: string | null | undefined): { removedCount: number; refusedInvalidIdentity: boolean } {
  const result = purgeSessionDraftsForUser(defaultSessionDraftStore(), userId, { namespace: socialDraftNamespace });
  return { removedCount: result.removedKeys.length, refusedInvalidIdentity: result.refusedInvalidIdentity };
}

export function isSocialTerminal(job: GenerationJob | null) { return !!job && terminalStatuses.has(job.status); }
export function shouldPollSocialJob(job: GenerationJob | null) { return !!job && !isSocialTerminal(job); }
export function nextSocialPollDelay(job: GenerationJob | null, retryAttempt = 0) { return shouldPollSocialJob(job) ? Math.min(700 * Math.max(1, retryAttempt + 1), 2_800) : null; }
export function nextSocialRestoreDelay(retryAttempt = 0) { return retryAttempt <= 0 ? 0 : Math.min(700 * 2 ** Math.min(retryAttempt - 1, 4), 8_000); }
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
function isRecord(value: unknown): value is Record<string, unknown> { return typeof value === "object" && value !== null; }
function nonEmptyString(value: unknown): value is string { return typeof value === "string" && value.trim().length > 0; }
function boundedString(value: unknown, maxLength: number) { return typeof value === "string" ? value.slice(0, maxLength) : ""; }
function boundedIds(value: unknown, maxLength: number) { return Array.isArray(value) ? value.filter(nonEmptyString).map((id) => id.trim()).slice(0, maxLength) : []; }
function allowedDraftValue(value: unknown, allowed: Set<string>, fallback: string) {
  const normalized = typeof value === "string" ? value.trim().toLowerCase() : "";
  return allowed.has(normalized) ? normalized : fallback;
}
function normalizeSocialDraft(value: Record<string, unknown>): SocialStudioDraft {
  return {
    projectId: boundedString(value.projectId, 100),
    selectedFiles: boundedIds(value.selectedFiles, 5),
    selectedAssets: boundedIds(value.selectedAssets, 8),
    prompt: boundedString(value.prompt, 6000),
    socialType: allowedDraftValue(value.socialType, socialTypes, "auto"),
    platform: allowedDraftValue(value.platform, socialPlatforms, "multi"),
    tone: allowedDraftValue(value.tone, socialTones, "professional"),
    language: allowedDraftValue(value.language, socialLanguages, "auto"),
    audience: boundedString(value.audience, 400),
    brandVoice: boundedString(value.brandVoice, 1000),
    callToAction: boundedString(value.callToAction, 400),
    includeHashtags: value.includeHashtags !== false,
    includeEmojis: value.includeEmojis === true,
    generateVariants: value.generateVariants !== false,
  };
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
