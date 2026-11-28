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

import { movieMakeoverCopy, movieMakeoverDirection, localizedMovieMakeoverBlocker, localizedMovieMakeoverGap, localizedMovieMakeoverSourcePendingCopy, type MovieMakeoverLocale } from "./movieVideoMakeoverLocale";
import {
  countSelectableMovieVideoMakeoverSources,
  type MovieVideoMakeoverSourceEntry,
} from "@/lib/movieVideoMakeoverSourceCatalog";

export type MovieVideoMakeoverSourcePanelProps = {
  readonly entries: readonly MovieVideoMakeoverSourceEntry[];
  readonly selectedAssetId: string | null;
  readonly mode: string | null;
  readonly onAttach: (assetId: string) => void;
  readonly onDetach: () => void;
  readonly locale?: MovieMakeoverLocale;
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
  locale: MovieMakeoverLocale = "en",
): MovieVideoMakeoverSourcePanelModel {
  const c = movieMakeoverCopy(locale);
  const rows: MovieVideoMakeoverSourceRow[] = entries.map((entry) => {
    const attached = entry.assetId !== "" && entry.assetId === selectedAssetId;
    return {
      assetId: entry.assetId,
      label: entry.label,
      attached,
      attachable: entry.selectable,
      reasons: entry.blockers.map((blocker) => localizedMovieMakeoverBlocker(blocker.code, blocker.message, locale)),
      pendingFacts: entry.gaps.map((gap) => localizedMovieMakeoverGap(gap.code, gap.message, locale)),
    };
  });

  const attachedEntry = entries.find((entry) => entry.assetId !== "" && entry.assetId === selectedAssetId) ?? null;
  const selectableCount = countSelectableMovieVideoMakeoverSources(entries);

  let notice: string;
  if (entries.length === 0) {
    notice = c.sourceNoClips;
  } else if (selectableCount === 0) {
    notice = c.sourceNoneEligible;
  } else {
    notice = c.sourceCount(selectableCount, entries.length);
  }

  const copy = localizedMovieMakeoverSourcePendingCopy(attachedEntry, mode, locale);
  return { rows, notice, headline: copy.headline, details: copy.details, attachedEntry };
}

export function MovieVideoMakeoverSourcePanel({
  entries,
  selectedAssetId,
  mode,
  onAttach,
  onDetach,
  locale = "en",
}: MovieVideoMakeoverSourcePanelProps) {
  const model = movieVideoMakeoverSourcePanelModel(entries, selectedAssetId, mode, locale);
  const c = movieMakeoverCopy(locale);

  return (
    <section className="movie-makeover-source-panel" dir={movieMakeoverDirection(locale)} aria-label={c.sourceRegion}>
      <header className="movie-makeover-source-header">
        <h3>{c.source}</h3>
        <p className="movie-makeover-source-notice" role="note">{model.notice}</p>
        <p className="movie-makeover-source-scope" role="note">
          {c.sourceScope}
        </p>
      </header>

      {model.rows.length === 0 ? (
        <p className="movie-makeover-source-empty">
          {c.sourceEmpty}
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
                  {c.attach}
                </button>
              )}
              {row.attached && (
                <span className="movie-makeover-source-attached">
                  {c.attached}
                  <button type="button" onClick={onDetach}>{c.detach}</button>
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
