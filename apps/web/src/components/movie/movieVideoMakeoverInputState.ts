/**
 * Provider-neutral UI state for the AI Video Makeover input slice (issue #698).
 *
 * This module is deliberately free of React, DOM, and network concerns. It owns
 * only the honest presentation vocabulary for the input panel:
 *
 * - Which makeover modes the panel offers, and which mode is shown as an
 *   unsupported future feature (wardrobe / outfit change, #698 Phase B item 5).
 * - How a panel selection maps onto a draft input understood by
 *   `@/lib/movieVideoMakeoverDraftState` (PR #775), and how the resulting
 *   fail-closed admission maps back onto honest UI copy.
 * - Capability chips: required features are derived per mode from the shared
 *   `MOVIE_VIDEO_MAKEOVER_MODE_REQUIREMENTS`; nothing here ever infers or
 *   defaults a capability to "supported".
 *
 * No call in this file or its consumers creates a provider job, reserves
 * credit, requests an estimate, or enables an action; the panel renders the
 * literal `false` safety flags it receives from the admission helper.
 */

import {
  MOVIE_VIDEO_MAKEOVER_MODE_REQUIREMENTS,
  MOVIE_VIDEO_MAKEOVER_MODES,
  type MovieVideoMakeoverAdmission,
  type MovieVideoMakeoverCapabilityInput,
  type MovieVideoMakeoverDraft,
  type MovieVideoMakeoverDraftInput,
  type MovieVideoMakeoverEvidenceLabel,
  type MovieVideoMakeoverFeatureKey,
  type MovieVideoMakeoverMode,
} from "@/lib/movieVideoMakeoverDraftState";

export type MovieVideoMakeoverUiMode = MovieVideoMakeoverMode | "wardrobe-change";

export type MovieVideoMakeoverModeChoice = {
  readonly value: MovieVideoMakeoverUiMode;
  readonly label: string;
  readonly supported: boolean;
  readonly availabilityLabel: string;
};

/** Supported Phase A modes first, then the honest future-feature entry. */
export const MOVIE_VIDEO_MAKEOVER_UI_MODE_CHOICES: readonly MovieVideoMakeoverModeChoice[] = [
  { value: "studio-swap", label: "Studio swap", supported: true, availabilityLabel: "Available when a verified route reports it" },
  { value: "relight", label: "AI relight", supported: true, availabilityLabel: "Available when a verified route reports it" },
  { value: "restyle", label: "Video restyle", supported: true, availabilityLabel: "Available when a verified route reports it" },
  {
    value: "wardrobe-change",
    label: "Outfit change",
    supported: false,
    availabilityLabel: "Future feature - not supported by any verified route yet",
  },
];

export const MOVIE_VIDEO_MAKEOVER_UI_PRESET_IDS = [
  "studio.clean-neutral",
  "studio.warm-key",
  "light.soft-key",
  "light.cinematic",
  "style.premium-grade",
] as const;

export type MovieVideoMakeoverEvidenceChoice = {
  readonly value: MovieVideoMakeoverEvidenceLabel;
  readonly label: string;
};

/** Mirrors the spike document's V/C/U/N evidence scale; only V admits. */
export const MOVIE_VIDEO_MAKEOVER_UI_EVIDENCE_CHOICES: readonly MovieVideoMakeoverEvidenceChoice[] = [
  { value: "verified", label: "Verified (documented + tested capability)" },
  { value: "vendor-claim", label: "Vendor claim only" },
  { value: "unverified", label: "Unverified" },
  { value: "unknown", label: "Unknown" },
];

export const MOVIE_VIDEO_MAKEOVER_UI_FEATURE_LABELS: Readonly<Record<MovieVideoMakeoverFeatureKey, string>> = {
  "edit-in-place": "In-place video edit",
  "region-window-edit": "Region window edit",
  "source-audio-passthrough": "Source audio passthrough",
  "identity-preserve": "Identity preservation",
  "frame-rate-conform": "Frame rate conform",
  "wardrobe-transform": "Wardrobe transform",
};

/** One row of the capability panel; `selected` is a creator toggle, not a claim of support. */
export type MovieVideoMakeoverCapabilitySelection = {
  readonly feature: MovieVideoMakeoverFeatureKey;
  readonly selected: boolean;
  readonly evidenceLabel: MovieVideoMakeoverEvidenceLabel;
  readonly sourceUrl: string | null;
  readonly capturedAtUtc: string | null;
};

export type MovieVideoMakeoverSelection = {
  readonly mode: MovieVideoMakeoverUiMode | null;
  readonly presetId: string | null;
  readonly prompt: string;
  readonly sourceAssetId: string | null;
  readonly sourceOwnerPrincipalId: string | null;
  readonly sourceWorkspaceId: string | null;
  readonly sourceVisibilityPrivate: boolean;
  readonly sourceRetentionVerified: boolean;
  readonly sourceAudioPresent: boolean | null;
  readonly sourceDurationSeconds: number | null;
  readonly sourceWidth: number | null;
  readonly sourceHeight: number | null;
  readonly sourceFrameRate: number | null;
  readonly sourceSizeBytes: number | null;
  readonly preserveOriginalAudio: boolean;
  readonly preserveSubjectIdentity: boolean;
  readonly creatorApproved: boolean;
  readonly approvalReference: string | null;
  readonly capabilities: readonly MovieVideoMakeoverCapabilitySelection[];
};

