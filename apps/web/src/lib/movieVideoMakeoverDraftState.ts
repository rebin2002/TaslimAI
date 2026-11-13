/**
 * Provider-neutral AI Video Makeover draft + admission state helper (issue #698).
 *
 * Scope and guarantees, stated deliberately narrow:
 *
 * 1. This module is a PURE, side-effect-free state helper. It performs no I/O,
 *    imports nothing, calls no provider, reserves no credit, creates no job, and
 *    enables no user interface. Every admission result it can produce keeps
 *    `launchPermitted`, `providerJobCreated`, `billable` and `uiEnabled` pinned to
 *    the literal type `false`, so no caller can widen a draft into an execution
 *    grant by accident or by casting the object literal.
 * 2. It is FAIL-CLOSED. Anything missing, out of bounds, unowned, unapproved, or
 *    carrying an evidence label weaker than `verified` returns a typed blocked
 *    reason. It never infers support, never defaults a capability to available,
 *    and never claims that execution is possible.
 * 3. Capability vocabulary intentionally reuses the kebab-case feature keys and
 *    evidence labels established by the #698 spike document and the Phase 1
 *    server-side capability vocabulary, so this client-side slice does not invent
 *    a parallel naming scheme.
 * 4. Wardrobe / outfit change (issue #698 Phase B item 5) stays an unsupported
 *    future feature. It is named here only so that it can be blocked honestly; no
 *    code path in this module can mark it supported.
 * 5. Preview and cost estimation are NOT launch. A `ready-for-estimate` result
 *    only authorises a non-billable estimate request. Actual launch requires the
 *    server-side cost firewall, authorization and confirmation flow, which lives
 *    outside this module and is never asserted here.
 */

/** Schema version of the draft shape; bumped only on a breaking change. */
export const MOVIE_VIDEO_MAKEOVER_DRAFT_SCHEMA_VERSION = 1;

/**
 * Phase A modes. Anything outside this list is not a makeover mode this helper
 * will admit, including the Phase B wardrobe request.
 */
export const MOVIE_VIDEO_MAKEOVER_MODES = ["studio-swap", "relight", "restyle"] as const;

export type MovieVideoMakeoverMode = (typeof MOVIE_VIDEO_MAKEOVER_MODES)[number];

/**
 * Named explicitly as unsupported-future rather than silently dropped, so a
 * wardrobe request produces an honest blocked reason instead of a fallback.
 */
export const MOVIE_VIDEO_MAKEOVER_UNSUPPORTED_FUTURE_MODES = ["wardrobe-change", "outfit-change"] as const;

export type MovieVideoMakeoverUnsupportedFutureMode =
  (typeof MOVIE_VIDEO_MAKEOVER_UNSUPPORTED_FUTURE_MODES)[number];

/** Feature keys shared with the #698 capability vocabulary. */
export const MOVIE_VIDEO_MAKEOVER_FEATURE_KEYS = [
  "edit-in-place",
  "region-window-edit",
  "source-audio-passthrough",
  "identity-preserve",
  "frame-rate-conform",
  "wardrobe-transform",
] as const;

export type MovieVideoMakeoverFeatureKey = (typeof MOVIE_VIDEO_MAKEOVER_FEATURE_KEYS)[number];

/**
 * Evidence labels, mirroring the spike document's V/C/U/N scale. Only `verified`
 * (documented by the provider and confirmed by a tested capability) can admit a
 * feature; `vendor-claim`, `unverified` and `unknown` always block.
 */
export const MOVIE_VIDEO_MAKEOVER_EVIDENCE_LABELS = ["verified", "vendor-claim", "unverified", "unknown"] as const;

export type MovieVideoMakeoverEvidenceLabel = (typeof MOVIE_VIDEO_MAKEOVER_EVIDENCE_LABELS)[number];

/** Bounded input limits. Provider-neutral; no route inherits a larger value here. */
export const MOVIE_VIDEO_MAKEOVER_INPUT_LIMITS = {
  maxAssetIdLength: 120,
  maxPrincipalIdLength: 120,
  maxWorkspaceIdLength: 120,
  maxApprovalRefLength: 200,
  maxPresetIdLength: 120,
  maxPromptLength: 2000,
  maxSourceBytes: 5368709120,
  minDurationSeconds: 1,
  maxDurationSeconds: 3600,
  minDimension: 1,
  maxDimension: 16384,
  minFrameRate: 1,
  maxFrameRate: 240,
  maxCapabilities: 32,
  maxProvenanceUrlLength: 512,
} as const;

