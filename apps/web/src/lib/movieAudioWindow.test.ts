import { describe, expect, it } from "vitest";
import type { MovieSoundTrack } from "@/lib/api";
import { formatSoundTrackWindow, sortSoundTracksByAuthoredWindow } from "@/lib/movieAudioWindow";

/**
 * Builds a fake persisted sound-track record. No real media, providers or
 * network are involved: the audio room aligns these payloads exactly as the
 * shot polling endpoint delivers them, so timing fields are intentionally
 * typed loosely to model absent and fractional metadata.
 */
function soundTrack(overrides: Partial<Record<string, unknown>> = {}): MovieSoundTrack {
  return {
    id: "track-1",
    movieProjectId: "project-1",
    movieSceneId: "scene-1",
    movieShotId: null,
    assetId: null,
    generationJobId: null,
    libraryReferenceId: null,
    kind: "SoundEffect",
    layer: "Foreground",
    name: "Rain bed",
    description: "",
    startMilliseconds: 0,
    endMilliseconds: 2_000,
    fadeInMilliseconds: 0,
    fadeOutMilliseconds: 0,
    gainDb: 0,
    status: "Draft",
    sourceKind: "Imported",
    createdAt: "2026-11-12T00:00:00Z",
    updatedAt: "2026-11-12T00:00:00Z",
    approvedAt: null,
    approvals: [],
    ...overrides,
  } as MovieSoundTrack;
}

// The comparator the audio room used before this fix: raw subtraction over
// whatever the JSON payload happened to carry.
const legacySort = (tracks: MovieSoundTrack[]) =>
  [...tracks].sort((left, right) => left.startMilliseconds - right.startMilliseconds);

describe("movie audio window alignment", () => {
  it("keeps a record with absent timing metadata out of the frame-zero slot", () => {
    const authoredAtFrameZero = soundTrack({ id: "authored-zero", startMilliseconds: 0, endMilliseconds: 2_000 });
    const unauthored = soundTrack({ id: "legacy-import", startMilliseconds: undefined, endMilliseconds: undefined });

    // Reproduction: with a NaN comparator the legacy sort never reorders the
    // pair, so whenever the polling payload delivers the unauthored record
    // first it keeps the head of the sound stage ahead of the authored
    // frame-zero cue, and the raw template renders "undefined–undefined ms"
    // as if that record were aligned.
    expect(legacySort([unauthored, authoredAtFrameZero])[0]!.id).toBe("legacy-import");
    expect(`${(unauthored as { startMilliseconds?: unknown }).startMilliseconds}–${(unauthored as { endMilliseconds?: unknown }).endMilliseconds} ms`).toBe("undefined–undefined ms");

    const ordered = sortSoundTracksByAuthoredWindow([authoredAtFrameZero, unauthored]);
    expect(ordered.map((track) => track.id)).toEqual(["authored-zero", "legacy-import"]);
    // The fixed order no longer depends on arrival order.
    expect(sortSoundTracksByAuthoredWindow([unauthored, authoredAtFrameZero]).map((track) => track.id)).toEqual(["authored-zero", "legacy-import"]);

    // A null-timed record is the other half of the coercion defect: subtraction
    // reads it as -N against any authored start > 0, so it always wins the head.
    const nullWindow = soundTrack({ id: "null-window", startMilliseconds: null, endMilliseconds: null });
    const authoredLater = soundTrack({ id: "authored-500", startMilliseconds: 500, endMilliseconds: 1_500 });
    expect(legacySort([authoredLater, nullWindow])[0]!.id).toBe("null-window");
    expect(sortSoundTracksByAuthoredWindow([authoredLater, nullWindow]).map((track) => track.id)).toEqual(["authored-500", "null-window"]);
  });

  it("orders authored windows deterministically and preserves input order otherwise", () => {
    const tail = soundTrack({ id: "no-start", startMilliseconds: undefined, endMilliseconds: 4_000 });
    const head = soundTrack({ id: "zero", startMilliseconds: 0, endMilliseconds: 1_000 });
    const halfFrame = soundTrack({ id: "half-frame", startMilliseconds: 20.833, endMilliseconds: 1_000 });
    const wholeFrame = soundTrack({ id: "whole-frame", startMilliseconds: 41.667, endMilliseconds: 1_000 });
    const zeroDuration = soundTrack({ id: "zero-duration", startMilliseconds: 1_000, endMilliseconds: 1_000 });
    const anotherUnauthored = soundTrack({ id: "no-window", startMilliseconds: Number.NaN, endMilliseconds: 3_000 });

    const merged = [tail, halfFrame, zeroDuration, head, wholeFrame, anotherUnauthored];
    const ordered = sortSoundTracksByAuthoredWindow(merged);

    expect(ordered.map((track) => track.id)).toEqual([
      "zero",
      "half-frame",
      "whole-frame",
      "zero-duration",
      "no-start",
      "no-window",
    ]);
    // The helper never mutates the caller's merged list.
    expect(merged.map((track) => track.id)).toEqual(["no-start", "half-frame", "zero-duration", "zero", "whole-frame", "no-window"]);
  });

  it("formats fractional windows to whole milliseconds and flags absent windows", () => {
    expect(formatSoundTrackWindow(soundTrack({ startMilliseconds: 20.833, endMilliseconds: 1_041.667 }))).toBe("21–1042 ms");
    expect(formatSoundTrackWindow(soundTrack({ startMilliseconds: 0, endMilliseconds: 0 }))).toBe("0–0 ms");
    expect(formatSoundTrackWindow(soundTrack({ startMilliseconds: undefined, endMilliseconds: undefined }))).toBe("—–— ms");
    expect(formatSoundTrackWindow(soundTrack({ startMilliseconds: 1_000, endMilliseconds: null }))).toBe("1000–— ms");
  });
});
