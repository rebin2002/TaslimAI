/**
 * AI Video Makeover source-catalog bridge (issue #698, source-integration slice).
 *
 * Why this module exists
 * ----------------------
 * The draft/admission helper in `@/lib/movieVideoMakeoverDraftState` (#775) and the
 * input panel in `@/components/movie/MovieVideoMakeoverInputPanel` (#784) are both
 * complete on their own, but they are not connected to anything a creator can
 * actually point at. The panel's source control writes one literal placeholder
 * (`workspace-clip-001`) and never touches the metadata fields, so selecting a
 * different clip keeps the previous clip's numbers attached and no real clip can
 * ever be described. This bridge is the missing product-facing middle step: it
 * reads the Movie Studio source descriptors that already exist and turns them into
 * the exact `MovieVideoMakeoverSelection` source fields #784 feeds into #775.
 *
 * Guarantees, stated deliberately narrow
 * --------------------------------------
 * 1. PURE AND PROVIDER-FREE. No fetch, no API client import, no provider call, no
 *    credit reservation, no job, no upload transport. This module maps data that a
 *    caller already holds. Attaching a source never implies an estimate or a launch.
 * 2. NO FACT INVENTION. A descriptor field that the browser cannot observe is
 *    carried through as `null`/absent, never defaulted. Absent facts become an
 *    honest gap entry, and each gap names the #775 block code it will produce, so
 *    the UI explains the pending verification instead of hiding it.
 * 3. SOURCE SWITCHING CLEARS STALE SOURCE FACTS. Every source-scoped field is
 *    rewritten from the chosen descriptor, so a second selection can never inherit
 *    the first clip's duration, dimensions, frame rate, audio state, size,
 *    ownership, visibility, or retention verdict. Non-source parts of the draft
 *    (mode, preset, prompt, preservation, capability evidence, creator approval)
 *    are preserved untouched; #775 re-admits them against the new source.
 * 4. NO OWNED-FILE DEPENDENCIES. It imports `#775` and `#784` types only, and edits
 *    neither. `MovieStudioView.tsx` (#762/#580), `lib/api.ts` (#686), the spike doc
 *    (#765), the comparison panel (#801), and `lib/i18n.ts` (#762) are untouched, and
 *    nothing here is referenced from them.
 * 5. NOT A RESTATEMENT OF THE CAPABILITY VOCABULARY. Feature keys, evidence labels,
 *    and mode requirements continue to live only in #775/#771/#765. This file never
 *    mentions a provider, a route, or a feature key; it speaks only about source
 *    facts the creator attached.
 */

import type {
  MovieVideoMakeoverBlockCode,
  MovieVideoMakeoverSourceInput,
} from "@/lib/movieVideoMakeoverDraftState";
import {
  MOVIE_VIDEO_MAKEOVER_INPUT_LIMITS,
  isMovieVideoMakeoverUnsupportedFutureMode,
} from "@/lib/movieVideoMakeoverDraftState";
import type { MovieVideoMakeoverSelection } from "@/components/movie/movieVideoMakeoverInputState";

/**
 * Clip statuses Movie Studio treats as finished output. Mirrors the readiness rule
 * already used by the quick-result path (`MovieStudioView.tsx:39`); duplicated here
 * on purpose because that file is owned by #762/#580 and cannot be imported from a
 * client component.
 */
export const MOVIE_VIDEO_MAKEOVER_READY_CLIP_STATUSES = ["Completed", "Succeeded", "Ready"] as const;

/** The only asset type a makeover can take as source. */
export const MOVIE_VIDEO_MAKEOVER_SOURCE_ASSET_TYPE = "video";

/** Asset lifecycle states that make a stored file unusable as a source. */
export const MOVIE_VIDEO_MAKEOVER_UNAVAILABLE_ASSET_STATUSES = ["Archived"] as const;

