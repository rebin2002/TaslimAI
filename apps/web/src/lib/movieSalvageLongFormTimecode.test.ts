import { describe, expect, it } from "vitest";
import type { MovieTimeline } from "@/lib/api";
import { buildSalvageTimelineRevision, formatSalvageTime, type SalvageRecommendation } from "@/lib/movieSalvage";

/**
 * Issue #685 asks for two independent duration scales to coexist: a whole
 * film/project *plan* may run to 120 minutes (7,200 s) while one provider
 * generation *clip* stays inside its own short cap. The backend keeps those
 * scales separate on purpose (`MovieIntercutPlanner.MaxClipDurationSeconds`,
 * "Keep MaxClipDurationSeconds independent from the two-hour film cap"), and
 * #1057/#1078/#1083/#1088/#1101 pin the server side.
 *
 * The client half was unclaimed. It is where the two scales meet: the Selects
 * workspace prints the whole-film total
 * (`timeline.currentRevision.durationMilliseconds`, legitimately 7,200,000 ms
 * for a two-hour plan) through the *same* `formatSalvageTime` helper it uses
 * for one take's source range - and that helper only has MM:SS.mmm fields. A
 * single provider-capped clip can never leave the minute scale, which is why
 * every existing assertion on this helper sits below a minute
 * (`movieSalvage.test.ts` covers 6,600 ms) and the long-form scale was never
 * exercised: the first value at or above one hour makes the minutes field run
 * past two digits instead of rolling into hours, so a customer reads a
 * two-hour film as "120:00.000 total".
 *
 * The two long-form display cases are RED by design and document the boundary
 * for the fix lane; the sub-hour cases are the GREEN guards a fix must not
 * disturb, because they are the per-clip readout customers already rely on.
 * Purely synthetic numbers: no fetch, no provider, no paid generation.
 */

const CLIP_CAP_MILLISECONDS = 10_000;
const CLIPS_IN_LONG_PLAN = 720;
const ONE_HOUR_MILLISECONDS = 3_600_000;
/** 720 provider-capped 10s clips: the 120-minute plan ceiling, 7,200,000 ms. */
const PLAN_CEILING_MILLISECONDS = CLIP_CAP_MILLISECONDS * CLIPS_IN_LONG_PLAN;

function longFormTimeline(): MovieTimeline {
  const items = Array.from({ length: CLIPS_IN_LONG_PLAN }, (_, index) => ({
    id: `item-${index + 1}`,
    sequence: index + 1,
    kind: "VisualTake",
    sourceTakeId: `take-${index + 1}`,
    sourceAssetId: `asset-${index + 1}`,
    timelineInMilliseconds: index * CLIP_CAP_MILLISECONDS,
    timelineOutMilliseconds: (index + 1) * CLIP_CAP_MILLISECONDS,
    sourceInMilliseconds: 0,
    sourceOutMilliseconds: CLIP_CAP_MILLISECONDS,
    durationMilliseconds: CLIP_CAP_MILLISECONDS,
    label: `Take ${index + 1}`,
    metadataJson: null,
    isGap: false,
  }));
  return {
    id: "timeline-long-form",
    movieProjectId: "movie-long-form",
    currentRevisionNumber: 1,
    currentRevisionId: "revision-long-form",
    lockedRevisionNumber: null,
    lockedRevisionId: null,
    revisions: [],
    currentRevision: {
      id: "revision-long-form",
      movieProjectId: "movie-long-form",
      revisionNumber: 1,
      baseRevisionId: null,
      status: "Draft",
      label: "Long-form draft",
      changeSummary: null,
      durationMilliseconds: PLAN_CEILING_MILLISECONDS,
      createdAt: "2026-11-19T00:00:00Z",
      updatedAt: "2026-11-19T00:00:00Z",
      lockedAt: null,
      lockedByUserId: null,
      tracks: [{ id: "track-video", trackNumber: 1, kind: "Video", name: "Selects", isMuted: false, items }],
    },
  } as unknown as MovieTimeline;
}

