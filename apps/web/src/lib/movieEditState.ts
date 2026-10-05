import type { MovieProject } from "@/lib/api";

export type MovieEditReadiness = "ready" | "needs-review" | "blocked";

export type MovieEditScene = {
  id: string;
  sequence: number;
  title: string;
  durationSeconds: number;
  startSeconds: number;
  endSeconds: number;
  clipAssetId: string | null;
  clipStatus: string | null;
  readiness: MovieEditReadiness;
  blockers: string[];
};

export type MovieEditModel = {
  scenes: MovieEditScene[];
  durationSeconds: number;
  readySceneCount: number;
  reviewSceneCount: number;
  blockedSceneCount: number;
  timelineReady: boolean;
};

const readyClipStatuses = new Set(["Completed", "Succeeded", "Ready"]);
const activeClipStatuses = new Set(["Pending", "Queued", "Running", "ReviewRequired"]);

type MovieScene = MovieProject["scenes"][number];

type SceneClip = MovieScene["clips"][number];

function sceneDuration(scene: MovieScene, clip: SceneClip | null) {
  return Math.max(0, scene.durationSeconds ?? clip?.durationSeconds ?? 0);
}

function readyClip(scene: MovieScene) {
  return scene.clips.find((clip) => Boolean(clip.assetId) && readyClipStatuses.has(clip.status)) ?? null;
}

function sceneEditState(scene: MovieScene, clip: SceneClip | null): Pick<MovieEditScene, "readiness" | "blockers"> {
  const blockers: string[] = [];
  if (scene.durationSeconds === null && clip?.durationSeconds == null) blockers.push("Set a scene duration before editing.");
  if (!clip?.assetId || !readyClipStatuses.has(clip.status)) {
    if (scene.clips.some((item) => activeClipStatuses.has(item.status))) blockers.push("The current scene pass is still in progress.");
    else blockers.push("A reviewable scene output is required.");
  }
  if (blockers.length === 0) return { readiness: "ready", blockers };
  return {
    readiness: scene.clips.some((item) => activeClipStatuses.has(item.status)) ? "needs-review" : "blocked",
    blockers,
  };
}

export function buildMovieEditModel(project: MovieProject): MovieEditModel {
  let cursor = 0;
  const scenes = [...project.scenes]
    .sort((left, right) => left.sequence - right.sequence)
    .map((scene) => {
      const clip = readyClip(scene) ?? scene.clips[0] ?? null;
      const durationSeconds = sceneDuration(scene, clip);
      const startSeconds = cursor;
      cursor += durationSeconds;
      const editState = sceneEditState(scene, clip);
      return {
        id: scene.id,
        sequence: scene.sequence,
        title: scene.title,
        durationSeconds,
        startSeconds,
        endSeconds: cursor,
        clipAssetId: clip?.assetId ?? null,
        clipStatus: clip?.status ?? null,
        ...editState,
      };
    });
  const readySceneCount = scenes.filter((scene) => scene.readiness === "ready").length;
  const reviewSceneCount = scenes.filter((scene) => scene.readiness === "needs-review").length;
  const blockedSceneCount = scenes.filter((scene) => scene.readiness === "blocked").length;
  return {
    scenes,
    durationSeconds: cursor,
    readySceneCount,
    reviewSceneCount,
    blockedSceneCount,
    timelineReady: scenes.length > 0 && readySceneCount === scenes.length,
  };
}

export function moveMovieEditSelection(model: MovieEditModel, currentId: string | null, key: string) {
  if (!model.scenes.length) return null;
  const currentIndex = Math.max(0, model.scenes.findIndex((scene) => scene.id === currentId));
  if (key === "Home") return model.scenes[0].id;
  if (key === "End") return model.scenes[model.scenes.length - 1].id;
  if (key === "ArrowRight" || key === "ArrowDown") return model.scenes[Math.min(model.scenes.length - 1, currentIndex + 1)].id;
  if (key === "ArrowLeft" || key === "ArrowUp") return model.scenes[Math.max(0, currentIndex - 1)].id;
  return null;
}

export function clampMovieEditPlayhead(value: number, durationSeconds: number) {
  if (!Number.isFinite(value)) return 0;
  return Math.min(Math.max(0, value), Math.max(0, durationSeconds));
}
