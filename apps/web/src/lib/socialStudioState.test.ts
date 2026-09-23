import { describe, expect, it } from "vitest";
import type { GenerationJob } from "./api";
import { displaySocialProgress, formatSocialPostForCopy, isSocialSourceReady, parseSocialJobResult, socialStudioState } from "./socialStudioState";

const job = (overrides: Partial<GenerationJob> = {}): GenerationJob => ({ id: "job-1", workspaceId: "workspace-1", projectId: null, jobType: "social.generate", status: "Running", title: null, progressPercent: 100, resultJson: null, errorCode: null, errorMessage: null, cancellationRequested: false, createdAt: "2026-01-01T00:00:00Z", queuedAt: null, startedAt: null, completedAt: null, failedAt: null, cancelledAt: null, outputs: [], ...overrides });

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
});