export function createEmptyMovieVideoMakeoverSelection(): MovieVideoMakeoverSelection {
  return {
    mode: null,
    presetId: null,
    prompt: "",
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
    preserveOriginalAudio: false,
    preserveSubjectIdentity: false,
    creatorApproved: false,
    approvalReference: null,
    capabilities: [],
  };
}

export function isMovieVideoMakeoverUiMode(value: string): value is MovieVideoMakeoverUiMode {
  return (MOVIE_VIDEO_MAKEOVER_UI_MODE_CHOICES as readonly { value: string }[]).some((c) => c.value === value);
}

export function movieVideoMakeoverModeChoice(
  value: MovieVideoMakeoverUiMode | null,
): MovieVideoMakeoverModeChoice | null {
  if (value === null) {
    return null;
  }
  return MOVIE_VIDEO_MAKEOVER_UI_MODE_CHOICES.find((choice) => choice.value === value) ?? null;
}

/** Required features for the current selection; wardrobe (Phase B) has none to claim. */
export function movieVideoMakeoverSelectedRequiredFeatures(
  selection: MovieVideoMakeoverSelection,
): readonly MovieVideoMakeoverFeatureKey[] {
  if (selection.mode === null || selection.mode === "wardrobe-change") {
    return [];
  }
  const required = new Set<MovieVideoMakeoverFeatureKey>(
    MOVIE_VIDEO_MAKEOVER_MODE_REQUIREMENTS[selection.mode],
  );
  if (selection.preserveOriginalAudio && selection.sourceAudioPresent === true) {
    required.add("source-audio-passthrough");
  }
  return MOVIE_VIDEO_MAKEOVER_MODES
    .flatMap((m) => MOVIE_VIDEO_MAKEOVER_MODE_REQUIREMENTS[m])
    .filter((feature, index, all) => all.indexOf(feature) === index && required.has(feature));
}

/**
 * Turn panel selections into the shape `createMovieVideoMakeoverDraft` accepts.
 * A capability row that is not selected contributes no observation, so the
 * admission helper reports `capability_missing` honestly instead of a default.
 */
export function buildMovieVideoMakeoverDraftInput(
  selection: MovieVideoMakeoverSelection,
): MovieVideoMakeoverDraftInput {
  const capabilities: MovieVideoMakeoverCapabilityInput[] = selection.capabilities
    .filter((row) => row.selected)
    .map((row) => ({
      feature: row.feature,
      available: row.evidenceLabel !== "unknown",
      evidenceLabel: row.evidenceLabel,
      sourceUrl: row.sourceUrl,
      capturedAtUtc: row.capturedAtUtc,
    }));

  const hasSource = selection.sourceAssetId !== null && selection.sourceAssetId.trim() !== "";

  return {
    mode: selection.mode,
    presetId: selection.presetId,
    prompt: selection.prompt.trim() === "" ? null : selection.prompt.trim(),
    source: hasSource
      ? {
          assetId: selection.sourceAssetId,
          ownerPrincipalId: selection.sourceOwnerPrincipalId,
          workspaceId: selection.sourceWorkspaceId,
          visibility: selection.sourceVisibilityPrivate ? "private" : "unverified",
          retentionPolicyVerified: selection.sourceRetentionVerified,
          audioPresent: selection.sourceAudioPresent,
          durationSeconds: selection.sourceDurationSeconds,
          width: selection.sourceWidth,
          height: selection.sourceHeight,
          frameRate: selection.sourceFrameRate,
          sizeBytes: selection.sourceSizeBytes,
        }
      : {},
    consent: {
      creatorApproved: selection.creatorApproved,
      approvedByPrincipalId: selection.sourceOwnerPrincipalId,
      approvalReference: selection.approvalReference,
    },
    preservation: {
      preserveOriginalAudio: selection.preserveOriginalAudio,
      preserveSubjectIdentity: selection.preserveSubjectIdentity,
    },
    capabilities,
  };
}

export type MovieVideoMakeoverUiStatus = {
  readonly headline: string;
  readonly estimateButtonEnabledWording: string;
  readonly blockedFields: ReadonlySet<string>;
};

/**
 * Honest status copy derived from a fail-closed admission. The panel keeps the
 * `uiEnabled === false` invariant from the helper: no admission state here ever
 * enables an execution or estimate control, because none is wired.
 */
export function movieVideoMakeoverUiStatus(admission: MovieVideoMakeoverAdmission): MovieVideoMakeoverUiStatus {
  const blockedFields = new Set(admission.blockedReasons.map((reason) => reason.field));
  if (admission.status === "ready-for-estimate") {
    return {
      headline: "Draft complete - ready for a non-billable estimate when one is wired.",
      estimateButtonEnabledWording: "Estimate is not wired yet",
      blockedFields,
    };
  }
  return {
    headline: "Blocked until resolved (" + String(admission.blockedReasons.length) + " open item"
      + (admission.blockedReasons.length === 1 ? "" : "s") + ").",
    estimateButtonEnabledWording: "Resolve the items above first",
    blockedFields,
  };
}

export type { MovieVideoMakeoverDraft, MovieVideoMakeoverAdmission };
