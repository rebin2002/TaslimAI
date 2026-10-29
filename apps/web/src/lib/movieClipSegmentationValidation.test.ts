import { describe, expect, it } from "vitest";
import type { MovieClipSegmentationPreview } from "./api";
import { isConsistentMovieClipSegmentation, isConsistentMovieSceneClipSegmentation } from "./movieClipSegmentationValidation";

const preview = (overrides: Partial<MovieClipSegmentationPreview> = {}): MovieClipSegmentationPreview => ({
  movieProjectId: "test-project",
  totalDurationSeconds: 11,
  providerKey: "fake-video",
  providerDeclaresSceneClipOperation: true,
  canSplitByDuration: true,
  minimumClipSeconds: 2,
  maximumClipSeconds: 10,
  segments: [
    { sequence: 1, startSeconds: 0, durationSeconds: 6 },
    { sequence: 2, startSeconds: 6, durationSeconds: 5 },
  ],
  ...overrides,
});

describe("Movie Studio provider-neutral segmentation preview integrity", () => {
  it("accepts a contiguous uneven split without truncating the requested runtime", () => {
    expect(isConsistentMovieClipSegmentation(preview())).toBe(true);
  });

  it("accepts all 720 declared ten-second slots in a two-hour film", () => {
    const segments = Array.from({ length: 720 }, (_, index) => ({
      sequence: index + 1,
      startSeconds: index * 10,
      durationSeconds: 10,
    }));
    expect(isConsistentMovieClipSegmentation(preview({
      totalDurationSeconds: 7_200,
      segments,
    }))).toBe(true);
  });

  it("rejects gaps, overlaps, reordered slots and partial final coverage", () => {
    const invalid = [
      [{ sequence: 1, startSeconds: 0, durationSeconds: 6 },
        { sequence: 2, startSeconds: 7, durationSeconds: 4 }],
      [{ sequence: 1, startSeconds: 0, durationSeconds: 6 },
        { sequence: 2, startSeconds: 5, durationSeconds: 6 }],
      [{ sequence: 2, startSeconds: 0, durationSeconds: 6 },
        { sequence: 1, startSeconds: 6, durationSeconds: 5 }],
      [{ sequence: 1, startSeconds: 0, durationSeconds: 6 },
        { sequence: 2, startSeconds: 6, durationSeconds: 4 }],
    ];
    for (const segments of invalid) {
      expect(isConsistentMovieClipSegmentation(preview({ segments }))).toBe(false);
    }
  });

  it("rejects non-boolean capability claims and blank provider identities", () => {
    const malformed = [
      { providerKey: "" },
      { providerKey: "  " },
      { providerKey: null },
      { providerDeclaresSceneClipOperation: "true" },
      { canSplitByDuration: "true" },
      { providerDeclaresSceneClipOperation: 1 },
      { canSplitByDuration: 1 },
    ] as unknown as Array<Partial<MovieClipSegmentationPreview>>;
    for (const variant of malformed) {
      expect(isConsistentMovieClipSegmentation(preview(variant))).toBe(false);
    }
  });

  it("rejects null, primitive and malformed slots from an untrusted API response", () => {
    const malformedSlots: unknown[] = [null, undefined, "clip", 5, false];
    for (const slot of malformedSlots) {
      const segments = [slot] as MovieClipSegmentationPreview["segments"];
      expect(isConsistentMovieClipSegmentation(preview({ segments }))).toBe(false);
    }
    expect(isConsistentMovieClipSegmentation(preview({
      segments: {} as MovieClipSegmentationPreview["segments"],
    }))).toBe(false);
  });

  it("rejects unsupported declared durations and absent provider capabilities", () => {
    const variants: Array<Partial<MovieClipSegmentationPreview>> = [
      { minimumClipSeconds: null },
      { maximumClipSeconds: null },
      { minimumClipSeconds: 12, maximumClipSeconds: 10 },
      { minimumClipSeconds: 6 },
      { maximumClipSeconds: 5 },
      { providerDeclaresSceneClipOperation: false },
      { canSplitByDuration: false },
      { totalDurationSeconds: 7_201 },
      { totalDurationSeconds: 11.5 },
      { segments: [{ sequence: 1, startSeconds: 0, durationSeconds: 11 }] },
      { segments: [] },
    ];
    for (const variant of variants) {
      expect(isConsistentMovieClipSegmentation(preview(variant))).toBe(false);
    }
  });
});