/**
 * Features required per mode before any estimate may be requested. `identity-preserve`
 * is mandatory for every mode because issue #698 forbids identity substitution;
 * `source-audio-passthrough` is added conditionally when the draft asks to keep the
 * original audio of a clip that actually has audio.
 */
export const MOVIE_VIDEO_MAKEOVER_MODE_REQUIREMENTS:
  Readonly<Record<MovieVideoMakeoverMode, readonly MovieVideoMakeoverFeatureKey[]>> = {
  "studio-swap": ["edit-in-place", "identity-preserve"],
  relight: ["edit-in-place", "identity-preserve"],
  restyle: ["edit-in-place", "identity-preserve", "frame-rate-conform"],
};

export type MovieVideoMakeoverBlockCode =
  | "mode_missing"
  | "mode_unsupported"
  | "wardrobe_unsupported_future_feature"
  | "preset_missing"
  | "preset_invalid"
  | "prompt_invalid"
  | "source_missing"
  | "source_reference_invalid"
  | "source_ownership_unverified"
  | "source_visibility_not_private"
  | "source_retention_unverified"
  | "source_metadata_missing"
  | "source_metadata_out_of_bounds"
  | "consent_missing"
  | "consent_reference_invalid"
  | "consent_principal_mismatch"
  | "identity_preservation_required"
  | "source_audio_state_unknown"
  | "capability_input_invalid"
  | "capability_missing"
  | "capability_unknown"
  | "capability_unverified"
  | "capability_unavailable";

export type MovieVideoMakeoverBlockReason = {
  readonly code: MovieVideoMakeoverBlockCode;
  readonly field: string;
  readonly message: string;
};

/** A private, owned source clip plus the metadata needed to bound the draft. */
export type MovieVideoMakeoverSourceInput = {
  readonly assetId?: string | null;
  readonly ownerPrincipalId?: string | null;
  readonly workspaceId?: string | null;
  readonly visibility?: string | null;
  readonly retentionPolicyVerified?: boolean | null;
  readonly durationSeconds?: number | null;
  readonly width?: number | null;
  readonly height?: number | null;
  readonly frameRate?: number | null;
  readonly audioPresent?: boolean | null;
  readonly sizeBytes?: number | null;
};

/** Explicit creator approval; never inferred from a draft being opened. */
export type MovieVideoMakeoverConsentInput = {
  readonly creatorApproved?: boolean | null;
  readonly approvedByPrincipalId?: string | null;
  readonly approvalReference?: string | null;
};

/** Preservation flags, declared by the creator rather than assumed. */
export type MovieVideoMakeoverPreservationInput = {
  readonly preserveOriginalAudio?: boolean | null;
  readonly preserveSubjectIdentity?: boolean | null;
};

/** One capability observation for one feature key, with mandatory provenance. */
export type MovieVideoMakeoverCapabilityInput = {
  readonly feature?: string | null;
  readonly available?: boolean | null;
  readonly evidenceLabel?: string | null;
  readonly sourceUrl?: string | null;
  readonly capturedAtUtc?: string | null;
};

export type MovieVideoMakeoverDraftInput = {
  readonly mode?: string | null;
  readonly presetId?: string | null;
  readonly prompt?: string | null;
  readonly source?: MovieVideoMakeoverSourceInput | null;
  readonly consent?: MovieVideoMakeoverConsentInput | null;
  readonly preservation?: MovieVideoMakeoverPreservationInput | null;
  readonly capabilities?: readonly MovieVideoMakeoverCapabilityInput[] | null;
};

/** Normalized draft. Raw strings are retained so admission stays deterministic. */
export type MovieVideoMakeoverDraft = {
  readonly schemaVersion: number;
  readonly mode: string | null;
  readonly presetId: string | null;
  readonly prompt: string | null;
  readonly source: MovieVideoMakeoverSourceInput;
  readonly consent: MovieVideoMakeoverConsentInput;
  readonly preservation: MovieVideoMakeoverPreservationInput;
  readonly capabilities: readonly MovieVideoMakeoverCapabilityInput[];
};

/** Fields that stay false on every branch; the non-billable invariant. */
export type MovieVideoMakeoverSafetyFlags = {
  readonly launchPermitted: false;
  readonly providerJobCreated: false;
  readonly billable: false;
  readonly uiEnabled: false;
};

