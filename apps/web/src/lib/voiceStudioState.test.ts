import { describe, expect, it } from "vitest";
import { canCancelVoiceJob, formatVoiceDuration, formatVoiceFileSize, isVoiceAsset, isVoiceJob, parseVoiceJobResult } from "./voiceStudioState";
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
  it("recognizes Voice jobs and parses user-safe audio metadata", () => {
    expect(isVoiceJob(baseJob)).toBe(true);
    expect(parseVoiceJobResult(baseJob)).toMatchObject({ assetId: "asset-1", assetType: "audio", language: "ar", format: "mp3" });
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
});
