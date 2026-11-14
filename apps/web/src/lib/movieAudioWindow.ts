import type { MovieSoundTrack } from "@/lib/api";

/**
 * The sound stage merges per-shot track groups exactly as the polling payload
 * delivers them. A persisted record can legitimately arrive without authored
 * timing (a legacy import whose window metadata was never resolved) or with a
 * fractional millisecond window (frame-stepped placements such as 20.833 ms at
 * 24 fps). Raw numeric subtraction treats an absent window as 0, so those
 * records sort ahead of genuine frame-zero cues and the room displays a window
 * that was never authored. These helpers keep the alignment honest: authored
 * windows order first and deterministically, unauthored records hold their
 * input order at the end, and fractional windows display as whole milliseconds.
 */

/** The timing fields as they arrive on the wire, before any trust is placed in them. */
export type SoundTrackWindow = {
  startMilliseconds: number | null;
  endMilliseconds: number | null;
};

type SoundTrackTimingFields = Pick<MovieSoundTrack, "startMilliseconds" | "endMilliseconds">;

function authoredMilliseconds(value: unknown): number | null {
  return typeof value === "number" && Number.isFinite(value) ? value : null;
}

export function soundTrackWindow(track: Partial<SoundTrackTimingFields>): SoundTrackWindow {
  return {
    startMilliseconds: authoredMilliseconds(track.startMilliseconds),
    endMilliseconds: authoredMilliseconds(track.endMilliseconds),
  };
}

export function hasAuthoredSoundTrackWindow(track: Partial<SoundTrackTimingFields>): boolean {
  const window = soundTrackWindow(track);
  return window.startMilliseconds !== null && window.endMilliseconds !== null;
}

/**
 * Orders merged sound tracks by their authored window without coercing absent
 * timing to frame zero. Authored windows sort by start, then end; records
 * without a fully authored window keep their input order at the tail. The
 * caller's array is never mutated.
 */
export function sortSoundTracksByAuthoredWindow(tracks: readonly MovieSoundTrack[]): MovieSoundTrack[] {
  return tracks
    .map((track, index) => ({ track, index, window: soundTrackWindow(track) }))
    .sort((left, right) => {
      const leftStart = left.window.startMilliseconds;
      const rightStart = right.window.startMilliseconds;
      if (leftStart === null && rightStart === null) return left.index - right.index;
      if (leftStart === null) return 1;
      if (rightStart === null) return -1;
      if (leftStart !== rightStart) return leftStart - rightStart;
      const leftEnd = left.window.endMilliseconds ?? Number.POSITIVE_INFINITY;
      const rightEnd = right.window.endMilliseconds ?? Number.POSITIVE_INFINITY;
      if (leftEnd !== rightEnd) return leftEnd - rightEnd;
      return left.index - right.index;
    })
    .map((entry) => entry.track);
}

/** Formats one ms value for the Window row; absent timing renders as an em dash. */
function formatWindowValue(value: number | null): string {
  return value === null ? "—" : String(Math.round(value));
}

/** Renders the track's window as whole milliseconds, or dashes when unauthored. */
export function formatSoundTrackWindow(track: Partial<SoundTrackTimingFields>): string {
  const window = soundTrackWindow(track);
  return `${formatWindowValue(window.startMilliseconds)}–${formatWindowValue(window.endMilliseconds)} ms`;
}
