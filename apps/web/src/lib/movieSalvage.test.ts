import { describe, expect, it } from "vitest";
import type { MovieProject } from "@/lib/api";
import { buildMovieSubclips, buildSalvageRecommendations, buildSalvageTimelineRevision, formatSalvageTime } from "@/lib/movieSalvage";

function projectWithTake(): MovieProject {
  return {
    id: "movie-1",
    workspaceId: "workspace-1",
    projectId: null,
    mode: "Full",
    status: "Draft",
    title: "A small scene",
    description: "Reviewable footage.",
    durationSeconds: 12,
    aspectRatio: "16:9",
    style: "cinematic",
    language: "en",
    additionalInstructions: null,
    createdAt: "2026-10-01T00:00:00Z",
    updatedAt: "2026-10-01T00:00:00Z",
    guide: {} as MovieProject["guide"],
    scenes: [{
      id: "scene-1",
      sequence: 1,
      title: "Platform",
      summary: "A train arrives.",
      durationSeconds: 12,
      continuityNotes: null,
      narration: null,
      dialogue: null,
      clips: [{ id: "clip-1", movieSceneId: "scene-1", movieShotId: "shot-1", generationJobId: "job-1", assetId: "asset-1", status: "Succeeded", durationSeconds: 12, metadataJson: JSON.stringify({ durationMilliseconds: 12000, usableRanges: [{ startMilliseconds: 1800, endMilliseconds: 8400 }], qcIssues: [{ label: "Hand drift", detail: "The camera drifts after the usable beat.", severity: "warning" }] }), continuitySnapshotJson: null }],
      shots: [{
        id: "shot-1",
        sequence: 1,
        description: "A train enters the frame.",
        purpose: null,
        subjects: "Train",
        subjectCharacterIds: [],
        locationSet: "Station",
        durationSeconds: 12,
        productionRequirements: null,
        continuityReferences: null,
        cameraAndFraming: null,
        cameraMotion: null,
        cinematographyJson: null,
        cinematographySummary: null,
        narration: null,
        dialogue: null,
        visualContinuityNotes: null,
        status: "Active",
        planState: "Production",
        readiness: { ready: true, checks: [], missing: [], summary: "Ready" },
        productionStage: "SelectedFinalTake",
        clips: [{ id: "clip-1", movieSceneId: "scene-1", movieShotId: "shot-1", generationJobId: "job-1", assetId: "asset-1", status: "Succeeded", durationSeconds: 12, metadataJson: JSON.stringify({ durationMilliseconds: 12000, usableRanges: [{ startMilliseconds: 1800, endMilliseconds: 8400 }], qcIssues: [{ label: "Hand drift", detail: "The camera drifts after the usable beat.", severity: "warning" }] }), continuitySnapshotJson: null }],
        productionVersions: [],
        takes: [{ id: "take-1", movieShotId: "shot-1", versionNumber: 1, label: "Take 1", status: "Approved", qualityLevel: "Fast", autoDirectorEnabled: false, movieClipId: "clip-1", generationJobId: "job-1", assetId: "asset-1", notes: null, selectedAt: "2026-10-01T00:00:00Z", finalizedAt: null, createdAt: "2026-10-01T00:00:00Z", updatedAt: "2026-10-01T00:00:00Z", approvals: [], execution: null }],
      }],
    }],
    characters: [],
    locations: [],
    clips: [],
    assemblies: [],
    world: { locations: [], sets: [], props: [], references: [], usages: [], facts: [], locks: [] },
  };
}

describe("movie salvage model", () => {
  it("extracts a bounded usable range and QC evidence from persisted clip metadata", () => {
    const [subclip] = buildMovieSubclips(projectWithTake());
    expect(subclip.sourceInMilliseconds).toBe(1800);
    expect(subclip.sourceOutMilliseconds).toBe(8400);
    expect(subclip.issues[0]).toMatchObject({ label: "Hand drift", severity: "warning" });
    expect(formatSalvageTime(subclip.sourceOutMilliseconds - subclip.sourceInMilliseconds)).toBe("00:06.600");
  });

  it("creates a review-only recommendation without changing the take", () => {
    const [recommendation] = buildSalvageRecommendations(projectWithTake());
    expect(recommendation.id).toBe("salvage-take-1");
    expect(recommendation.decision).toBe("pending");
    expect(recommendation.summary).toContain("usable range");
  });

  it("builds a first draft timeline revision with only the proposed visual subclip", () => {
    const [recommendation] = buildSalvageRecommendations(projectWithTake());
    const revision = buildSalvageTimelineRevision(null, {
      recommendationId: recommendation.id,
      takeId: recommendation.subclip.takeId,
      sourceInMilliseconds: 1800,
      sourceOutMilliseconds: 8400,
      timelineInMilliseconds: 0,
      label: "Salvage insert · Take 1",
    }, recommendation);
    expect(revision.tracks).toHaveLength(1);
    expect(revision.tracks?.[0].kind).toBe("Video");
    expect(revision.tracks?.[0].items[0]).toMatchObject({ kind: "VisualTake", sourceTakeId: "take-1", timelineOutMilliseconds: 6600 });
    expect(revision.tracks?.[0].items[0].metadataJson).toContain("salvage-director");
  });
});
