import type {
  MovieFinalMaster,
  MovieProductionReviewInput,
  MovieProductionVersion,
  MovieProject,
  MovieTake,
} from "@/lib/api";
import type { MovieRenderIntent } from "@/lib/movieProductionResolution";

export type ProductionWorkspaceFilter = "all" | "needs-review" | "ready" | "blocked";
export type ProductionReadiness = "blocked" | "needs-review" | "in-progress" | "ready";
export type ProductionWorkspaceStage = "shot-plan" | "keyframe" | "candidates" | "review" | "select" | "finish" | "timeline";

export type ProductionWorkspaceShot = {
  sceneId: string;
  sceneSequence: number;
  sceneTitle: string;
  shot: MovieProject["scenes"][number]["shots"][number];
  versions: MovieProductionVersion[];
  takes: MovieTake[];
  storyboard: MovieProductionVersion | null;
  keyframe: MovieProductionVersion | null;
  pendingKeyframe: MovieProductionVersion | null;
  motion: MovieProductionVersion | null;
  render: MovieProductionVersion | null;
  selectedTake: MovieTake | null;
  finalTake: MovieTake | null;
  readiness: ProductionReadiness;
  readinessLabel: string;
  nextAction: string;
};

export type ProductionTimelineRow = {
  sceneId: string;
  sequence: number;
  title: string;
  durationSeconds: number;
  shotCount: number;
  readyShotCount: number;
  readiness: ProductionReadiness;
  detail: string;
};

export type ProductionWorkspaceModel = {
  shots: ProductionWorkspaceShot[];
  timeline: ProductionTimelineRow[];
  counts: {
    scenes: number;
    shots: number;
    shotPlanReady: number;
    keyframesReady: number;
    candidates: number;
    takes: number;
    selectedTakes: number;
    readyForTimeline: number;
    needsReview: number;
    blocked: number;
  };
  currentStage: ProductionWorkspaceStage;
  progressPercent: number;
  timelineReady: boolean;
  timelineSummary: string;
};

function latestVersion(versions: MovieProductionVersion[], predicate: (version: MovieProductionVersion) => boolean) {
  return [...versions].filter(predicate).sort((left, right) => right.versionNumber - left.versionNumber)[0] ?? null;
}

function isReadyTake(take: MovieTake | null) {
  return Boolean(take && take.assetId && ["Ready", "ReviewRequired", "Approved", "Selected"].includes(take.status));
}

function shotPlanReady(shot: ProductionWorkspaceShot["shot"]) {
  return shot.readiness.ready || ["ReadyForStoryboard", "Storyboard", "Production"].includes(shot.planState);
}

function buildShot(scene: MovieProject["scenes"][number], shot: ProductionWorkspaceShot["shot"]): ProductionWorkspaceShot {
  const versions = [...(shot.productionVersions ?? [])].sort((left, right) => right.versionNumber - left.versionNumber);
  const takes = [...(shot.takes ?? [])].sort((left, right) => right.versionNumber - left.versionNumber);
  const storyboard = latestVersion(versions, (version) => ["ApprovedStoryboard", "StoryboardCandidate"].includes(version.stage) && version.status === "Approved");
  const keyframe = latestVersion(versions, (version) => ["ApprovedKeyframe", "ProductionKeyframe"].includes(version.stage) && version.status === "Approved");
  const pendingKeyframe = latestVersion(versions, (version) => version.stage === "ProductionKeyframe" && version.status === "PendingApproval");
  const motion = latestVersion(versions, (version) => version.stage === "MotionPreview");
  const render = latestVersion(versions, (version) => version.stage === "ProductionRender");
  const selectedTake = takes.find((take) => Boolean(take.selectedAt)) ?? null;
  const finalTake = takes.find((take) => Boolean(take.finalizedAt)) ?? null;
  const failedRender = render?.execution?.status === "Failed";

  let readiness: ProductionReadiness = "blocked";
  let readinessLabel = "Shot plan needed";
  let nextAction = "Complete the approved shot plan";
  if (failedRender) {
    readiness = "needs-review";
    readinessLabel = "Pass needs attention";
    nextAction = "Review the failed pass and retry when ready";
  } else if (finalTake || (selectedTake && isReadyTake(selectedTake))) {
    readiness = "ready";
    readinessLabel = "Ready for timeline";
    nextAction = "Review the selected take or record a master hand-off";
  } else if (render?.execution?.status === "Succeeded") {
    readiness = "needs-review";
    readinessLabel = "Take needs review";
    nextAction = "Save the render as a candidate take";
  } else if (motion?.status === "PendingApproval") {
    readiness = "needs-review";
    readinessLabel = "Motion check needs review";
    nextAction = "Review the motion check";
  } else if (keyframe) {
    readiness = "in-progress";
    readinessLabel = "Source frame approved";
    nextAction = "Prepare a motion check";
  } else if (pendingKeyframe) {
    readiness = "needs-review";
    readinessLabel = "Source frame needs review";
    nextAction = "Review the source frame";
  } else if (storyboard) {
    readiness = "in-progress";
    readinessLabel = "Visual plan approved";
    nextAction = "Prepare the source frame";
  } else if (shotPlanReady(shot)) {
    readiness = "in-progress";
    readinessLabel = "Shot plan approved";
    nextAction = "Open Storyboard to approve a visual plan";
  }

  return {
    sceneId: scene.id,
    sceneSequence: scene.sequence,
    sceneTitle: scene.title,
    shot,
    versions,
    takes,
    storyboard,
    keyframe,
    pendingKeyframe,
    motion,
    render,
    selectedTake,
    finalTake,
    readiness,
    readinessLabel,
    nextAction,
  };
}