/**
 * Everything the caller may know about one candidate source clip. Optional fields
 * stay optional: absence is a fact about the read model, not a zero to fill in.
 */
export type MovieVideoMakeoverSourceDescriptor = {
  readonly assetId?: string | null;
  readonly name?: string | null;
  readonly assetType?: string | null;
  readonly mimeType?: string | null;
  readonly clipStatus?: string | null;
  readonly assetStatus?: string | null;
  readonly workspaceId?: string | null;
  readonly projectId?: string | null;
  readonly ownerPrincipalId?: string | null;
  readonly visibility?: string | null;
  readonly retentionPolicyVerified?: boolean | null;
  readonly hasFile?: boolean | null;
  readonly canPreview?: boolean | null;
  readonly sizeBytes?: number | null;
  readonly durationSeconds?: number | null;
  readonly width?: number | null;
  readonly height?: number | null;
  readonly frameRate?: number | null;
  readonly audioPresent?: boolean | null;
};

/** The acting creator and workspace, supplied by the caller, never inferred here. */
export type MovieVideoMakeoverSourceContext = {
  readonly principalId?: string | null;
  readonly workspaceId?: string | null;
};

/** A source that can never be attached, with the reason shown to the creator. */
export type MovieVideoMakeoverSourceBlockCode =
  | "source_asset_missing"
  | "source_asset_not_video"
  | "source_asset_unavailable"
  | "source_clip_not_ready"
  | "source_file_missing"
  | "source_cross_workspace"
  | "source_preview_unavailable";

export type MovieVideoMakeoverSourceBlocker = {
  readonly code: MovieVideoMakeoverSourceBlockCode;
  readonly message: string;
};

/**
 * Facts a descriptor does not carry. Each one names the #775 block code it is
 * expected to produce, so the panel's explanation matches the admission result
 * instead of being a separate, possibly contradictory opinion.
 */
export type MovieVideoMakeoverSourceGapCode =
  | "ownership_unverified"
  | "visibility_unverified"
  | "retention_unverified"
  | "duration_unknown"
  | "dimensions_unknown"
  | "frame_rate_unknown"
  | "audio_state_unknown"
  | "size_unknown";

export type MovieVideoMakeoverSourceGap = {
  readonly code: MovieVideoMakeoverSourceGapCode;
  readonly message: string;
  readonly admissionBlockCode: MovieVideoMakeoverBlockCode;
};

export type MovieVideoMakeoverSourceEntry = {
  readonly assetId: string;
  readonly label: string;
  readonly selectable: boolean;
  readonly blockers: readonly MovieVideoMakeoverSourceBlocker[];
  readonly gaps: readonly MovieVideoMakeoverSourceGap[];
  readonly source: MovieVideoMakeoverSourceInput;
};

/** Maps a source gap onto the fail-closed code #775 will report for it. */
const GAP_ADMISSION_CODES: Readonly<Record<MovieVideoMakeoverSourceGapCode, MovieVideoMakeoverBlockCode>> = {
  ownership_unverified: "source_ownership_unverified",
  visibility_unverified: "source_visibility_not_private",
  retention_unverified: "source_retention_unverified",
  duration_unknown: "source_metadata_missing",
  dimensions_unknown: "source_metadata_missing",
  frame_rate_unknown: "source_metadata_missing",
  audio_state_unknown: "source_metadata_missing",
  size_unknown: "source_metadata_missing",
};

const GAP_MESSAGES: Readonly<Record<MovieVideoMakeoverSourceGapCode, string>> = {
  ownership_unverified: "The descriptor carries no owning creator, so source ownership stays unverified.",
  visibility_unverified: "The descriptor carries no visibility flag, so private-source handling stays unverified.",
  retention_unverified: "The descriptor carries no verified retention policy for the source file.",
  duration_unknown: "The descriptor carries no duration, so the source length stays unverified.",
  dimensions_unknown: "The descriptor carries no frame dimensions, so the source size in pixels stays unverified.",
  frame_rate_unknown: "The descriptor carries no frame rate, so temporal continuity stays unverified.",
  audio_state_unknown: "The descriptor does not say whether the source has audio.",
  size_unknown: "The descriptor carries no file size, so the upload bound stays unverified.",
};

