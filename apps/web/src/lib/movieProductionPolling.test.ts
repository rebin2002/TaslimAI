import { describe, expect, it } from "vitest";
import type { MovieProject } from "./api";
import { hasActiveMovieProductionExecution, nextMovieProductionPollDelay } from "./movieProductionPolling";

function projectWith(overrides: Partial<MovieProject["scenes"][number]["shots"][number]> = {}) {
  return {
    scenes: [{ shots: [{ productionVersions: [], takes: [], ...overrides }] }],
  } as unknown as MovieProject;
}

describe("movie production polling", () => {
  it("detects active persisted executions across versions and takes", () => {
    expect(hasActiveMovieProductionExecution(projectWith())).toBe(false);
    expect(hasActiveMovieProductionExecution(projectWith({ productionVersions: [{ execution: { status: "Running" } }] as never }))).toBe(true);
    expect(hasActiveMovieProductionExecution(projectWith({ takes: [{ execution: { status: "Queued" } }] as never }))).toBe(true);
    expect(hasActiveMovieProductionExecution(projectWith({ productionVersions: [{ execution: { status: "Failed" } }] as never }))).toBe(false);
  });

  it("uses bounded linear backoff only while an execution is active", () => {
    expect(nextMovieProductionPollDelay(false)).toBeNull();
    expect(nextMovieProductionPollDelay(true)).toBe(1_000);
    expect(nextMovieProductionPollDelay(true, 1)).toBe(2_000);
    expect(nextMovieProductionPollDelay(true, 20)).toBe(5_000);
  });
});
