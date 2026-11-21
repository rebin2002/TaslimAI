import type { MovieDeliveryKey } from "./movieDeliveryI18n";

/**
 * The statuses the API treats as final for a master assembly. A record in one of
 * these states is never advanced again, so nothing in the queue may keep
 * promising that it will.
 */
export const movieExportTerminalStatuses = [
  "Ready",
  "Failed",
  "Cancelled",
] as const;

export type MovieExportCardTone = "is-ready" | "is-failed" | "is-pending";

export type MovieExportCardIcon =
  | "ready"
  | "failed"
  | "cancelled"
  | "producing";

export type MovieExportCardState = {
  tone: MovieExportCardTone;
  icon: MovieExportCardIcon;
  /**
   * Catalog key for the card's action area when no output asset is published.
   * A Ready record without an asset is the anomaly the hint still has to speak
   * to, so every state carries an honest hint instead of falling back to an
   * in-flight claim.
   */
  hintKey: MovieDeliveryKey;
  isTerminal: boolean;
  isProducing: boolean;
};

const producingState: MovieExportCardState = {
  tone: "is-pending",
  icon: "producing",
  hintKey: "stillProducing",
  isTerminal: false,
  isProducing: true,
};

const terminalStates: Record<
  (typeof movieExportTerminalStatuses)[number],
  MovieExportCardState
> = {
  Ready: {
    tone: "is-ready",
    icon: "ready",
    hintKey: "stillProducing",
    isTerminal: true,
    isProducing: false,
  },
  Failed: {
    tone: "is-failed",
    icon: "failed",
    hintKey: "failedOutput",
    isTerminal: true,
    isProducing: false,
  },
  Cancelled: {
    tone: "is-failed",
    icon: "cancelled",
    hintKey: "cancelledOutput",
    isTerminal: true,
    isProducing: false,
  },
};

export function isMovieExportTerminalStatus(status: string) {
  return (
    (movieExportTerminalStatuses as readonly string[]).includes(status) === true
  );
}

/**
 * Decides how one persisted assembly record is allowed to speak about itself in
 * the export queue. Anything outside the terminal set keeps the in-flight
 * wording, because the worker still owns that record.
 */
export function movieExportCardState(status: string): MovieExportCardState {
  if (isMovieExportTerminalStatus(status)) {
    return terminalStates[status as (typeof movieExportTerminalStatuses)[number]];
  }
  return producingState;
}

/**
 * The progress bar is a durable value: a cancelled or failed master keeps the
 * percentage written by the last checkpoint. It is clamped to a presentable
 * range so a malformed stored value cannot render an impossible bar.
 */
export function movieExportProgressPercent(value: number) {
  if (!Number.isFinite(value)) return 0;
  return Math.max(0, Math.min(100, Math.trunc(value)));
}
