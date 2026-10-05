import { afterEach, describe, expect, it, vi } from "vitest";
import type { GenerationJob } from "./api";
import { clearSocialActiveJobId, clearSocialDraft, displaySocialProgress, formatSocialPostForCopy, isSocialSourceReady, normalizeSocialPreviewPlatform, parseSocialJobResult, persistSocialActiveJobId, persistSocialDraft, readSocialActiveJobId, readSocialDraft, socialActiveJobStorageKey, socialDraftStorageKey, socialStudioState, type SocialComposeDraft } from "./socialStudioState";

const job = (overrides: Partial<GenerationJob> = {}): GenerationJob => ({ id: "job-1", workspaceId: "workspace-1", projectId: null, jobType: "social.generate", status: "Running", title: null, progressPercent: 100, resultJson: null, errorCode: null, errorMessage: null, cancellationRequested: false, createdAt: "2026-01-01T00:00:00Z", queuedAt: null, startedAt: null, completedAt: null, failedAt: null, cancelledAt: null, outputs: [], ...overrides });

afterEach(() => vi.unstubAllGlobals());

describe("socialStudioState", () => {
  it("caps active progress at 99 and permits terminal 100", () => {
    expect(displaySocialProgress(job({ progressPercent: 100 }))).toBe(99);
    expect(displaySocialProgress(job({ status: "Succeeded", progressPercent: 100 }))).toBe(100);
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
  it("persists compose drafts by workspace and sanitizes restored values", () => {
    const values = new Map<string, string>();
    vi.stubGlobal("window", { sessionStorage: { getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => values.set(key, value), removeItem: (key: string) => values.delete(key) } });
    const draft: SocialComposeDraft = {
      projectId: "project-1",
      selectedFiles: ["file-1"],
      selectedAssets: ["asset-1"],
      prompt: "  Write a launch post  ",
      socialType: "announcement",
      platform: "linkedin",
      tone: "professional",
      language: "en",
      audience: "operators",
      brandVoice: "clear",
      callToAction: "Join us",
      includeHashtags: true,
      includeEmojis: false,
      generateVariants: true,
    };
    persistSocialDraft("workspace-1", draft);
    persistSocialDraft("workspace-2", { ...draft, prompt: "Other workspace" });
    expect(readSocialDraft("workspace-1")).toMatchObject({ ...draft, prompt: "Write a launch post" });
    expect(readSocialDraft("workspace-2")?.prompt).toBe("Other workspace");
    values.set(socialDraftStorageKey("workspace-1"), JSON.stringify({ ...draft, platform: "provider-specific", selectedFiles: ["file-1", "file-2", "file-3", "file-4", "file-5", "file-6"] }));
    expect(readSocialDraft("workspace-1")).toMatchObject({ platform: "multi", selectedFiles: ["file-1", "file-2", "file-3", "file-4", "file-5"] });
    clearSocialDraft("workspace-1");
    expect(readSocialDraft("workspace-1")).toBeNull();
    expect(readSocialDraft("workspace-2")?.prompt).toBe("Other workspace");
  });
});
