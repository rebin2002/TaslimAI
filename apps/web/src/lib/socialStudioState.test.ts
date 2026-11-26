import { readFileSync } from "node:fs";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { GenerationJob } from "./api";
import { clearSocialActiveJobId, clearSocialDraft, displaySocialProgress, formatSocialPostForCopy, isSocialJobForWorkspace, isSocialSourceReady, legacySocialDraftStorageKey, nextSocialRestoreDelay, normalizeSocialPreviewPlatform, parseSocialJobResult, persistSocialActiveJobId, persistSocialDraft, purgeSocialDraftsOnAccountBoundary, readSocialActiveJobId, readSocialDraft, resolveSocialDraftScope, retireLegacySocialDraft, socialActiveJobStorageKey, socialDraftScopeToken, socialDraftStorageKey, socialStudioState } from "./socialStudioState";

const job = (overrides: Partial<GenerationJob> = {}): GenerationJob => ({ id: "job-1", workspaceId: "workspace-1", projectId: null, jobType: "social.generate", status: "Running", title: null, progressPercent: 100, resultJson: null, errorCode: null, errorMessage: null, cancellationRequested: false, createdAt: "2026-01-01T00:00:00Z", queuedAt: null, startedAt: null, completedAt: null, failedAt: null, cancelledAt: null, outputs: [], ...overrides });

afterEach(() => vi.unstubAllGlobals());