function cleanText(value: string | null | undefined, max: number): string | null {
  if (typeof value !== "string") {
    return null;
  }
  const trimmed = value.trim();
  if (trimmed.length === 0 || trimmed.length > max) {
    return null;
  }
  return trimmed;
}

/**
 * A finite number inside the #775 bounds is kept exactly as supplied; anything else
 * becomes null. Out-of-bounds values are deliberately passed through unchanged in
 * the source object (see `describe` below) so #775 still reports
 * `source_metadata_out_of_bounds` rather than a silent drop.
 */
function carryNumber(value: number | null | undefined): number | null {
  if (typeof value !== "number" || !Number.isFinite(value)) {
    return null;
  }
  return value;
}

function isVideoAsset(descriptor: MovieVideoMakeoverSourceDescriptor): boolean {
  const assetType = cleanText(descriptor.assetType, 40);
  const mimeType = cleanText(descriptor.mimeType, 100);
  if (assetType !== null) {
    return assetType.toLowerCase() === MOVIE_VIDEO_MAKEOVER_SOURCE_ASSET_TYPE;
  }
  if (mimeType !== null) {
    return mimeType.toLowerCase().startsWith(MOVIE_VIDEO_MAKEOVER_SOURCE_ASSET_TYPE + "/");
  }
  // Neither field present: the read model does not say, so the clip is not a
  // confirmed makeover source and must not be advertised as one.
  return false;
}

function isClipReady(descriptor: MovieVideoMakeoverSourceDescriptor): boolean {
  const status = cleanText(descriptor.clipStatus, 40);
  if (status === null) {
    // No status supplied is not "ready"; the caller must say so explicitly.
    return false;
  }
  return (MOVIE_VIDEO_MAKEOVER_READY_CLIP_STATUSES as readonly string[]).includes(status);
}

/**
 * The unified Asset model offers archive/restore rather than deletion, so an
 * archived asset is present but not an usable source. Anything the read model does
 * not say is treated as available and reported as a gap elsewhere, never invented.
 */
function isAssetUnavailable(descriptor: MovieVideoMakeoverSourceDescriptor): boolean {
  const status = cleanText(descriptor.assetStatus, 40);
  return status !== null
    && (MOVIE_VIDEO_MAKEOVER_UNAVAILABLE_ASSET_STATUSES as readonly string[]).includes(status);
}

function blocker(
  code: MovieVideoMakeoverSourceBlockCode,
  message: string,
): MovieVideoMakeoverSourceBlocker {
  return { code, message };
}

function gap(code: MovieVideoMakeoverSourceGapCode): MovieVideoMakeoverSourceGap {
  return { code, message: GAP_MESSAGES[code], admissionBlockCode: GAP_ADMISSION_CODES[code] };
}

/**
 * Builds the honest source part of a draft from one descriptor. Field-by-field:
 * present values are carried through, absent values become `null` plus a gap entry.
 */
