import { describe, expect, it } from "vitest";
import type { GenerationJob } from "./api";
import { canCancelMusicJob, isMusicJob, parseMusicJobResult, safeMusicJobView } from "./musicStudioState";

function job(overrides: Partial<GenerationJob> = {}): GenerationJob {
  return {
    id: "music-job-1", workspaceId: "workspace-1", projectId: null, jobType: "music.generate", status: "Running", title: "Calm bed", progressPercent: 42,
    resultJson: null, errorCode: null, errorMessage: null, cancellationRequested: false, createdAt: "2026-01-01T00:00:00Z", queuedAt: "2026-01-01T00:00:00Z",
    startedAt: "2026-01-01T00:00:01Z", completedAt: null, failedAt: null, cancelledAt: null, outputs: [], ...overrides,
  };
}

describe("music studio state", () => {
  it("recognizes music jobs and allows cancellation before terminal states", () => {
    expect(isMusicJob(job())).toBe(true);
    expect(canCancelMusicJob(job({ status: "Queued" }))).toBe(true);
    expect(canCancelMusicJob(job({ status: "Succeeded" }))).toBe(false);
    expect(canCancelMusicJob(job({ cancellationRequested: true }))).toBe(false);
  });

  it("parses only safe Asset result fields", () => {
    const result = parseMusicJobResult(job({ status: "Succeeded", progressPercent: 100, resultJson: '{"assetId":"asset-1","assetType":"music","format":"mp3","durationSeconds":60,"provider":"secret","model":"secret"}' }));
    expect(result).toMatchObject({ assetId: "asset-1", assetType: "music", format: "mp3", durationSeconds: 60 });
    expect(result).not.toHaveProperty("provider");
    expect(result).not.toHaveProperty("model");
  });

  it("exposes a safe failed view and ignores malformed results", () => {
    const failed = job({ status: "Failed", errorCode: "MUSIC_PROVIDER_UNAVAILABLE", errorMessage: "Music generation is temporarily unavailable. Please try again later." });
    expect(safeMusicJobView(failed)).toMatchObject({ status: "Failed", errorCode: "MUSIC_PROVIDER_UNAVAILABLE" });
    expect(parseMusicJobResult(job({ status: "Succeeded", resultJson: "not-json" }))).toBeNull();
  });
});
