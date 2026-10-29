import { describe, expect, it } from "vitest";
import type { MovieClipSegmentationPreview } from "./api";
import { isConsistentMovieClipSegmentation } from "./movieClipSegmentationValidation";

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
