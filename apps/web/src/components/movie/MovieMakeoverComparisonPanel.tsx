/**
 * Standalone AI Video Makeover BEFORE/AFTER result comparison panel
 * (issue #698, result-preview slice).
 *
 * Scope and guarantees, stated deliberately narrow:
 *
 * 1. NOT WIRED TO ANY PROVIDER, BACKEND OR NETWORK CALL. The panel performs no
 *    network request of any kind and imports no API client. It renders only the
 *    already-authorized preview URLs the caller supplies (blob:, data:,
 *    same-origin absolute paths, or http(s) URLs). A disallowed URL scheme is
 *    fail-closed: it can never reach a media element and the pane shows the
 *    honest unavailable notice instead.
 * 2. HONEST UNAVAILABLE STATE. A missing or blocked makeover output renders a
 *    role="status" notice in its own pane. The original (before) video is never
 *    passed off as the transformed (after) result, and no retry, re-request or
 *    silent fallback happens anywhere in this module.
 * 3. FULLY CONTROLLED. No hidden state: view mode, split position, sources,
 *    direction and labels all come from props, so the panel renders
 *    deterministically with `renderToStaticMarkup` like the rest of this repo's
 *    component tests (no DOM shim needed).
 * 4. KEYBOARD ACCESSIBLE. The splitter exposes `role="slider"` semantics and a
 *    pure key helper (ArrowLeft/ArrowRight ±1, ArrowUp/ArrowDown ±10,
 *    PageUp/PageDown ±25, Home, End). Arrow key semantics mirror under `rtl`
 *    reading direction; native video controls stay available per pane.
 * 5. RTL-SAFE. Visual mirroring uses CSS logical properties and direction-aware
 *    clipping only, so `dir="rtl"` with localized labels flips the before/after
 *    layout without any shared stylesheet change.
 * 6. NO OWNED-FILE DEPENDENCIES. This module imports nothing from PR #775
 *    (apps/web/src/lib/movieVideoMakeoverDraftState), PR #784
 *    (MovieVideoMakeoverInputPanel / movieVideoMakeoverInputState) or PR #250
 *    (FullMovieWorkspaceView), and it edits no shared CSS. Labels are
 *    panel-local constants, overridable per call, so no i18n keys are claimed.
 *    Integration into Movie Studio routes is a separate, owned change.
 */
import type { CSSProperties, KeyboardEvent, ReactElement } from "react";

export type MovieMakeoverComparisonDirection = "ltr" | "rtl";
export type MovieMakeoverComparisonViewMode = "split" | "before" | "after";

export type MovieMakeoverComparisonSource = Readonly<{
  /** Already-authorized preview location; the panel never requests it itself. */
  url: string;
  posterUrl?: string;
  durationSeconds?: number;
  width?: number;
  height?: number;
}>;

export type MovieMakeoverComparisonLabels = Readonly<{
  region: string;
  modeGroup: string;
  modeSplit: string;
  modeBefore: string;
  modeAfter: string;
  beforePane: string;
  afterPane: string;
  splitter: string;
  beforeUnavailable: string;
  afterUnavailable: string;
  blocked: string;
}>;

export type MovieMakeoverComparisonPanelProps = Readonly<{
  beforeSource: MovieMakeoverComparisonSource | null;
  afterSource: MovieMakeoverComparisonSource | null;
  viewMode: MovieMakeoverComparisonViewMode;
  splitPercent: number;
  dir?: MovieMakeoverComparisonDirection;
  labels?: Partial<MovieMakeoverComparisonLabels>;
  onSplitPercentChange: (percent: number) => void;
  onViewModeChange: (mode: MovieMakeoverComparisonViewMode) => void;
}>;

export type MovieMakeoverComparisonPaneState =
  | { kind: "ready"; url: string }
  | { kind: "unavailable" }
  | { kind: "blocked" };

const SPLIT_MIN = 0;
const SPLIT_MAX = 100;
const SPLIT_DEFAULT = 50;

/**
 * Schemes this panel may ever place into a media element `src`. Anything
 * without one of these schemes — including protocol-relative and bare
 * relative paths whose base origin the panel cannot verify — is fail-closed.
 */
const ALLOWED_PREVIEW_SCHEMES = ["blob:", "data:", "http:", "https:"] as const;

