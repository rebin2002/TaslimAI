import { describe, expect, it } from "vitest";
import type { MovieProductionExecution, MovieProductionVersion, MovieProject, MovieShot } from "@/lib/api";
import { buildMovieProductionWorkspaceModel, createFakeMovieProductionWorkspaceAdapter, matchesProductionFilter, movieProductionExecutionState, movieProductionProgressPercent } from "@/lib/movieProductionWorkspace";

function projectWith(overrides: Partial<MovieProject> = {}): MovieProject {
  return {
    id: "movie-1",
    workspaceId: "workspace-1",
    projectId: null,
    mode: "Full",
    status: "Draft",
    title: "Night train",
    description: "A quiet journey through the city.",
    durationSeconds: 60,
    aspectRatio: "16:9",
    style: "cinematic",
    language: "en",
    additionalInstructions: null,
    createdAt: "2026-09-30T00:00:00Z",
    updatedAt: "2026-09-30T00:00:00Z",
    guide: {
      id: "guide-1",
      visualLanguage: "Natural",
      cameraLanguage: "Handheld",
      colorAndLighting: "Blue hour",
      soundAndNarration: "Sparse",
      continuityRules: "Keep the red scarf visible.",
      updatedAt: "2026-09-30T00:00:00Z",
    },
    scenes: [],
    characters: [],
    locations: [],
    clips: [],
    assemblies: [],
    world: {
      locations: [],
      sets: [],
      props: [],
      references: [],
      usages: [],
      facts: [],
      locks: [],
    },
    ...overrides,
  };
}

function shot(overrides: Partial<MovieShot> = {}): MovieShot {
  return {
    id: "shot-1",
    sequence: 1,
    description: "A train enters the frame.",
    purpose: "Establish the journey",
    subjects: "Train",
    subjectCharacterIds: [],
    locationSet: "Station",
    durationSeconds: 8,
    productionRequirements: null,
    continuityReferences: null,
    cameraAndFraming: "Wide · 35mm",
    cameraMotion: "Slow push",
    cinematographyJson: null,
    cinematographySummary: "Quiet wide shot",
    narration: null,
    dialogue: null,
    visualContinuityNotes: "Red scarf remains visible.",
    status: "Active",
    planState: "ReadyForStoryboard",
    readiness: { ready: true, checks: [], missing: [], summary: "Shot plan is ready." },
    productionStage: "ShotPlan",
    clips: [],
    productionVersions: [],
    takes: [],
    ...overrides,
  };
}

