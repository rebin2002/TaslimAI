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
  MovieMakeoverComparisonPanel,
  type MovieMakeoverComparisonPanelProps,
} from "@/components/movie/MovieMakeoverComparisonPanel";
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

/**
 * Route-owned identity/source evidence for the media URLs in a comparison.
 * The embedding route must obtain this binding alongside the already-authorized
 * previews. It is never inferred from the displayed URL or a draft field.
 */
export type MovieVideoMakeoverComparisonBinding = Readonly<{
  principalId: string;
  workspaceId: string;
  sourceAssetId: string;
}>;

export type MovieVideoMakeoverStudioProps = {
  readonly project: MovieVideoMakeoverProjectSource | null;
  readonly assets: readonly MovieVideoMakeoverAssetSource[];
  readonly principalId: string | null;
  readonly workspaceId: string | null;
  readonly selection: MovieVideoMakeoverSelection;
  readonly onChange: (nextSelection: MovieVideoMakeoverSelection) => void;
  /** Already-authorized preview sources supplied by the route owner, never fetched here. */
  readonly comparison?: MovieMakeoverComparisonPanelProps | null;
  /** Must match the current authenticated principal, workspace and attached clip. */
  readonly comparisonBinding?: MovieVideoMakeoverComparisonBinding | null;
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

/**
 * Catalogue changes can revoke access to a previously selected clip. Mask stale
 * source facts in the rendered draft immediately, without mutating the caller's
 * controlled state during render. The owner may then persist the cleared draft.
 */
export function reconcileMovieVideoMakeoverStudioSelection(
  selection: MovieVideoMakeoverSelection,
  entries: ReturnType<typeof describeMovieVideoMakeoverProjectSources>,
): MovieVideoMakeoverSelection {
  if (selection.sourceAssetId === null) return selection;
  const current = entries.find((entry) => entry.assetId === selection.sourceAssetId);
  if (!current?.selectable) return clearMovieVideoMakeoverSourceSelection(selection);

  // An asset can retain its ID while its authoritative catalogue facts change
  // (replacement file, updated duration, revoked ownership or retention). Never
  // render previously selected metadata as if it were still current. Reapplying
  // the live descriptor also clears claims the current catalogue cannot verify.
  const refreshed = applyMovieVideoMakeoverSourceSelection(selection, current);
  const sourceFields: readonly (keyof MovieVideoMakeoverSelection)[] = [
    "sourceAssetId", "sourceOwnerPrincipalId", "sourceWorkspaceId",
    "sourceVisibilityPrivate", "sourceRetentionVerified", "sourceAudioPresent",
    "sourceDurationSeconds", "sourceWidth", "sourceHeight", "sourceFrameRate",
    "sourceSizeBytes", "preserveOriginalAudio",
  ];
  return sourceFields.every((field) => Object.is(selection[field], refreshed[field]))
    ? selection
    : refreshed;
}

export function MovieVideoMakeoverStudio({
  project,
  assets,
  principalId,
  workspaceId,
  selection,
  onChange,
  comparison = null,
  comparisonBinding = null,
}: MovieVideoMakeoverStudioProps) {
  const context = { principalId, workspaceId };
  // Route authentication can briefly be unresolved after logout or an account
  // switch. Fail closed before rendering even clip titles; no caller-supplied
  // project/asset cache may stand in for the current authenticated identity.
  const hasAuthenticatedScope = Boolean(principalId?.trim() && workspaceId?.trim());
  const entries = hasAuthenticatedScope
    ? describeMovieVideoMakeoverProjectSources(project, assets, context)
    : [];
  const visibleSelection = reconcileMovieVideoMakeoverStudioSelection(selection, entries);
  const attached = entries.find((entry) => entry.selectable && entry.assetId === visibleSelection.sourceAssetId) ?? null;
  const copy = movieVideoMakeoverSourcePendingCopy(attached, visibleSelection.mode);
  const tally = tallyMovieVideoMakeoverSourceGaps(attached === null ? [] : [attached]);
  const pendingCount = Object.values(tally).reduce((total, count) => total + count, 0);
  // Never render media from a previous principal, workspace or source clip.
  // URL scheme validation in the comparison panel is not an ownership check.
  const comparisonMatchesSource = Boolean(
    comparison && comparisonBinding && attached && principalId && workspaceId &&
    comparisonBinding.principalId === principalId &&
    comparisonBinding.workspaceId === workspaceId &&
    comparisonBinding.sourceAssetId === attached.assetId,
  );

  return (
    <div className="movie-makeover-studio" data-pending-source-facts={String(pendingCount)}>
      <MovieVideoMakeoverSourcePanel
        entries={entries}
        selectedAssetId={visibleSelection.sourceAssetId}
        mode={visibleSelection.mode}
        onAttach={(assetId) => onChange(reduceMovieVideoMakeoverStudioSource(visibleSelection, assetId, entries))}
        onDetach={() => onChange(reduceMovieVideoMakeoverStudioSource(visibleSelection, null, entries))}
      />
      <p className="movie-makeover-studio-summary" role="note">
        {copy.headline}
      </p>
      <MovieVideoMakeoverInputPanel selection={visibleSelection} onChange={onChange} showPlaceholderSourceControl={false} />
      {/* No source is inferred from the draft. A missing makeover result must
          remain unavailable, never be replaced with the original clip. */}
      {comparisonMatchesSource && comparison ? <MovieMakeoverComparisonPanel {...comparison} /> : null}
    </div>
  );
}