export function isAllowedMakeoverPreviewUrl(url: string): boolean {
  const value = url.trim();
  if (value.length === 0) return false;
  if (value.startsWith("/") && !value.startsWith("//")) return true; // same-origin absolute path
  const schemeMatch = /^([a-z][a-z0-9+.-]*:)/i.exec(value);
  if (!schemeMatch) return false;
  return (ALLOWED_PREVIEW_SCHEMES as readonly string[]).includes(schemeMatch[1].toLowerCase());
}

export function movieMakeoverPaneState(
  source: MovieMakeoverComparisonSource | null | undefined,
): MovieMakeoverComparisonPaneState {
  if (!source) return { kind: "unavailable" };
  const url = source.url.trim();
  if (url.length === 0) return { kind: "unavailable" };
  if (!isAllowedMakeoverPreviewUrl(url)) return { kind: "blocked" };
  return { kind: "ready", url };
}

export function clampMakeoverSplitPercent(value: number): number {
  if (!Number.isFinite(value)) return SPLIT_DEFAULT;
  return Math.min(SPLIT_MAX, Math.max(SPLIT_MIN, Math.round(value)));
}

/**
 * Pure keyboard semantics for the splitter. Returns the next clamped percent,
 * or null when the key belongs to someone else (the caller must not consume
 * it). Horizontal arrows follow reading direction: in `rtl` the inline-start
 * is the right edge, so ArrowRight reveals less, not more.
 */
export function movieMakeoverSplitPercentForKey(
  key: string,
  currentPercent: number,
  dir: MovieMakeoverComparisonDirection = "ltr",
): number | null {
  const current = clampMakeoverSplitPercent(currentPercent);
  const step = (delta: number): number => clampMakeoverSplitPercent(current + delta);
  switch (key) {
    case "ArrowLeft":
      return step(dir === "rtl" ? 1 : -1);
    case "ArrowRight":
      return step(dir === "rtl" ? -1 : 1);
    case "ArrowUp":
      return step(10);
    case "ArrowDown":
      return step(-10);
    case "PageUp":
      return step(25);
    case "PageDown":
      return step(-25);
    case "Home":
      return SPLIT_MIN;
    case "End":
      return SPLIT_MAX;
    default:
      return null;
  }
}

export const MOVIE_MAKEOVER_COMPARISON_DEFAULT_LABELS: MovieMakeoverComparisonLabels = {
  region: "AI Video Makeover before and after comparison",
  modeGroup: "Comparison view",
  modeSplit: "Split",
  modeBefore: "Before",
  modeAfter: "After",
  beforePane: "Original video",
  afterPane: "Makeover result",
  splitter: "Reveal position between original and makeover result",
  beforeUnavailable: "Original video preview is not available.",
  afterUnavailable:
    "Makeover result preview is not available yet. This screen did not request it again.",
  blocked:
    "This preview source was not accepted. Only authorized local or blob preview sources are shown here.",
};

const stageStyle: CSSProperties = {
  position: "relative",
  overflow: "hidden",
  borderRadius: 12,
  background: "#0b1829",
};
const fullPaneStyle: CSSProperties = { position: "absolute", inset: 0 };
const videoStyle: CSSProperties = {
  width: "100%",
  height: "100%",
  objectFit: "contain",
  display: "block",
  background: "#0b1829",
};
const noticeStyle: CSSProperties = {
  position: "absolute",
  inset: 0,
  display: "flex",
  alignItems: "center",
  justifyContent: "center",
  padding: 16,
  textAlign: "center",
};

function clipBeforeStyle(percent: number, dir: MovieMakeoverComparisonDirection): CSSProperties {
  // Physical clip-path insets do not flip with `dir`, so compute per direction:
  // ltr keeps the inline-start (left) slice, rtl keeps the right slice.
  const hidden = SPLIT_MAX - percent;
  return {
    ...fullPaneStyle,
    clipPath: dir === "rtl" ? `inset(0 0 0 ${hidden}%)` : `inset(0 ${hidden}% 0 0)`,
  };
}

function renderNotice(text: string): ReactElement {
  return (
    <div role="status" style={noticeStyle}>
      <p style={{ margin: 0 }}>{text}</p>
    </div>
  );
}

