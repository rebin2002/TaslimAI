import { describe, expect, it } from "vitest";
import { canCancelVoiceJob, isVoiceJob, parseVoiceJobResult } from "./voiceStudioState";
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
});
