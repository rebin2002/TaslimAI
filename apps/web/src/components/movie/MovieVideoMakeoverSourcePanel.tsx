/**
 * AI Video Makeover source picker (issue #698, source-integration slice).
 *
 * Product-facing half of the bridge. It shows the creator the finished clips this
 * workspace can actually attach as a makeover source, says plainly which ones cannot
 * be attached and why, and lists the source facts that still need verification
 * before the #775 draft helper will admit an estimate.
 *
 * Guarantees
 * ----------
 * 1. FULLY CONTROLLED. No hooks, no local state, no I/O. Every rendered value comes
 *    from props, so it renders with `renderToStaticMarkup` like the sibling #784
 *    panel and its pure model is testable without a DOM.
 * 2. NO EXECUTION SURFACE. It renders no estimate, start, retry, download or upload
 *    control. Attaching a source is a draft edit only; the wording says so.
 * 3. TRUTHFUL ABOUT GAPS AND ABOUT ITSELF. Missing descriptor facts are shown as
 *    pending verification with the exact reason, never as a green check. Upload is
 *    named as not wired here rather than faked.
 * 4. UNSUPPORTED REQUESTS STAY UNSUPPORTED. A wardrobe/outfit request (#698 Phase B)
 *    reads as a future feature in this panel too, using the shared #775 helper's
 *    wording rather than a new claim, and it is never presented as fixable by
 *    picking a different clip.
 * 5. NO OWNED-FILE DEPENDENCIES. Adds only files under `components/movie/` and one
 *    new lib module; it edits no file owned by #762/#580, #673, #686, #765, #771,
 *    #775, #778, #784 or #801, and nothing imports it from MovieStudioView.
 */

import {
  countSelectableMovieVideoMakeoverSources,
  movieVideoMakeoverSourcePendingCopy,
  type MovieVideoMakeoverSourceEntry,
} from "@/lib/movieVideoMakeoverSourceCatalog";

export type MovieVideoMakeoverSourcePanelProps = {
  readonly entries: readonly MovieVideoMakeoverSourceEntry[];
  readonly selectedAssetId: string | null;
  readonly mode: string | null;
  readonly onAttach: (assetId: string) => void;
  readonly onDetach: () => void;
};

export type MovieVideoMakeoverSourceRow = {
  readonly assetId: string;
  readonly label: string;
  readonly attached: boolean;
  readonly attachable: boolean;
  readonly reasons: readonly string[];
  readonly pendingFacts: readonly string[];
};

export type MovieVideoMakeoverSourcePanelModel = {
  readonly rows: readonly MovieVideoMakeoverSourceRow[];
  readonly notice: string;
  readonly headline: string;
  readonly details: readonly string[];
  readonly attachedEntry: MovieVideoMakeoverSourceEntry | null;
};

/**
 * Pure view model. Kept separate from the JSX so the branch logic (attachable vs
 * blocked vs attached-with-pending-facts, plus the no-clip and wardrobe cases) is
 * directly testable without rendering.
 */
export function movieVideoMakeoverSourcePanelModel(
  entries: readonly MovieVideoMakeoverSourceEntry[],
  selectedAssetId: string | null,
  mode: string | null,
): MovieVideoMakeoverSourcePanelModel {
  const rows: MovieVideoMakeoverSourceRow[] = entries.map((entry) => {
    const attached = entry.assetId !== "" && entry.assetId === selectedAssetId;
    return {
      assetId: entry.assetId,
      label: entry.label,
      attached,
      attachable: entry.selectable,
      reasons: entry.blockers.map((blocker) => blocker.message),
      pendingFacts: entry.gaps.map((gap) => gap.message),
    };
  });

  const attachedEntry = entries.find((entry) => entry.assetId !== "" && entry.assetId === selectedAssetId) ?? null;
  const selectableCount = countSelectableMovieVideoMakeoverSources(entries);

  let notice: string;
  if (entries.length === 0) {
    notice = "No clips to list. Finish a short-form Movie render, or upload a video asset, then reopen this picker.";
  } else if (selectableCount === 0) {
    notice = "None of these clips can be a makeover source yet. Each row says what is missing.";
  } else {
    notice = String(selectableCount) + " of " + String(entries.length)
      + " clip" + (entries.length === 1 ? "" : "s") + " can be attached as a source.";
  }

  const copy = movieVideoMakeoverSourcePendingCopy(attachedEntry, mode);
  return { rows, notice, headline: copy.headline, details: copy.details, attachedEntry };
}

export function MovieVideoMakeoverSourcePanel({
  entries,
  selectedAssetId,
  mode,
  onAttach,
  onDetach,
}: MovieVideoMakeoverSourcePanelProps) {
  const model = movieVideoMakeoverSourcePanelModel(entries, selectedAssetId, mode);

  return (
    <section className="movie-makeover-source-panel" aria-label="Makeover source video">
      <header className="movie-makeover-source-header">
        <h3>Source video</h3>
        <p className="movie-makeover-source-notice" role="note">{model.notice}</p>
        <p className="movie-makeover-source-scope" role="note">
          {"Picking a source only edits the draft. Upload from this panel is not wired, and attaching a clip starts no job and reserves no credit."}
        </p>
      </header>

      {model.rows.length === 0 ? (
        <p className="movie-makeover-source-empty">
          Nothing to show yet. A source has to be a finished video asset in this workspace.
        </p>
      ) : (
        <ul className="movie-makeover-source-list">
          {model.rows.map((row) => (
            <li
              key={row.assetId === "" ? row.label : row.assetId}
              className={
                "movie-makeover-source-row"
                + (row.attached ? " is-attached" : "")
                + (row.attachable ? "" : " is-blocked")
              }
              data-attached={row.attached ? "true" : "false"}
              data-attachable={row.attachable ? "true" : "false"}
            >
              <span className="movie-makeover-source-label">{row.label}</span>
              {!row.attachable && (
                <ul className="movie-makeover-source-reasons">
                  {row.reasons.map((reason) => <li key={reason}>{reason}</li>)}
                </ul>
              )}
              {row.attachable && !row.attached && (
                <button type="button" onClick={() => onAttach(row.assetId)}>
                  Attach as source
                </button>
              )}
              {row.attached && (
                <span className="movie-makeover-source-attached">
                  Attached
                  <button type="button" onClick={onDetach}>Detach</button>
                </span>
              )}
            </li>
          ))}
        </ul>
      )}

      <footer className="movie-makeover-source-status" aria-live="polite">
        <p className="movie-makeover-source-headline">{model.headline}</p>
        {model.details.length > 0 && (
          <ul className="movie-makeover-source-details">
            {model.details.map((detail) => <li key={detail}>{detail}</li>)}
          </ul>
        )}
      </footer>
    </section>
  );
}