describe("Movie Studio scene-aware read-only preview integrity", () => {
  const firstId = "11111111-1111-4111-8111-111111111111";
  const secondId = "22222222-2222-4222-8222-222222222222";
  const aligned = (): MovieClipSegmentationPreview => preview({
    canSplitByScene: true,
    sceneSegments: [
      { sceneId: firstId, sceneSequence: 1, clipSequence: 1,
        timelineStartSeconds: 0, sceneOffsetSeconds: 0, durationSeconds: 6 },
      { sceneId: secondId, sceneSequence: 2, clipSequence: 2,
        timelineStartSeconds: 6, sceneOffsetSeconds: 0, durationSeconds: 5 },
    ],
  });

  it("accepts exact scene boundaries and older API responses without optional fields", () => {
    expect(isConsistentMovieSceneClipSegmentation(aligned())).toBe(true);
    expect(isConsistentMovieSceneClipSegmentation(preview())).toBe(true);
    expect(isConsistentMovieSceneClipSegmentation(preview({
      canSplitByScene: false, sceneSegments: [],
    }))).toBe(true);
  });

  it("rejects malformed or invented scene claims before showing an aligned plan", () => {
    const variants = [
      { canSplitByScene: true, sceneSegments: [] },
      { canSplitByScene: false, sceneSegments: aligned().sceneSegments },
      { canSplitByScene: "true", sceneSegments: aligned().sceneSegments },
      { canSplitByScene: true, sceneSegments: [null] },
      { canSplitByScene: true, sceneSegments: [{ ...aligned().sceneSegments![0], sceneId: "not-a-guid" }] },
      { canSplitByScene: true, sceneSegments: [{ ...aligned().sceneSegments![0], sceneId: "00000000-0000-0000-0000-000000000000" }] },
    ] as unknown as Array<Partial<MovieClipSegmentationPreview>>;
    for (const variant of variants) {
      expect(isConsistentMovieSceneClipSegmentation(preview(variant))).toBe(false);
    }
  });

  it("rejects missing seconds, gaps, repeated scenes and inconsistent scene offsets", () => {
    const initial = aligned();
    const slots = initial.sceneSegments!;
    const badPlans = [
      [{ ...slots[0], durationSeconds: 5 }, slots[1]],
      [slots[0], { ...slots[1], timelineStartSeconds: 7 }],
      [slots[0], { ...slots[1], clipSequence: 3 }],
      [slots[0], { ...slots[1], sceneOffsetSeconds: 6 }],
      [slots[0], { ...slots[1], sceneId: firstId, sceneSequence: 2 }],
      [slots[0], { ...slots[1], sceneSequence: 1 }],
      [slots[0], { ...slots[1], durationSeconds: 6 }],
    ];
    for (const sceneSegments of badPlans) {
      expect(isConsistentMovieSceneClipSegmentation({
        ...initial, sceneSegments,
      })).toBe(false);
    }
  });

  it("accepts consecutive slots within one scene with matching relative offsets", () => {
    const oneScene = aligned();
    oneScene.sceneSegments = [
      { sceneId: firstId, sceneSequence: 1, clipSequence: 1,
        timelineStartSeconds: 0, sceneOffsetSeconds: 0, durationSeconds: 6 },
      { sceneId: firstId, sceneSequence: 1, clipSequence: 2,
        timelineStartSeconds: 6, sceneOffsetSeconds: 6, durationSeconds: 5 },
    ];
    expect(isConsistentMovieSceneClipSegmentation(oneScene)).toBe(true);
  });
});