function describeSource(
  descriptor: MovieVideoMakeoverSourceDescriptor,
  assetId: string,
): { readonly source: MovieVideoMakeoverSourceInput; readonly gaps: readonly MovieVideoMakeoverSourceGap[] } {
  const gaps: MovieVideoMakeoverSourceGap[] = [];

  const ownerPrincipalId = cleanText(descriptor.ownerPrincipalId, MOVIE_VIDEO_MAKEOVER_INPUT_LIMITS.maxPrincipalIdLength);
  if (ownerPrincipalId === null) {
    gaps.push(gap("ownership_unverified"));
  }

  const workspaceId = cleanText(descriptor.workspaceId, MOVIE_VIDEO_MAKEOVER_INPUT_LIMITS.maxWorkspaceIdLength);
  const visibility = cleanText(descriptor.visibility, 40);
  // #775 admits only the literal "private"; anything else is carried as stated.
  if (visibility === null) {
    gaps.push(gap("visibility_unverified"));
  }

  const retentionPolicyVerified = descriptor.retentionPolicyVerified === true
    ? true
    : descriptor.retentionPolicyVerified === false
      ? false
      : null;
  if (retentionPolicyVerified !== true) {
    gaps.push(gap("retention_unverified"));
  }

  const durationSeconds = carryNumber(descriptor.durationSeconds);
  if (durationSeconds === null) {
    gaps.push(gap("duration_unknown"));
  }
  const width = carryNumber(descriptor.width);
  const height = carryNumber(descriptor.height);
  if (width === null || height === null) {
    gaps.push(gap("dimensions_unknown"));
  }
  const frameRate = carryNumber(descriptor.frameRate);
  if (frameRate === null) {
    gaps.push(gap("frame_rate_unknown"));
  }
  const audioPresent = typeof descriptor.audioPresent === "boolean" ? descriptor.audioPresent : null;
  if (audioPresent === null) {
    gaps.push(gap("audio_state_unknown"));
  }
  const sizeBytes = carryNumber(descriptor.sizeBytes);
  if (sizeBytes === null) {
    gaps.push(gap("size_unknown"));
  }

  return {
    gaps,
    source: {
      assetId,
      ownerPrincipalId,
      workspaceId,
      visibility,
      retentionPolicyVerified,
      durationSeconds,
      width,
      height,
      frameRate,
      audioPresent,
      sizeBytes,
    },
  };
}

/**
 * Turns one descriptor into an attachable-or-not source entry. `selectable` is
 * false for any structural blocker; missing facts do NOT block selection, they
 * only explain why #775 will still refuse an estimate.
 */
export function describeMovieVideoMakeoverSource(
  descriptor: MovieVideoMakeoverSourceDescriptor,
  context: MovieVideoMakeoverSourceContext = {},
): MovieVideoMakeoverSourceEntry {
  const assetId = cleanText(descriptor.assetId, MOVIE_VIDEO_MAKEOVER_INPUT_LIMITS.maxAssetIdLength);
  const blockers: MovieVideoMakeoverSourceBlocker[] = [];

  if (assetId === null) {
    blockers.push(blocker("source_asset_missing", "This clip has no asset identifier to attach."));
  }
  if (assetId !== null && !isVideoAsset(descriptor)) {
    blockers.push(blocker("source_asset_not_video", "Makeover takes a finished video asset; this candidate is not one."));
  }
  if (assetId !== null && !isClipReady(descriptor)) {
    blockers.push(blocker("source_clip_not_ready", "The clip has not finished rendering, so there is nothing to makeover yet."));
  }
  if (assetId !== null && isAssetUnavailable(descriptor)) {
    blockers.push(blocker("source_asset_unavailable", "This asset is archived, so its stored file is not usable as a source."));
  }
  if (assetId !== null && descriptor.hasFile === false) {
    blockers.push(blocker("source_file_missing", "The asset reports no stored file behind it."));
  }
  if (assetId !== null && descriptor.canPreview === false) {
    blockers.push(blocker("source_preview_unavailable", "The asset cannot be previewed, so a before/after check would be unverifiable."));
  }

  const contextWorkspaceId = cleanText(context.workspaceId, MOVIE_VIDEO_MAKEOVER_INPUT_LIMITS.maxWorkspaceIdLength);
  const descriptorWorkspaceId = cleanText(descriptor.workspaceId, MOVIE_VIDEO_MAKEOVER_INPUT_LIMITS.maxWorkspaceIdLength);
  if (assetId !== null && contextWorkspaceId !== null && descriptorWorkspaceId !== null
    && contextWorkspaceId !== descriptorWorkspaceId) {
    blockers.push(blocker("source_cross_workspace", "This clip belongs to a different workspace and cannot be attached here."));
  }

  const label = cleanText(descriptor.name, 160)
    ?? assetId
    ?? "Untitled clip";

  const described = describeSource(descriptor, assetId ?? "");
  return {
    assetId: assetId ?? "",
    label,
    selectable: blockers.length === 0,
    blockers,
    gaps: described.gaps,
    source: described.source,
  };
}

