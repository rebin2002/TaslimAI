import { afterEach, describe, expect, it, vi } from "vitest";
import { canCancelVoiceJob, clearVoiceActiveJobId, formatVoiceDuration, formatVoiceFileSize, isRestorableVoiceJob, isVoiceAsset, isVoiceJob, isVoiceTerminal, nextVoicePollDelay, parseVoiceJobResult, persistVoiceActiveJobId, readVoiceActiveJobId, shouldPollVoiceJob, voiceActiveJobStorageKey } from "./voiceStudioState";
import type { GenerationJob } from "./api";

const baseJob: GenerationJob = {
  id: "job-1",
  workspaceId: "workspace-1",
  projectId: null,
  jobType: "voice.generate",
  status: "Succeeded",
  title: null,
  progressPercent: 100,
  resultJson: JSON.stringify({ assetId: "asset-1", assetType: "audio", contentType: "audio/mpeg", format: "mp3", language: "ar", voiceStyle: "warm", speakingStyle: "clear", sizeBytes: 320 }),
  errorCode: null,
  errorMessage: null,
  cancellationRequested: false,
  createdAt: "2026-09-23T00:00:00Z",
  queuedAt: null,
  startedAt: null,
  completedAt: "2026-09-23T00:00:01Z",
  failedAt: null,
  cancelledAt: null,
  outputs: [],
};

describe("voiceStudioState", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("recognizes Voice jobs and parses user-safe audio metadata", () => {
    expect(isVoiceJob(baseJob)).toBe(true);
    expect(isRestorableVoiceJob(baseJob, "workspace-1")).toBe(true);
    expect(isRestorableVoiceJob({ ...baseJob, workspaceId: "workspace-2" }, "workspace-1")).toBe(false);
    expect(isRestorableVoiceJob({ ...baseJob, jobType: "image.generate" }, "workspace-1")).toBe(false);
    expect(parseVoiceJobResult(baseJob)).toMatchObject({ assetId: "asset-1", assetType: "audio", language: "ar", format: "mp3" });
  });

  it("polls active jobs with bounded retry backoff and stops at terminal states", () => {
    const running = { ...baseJob, status: "Running" as const, progressPercent: 50 };
    expect(isVoiceTerminal(running)).toBe(false);
    expect(shouldPollVoiceJob(running)).toBe(true);
    expect(nextVoicePollDelay(running, 0)).toBe(650);
    expect(nextVoicePollDelay(running, 2)).toBe(1_950);
    expect(nextVoicePollDelay(running, 99)).toBe(2_800);
    expect(isVoiceTerminal(baseJob)).toBe(true);
    expect(shouldPollVoiceJob(baseJob)).toBe(false);
    expect(nextVoicePollDelay(baseJob)).toBeNull();
  });

  it("rejects malformed or non-audio results", () => {
    expect(parseVoiceJobResult({ ...baseJob, resultJson: "not-json" })).toBeNull();
    expect(parseVoiceJobResult({ ...baseJob, resultJson: JSON.stringify({ assetId: "asset-1", assetType: "image" }) })).toBeNull();
  });

  it("only allows cancellation for active Voice jobs", () => {
    expect(canCancelVoiceJob({ ...baseJob, status: "Queued", progressPercent: 0 })).toBe(true);
    expect(canCancelVoiceJob({ ...baseJob, status: "Running", progressPercent: 50 })).toBe(true);
    expect(canCancelVoiceJob({ ...baseJob, status: "Running", cancellationRequested: true })).toBe(false);
    expect(canCancelVoiceJob(baseJob)).toBe(false);
  });

  it("keeps only Voice audio assets in the recent shelf", () => {
    const asset = {
      id: "asset-1", workspaceId: "workspace-1", projectId: null, projectName: null, name: "Narration", description: null,
      assetType: "audio" as const, mimeType: "audio/mpeg", status: "Active" as const, hasFile: true, canPreview: true,
      fileSizeBytes: 2048, sourceStudio: "voice", sourceJobTitle: "Generated speech", createdAt: "2026-09-23T00:00:00Z", updatedAt: "2026-09-23T00:00:00Z", archivedAt: null, representations: [],
    };
    expect(isVoiceAsset(asset)).toBe(true);
    expect(isVoiceAsset({ ...asset, sourceStudio: "music" })).toBe(false);
    expect(isVoiceAsset({ ...asset, assetType: "music" })).toBe(false);
  });

  it("formats player duration and file size without inventing missing metadata", () => {
    expect(formatVoiceDuration(90500)).toBe("1:30");
    expect(formatVoiceDuration(null)).toBe("0:00");
    expect(formatVoiceFileSize(2048)).toBe("2.0 KB");
    expect(formatVoiceFileSize(null)).toBe("");
  });

  it("persists active jobs by workspace and clears stale pointers", () => {
    const values = new Map<string, string>();
    vi.stubGlobal("window", {
      sessionStorage: {
        getItem: (key: string) => values.get(key) ?? null,
        setItem: (key: string, value: string) => values.set(key, value),
        removeItem: (key: string) => values.delete(key),
      },
    });
    persistVoiceActiveJobId("workspace-1", "job-1");
    expect(values.get(voiceActiveJobStorageKey("workspace-1"))).toBe("job-1");
    expect(readVoiceActiveJobId("workspace-1")).toBe("job-1");
    expect(readVoiceActiveJobId("workspace-2")).toBeNull();
    clearVoiceActiveJobId("workspace-1");
    expect(readVoiceActiveJobId("workspace-1")).toBeNull();
  });
});