function renderMedia(
  state: MovieMakeoverComparisonPaneState,
  source: MovieMakeoverComparisonSource | null,
  paneLabel: string,
  unavailableText: string,
): ReactElement {
  if (state.kind !== "ready") return renderNotice(unavailableText);
  const poster =
    source?.posterUrl && isAllowedMakeoverPreviewUrl(source.posterUrl)
      ? source.posterUrl.trim()
      : undefined;
  return (
    <video
      src={state.url}
      poster={poster}
      aria-label={paneLabel}
      controls
      muted
      playsInline
      preload="metadata"
      style={videoStyle}
    />
  );
}

export function MovieMakeoverComparisonPanel({
  beforeSource,
  afterSource,
  viewMode,
  splitPercent,
  dir = "ltr",
  labels: labelOverrides,
  onSplitPercentChange,
  onViewModeChange,
}: MovieMakeoverComparisonPanelProps) {
  const labels: MovieMakeoverComparisonLabels = {
    ...MOVIE_MAKEOVER_COMPARISON_DEFAULT_LABELS,
    ...(labelOverrides ?? {}),
  };
  const percent = clampMakeoverSplitPercent(splitPercent);
  const beforeState = movieMakeoverPaneState(beforeSource);
  const afterState = movieMakeoverPaneState(afterSource);

  const measurable = (source: MovieMakeoverComparisonSource | null): string | undefined =>
    source &&
    typeof source.width === "number" &&
    typeof source.height === "number" &&
    source.width > 0 &&
    source.height > 0
      ? `${source.width} / ${source.height}`
      : undefined;
  const aspectRatio = measurable(afterSource) ?? measurable(beforeSource) ?? "16 / 9";

  const beforeText = beforeState.kind === "blocked" ? labels.blocked : labels.beforeUnavailable;
  const afterText = afterState.kind === "blocked" ? labels.blocked : labels.afterUnavailable;

  const handleKey = (event: KeyboardEvent<HTMLDivElement>): void => {
    const next = movieMakeoverSplitPercentForKey(event.key, percent, dir);
    if (next !== null) {
      event.preventDefault();
      onSplitPercentChange(next);
    }
  };

  const modeButton = (mode: MovieMakeoverComparisonViewMode, label: string): ReactElement => (
    <button type="button" aria-pressed={viewMode === mode} onClick={() => onViewModeChange(mode)}>
      {label}
    </button>
  );

  return (
    <section className="movie-makeover-comparison-panel" dir={dir} aria-label={labels.region}>
      <div className="movie-makeover-comparison-modes" role="group" aria-label={labels.modeGroup}>
        {modeButton("split", labels.modeSplit)}
        {modeButton("before", labels.modeBefore)}
        {modeButton("after", labels.modeAfter)}
      </div>
      <div className="movie-makeover-comparison-stage" style={{ ...stageStyle, aspectRatio }}>
        {/* After fills the stage; before is the layer clipped at the split line. */}
        <div className="movie-makeover-comparison-pane-after" style={fullPaneStyle}>
          {viewMode === "before"
            ? null
            : renderMedia(afterState, afterSource, labels.afterPane, afterText)}
        </div>
        {viewMode === "after" ? null : (
          <div
            className="movie-makeover-comparison-pane-before"
            style={viewMode === "before" ? fullPaneStyle : clipBeforeStyle(percent, dir)}
          >
            {renderMedia(beforeState, beforeSource, labels.beforePane, beforeText)}
          </div>
        )}
        {viewMode === "split" ? (
          <div
            className="movie-makeover-comparison-splitter"
            role="slider"
            tabIndex={0}
            aria-label={labels.splitter}
            aria-orientation="horizontal"
            aria-valuemin={SPLIT_MIN}
            aria-valuemax={SPLIT_MAX}
            aria-valuenow={percent}
            aria-valuetext={`${percent}%`}
            onKeyDown={handleKey}
            style={{
              position: "absolute",
              insetBlock: 0,
              insetInlineStart: `${percent}%`,
              width: 2,
              transform: "translateX(-50%)",
              background: "rgba(255,255,255,0.85)",
            }}
          >
            <span
              aria-hidden="true"
              style={{
                position: "absolute",
                top: "50%",
                left: "50%",
                width: 22,
                height: 22,
                transform: "translate(-50%, -50%)",
                borderRadius: 999,
                background: "#ffffff",
                boxShadow: "0 1px 4px rgba(7, 17, 31, 0.35)",
              }}
            />
          </div>
        ) : null}
      </div>
    </section>
  );
}