export type MovieVideoMakeoverAdmission =
  | (MovieVideoMakeoverSafetyFlags & {
      readonly status: "ready-for-estimate";
      readonly mode: MovieVideoMakeoverMode;
      readonly requiredFeatures: readonly MovieVideoMakeoverFeatureKey[];
      readonly previewPermitted: true;
      readonly estimatePermitted: true;
      readonly blockedReasons: readonly [];
    })
  | (MovieVideoMakeoverSafetyFlags & {
      readonly status: "blocked";
      readonly mode: MovieVideoMakeoverMode | null;
      readonly requiredFeatures: readonly MovieVideoMakeoverFeatureKey[];
      readonly previewPermitted: false;
      readonly estimatePermitted: false;
      readonly blockedReasons: readonly MovieVideoMakeoverBlockReason[];
    });

function text(value: string | null | undefined, max: number): string | null {
  if (typeof value !== "string") {
    return null;
  }
  const trimmed = value.trim();
  if (trimmed.length === 0 || trimmed.length > max) {
    return null;
  }
  // Control characters would leak into labels and logs; reject rather than strip.
  for (let index = 0; index < trimmed.length; index += 1) {
    const code = trimmed.charCodeAt(index);
    if (code < 32 || code === 127) {
      return null;
    }
  }
  return trimmed;
}

function boundedPositive(value: number | null | undefined, min: number, max: number): boolean {
  return typeof value === "number" && Number.isFinite(value) && value >= min && value <= max;
}

/** Trim only; admission owns the bounded-length decision so it can report it. */
function rawText(value: string | null | undefined): string | null {
  if (typeof value !== "string") {
    return null;
  }
  const trimmed = value.trim();
  return trimmed.length === 0 ? null : trimmed;
}

function push(
  reasons: MovieVideoMakeoverBlockReason[],
  code: MovieVideoMakeoverBlockCode,
  field: string,
  message: string,
): void {
  if (reasons.some((reason) => reason.code === code && reason.field === field)) {
    return;
  }
  reasons.push({ code, field, message });
}

/** Narrow a raw mode string to a supported Phase A mode. */
export function isMovieVideoMakeoverMode(value: string | null | undefined): value is MovieVideoMakeoverMode {
  return typeof value === "string"
    && (MOVIE_VIDEO_MAKEOVER_MODES as readonly string[]).includes(value);
}

/** True when the raw mode names the Phase B wardrobe request. */
export function isMovieVideoMakeoverUnsupportedFutureMode(
  value: string | null | undefined,
): value is MovieVideoMakeoverUnsupportedFutureMode {
  return typeof value === "string"
    && (MOVIE_VIDEO_MAKEOVER_UNSUPPORTED_FUTURE_MODES as readonly string[]).includes(value);
}

/**
 * Normalize a draft without judging it. Normalization is deliberately forgiving
 * about invalid values so that admission, not normalization, owns the fail-closed
 * decision and the caller can show per-field blocked reasons.
 */
export function createMovieVideoMakeoverDraft(input: MovieVideoMakeoverDraftInput = {}): MovieVideoMakeoverDraft {
  const capabilities = Array.isArray(input.capabilities) ? input.capabilities.slice() : [];
  return {
    schemaVersion: MOVIE_VIDEO_MAKEOVER_DRAFT_SCHEMA_VERSION,
    mode: rawText(input.mode),
    presetId: rawText(input.presetId),
    prompt: rawText(input.prompt),
    source: { ...(input.source ?? {}) },
    consent: { ...(input.consent ?? {}) },
    preservation: { ...(input.preservation ?? {}) },
    capabilities,
  };
}

function resolveRequiredFeatures(
  mode: MovieVideoMakeoverMode,
  source: MovieVideoMakeoverSourceInput,
  preservation: MovieVideoMakeoverPreservationInput,
): readonly MovieVideoMakeoverFeatureKey[] {
  const required = new Set<MovieVideoMakeoverFeatureKey>(MOVIE_VIDEO_MAKEOVER_MODE_REQUIREMENTS[mode]);
  if (preservation.preserveOriginalAudio === true && source.audioPresent === true) {
    required.add("source-audio-passthrough");
  }
  // Ordered output keeps the admission result byte-for-byte deterministic.
  return MOVIE_VIDEO_MAKEOVER_FEATURE_KEYS.filter((key) => required.has(key));
}

/**
 * Fail-closed admission of a Video Makeover draft.
 *
 * Returns either a `ready-for-estimate` admission, which authorises only a
 * non-billable estimate and a preview, or a `blocked` admission carrying every
 * typed reason found. Both branches keep the safety flags at the literal `false`.
 */