/** Catalog for a whole project, in the order supplied. Filtered by the caller. */
export function buildMovieVideoMakeoverSourceCatalog(
  descriptors: readonly MovieVideoMakeoverSourceDescriptor[],
  context: MovieVideoMakeoverSourceContext = {},
): readonly MovieVideoMakeoverSourceEntry[] {
  return descriptors.map((descriptor) => describeMovieVideoMakeoverSource(descriptor, context));
}

/** Count of attachable entries, so a panel can say "no finished clip yet" honestly. */
export function countSelectableMovieVideoMakeoverSources(
  entries: readonly MovieVideoMakeoverSourceEntry[],
): number {
  return entries.filter((entry) => entry.selectable).length;
}

/**
 * Fields of the browser-side Movie read models this bridge is allowed to read.
 *
 * Declared structurally rather than as `Pick<MovieClip, ...>` / `Pick<MovieProject,
 * ...>` so a caller can hand over the real `MovieProject` (which satisfies these
 * shapes) without the bridge depending on fields it must not read, and so tests can
 * pass the narrowest honest fixture instead of a fully populated Movie payload.
 */
export type MovieVideoMakeoverProjectClip = {
  readonly id: string;
  readonly assetId: string | null;
  readonly status: string;
  readonly durationSeconds: number | null;
  readonly metadataJson: string | null;
};
export type MovieVideoMakeoverProjectSource = {
  readonly workspaceId: string;
  readonly clips: readonly MovieVideoMakeoverProjectClip[];
};
export type MovieVideoMakeoverAssetSource = {
  readonly id: string;
  readonly name: string;
  readonly workspaceId: string;
  readonly assetType: string;
  readonly mimeType: string | null;
  readonly status: string;
  readonly hasFile: boolean;
  readonly canPreview: boolean;
  readonly fileSizeBytes: number | null;
};

/**
 * Duration sometimes lives only in the clip metadata bag. A finite positive
 * `durationMilliseconds` is converted to seconds; a malformed or absent bag stays
 * unknown rather than becoming a guess.
 */
function clipDurationSeconds(clip: MovieVideoMakeoverProjectClip): number | null {
  if (typeof clip.durationSeconds === "number" && Number.isFinite(clip.durationSeconds)) {
    return clip.durationSeconds;
  }
  if (typeof clip.metadataJson !== "string" || clip.metadataJson.trim() === "") {
    return null;
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(clip.metadataJson);
  } catch {
    return null;
  }
  if (typeof parsed !== "object" || parsed === null) {
    return null;
  }
  const milliseconds = (parsed as { durationMilliseconds?: unknown }).durationMilliseconds;
  if (typeof milliseconds !== "number" || !Number.isFinite(milliseconds) || milliseconds <= 0) {
    return null;
  }
  return Math.round(milliseconds / 100) / 10;
}

/**
 * Maps the real Movie Studio clips and their published Assets into source entries.
 *
 * This is deliberately a downgrade as much as a bridge: the browser read models
 * expose identifier, type, status, file presence, previewability, byte size and
 * duration, and nothing else. Owner, visibility, retention verification, frame
 * dimensions, frame rate and audio state are not in `GET /api/assets` or the Movie
 * project payload, so each one is carried as unknown and surfaces as a gap with the
 * #775 code it produces. A creator can therefore attach a clip and read exactly
 * which facts the server still has not confirmed, instead of seeing a source that
 * looks complete but is not admissible.
 */
