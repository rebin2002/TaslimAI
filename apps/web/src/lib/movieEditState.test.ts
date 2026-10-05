import { describe, expect, it } from "vitest";
import type { MovieClip, MovieProject } from "@/lib/api";
import { buildMovieEditModel, clampMovieEditPlayhead, moveMovieEditSelection } from "@/lib/movieEditState";

function clip(overrides: Partial<MovieClip> = {}): MovieClip {
  return {
    id: "clip-1",
    movieSceneId: "scene-1",
    movieShotId: null,
    generationJobId: null,
    assetId: null,
    status: "Pending",
    durationSeconds: null,
    metadataJson: null,
    continuitySnapshotJson: null,
    ...overrides,
  };
}

function projectWithScenes(scenes: Array<Record<string, unknown>>): MovieProject {
  return { scenes } as unknown as MovieProject;
}

describe("movie edit state", () => {
  it("sorts scenes, derives a contiguous timeline, and prefers a reviewable clip", () => {
    const model = buildMovieEditModel(projectWithScenes([
      { id: "scene-2", sequence: 2, title: "Arrival", durationSeconds: 4, clips: [clip({ id: "clip-2", assetId: "asset-2", status: "Ready" })] },
      { id: "scene-1", sequence: 1, title: "Platform", durationSeconds: 6, clips: [clip({ id: "clip-1", assetId: "asset-1", status: "Succeeded" })] },
    ]));

    expect(model.scenes.map((scene) => scene.id)).toEqual(["scene-1", "scene-2"]);
    expect(model.scenes.map((scene) => [scene.startSeconds, scene.endSeconds])).toEqual([[0, 6], [6, 10]]);
    expect(model.durationSeconds).toBe(10);
    expect(model.readySceneCount).toBe(2);
    expect(model.timelineReady).toBe(true);
  });

  it("keeps incomplete scenes visible and explains whether they are active or blocked", () => {
    const model = buildMovieEditModel(projectWithScenes([
      { id: "scene-1", sequence: 1, title: "Pending", durationSeconds: 5, clips: [clip({ status: "Running" })] },
      { id: "scene-2", sequence: 2, title: "Missing", durationSeconds: null, clips: [] },
    ]));

    expect(model.scenes[0].readiness).toBe("needs-review");
    expect(model.scenes[0].blockers).toContain("The current scene pass is still in progress.");
    expect(model.scenes[1].readiness).toBe("blocked");
    expect(model.scenes[1].blockers).toEqual([
      "Set a scene duration before editing.",
      "A reviewable scene output is required.",
    ]);
    expect(model.timelineReady).toBe(false);
  });

  it("supports predictable listbox navigation and bounded playhead values", () => {
    const model = buildMovieEditModel(projectWithScenes([
      { id: "scene-1", sequence: 1, title: "One", durationSeconds: 3, clips: [] },
      { id: "scene-2", sequence: 2, title: "Two", durationSeconds: 4, clips: [] },
      { id: "scene-3", sequence: 3, title: "Three", durationSeconds: 5, clips: [] },
    ]));

    expect(moveMovieEditSelection(model, "scene-2", "ArrowRight")).toBe("scene-3");
    expect(moveMovieEditSelection(model, "scene-2", "ArrowLeft")).toBe("scene-1");
    expect(moveMovieEditSelection(model, "scene-2", "Home")).toBe("scene-1");
    expect(moveMovieEditSelection(model, "scene-2", "End")).toBe("scene-3");
    expect(moveMovieEditSelection(model, "scene-2", "Enter")).toBeNull();
    expect(clampMovieEditPlayhead(-2, 12)).toBe(0);
    expect(clampMovieEditPlayhead(20, 12)).toBe(12);
    expect(clampMovieEditPlayhead(Number.NaN, 12)).toBe(0);
  });
});
