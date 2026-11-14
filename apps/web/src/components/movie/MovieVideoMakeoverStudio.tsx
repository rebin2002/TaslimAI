/**
 * AI Video Makeover studio composer (issue #698, source-integration slice).
 *
 * What it adds over the existing slices
 * -------------------------------------
 * #775 owns the fail-closed draft/admission helper, #784 owns the input panel that
 * renders mode, preset, prompt, capability evidence and consent, and this module is
 * the seam between them and the clips a creator actually has. It reads Movie Studio's
 * published clips and Assets, shows which can be attached and why the others cannot,
 * and writes the attached source's facts straight into the selection #784 admits.
 * Before this seam, the input panel's source control could only ever hold one
 * placeholder identifier, so a real clip could not be attached and a second
 * attachment kept the first clip's numbers.
 *
 * Guarantees
 * ----------
 * 1. NO NEW EXECUTION SURFACE. It renders no estimate, launch, retry, download or
 *    upload control and adds none of its own: the disabled controls still come from
 *    #784, whose `uiEnabled`/`billable`/`launchPermitted`/`providerJobCreated` flags
 *    stay the literal `false`. Nothing here calls `@/lib/api`, fetches, uploads,
 *    reserves credit, or reaches a provider.
 * 2. FULLY CONTROLLED. All state lives in the `selection` the caller owns, so the
 *    whole composer renders with `renderToStaticMarkup` like its siblings.
 * 3. TRUTHFUL ABOUT ITS OWN LIMITS. Attaching is stated as a draft edit, uploading a
 *    brand-new file into the source slot is stated as not wired, and source facts the
 *    read models do not carry are listed as pending verification instead of hidden.
 * 4. UNSUPPORTED REQUESTS DO NOT BECOME SUPPORTABLE BY CHOOSING ANOTHER CLIP. A
 *    wardrobe/outfit request (#698 Phase B) keeps the shared #775 wording through
 *    this composer; it is never presented as a source problem.
 * 5. NO OWNED-FILE CONFLICT. It imports the #784 panel and the #775 helper, and edits
 *    neither. `MovieStudioView.tsx` (#762/#580), `app/create/movie/page.tsx` (#673),
 *    `lib/api.ts` (#686), the spike doc (#765), the comparison panel (#801), and
 *    `lib/i18n.ts` (#762) are untouched and import nothing from this slice, so the
 *    feature still has to be mounted by its file owner behind the cost-firewall gate.
 *
 * Known overlap, left for its owner to remove
 * -------------------------------------------
 * #784's panel still renders its own placeholder source control ("Choose an uploaded
 * clip", which writes the literal `workspace-clip-001`). That file is owned by PR
 * #784, so this slice does not edit it: while both are mounted there are two enabled
 * controls on the source step, and only this composer's attach/detach pair describes
 * a real clip. The one-line handoff is for #784 to drop its placeholder block once
 * this composer is mounted, which its owner can do without touching any file here.
 */

import {
  MovieVideoMakeoverInputPanel,
} from "@/components/movie/MovieVideoMakeoverInputPanel";
import type { MovieVideoMakeoverSelection } from "@/components/movie/movieVideoMakeoverInputState";
import {
  MovieVideoMakeoverSourcePanel,
} from "@/components/movie/MovieVideoMakeoverSourcePanel";
import {
  applyMovieVideoMakeoverSourceSelection,
  clearMovieVideoMakeoverSourceSelection,
  describeMovieVideoMakeoverProjectSources,
  movieVideoMakeoverSourcePendingCopy,
  tallyMovieVideoMakeoverSourceGaps,
  type MovieVideoMakeoverAssetSource,
  type MovieVideoMakeoverProjectSource,
} from "@/lib/movieVideoMakeoverSourceCatalog";

export type MovieVideoMakeoverStudioProps = {
  readonly project: MovieVideoMakeoverProjectSource | null;
  readonly assets: readonly MovieVideoMakeoverAssetSource[];
  readonly principalId: string | null;
  readonly workspaceId: string | null;
  readonly selection: MovieVideoMakeoverSelection;
  readonly onChange: (nextSelection: MovieVideoMakeoverSelection) => void;
};

/**
 * Pure step used by the composer and by callers that already hold a catalogue:
 * attach the chosen clip's facts, or detach entirely. A clip the catalogue marks
 * unattachable leaves the draft untouched, so a click cannot half-apply a source.
 */
export function reduceMovieVideoMakeoverStudioSource(
  selection: MovieVideoMakeoverSelection,
  assetId: string | null,
  entries: ReturnType<typeof describeMovieVideoMakeoverProjectSources>,
): MovieVideoMakeoverSelection {
  if (assetId === null) {
    return clearMovieVideoMakeoverSourceSelection(selection);
  }
  const entry = entries.find((candidate) => candidate.assetId === assetId);
  if (entry === undefined) {
    return selection;
  }
  return applyMovieVideoMakeoverSourceSelection(selection, entry);
}

export function MovieVideoMakeoverStudio({
  project,
  assets,
  principalId,
  workspaceId,
  selection,
  onChange,
}: MovieVideoMakeoverStudioProps) {
  const context = { principalId, workspaceId };
  const entries = describeMovieVideoMakeoverProjectSources(project, assets, context);
  const attached = entries.find((entry) => entry.assetId !== "" && entry.assetId === selection.sourceAssetId) ?? null;
  const copy = movieVideoMakeoverSourcePendingCopy(attached, selection.mode);
  const tally = tallyMovieVideoMakeoverSourceGaps(attached === null ? [] : [attached]);
  const pendingCount = Object.values(tally).reduce((total, count) => total + count, 0);

  return (
    <div className="movie-makeover-studio" data-pending-source-facts={String(pendingCount)}>
      <MovieVideoMakeoverSourcePanel
        entries={entries}
        selectedAssetId={selection.sourceAssetId}
        mode={selection.mode}
        onAttach={(assetId) => onChange(reduceMovieVideoMakeoverStudioSource(selection, assetId, entries))}
        onDetach={() => onChange(reduceMovieVideoMakeoverStudioSource(selection, null, entries))}
      />
      <p className="movie-makeover-studio-summary" role="note">
        {copy.headline}
      </p>
      <MovieVideoMakeoverInputPanel selection={selection} onChange={onChange} />
    </div>
  );
}