export function describeMovieVideoMakeoverProjectSources(
  project: MovieVideoMakeoverProjectSource | null | undefined,
  assets: readonly MovieVideoMakeoverAssetSource[],
  context: MovieVideoMakeoverSourceContext = {},
): readonly MovieVideoMakeoverSourceEntry[] {
  const clips = project?.clips ?? [];
  if (clips.length === 0) {
    return [];
  }
  const byAssetId = new Map<string, MovieVideoMakeoverAssetSource>();
  for (const asset of assets) {
    if (!byAssetId.has(asset.id)) {
      byAssetId.set(asset.id, asset);
    }
  }

  const seen = new Set<string>();
  const entries: MovieVideoMakeoverSourceEntry[] = [];
  for (const clip of clips) {
    const assetId = cleanText(clip.assetId, MOVIE_VIDEO_MAKEOVER_INPUT_LIMITS.maxAssetIdLength);
    // Keep a placeholder row for a clip with no asset so the list still reflects
    // the project; describe() turns it into an honest `source_asset_missing`.
    const key = assetId ?? "clip:" + String(clip.id);
    if (seen.has(key)) {
      continue;
    }
    seen.add(key);
    const asset = assetId === null ? undefined : byAssetId.get(assetId);
    entries.push(describeMovieVideoMakeoverSource({
      assetId: assetId,
      // A clip whose asset row is missing is still identifiable by its own id, and
      // naming it "Untitled clip" would hide which row the creator is looking at.
      name: asset?.name ?? clip.id,
      assetType: asset?.assetType ?? null,
      mimeType: asset?.mimeType ?? null,
      clipStatus: clip.status,
      assetStatus: asset?.status ?? null,
      workspaceId: asset?.workspaceId ?? cleanText(project?.workspaceId, MOVIE_VIDEO_MAKEOVER_INPUT_LIMITS.maxWorkspaceIdLength),
      hasFile: typeof asset?.hasFile === "boolean" ? asset.hasFile : null,
      canPreview: typeof asset?.canPreview === "boolean" ? asset.canPreview : null,
      sizeBytes: typeof asset?.fileSizeBytes === "number" ? asset.fileSizeBytes : null,
      durationSeconds: clipDurationSeconds(clip),
      // Not modelled by the browser read models; never invented here.
      ownerPrincipalId: null,
      visibility: null,
      retentionPolicyVerified: null,
      width: null,
      height: null,
      frameRate: null,
      audioPresent: null,
    }, context));
  }
  return entries;
}

/**
 * Per-gap tally across a catalogue, so a caller can state the outstanding
 * verifications once instead of repeating them per row.
 */
export function tallyMovieVideoMakeoverSourceGaps(
  entries: readonly MovieVideoMakeoverSourceEntry[],
): Readonly<Record<MovieVideoMakeoverSourceGapCode, number>> {
  const tally = {} as Record<MovieVideoMakeoverSourceGapCode, number>;
  for (const code of Object.keys(GAP_ADMISSION_CODES) as MovieVideoMakeoverSourceGapCode[]) {
    tally[code] = 0;
  }
  for (const entry of entries) {
    for (const row of entry.gaps) {
      tally[row.code] += 1;
    }
  }
  return tally;
}

/**
 * Applies a chosen source to an in-progress selection. Every source-scoped field is
 * rewritten from the entry, so switching clips drops the old clip's facts. Non-source
 * draft parts are preserved. A blocked entry never changes the draft.
 */
