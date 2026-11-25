import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import type { MovieProductionExecution, MovieProductionVersion, MovieProject, MovieShot } from "@/lib/api";
import { createFakeMovieProductionWorkspaceAdapter } from "@/lib/movieProductionWorkspace";
import { MovieProductionWorkspace } from "./MovieProductionWorkspace";

vi.mock("@/components/LocaleProvider", () => ({
  useLocale: () => ({ locale: "en", t: (key: string) => key }),
}));

// Synthetic fixtures only: no account, project, asset, or provider data, and
// nothing here contacts a service. The workspace renders a persisted project
// graph; these cases pin what the command bar and a cancelled master pass are
// allowed to claim on screen.
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
    world: { locations: [], sets: [], props: [], references: [], usages: [], facts: [], locks: [] },
    ...overrides,
  };
}
function projectWithRenderPass(renderExecution: MovieProductionExecution): MovieProject {
  return projectWith({
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
        productionStage: "ProductionRender",
        productionVersions: [
          version({ id: "storyboard-1", stage: "ApprovedStoryboard", status: "Approved", label: "Approved visual plan", reviewedAt: "2026-09-30T00:00:00Z" }),
          version({ id: "keyframe-1", versionNumber: 2, stage: "ApprovedKeyframe", status: "Approved", label: "Source frame", sourceVersionId: "storyboard-1", reviewedAt: "2026-09-30T00:00:00Z" }),
          version({ id: "motion-1", versionNumber: 3, stage: "MotionPreview", status: "Approved", label: "Motion check", sourceVersionId: "keyframe-1", reviewedAt: "2026-09-30T00:00:00Z" }),
          version({ id: "render-1", versionNumber: 4, stage: "ProductionRender", status: "Draft", label: "Master pass", sourceVersionId: "motion-1", execution: renderExecution }),
        ],
      })],
    }],
  });
}
function workspace(project: MovieProject, completionPercent: number) {
  return renderToStaticMarkup(
    <MovieProductionWorkspace
      project={project}
      completionPercent={completionPercent}
      onRefresh={async () => undefined}
      adapter={createFakeMovieProductionWorkspaceAdapter()}
    />,
  );
}

describe("Movie production workspace terminal progress", () => {
  it("presents the command bar percent as a whole number clamped to the bar range", () => {
    // A shotless project reads the overview-supplied percent straight through;
    // the bar must round it and keep an out-of-range or malformed number from
    // rendering a percentage the bar cannot present.
    expect(workspace(projectWith(), 62.5)).toContain('aria-label="63% production progress"');
    expect(workspace(projectWith(), 33.4)).toContain('aria-label="33% production progress"');
    expect(workspace(projectWith(), 130.6)).toContain('aria-label="100% production progress"');
    const clamped = workspace(projectWith(), 130.6);
    expect(clamped).not.toContain("130.6");
    expect(workspace(projectWith(), -20)).toContain('aria-label="0% production progress"');
    expect(workspace(projectWith(), Number.NaN)).toContain('aria-label="0% production progress"');
    expect(workspace(projectWith(), 62.5)).toContain('style="width:63%"');
    expect(workspace(projectWith(), 130.6)).toContain('style="width:100%"');
  });

  it("states a cancelled master pass as terminal and keeps a retry path visible", () => {
    const html = workspace(projectWithRenderPass(execution({ status: "Cancelled", progressPercent: 45 })), 50);
    // The record can never advance again and polling has stopped for it, so
    // the card must name the frozen outcome instead of claiming ongoing work.
    expect(html).toContain("Pass was cancelled");
    expect(html).not.toContain("Source frame approved");
    // The saved-pass history row says one honest word about the execution.
    expect(html).toContain("<strong>Cancelled</strong>");
    // The room still offers exactly one forward path: start a new pass.
    expect(html).toContain("Retry master pass");
  });

  it("keeps an in-flight master pass described as progressing with no retry offer", () => {
    const html = workspace(projectWithRenderPass(execution({ status: "Running", progressPercent: 45 })), 50);
    expect(html).not.toContain("Pass was cancelled");
    expect(html).not.toContain("Retry master pass");
    expect(html).not.toContain("<strong>Cancelled</strong>");
  });

  it("keeps a succeeded render on the take-review family with Ready wording in history", () => {
    const html = workspace(projectWithRenderPass(execution({ status: "Succeeded", progressPercent: 100 })), 50);
    expect(html).toContain("<strong>Ready</strong>");
    expect(html).not.toContain("Retry master pass");
  });
});
