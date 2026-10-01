import { describe, expect, it } from "vitest";
import type { MovieProject, MovieShot } from "@/lib/api";
import { buildMovieProductionWorkspaceModel, createFakeMovieProductionWorkspaceAdapter, matchesProductionFilter } from "@/lib/movieProductionWorkspace";

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

  it("uses a deterministic internal fake adapter without contacting a service", async () => {
    const adapter = createFakeMovieProductionWorkspaceAdapter();
    await adapter.reviewVersion("version-1", { approve: true, reason: "Looks good." });
    await adapter.queueRender("shot-1", "motion-1", true);
    await adapter.requestMaster("take-1");
    expect(adapter.calls).toEqual(["review:version-1", "retry:shot-1", "master:take-1"]);
  });
});