describe("socialStudioState", () => {
  it("caps active progress at 99 and permits terminal 100", () => {
    expect(displaySocialProgress(job({ progressPercent: 100 }))).toBe(99);
    expect(displaySocialProgress(job({ status: "Succeeded", progressPercent: 100 }))).toBe(100);
  });
  it("uses immediate then bounded exponential backoff for persisted job recovery", () => {
    expect(nextSocialRestoreDelay(0)).toBe(0);
    expect(nextSocialRestoreDelay(1)).toBe(700);
    expect(nextSocialRestoreDelay(3)).toBe(2_800);
    expect(nextSocialRestoreDelay(20)).toBe(8_000);
  });
  it("defensively parses a completed result and rejects malformed posts", () => {
    const parsed = parseSocialJobResult(job({ status: "Succeeded", resultJson: JSON.stringify({ assetId: "asset-1", title: "Launch", posts: [{ order: 1, hook: "Hook", body: "Body", hashtags: ["#taslim"] }, { order: 2, body: "missing hook" }] }) }));
    expect(parsed?.assetId).toBe("asset-1");
    expect(parsed?.posts).toHaveLength(1);
    expect(socialStudioState(job({ status: "Succeeded" }), parsed)).toBe("succeeded");
  });
  it("formats a post for review and copy without HTML", () => {
    expect(formatSocialPostForCopy({ order: 1, hook: "Hook", body: "Body", callToAction: "Join", hashtags: ["#taslim"] })).toBe("Hook\n\nBody\n\nJoin\n\n#taslim");
  });
  it("only treats supported extracted source files as ready", () => {
    expect(isSocialSourceReady({ id: "file-1", originalFileName: "brief.pdf", extension: ".pdf", status: "Ready", textExtractionStatus: "Ready", sizeBytes: 100 } as never)).toBe(true);
    expect(isSocialSourceReady({ id: "file-2", originalFileName: "image.png", extension: ".png", status: "Ready", textExtractionStatus: "Ready", sizeBytes: 100 } as never)).toBe(false);
  });
  it("normalizes unknown result platforms to the safe multi-platform preview", () => {
    expect(normalizeSocialPreviewPlatform(" LinkedIn ")).toBe("linkedin");
    expect(normalizeSocialPreviewPlatform("provider-specific")).toBe("multi");
  });
  it("accepts only Social jobs belonging to the active workspace", () => {
    expect(isSocialJobForWorkspace(job({ jobType: " SOCIAL.GENERATE " }), "workspace-1")).toBe(true);
    expect(isSocialJobForWorkspace(job(), "workspace-2")).toBe(false);
    expect(isSocialJobForWorkspace(job({ jobType: "image.generate" }), "workspace-1")).toBe(false);
    expect(isSocialJobForWorkspace(null, "workspace-1")).toBe(false);
  });
  it("persists the active job by workspace and clears it when the user starts over", () => {
    const values = new Map<string, string>();
    vi.stubGlobal("window", { sessionStorage: { getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => values.set(key, value), removeItem: (key: string) => values.delete(key) } });
    persistSocialActiveJobId("workspace-1", "job-1");
    expect(values.get(socialActiveJobStorageKey("workspace-1"))).toBe("job-1");
    expect(readSocialActiveJobId("workspace-1")).toBe("job-1");
    expect(readSocialActiveJobId("workspace-2")).toBeNull();
    clearSocialActiveJobId("workspace-1");
    expect(readSocialActiveJobId("workspace-1")).toBeNull();
  });
  it("persists drafts for an identity + workspace pair and bounds values restored from session storage", () => {
    const values = new Map<string, string>();
    vi.stubGlobal("window", { sessionStorage: { getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => values.set(key, value), removeItem: (key: string) => values.delete(key) } });
    const owner = { userId: "user-1", workspaceId: "workspace-1" };
    persistSocialDraft(owner, { projectId: "project-1", selectedFiles: ["file-1"], selectedAssets: ["asset-1"], prompt: "Brief", socialType: "announcement", platform: "linkedin", tone: "professional", language: "en", audience: "Owners", brandVoice: "Clear", callToAction: "Learn more", includeHashtags: true, includeEmojis: false, generateVariants: true });
    expect(values.has(socialDraftStorageKey(owner) as string)).toBe(true);
    // Neither a neighbouring workspace nor a neighbouring identity can address this entry.
    expect(readSocialDraft({ userId: "user-1", workspaceId: "workspace-2" })).toBeNull();
    expect(readSocialDraft({ userId: "user-2", workspaceId: "workspace-1" })).toBeNull();
    expect(readSocialDraft(owner)?.prompt).toBe("Brief");
    values.set(socialDraftStorageKey(owner) as string, JSON.stringify({ v: 1, u: "user-1", w: "workspace-1", savedAt: 1, d: { prompt: "x".repeat(7000), selectedFiles: ["a", "b", "c", "d", "e", "f"] } }));
    const restored = readSocialDraft(owner);
    expect(restored?.prompt).toHaveLength(6000);
    expect(restored?.selectedFiles).toEqual(["a", "b", "c", "d", "e"]);
    values.set(socialDraftStorageKey(owner) as string, JSON.stringify({ v: 1, u: "user-1", w: "workspace-1", savedAt: 1, d: { socialType: "unsupported", platform: "provider-only", tone: "unknown", language: "fr" } }));
    expect(readSocialDraft(owner)).toMatchObject({ socialType: "auto", platform: "multi", tone: "professional", language: "auto" });
    // An entry claiming a different owner is never surfaced to this identity, and is purged.
    values.set(socialDraftStorageKey(owner) as string, JSON.stringify({ v: 1, u: "user-2", w: "workspace-1", savedAt: 1, d: { prompt: "SYNTHETIC foreign private" } }));
    expect(readSocialDraft(owner)).toBeNull();
    expect(values.has(socialDraftStorageKey(owner) as string)).toBe(false);
    persistSocialDraft(owner, { projectId: "", selectedFiles: [], selectedAssets: [], prompt: "Brief", socialType: "auto", platform: "multi", tone: "professional", language: "auto", audience: "", brandVoice: "", callToAction: "", includeHashtags: true, includeEmojis: false, generateVariants: true });
    clearSocialDraft(owner);
    expect(readSocialDraft(owner)).toBeNull();
  });
  it("refuses to address a persisted draft by workspace alone (#688)", () => {
    const values = new Map<string, string>();
    vi.stubGlobal("window", { sessionStorage: { getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => values.set(key, value), removeItem: (key: string) => values.delete(key) } });
    // A bare workspace id is an authorisation scope, not an authentication result: it resolves to no
    // address at all, so an un-migrated or unauthenticated call site fails closed instead of leaking.
    expect(socialDraftStorageKey("workspace-1")).toBeNull();
    expect(resolveSocialDraftScope("workspace-1")).toBeNull();
    expect(socialDraftScopeToken({ userId: null, workspaceId: "workspace-1" })).toBeNull();
    const draft = { projectId: "", selectedFiles: [], selectedAssets: [], prompt: "SYNTHETIC private brief", socialType: "auto", platform: "multi", tone: "professional", language: "auto", audience: "", brandVoice: "", callToAction: "", includeHashtags: true, includeEmojis: false, generateVariants: true };
    expect(persistSocialDraft("workspace-1", draft)).toBe(false);
    expect(readSocialDraft("workspace-1")).toBeNull();
    expect(clearSocialDraft("workspace-1")).toBe(false);
    expect([...values.keys()]).toEqual([]);
    // Sentinel identities are refused the same way, and trimming does not launder one into a real id.
    expect(resolveSocialDraftScope({ userId: "anonymous", workspaceId: "workspace-1" })).toBeNull();
    expect(resolveSocialDraftScope({ userId: "  ", workspaceId: "workspace-1" })).toBeNull();
    expect(resolveSocialDraftScope({ userId: "user-1", workspaceId: "workspace-1" })).toEqual({ userId: "user-1", workspaceId: "workspace-1" });
  });
  it("quarantines a pre-#688 workspace-only draft without ever surfacing it", () => {
    const values = new Map<string, string>();
    vi.stubGlobal("window", { sessionStorage: { getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => values.set(key, value), removeItem: (key: string) => values.delete(key) } });
    const legacyKey = legacySocialDraftStorageKey("workspace-1");
    values.set(legacyKey, JSON.stringify({ prompt: "SYNTHETIC pre-688 private draft", selectedAssets: ["synthetic-asset-688"] }));
    // The identity that happens to arrive next cannot obtain the payload, and the entry leaves the tab.
    expect(readSocialDraft({ userId: "user-1", workspaceId: "workspace-1" })).toBeNull();
    expect(retireLegacySocialDraft({ userId: "user-1", workspaceId: "workspace-1" })).toBe("quarantined-removed");
    expect(values.has(legacyKey)).toBe(false);
    expect([...values.values()].join(" ")).not.toContain("SYNTHETIC pre-688 private draft");
  });
  it("scrubs only the proven signing-out identity's drafts at the account boundary", () => {
    const values = new Map<string, string>();
    vi.stubGlobal("window", { sessionStorage: { getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => values.set(key, value), removeItem: (key: string) => values.delete(key), key: (index: number) => [...values.keys()][index] ?? null, get length() { return values.size; } } });
    const draft = (prompt: string) => ({ projectId: "", selectedFiles: [], selectedAssets: [], prompt, socialType: "auto", platform: "multi", tone: "professional", language: "auto", audience: "", brandVoice: "", callToAction: "", includeHashtags: true, includeEmojis: false, generateVariants: true });
    persistSocialDraft({ userId: "user-1", workspaceId: "workspace-1" }, draft("A private"));
    persistSocialDraft({ userId: "user-2", workspaceId: "workspace-1" }, draft("B private"));
    expect(purgeSocialDraftsOnAccountBoundary("user-1")).toEqual({ removedCount: 1, refusedInvalidIdentity: false });
    expect(readSocialDraft({ userId: "user-1", workspaceId: "workspace-1" })).toBeNull();
    expect(readSocialDraft({ userId: "user-2", workspaceId: "workspace-1" })?.prompt).toBe("B private");
    // An unproven identity destroys nothing at all, so a half-resolved session cannot wipe state.
    expect(purgeSocialDraftsOnAccountBoundary(null)).toEqual({ removedCount: 0, refusedInvalidIdentity: true });
    expect(purgeSocialDraftsOnAccountBoundary("anonymous")).toEqual({ removedCount: 0, refusedInvalidIdentity: true });
    expect(readSocialDraft({ userId: "user-2", workspaceId: "workspace-1" })?.prompt).toBe("B private");
  });
  it("ignores malformed draft storage instead of breaking the studio", () => {
    vi.stubGlobal("window", { sessionStorage: { getItem: () => "{not-json", setItem: vi.fn(), removeItem: vi.fn() } });
    expect(readSocialDraft({ userId: "user-1", workspaceId: "workspace-1" })).toBeNull();
  });
  it("keeps generated drafts out of broad live announcements", () => {
    const source = readFileSync(new URL("../components/SocialStudioView.tsx", import.meta.url), "utf8");
    expect(source).toContain('role="status" aria-live="polite" aria-atomic="true"');
    expect(source).toContain('aria-busy={shouldPollSocialJob(visibleCurrent)}');
    expect(source).toContain('aria-labelledby="social-result-title"');
    expect(source).toContain('<h2 id="social-result-title">');
    expect(source).not.toContain('<section className="social-result-shell" aria-live="polite">');
  });
});