export function applyMovieVideoMakeoverSourceSelection(
  selection: MovieVideoMakeoverSelection,
  entry: MovieVideoMakeoverSourceEntry,
): MovieVideoMakeoverSelection {
  if (!entry.selectable) {
    return selection;
  }
  const source = entry.source;
  // Confirmed-silent keeps the promise (it costs nothing to preserve a track that
  // is not there); only an unknown audio state invalidates it.
  const audioUnconfirmed = selection.preserveOriginalAudio && source.audioPresent === null;
  return {
    ...selection,
    sourceAssetId: source.assetId ?? null,
    sourceOwnerPrincipalId: source.ownerPrincipalId ?? null,
    sourceWorkspaceId: source.workspaceId ?? null,
    sourceVisibilityPrivate: source.visibility === "private",
    sourceRetentionVerified: source.retentionPolicyVerified === true,
    sourceAudioPresent: source.audioPresent ?? null,
    sourceDurationSeconds: source.durationSeconds ?? null,
    sourceWidth: source.width ?? null,
    sourceHeight: source.height ?? null,
    sourceFrameRate: source.frameRate ?? null,
    sourceSizeBytes: source.sizeBytes ?? null,
    // An audio promise that the new source cannot back is dropped rather than
    // carried over, so #775 never sees a claim inherited from another clip.
    preserveOriginalAudio: audioUnconfirmed ? false : selection.preserveOriginalAudio,
  };
}

/**
 * Clears the source and every source-scoped fact while leaving the rest of the draft
 * alone. Used by the panel's "Detach" control so clearing cannot leave orphan numbers.
 */
export function clearMovieVideoMakeoverSourceSelection(
  selection: MovieVideoMakeoverSelection,
): MovieVideoMakeoverSelection {
  if (selection.sourceAssetId === null
    && selection.sourceOwnerPrincipalId === null
    && selection.sourceWorkspaceId === null
    && !selection.sourceVisibilityPrivate
    && !selection.sourceRetentionVerified
    && selection.sourceAudioPresent === null
    && selection.sourceDurationSeconds === null
    && selection.sourceWidth === null
    && selection.sourceHeight === null
    && selection.sourceFrameRate === null
    && selection.sourceSizeBytes === null) {
    return selection;
  }
  return {
    ...selection,
    sourceAssetId: null,
    sourceOwnerPrincipalId: null,
    sourceWorkspaceId: null,
    sourceVisibilityPrivate: false,
    sourceRetentionVerified: false,
    sourceAudioPresent: null,
    sourceDurationSeconds: null,
    sourceWidth: null,
    sourceHeight: null,
    sourceFrameRate: null,
    sourceSizeBytes: null,
    // Without a source, promising original audio would be unverifiable.
    preserveOriginalAudio: false,
  };
}

/**
 * Honest explanation of what still stands between the attached source and an
 * estimate, for the currently selected mode. A wardrobe/outfit request is reported
 * as the unsupported future feature it is, in #775's words, and never as a source
 * problem the creator could fix by re-attaching a clip.
 */
export function movieVideoMakeoverSourcePendingCopy(
  entry: MovieVideoMakeoverSourceEntry | null,
  mode: string | null,
): { readonly headline: string; readonly details: readonly string[] } {
  if (isMovieVideoMakeoverUnsupportedFutureMode(mode)) {
    return {
      headline: "Outfit change is a future feature; no attached clip makes it available.",
      details: ["Verified in-place wardrobe editing is not documented and tested by any route, so this request stays blocked."],
    };
  }
  if (entry === null) {
    return {
      headline: "No source attached. Pick a finished clip from this workspace.",
      details: ["Uploading a new video from this panel is not wired; only clips that already exist here can be attached."],
    };
  }
  if (!entry.selectable) {
    return {
      headline: "This clip cannot be attached.",
      details: entry.blockers.map((blockerRow) => blockerRow.message),
    };
  }
  if (entry.gaps.length === 0) {
    return {
      headline: "Source facts complete. The draft is still non-billable until an estimate is wired.",
      details: ["Attaching a source never creates a job, reserves credit, or permits a launch."],
    };
  }
  return {
    headline: "Attached, but " + String(entry.gaps.length) + " source fact"
      + (entry.gaps.length === 1 ? "" : "s") + " still need verification.",
    details: entry.gaps.map((gapRow) => gapRow.message),
  };
}