describe("movie production workspace model", () => {
  it("keeps an empty project blocked and timeline-ineligible", () => {
    const model = buildMovieProductionWorkspaceModel(projectWith());
    expect(model.currentStage).toBe("shot-plan");
    expect(model.timelineReady).toBe(false);
    expect(model.timelineSummary).toContain("approved shot plan");
  });

  it("connects approved plan, keyframe, candidate take, selection, and timeline readiness", () => {
    const model = buildMovieProductionWorkspaceModel(projectWith({
      scenes: [{
        id: "scene-1",
        sequence: 1,
        title: "Platform",
        summary: "The train arrives.",
        durationSeconds: 8,
        continuityNotes: null,
        narration: null,
        dialogue: null,
        clips: [],
        shots: [shot({
          productionVersions: [
            { id: "storyboard-1", movieShotId: "shot-1", versionNumber: 1, stage: "ApprovedStoryboard", status: "Approved", label: "Approved visual plan", compositionJson: "{}", regenerationMetadataJson: null, stageProvenanceJson: null, continuitySnapshotReferenceJson: null, cinematographyReferenceJson: null, sourceVersionId: null, generationJobId: null, assetId: "asset-storyboard", firstFrameAssetId: null, lastFrameAssetId: null, firstFrameNotes: null, lastFrameNotes: null, rejectionReason: null, createdAt: "2026-09-30T00:00:00Z", updatedAt: "2026-09-30T00:00:00Z", reviewedAt: "2026-09-30T00:00:00Z", assetReferences: [], execution: null },
            { id: "keyframe-1", movieShotId: "shot-1", versionNumber: 2, stage: "ApprovedKeyframe", status: "Approved", label: "Source frame", compositionJson: "{}", regenerationMetadataJson: null, stageProvenanceJson: null, continuitySnapshotReferenceJson: null, cinematographyReferenceJson: null, sourceVersionId: "storyboard-1", generationJobId: null, assetId: "asset-keyframe", firstFrameAssetId: null, lastFrameAssetId: null, firstFrameNotes: null, lastFrameNotes: null, rejectionReason: null, createdAt: "2026-09-30T00:00:00Z", updatedAt: "2026-09-30T00:00:00Z", reviewedAt: "2026-09-30T00:00:00Z", assetReferences: [], execution: null },
          ],
          takes: [{ id: "take-1", movieShotId: "shot-1", versionNumber: 1, label: "Take 1", status: "Approved", qualityLevel: "Standard", autoDirectorEnabled: false, movieClipId: null, generationJobId: "job-1", assetId: "asset-video", notes: null, selectedAt: "2026-09-30T00:00:00Z", finalizedAt: null, createdAt: "2026-09-30T00:00:00Z", updatedAt: "2026-09-30T00:00:00Z", approvals: [], execution: null }],
          productionStage: "SelectedFinalTake",
        })],
      }],
    }));
    expect(model.counts.keyframesReady).toBe(1);
    expect(model.counts.selectedTakes).toBe(1);
    expect(model.timelineReady).toBe(true);
    expect(model.timeline[0].detail).toBe("1 of 1 shots ready");
    expect(model.shots[0].readinessLabel).toBe("Ready for timeline");
    expect(matchesProductionFilter(model.shots[0], "ready")).toBe(true);
    expect(matchesProductionFilter(model.shots[0], "blocked")).toBe(false);
  });

  it("does not treat an invalidated selected take as timeline-ready", () => {
    const model = buildMovieProductionWorkspaceModel(projectWith({
      scenes: [{
        id: "scene-1",
        sequence: 1,
        title: "Platform",
        summary: "The train arrives.",
        durationSeconds: 8,
        continuityNotes: null,
        narration: null,
        dialogue: null,
        clips: [],
        shots: [shot({
          takes: [{
            id: "take-invalidated",
            movieShotId: "shot-1",
            versionNumber: 1,
            label: "Invalidated selected take",
            status: "ReviewRequired",
            qualityLevel: "Standard",
            autoDirectorEnabled: false,
            movieClipId: null,
            generationJobId: "job-1",
            assetId: "asset-video",
            notes: null,
            selectedAt: "2026-09-30T00:00:00Z",
            finalizedAt: null,
            createdAt: "2026-09-30T00:00:00Z",
            updatedAt: "2026-09-30T00:00:00Z",
            approvals: [],
            execution: null,
          }],
        })],
      }],
    }));

    expect(model.shots[0].readiness).not.toBe("ready");
    expect(model.timelineReady).toBe(false);
  });

  it("uses a deterministic internal fake adapter without contacting a service", async () => {
    const adapter = createFakeMovieProductionWorkspaceAdapter();
    await adapter.reviewVersion("version-1", { approve: true, reason: "Looks good." });
    await adapter.queueRender("shot-1", "motion-1", true);
    await adapter.requestMaster("take-1");
    expect(adapter.calls).toEqual(["review:version-1", "retry:shot-1", "master:take-1"]);
  });
});