export function buildMovieProductionWorkspaceModel(project: MovieProject): ProductionWorkspaceModel {
  const shots = project.scenes
    .flatMap((scene) => scene.shots.map((shot) => buildShot(scene, shot)))
    .sort((left, right) => left.sceneSequence - right.sceneSequence || left.shot.sequence - right.shot.sequence);
  const timeline = project.scenes.map((scene) => {
    const sceneShots = shots.filter((item) => item.sceneId === scene.id);
    const readyShotCount = sceneShots.filter((item) => item.readiness === "ready").length;
    const readiness: ProductionReadiness = sceneShots.length === 0 ? "blocked" : readyShotCount === sceneShots.length ? "ready" : sceneShots.some((item) => item.readiness === "needs-review") ? "needs-review" : "in-progress";
    return {
      sceneId: scene.id,
      sequence: scene.sequence,
      title: scene.title,
      durationSeconds: scene.durationSeconds ?? sceneShots.reduce((total, item) => total + (item.shot.durationSeconds ?? 0), 0),
      shotCount: sceneShots.length,
      readyShotCount,
      readiness,
      detail: sceneShots.length === 0 ? "Add an approved shot plan" : `${readyShotCount} of ${sceneShots.length} shots ready`,
    };
  });
  const selectedTakes = shots.filter((item) => item.selectedTake).length;
  const readyForTimeline = shots.filter((item) => item.readiness === "ready").length;
  const needsReview = shots.filter((item) => item.readiness === "needs-review").length;
  const blocked = shots.filter((item) => item.readiness === "blocked").length;
  const shotPlanReadyCount = shots.filter((item) => shotPlanReady(item.shot)).length;
  const keyframesReady = shots.filter((item) => item.keyframe).length;
  const candidates = shots.reduce((total, item) => total + item.takes.length, 0);
  const stageUnits = shots.length * 4;
  const completedUnits = shots.reduce((total, item) => total + (shotPlanReady(item.shot) ? 1 : 0) + (item.keyframe ? 1 : 0) + (item.takes.length ? 1 : 0) + (item.readiness === "ready" ? 1 : 0), 0);
  const progressPercent = stageUnits === 0 ? 0 : Math.round((completedUnits / stageUnits) * 100);
  const currentStage: ProductionWorkspaceStage = shots.length === 0 ? "shot-plan" : blocked > 0 && shotPlanReadyCount < shots.length ? "shot-plan" : keyframesReady < shots.length ? "keyframe" : candidates === 0 ? "candidates" : needsReview > 0 ? "review" : selectedTakes < shots.length ? "select" : readyForTimeline === shots.length ? "timeline" : "finish";
  const timelineReady = shots.length > 0 && readyForTimeline === shots.length;
  return {
    shots,
    timeline,
    counts: {
      scenes: project.scenes.length,
      shots: shots.length,
      shotPlanReady: shotPlanReadyCount,
      keyframesReady,
      candidates,
      takes: candidates,
      selectedTakes,
      readyForTimeline,
      needsReview,
      blocked,
    },
    currentStage,
    progressPercent,
    timelineReady,
    timelineSummary: shots.length === 0 ? "Add an approved shot plan to begin." : timelineReady ? "Every shot has a selected take." : `${readyForTimeline} of ${shots.length} shots are ready for the timeline.`,
  };
}

export function matchesProductionFilter(item: ProductionWorkspaceShot, filter: ProductionWorkspaceFilter) {
  if (filter === "all") return true;
  if (filter === "needs-review") return item.readiness === "needs-review";
  if (filter === "ready") return item.readiness === "ready";
  return item.readiness === "blocked";
}

export type MovieProductionWorkspaceAdapter = {
  createKeyframe: (shotId: string, sourceVersionId: string, compositionJson: string) => Promise<unknown>;
  reviewVersion: (versionId: string, input: MovieProductionReviewInput) => Promise<unknown>;
  createMotionPreview: (shotId: string, sourceVersionId: string) => Promise<unknown>;
  queueRender: (shotId: string, sourceVersionId: string, retry: boolean, intent?: MovieRenderIntent) => Promise<unknown>;
  createTake: (versionId: string) => Promise<unknown>;
  approveTake: (takeId: string) => Promise<unknown>;
  selectTake: (takeId: string) => Promise<unknown>;
  finalizeTake: (takeId: string) => Promise<unknown>;
  requestMaster: (takeId: string) => Promise<MovieFinalMaster | unknown>;
};

export function createFakeMovieProductionWorkspaceAdapter(): MovieProductionWorkspaceAdapter & { calls: string[] } {
  const calls: string[] = [];
  const record = (name: string) => {
    calls.push(name);
    return Promise.resolve({ ok: true });
  };
  return {
    calls,
    createKeyframe: (shotId) => record(`keyframe:${shotId}`),
    reviewVersion: (versionId) => record(`review:${versionId}`),
    createMotionPreview: (shotId) => record(`motion:${shotId}`),
    queueRender: (shotId, _sourceVersionId, retry) => record(`${retry ? "retry" : "render"}:${shotId}`),
    createTake: (versionId) => record(`take:${versionId}`),
    approveTake: (takeId) => record(`approve-take:${takeId}`),
    selectTake: (takeId) => record(`select-take:${takeId}`),
    finalizeTake: (takeId) => record(`finalize-take:${takeId}`),
    requestMaster: (takeId) => record(`master:${takeId}`),
  };
}