export function admitMovieVideoMakeoverDraft(draft: MovieVideoMakeoverDraft): MovieVideoMakeoverAdmission {
  const reasons: MovieVideoMakeoverBlockReason[] = [];
  const limits = MOVIE_VIDEO_MAKEOVER_INPUT_LIMITS;

  // --- 1. Mode -------------------------------------------------------------
  let mode: MovieVideoMakeoverMode | null = null;
  if (draft.mode === null || draft.mode === "") {
    push(reasons, "mode_missing", "mode", "Select studio swap, relight, or restyle.");
  } else if (isMovieVideoMakeoverUnsupportedFutureMode(draft.mode)) {
    push(
      reasons,
      "wardrobe_unsupported_future_feature",
      "mode",
      "Wardrobe change is a future feature and is not supported by any verified route.",
    );
  } else if (isMovieVideoMakeoverMode(draft.mode)) {
    mode = draft.mode;
  } else {
    push(reasons, "mode_unsupported", "mode", "This makeover mode is not supported.");
  }

  // --- 2. Preset and prompt ------------------------------------------------
  if (draft.presetId === null) {
    push(reasons, "preset_missing", "presetId", "Select a preset for this makeover.");
  } else if (!/^[a-z0-9][a-z0-9._:-]{1,119}$/.test(draft.presetId)) {
    push(reasons, "preset_invalid", "presetId", "Preset identifier is not a safe bounded identifier.");
  }
  if (draft.prompt !== null && draft.prompt.length > limits.maxPromptLength) {
    push(reasons, "prompt_invalid", "prompt", "Optional prompt exceeds the bounded length.");
  }

  // --- 3. Source: owned, private, retained, bounded metadata ---------------
  const source = draft.source;
  const assetId = text(source.assetId, limits.maxAssetIdLength);
  const ownerPrincipalId = text(source.ownerPrincipalId, limits.maxPrincipalIdLength);
  const workspaceId = text(source.workspaceId, limits.maxWorkspaceIdLength);

  if (source.assetId === undefined || source.assetId === null || source.assetId.trim() === "") {
    push(reasons, "source_missing", "source.assetId", "Attach the source video to makeover.");
  } else if (assetId === null) {
    push(reasons, "source_reference_invalid", "source.assetId", "Source video identifier is invalid.");
  }
  if (source.assetId !== undefined && source.assetId !== null && source.assetId.trim() !== "") {
    if (ownerPrincipalId === null || workspaceId === null) {
      push(reasons, "source_ownership_unverified", "source.ownerPrincipalId", "Source ownership is not verified.");
    }
    if (source.visibility !== "private") {
      push(reasons, "source_visibility_not_private", "source.visibility", "Source video must be private to its owner.");
    }
    if (source.retentionPolicyVerified !== true) {
      push(reasons, "source_retention_unverified", "source.retentionPolicyVerified",
        "Source retention policy has not been verified.");
    }
    const missingMetadata =
      source.durationSeconds === undefined || source.durationSeconds === null
      || source.width === undefined || source.width === null
      || source.height === undefined || source.height === null
      || source.frameRate === undefined || source.frameRate === null
      || source.sizeBytes === undefined || source.sizeBytes === null
      ;
    if (missingMetadata) {
      push(reasons, "source_metadata_missing", "source", "Source video metadata is incomplete.");
    } else if (
      !boundedPositive(source.durationSeconds, limits.minDurationSeconds, limits.maxDurationSeconds)
      || !boundedPositive(source.width, limits.minDimension, limits.maxDimension)
      || !boundedPositive(source.height, limits.minDimension, limits.maxDimension)
      || !boundedPositive(source.frameRate, limits.minFrameRate, limits.maxFrameRate)
      || !boundedPositive(source.sizeBytes, 1, limits.maxSourceBytes)
    ) {
      push(reasons, "source_metadata_out_of_bounds", "source", "Source video metadata is out of the allowed bounds.");
    }
  }

  // --- 4. Creator consent: explicit, owned, referenced ---------------------
  const consent = draft.consent;
  if (consent.creatorApproved !== true) {
    push(reasons, "consent_missing", "consent.creatorApproved",
      "Explicit creator approval is required before any estimate.");
  } else {
    const approver = text(consent.approvedByPrincipalId, limits.maxPrincipalIdLength);
    if (approver === null) {
      push(reasons, "consent_reference_invalid", "consent.approvedByPrincipalId",
        "Approval must name the approving creator.");
    } else if (ownerPrincipalId !== null && approver !== ownerPrincipalId) {
      push(reasons, "consent_principal_mismatch", "consent.approvedByPrincipalId",
        "Approval principal does not own the source video.");
    }
    if (text(consent.approvalReference, limits.maxApprovalRefLength) === null) {
      push(reasons, "consent_reference_invalid", "consent.approvalReference",
        "Approval needs a bounded reference to the record it came from.");
    }
  }

  // --- 5. Preservation flags ----------------------------------------------
  const preservation = draft.preservation;
  if (preservation.preserveSubjectIdentity !== true) {
    push(reasons, "identity_preservation_required", "preservation.preserveSubjectIdentity",
      "Subject identity must be preserved; identity substitution is out of scope.");
  }
  if (preservation.preserveOriginalAudio === true && source.audioPresent === null
    && source.assetId !== undefined && source.assetId !== null && source.assetId.trim() !== "") {
    push(reasons, "source_audio_state_unknown", "source.audioPresent",
      "Whether the source has audio is unknown; audio preservation cannot be claimed.");
  }

  // --- 6. Capabilities: verified or nothing --------------------------------
  if (draft.capabilities.length > limits.maxCapabilities) {
    push(reasons, "capability_input_invalid", "capabilities", "Too many capability observations in one draft.");
  }
  const observations = new Map<string, MovieVideoMakeoverCapabilityInput>();
  draft.capabilities.forEach((observation, index) => {
    const field = "capabilities[" + String(index) + "]";
    const feature = typeof observation.feature === "string" ? observation.feature.trim() : "";
    if (feature.length === 0 || feature.length > 80) {
      push(reasons, "capability_input_invalid", field + ".feature", "Capability feature key is invalid.");
      return;
    }
    if (!(MOVIE_VIDEO_MAKEOVER_FEATURE_KEYS as readonly string[]).includes(feature)) {
      push(reasons, "capability_unknown", field + ".feature",
        "Unknown capability key cannot be treated as support.");
      return;
    }
    if (feature === "wardrobe-transform") {
      push(reasons, "wardrobe_unsupported_future_feature", field + ".feature",
        "Wardrobe transformation is a future feature and cannot be admitted.");
      return;
    }
    if (observations.has(feature)) {
      push(reasons, "capability_input_invalid", field + ".feature",
        "Duplicate capability observation for the same feature.");
      return;
    }
    observations.set(feature, observation);
  });

  const requiredFeatures = mode === null ? [] : resolveRequiredFeatures(mode, source, preservation);
  for (const feature of requiredFeatures) {
    const field = "capabilities." + feature;
    const observation = observations.get(feature);
    if (observation === undefined) {
      push(reasons, "capability_missing", field,
        "No capability observation was supplied for a required feature.");
      continue;
    }
    if (observation.available !== true) {
      push(reasons, "capability_unavailable", field, "The route reports this feature as unavailable.");
      continue;
    }
    if (observation.evidenceLabel !== "verified") {
      push(reasons, "capability_unverified", field,
        "This feature is not verified by documentation and a tested capability.");
      continue;
    }
    if (text(observation.sourceUrl, limits.maxProvenanceUrlLength) === null
      || text(observation.capturedAtUtc, 40) === null) {
      push(reasons, "capability_unverified", field,
        "A verified feature still needs citable provenance and a capture date.");
    }
  }

  const safety: MovieVideoMakeoverSafetyFlags = {
    launchPermitted: false,
    providerJobCreated: false,
    billable: false,
    uiEnabled: false,
  };

  if (mode === null || reasons.length > 0) {
    return {
      ...safety,
      status: "blocked",
      mode,
      requiredFeatures,
      previewPermitted: false,
      estimatePermitted: false,
      blockedReasons: reasons,
    };
  }

  return {
    ...safety,
    status: "ready-for-estimate",
    mode,
    requiredFeatures,
    previewPermitted: true,
    estimatePermitted: true,
    blockedReasons: [],
  };
}

/**
 * Convenience for callers that only need the honest answer to "may this draft be
 * estimated?". It never answers whether a job may run.
 */
export function movieVideoMakeoverDraftMayRequestEstimate(draft: MovieVideoMakeoverDraft): boolean {
  return admitMovieVideoMakeoverDraft(draft).status === "ready-for-estimate";
}
