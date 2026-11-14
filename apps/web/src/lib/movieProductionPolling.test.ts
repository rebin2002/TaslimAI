import { describe, expect, it } from "vitest";
import type { MovieClip, MovieProject } from "./api";
import { hasActiveMovieProductionExecution, nextMovieProductionPollDelay } from "./movieProductionPolling";

function projectWith(overrides: Partial<MovieProject["scenes"][number]["shots"][number]> = {}) {
  return {
    scenes: [{ shots: [{ productionVersions: [], takes: [], ...overrides }] }],
  } as unknown as MovieProject;
}

/**
 * Deterministic stand-in for the fake video provider's persisted result. The
 * worker keeps the Movie clip in a non-terminal clip status until it publishes
 * the private Asset the player reads, so the persisted clip row is the only
 * signal that a short-video output is still on its way.
 */
function providerClip(overrides: Partial<MovieClip> = {}): MovieClip {
  return {
    id: "clip-1",
    movieSceneId: "scene-1",
    movieShotId: null,
    generationJobId: "job-1",
    assetId: null,
    status: "Queued",
    durationSeconds: 8,
    metadataJson: null,
    continuitySnapshotJson: null,
    ...overrides,
  };
}

function projectWithSceneClips(clips: MovieClip[]) {
  return {
    scenes: [{ id: "scene-1", sequence: 1, shots: [], clips }],
  } as unknown as MovieProject;
}

function projectWithShotClips(clips: MovieClip[]) {
  return {
    scenes: [
      { id: "scene-1", sequence: 1, clips: [], shots: [{ productionVersions: [], takes: [], clips }] },
    ],
  } as unknown as MovieProject;
}

describe("movie production polling", () => {
  it("detects active persisted executions across versions and takes", () => {
    expect(hasActiveMovieProductionExecution(projectWith())).toBe(false);
    expect(hasActiveMovieProductionExecution(projectWith({ productionVersions: [{ execution: { status: "Running" } }] as never }))).toBe(true);
    expect(hasActiveMovieProductionExecution(projectWith({ takes: [{ execution: { status: "Queued" } }] as never }))).toBe(true);
    expect(hasActiveMovieProductionExecution(projectWith({ productionVersions: [{ execution: { status: "Failed" } }] as never }))).toBe(false);
  });

  it("keeps polling while a provider short clip has not published its private output", () => {
    expect(hasActiveMovieProductionExecution(projectWithSceneClips([providerClip()]))).toBe(true);
    expect(hasActiveMovieProductionExecution(projectWithSceneClips([providerClip({ status: "Generating" })]))).toBe(true);
    expect(hasActiveMovieProductionExecution(projectWithShotClips([providerClip({ movieShotId: "shot-1", status: "Generating" })]))).toBe(true);
  });

  it("stops polling once the short clip output is playable or the pass ended", () => {
    expect(hasActiveMovieProductionExecution(projectWithSceneClips([providerClip({ status: "Ready", assetId: "asset-1" })]))).toBe(false);
    expect(hasActiveMovieProductionExecution(projectWithSceneClips([providerClip({ status: "Failed" })]))).toBe(false);
    expect(hasActiveMovieProductionExecution(projectWithSceneClips([providerClip({ status: "Cancelled" })]))).toBe(false);
    expect(hasActiveMovieProductionExecution(projectWithSceneClips([providerClip({ status: "Planned" })]))).toBe(false);
    expect(hasActiveMovieProductionExecution(projectWithSceneClips([]))).toBe(false);
  });

  it("never treats a clip that already carries a published asset as in flight", () => {
    expect(hasActiveMovieProductionExecution(projectWithSceneClips([providerClip({ assetId: "asset-1" })]))).toBe(false);
  });

  it("uses bounded linear backoff only while an execution is active", () => {
    expect(nextMovieProductionPollDelay(false)).toBeNull();
    expect(nextMovieProductionPollDelay(true)).toBe(1_000);
    expect(nextMovieProductionPollDelay(true, 1)).toBe(2_000);
    expect(nextMovieProductionPollDelay(true, 20)).toBe(5_000);
  });

  it("keeps the bounded delay in force for an in-flight short clip", () => {
    const project = projectWithSceneClips([providerClip({ status: "Generating" })]);
    expect(nextMovieProductionPollDelay(hasActiveMovieProductionExecution(project))).toBe(1_000);
  });
});
