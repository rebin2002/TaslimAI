import type { MovieSoundTrack } from "./api";
import type { Locale } from "./i18n";
import { formatMovieAudioNumber } from "./movieAudioI18n";

type Timing = Pick<MovieSoundTrack, "startMilliseconds" | "endMilliseconds">;

function hasAuthoredWindow(track: Timing): boolean {
  return Number.isFinite(track.startMilliseconds) && Number.isFinite(track.endMilliseconds);
}

/** Complete windows sort by start then end. Stable sort preserves ties and the order of missing windows. */
export function sortSoundTracksByAuthoredWindow(tracks: readonly MovieSoundTrack[]): MovieSoundTrack[] {
  return [...tracks].sort((left, right) => {
    const leftAuthored = hasAuthoredWindow(left);
    const rightAuthored = hasAuthoredWindow(right);
    if (leftAuthored !== rightAuthored) return leftAuthored ? -1 : 1;
    if (!leftAuthored) return 0;
    return left.startMilliseconds - right.startMilliseconds || left.endMilliseconds - right.endMilliseconds;
  });
}

export function formatSoundTrackWindow(locale: Locale, track: Timing): string {
  const format = (value: number) => Number.isFinite(value) ? formatMovieAudioNumber(locale, Math.round(value)) : "—";
  return `${format(track.startMilliseconds)}–${format(track.endMilliseconds)} ms`;
}