function longFormRecommendation(): SalvageRecommendation {
  return {
    id: "salvage-take-long-form",
    title: "Salvage Take 721",
    summary: "A provider-capped take placed after the full 120-minute plan.",
    rationale: [],
    decision: "accepted",
    subclip: {
      id: "subclip-long-form",
      takeId: "take-long-form",
      shotId: "shot-long-form",
      sceneId: "scene-long-form",
      sceneSequence: CLIPS_IN_LONG_PLAN,
      sceneTitle: "Closing reel",
      shotSequence: 1,
      shotDescription: "The last beat of a two-hour film.",
      label: "Take 721",
      status: "Approved",
      assetId: "asset-long-form",
      durationMilliseconds: CLIP_CAP_MILLISECONDS,
      sourceInMilliseconds: 1_800,
      sourceOutMilliseconds: 8_400,
      issues: [],
      selected: true,
      finalized: false,
    },
  } as unknown as SalvageRecommendation;
}

describe("movie salvage long-form timeline timecode", () => {
  it("keeps the provider-clip readout unchanged below one hour", () => {
    // A single generation clip never leaves this scale, so its current shape is
    // the contract to preserve: MM:SS.mmm with both fields at two digits.
    expect(formatSalvageTime(6_600)).toBe("00:06.600");
    expect(formatSalvageTime(CLIP_CAP_MILLISECONDS)).toBe("00:10.000");
    // The last value the minute scale can still carry honestly.
    expect(formatSalvageTime(ONE_HOUR_MILLISECONDS - 1)).toBe("59:59.999");
  });

  it("renders an hour-long and a 120-minute whole-film total with an hours field", () => {
    // These are the values the Selects panel prints as "… total" from
    // timeline.currentRevision.durationMilliseconds for a legal long-form plan.
    expect(formatSalvageTime(ONE_HOUR_MILLISECONDS)).toBe("01:00:00.000");
    expect(formatSalvageTime(4_530_500)).toBe("01:15:30.500");
    expect(formatSalvageTime(PLAN_CEILING_MILLISECONDS)).toBe("02:00:00.000");
  });

  it("never lets the minutes field run past 59 anywhere on the plan scale", () => {
    for (let minutes = 1; minutes <= 120; minutes += 1) {
      const rendered = formatSalvageTime(minutes * 60_000 + 500);
      const fields = rendered.split(":");
      if (minutes >= 60) {
        // Past an hour the timecode must carry hours, not three-digit minutes.
        expect(fields).toHaveLength(3);
      }
      expect(Number(fields.length === 3 ? fields[1] : fields[0])).toBeLessThan(60);
    }
  });

  it("carries the 120-minute plan through the insert round trip without a per-clip clamp", () => {
    // The data half already holds: a long-form position is carried verbatim and
    // is never truncated to the scale of one provider-capped clip.
    const revision = buildSalvageTimelineRevision(
      longFormTimeline(),
      {
        recommendationId: "salvage-take-long-form",
        takeId: "take-long-form",
        sourceInMilliseconds: 1_800,
        sourceOutMilliseconds: 8_400,
        timelineInMilliseconds: PLAN_CEILING_MILLISECONDS,
        label: "Salvage insert · Take 721",
      },
      longFormRecommendation(),
    );
    const videoTrack = revision.tracks?.find((track) => track.kind === "Video");
    expect(videoTrack?.items).toHaveLength(CLIPS_IN_LONG_PLAN + 1);
    expect(videoTrack?.items[CLIPS_IN_LONG_PLAN - 1]?.timelineOutMilliseconds).toBe(PLAN_CEILING_MILLISECONDS);
    const inserted = videoTrack?.items[CLIPS_IN_LONG_PLAN];
    expect(inserted).toMatchObject({
      timelineInMilliseconds: PLAN_CEILING_MILLISECONDS,
      timelineOutMilliseconds: PLAN_CEILING_MILLISECONDS + 6_600,
    });
    expect(inserted?.metadataJson).toContain("salvage-director");
  });
});