// Synthetic fixtures only: no account, project, asset, or provider data. The
// worker marks a pass's generation job `Cancelled` when the user cancels it or
// its lease expires, deliberately leaves `progressPercent` at the last written
// value, and stops advancing the record — workspace polling stops for it too.
// A shot whose master pass ended this way must not keep claiming ongoing
// production, and every percent this room renders must be a whole number the
// bar can actually present.
function execution(overrides: Partial<MovieProductionExecution> = {}): MovieProductionExecution {
  return {
    generationJobId: "job-render-1",
    jobType: "movie_video_render",
    status: "Running",
    progressPercent: 45,
    retryCount: 0,
    attemptCount: 1,
    qualityControlStatus: "Pending",
    errorCode: null,
    errorMessage: null,
    assetId: null,
    assetType: null,
    attempts: [],
    ...overrides,
  };
}
function version(overrides: Partial<MovieProductionVersion> = {}): MovieProductionVersion {
  return {
    id: "version-1",
    movieShotId: "shot-1",
    versionNumber: 1,
    stage: "ProductionRender",
    status: "Draft",
    label: "Master pass",
    compositionJson: "{}",
    regenerationMetadataJson: null,
    stageProvenanceJson: null,
    continuitySnapshotReferenceJson: null,
    cinematographyReferenceJson: null,
    sourceVersionId: null,
    generationJobId: "job-render-1",
    assetId: null,
    firstFrameAssetId: null,
    lastFrameAssetId: null,
    firstFrameNotes: null,
    lastFrameNotes: null,
    rejectionReason: null,
    createdAt: "2026-09-30T00:00:00Z",
    updatedAt: "2026-09-30T00:00:00Z",
    reviewedAt: null,
    assetReferences: [],
    execution: null,
    ...overrides,
  };
}
function sceneWithRenderPass(renderExecution: MovieProductionExecution): MovieProject["scenes"][number] {
  return {
    id: "scene-1",
    sequence: 1,
    title: "Platform",
    summary: "The train arrives.",
    durationSeconds: 8,
    continuityNotes: null,
    narration: null,
    dialogue: null,
    clips: [],
    shots: [shot({
      productionStage: "ProductionRender",
      productionVersions: [
        version({ id: "storyboard-1", stage: "ApprovedStoryboard", status: "Approved", label: "Approved visual plan", assetId: "asset-storyboard", reviewedAt: "2026-09-30T00:00:00Z" }),
        version({ id: "keyframe-1", versionNumber: 2, stage: "ApprovedKeyframe", status: "Approved", label: "Source frame", sourceVersionId: "storyboard-1", assetId: "asset-keyframe", reviewedAt: "2026-09-30T00:00:00Z" }),
        version({ id: "motion-1", versionNumber: 3, stage: "MotionPreview", status: "Approved", label: "Motion check", sourceVersionId: "keyframe-1", reviewedAt: "2026-09-30T00:00:00Z" }),
        version({ id: "render-1", versionNumber: 4, stage: "ProductionRender", status: "Draft", label: "Master pass", sourceVersionId: "motion-1", execution: renderExecution }),
      ],
    })],
  };
}
describe("movie production workspace terminal progress", () => {
  it("reports a cancelled master pass as a terminal review state instead of ongoing production", () => {
    const model = buildMovieProductionWorkspaceModel(projectWith({ scenes: [sceneWithRenderPass(execution({ status: "Cancelled" }))] }));
    const item = model.shots[0];
    expect(item.render?.execution?.status).toBe("Cancelled");
    // The worker owns nothing in flight anymore: the shot must read as needing
    // a human decision about the cancelled pass, never as quietly progressing.
    expect(item.readiness).toBe("needs-review");
    expect(item.readinessLabel).toBe("Pass was cancelled");
    expect(item.nextAction).toContain("cancelled");
    expect(matchesProductionFilter(item, "needs-review")).toBe(true);
    // The scene row feeds the timeline board; it must not stay in the
    // still-moving family for a record that can never advance again.
    expect(model.timeline[0].readiness).toBe("needs-review");
    expect(Number.isInteger(model.progressPercent)).toBe(true);
    expect(model.progressPercent).toBeGreaterThanOrEqual(0);
    expect(model.progressPercent).toBeLessThanOrEqual(100);
  });
  it("keeps an active master pass in progress while the worker owns it", () => {
    const model = buildMovieProductionWorkspaceModel(projectWith({ scenes: [sceneWithRenderPass(execution({ status: "Running" }))] }));
    expect(model.shots[0].readiness).toBe("in-progress");
    expect(model.timeline[0].readiness).toBe("in-progress");
  });
  it("keeps a succeeded render on the take-review path and a failed one on attention copy", () => {
    const succeeded = buildMovieProductionWorkspaceModel(projectWith({ scenes: [sceneWithRenderPass(execution({ status: "Succeeded" }))] }));
    expect(succeeded.shots[0].readinessLabel).toBe("Take needs review");
    const failed = buildMovieProductionWorkspaceModel(projectWith({ scenes: [sceneWithRenderPass(execution({ status: "Failed" }))] }));
    expect(failed.shots[0].readinessLabel).toBe("Pass needs attention");
  });
  it("presents production progress as a rounded whole percent clamped to the bar range", () => {
    expect(movieProductionProgressPercent(62.5)).toBe(63);
    expect(movieProductionProgressPercent(33.4)).toBe(33);
    expect(movieProductionProgressPercent(100)).toBe(100);
    expect(movieProductionProgressPercent(130.6)).toBe(100);
    expect(movieProductionProgressPercent(-20)).toBe(0);
    expect(movieProductionProgressPercent(-0.4)).toBe(0);
    expect(movieProductionProgressPercent(Number.NaN)).toBe(0);
    expect(movieProductionProgressPercent(Number.POSITIVE_INFINITY)).toBe(0);
    expect(movieProductionProgressPercent(null)).toBe(0);
    expect(movieProductionProgressPercent(undefined)).toBe(0);
  });
  it("maps each terminal execution to one honest state word and keeps live passes unlabelled", () => {
    expect(movieProductionExecutionState("Succeeded")).toBe("Ready");
    expect(movieProductionExecutionState("Failed")).toBe("Needs attention");
    expect(movieProductionExecutionState("Cancelled")).toBe("Cancelled");
    expect(movieProductionExecutionState("Running")).toBeNull();
    expect(movieProductionExecutionState("Queued")).toBeNull();
    expect(movieProductionExecutionState(null)).toBeNull();
    expect(movieProductionExecutionState(undefined)).toBeNull();
  });
});
