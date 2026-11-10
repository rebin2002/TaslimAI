// Pure, deterministic state helpers for the Music Studio reference audio
// selection and preview path. Nothing in this module contacts a music
// provider: candidates are validated locally or read from the private asset
// library, so selecting or previewing a reference clip never starts a paid
// generation. This module must stay free of browser-only module imports so
// its tests run identically in Node and in a page.
import type { Asset } from "./api";

/** Client-side cap mirroring the API upload limit (25 MB). */
export const MAX_REFERENCE_AUDIO_BYTES = 25 * 1024 * 1024;

/** Extension -> exact MIME expected from the picker, mirroring upload validation. */
export const REFERENCE_AUDIO_EXTENSION_MIME_TYPES: Readonly<Record<string, string>> = {
  ".mp3": "audio/mpeg",
  ".wav": "audio/wav",
  ".m4a": "audio/mp4",
  ".ogg": "audio/ogg",
};

/** `accept` attribute for the picker; keeps unsupported files out of the path. */
export const REFERENCE_AUDIO_ACCEPT = Object.keys(REFERENCE_AUDIO_EXTENSION_MIME_TYPES).join(",");

export type ReferenceAudioSourceKind = "local" | "stored_asset";

export type ReferenceAudioRejectionReason = "empty" | "extension_unsupported" | "mime_mismatch" | "oversized";

export type FileHandle = {
  name: string;
  size: number;
  /** Empty string when the browser did not declare a MIME type. */
  mimeType: string;
};

export type ReferenceAudioCandidate = {
  id: string;
  sourceKind: ReferenceAudioSourceKind;
  displayName: string;
  extension: string;
  mimeType: string | null;
  sizeBytes: number | null;
  /** Asset id used to build the authenticated inline preview URL. */
  assetId: string | null;
  /** Only meaningful for stored asset candidates. */
  durationSeconds: number | null;
};

export type ReferenceAudioSupportCheck = {
  supported: boolean;
  /** Stable i18n key under `music.reference.reason.*`, present when unsupported. */
  reasonKey: string | null;
  extension: string;
};

export type ReferenceAudioState = {
  candidate: ReferenceAudioCandidate | null;
  /** Blob object URL created for a local preview; null for stored assets. */
  previewObjectUrl: string | null;
  /** Rejection message key if the last selection attempt was refused. */
  rejectedReasonKey: string | null;
};

export const emptyReferenceAudioState: ReferenceAudioState = {
  candidate: null,
  previewObjectUrl: null,
  rejectedReasonKey: null,
};

export function extensionOf(fileName: string): string {
  const dot = fileName.lastIndexOf(".");
  if (dot < 0) return "";
  return fileName.slice(dot).toLowerCase();
}

// Generated music assets often have no filename extension, so stored
// candidates are keyed on the authoritative MIME type recorded by the API.
const mimeToExtension = new Map<string, string>(
  Object.entries(REFERENCE_AUDIO_EXTENSION_MIME_TYPES).map(([extension, mime]) => [mime, extension]),
);

export function extensionForAudioMime(mimeType: string | null | undefined): string {
  return mimeToExtension.get((mimeType ?? "").trim().toLowerCase()) ?? "";
}

/** Deterministic candidate id from name and size; no randomness involved. */
export function localReferenceAudioId(name: string, sizeBytes: number): string {
  return `local:${sizeBytes}:${name.trim().toLowerCase()}`;
}

export function localCandidateFromFile(handle: FileHandle): ReferenceAudioCandidate {
  const extension = extensionOf(handle.name);
  const declaredMime = handle.mimeType.trim().toLowerCase();
  return {
    id: localReferenceAudioId(handle.name, handle.size),
    sourceKind: "local",
    displayName: handle.name.trim() || "reference-audio",
    extension,
    mimeType: declaredMime.length > 0 ? declaredMime : null,
    sizeBytes: handle.size,
    assetId: null,
    durationSeconds: null,
  };
}

export function referenceAudioSupportCheck(candidate: ReferenceAudioCandidate | null): ReferenceAudioSupportCheck {
  if (!candidate) return { supported: false, reasonKey: "music.reference.reason.empty", extension: "" };
  const expected = REFERENCE_AUDIO_EXTENSION_MIME_TYPES[candidate.extension];
  if (!expected) return { supported: false, reasonKey: "music.reference.reason.extensionUnsupported", extension: candidate.extension };
  if (candidate.sizeBytes === null || candidate.sizeBytes <= 0) return { supported: false, reasonKey: "music.reference.reason.empty", extension: candidate.extension };
  if (candidate.sourceKind === "local" && candidate.sizeBytes > MAX_REFERENCE_AUDIO_BYTES) {
    return { supported: false, reasonKey: "music.reference.reason.oversized", extension: candidate.extension };
  }
  // Mirror the shared upload boundary: an audio MIME on a non-audio file (or a
  // non-audio MIME on an audio extension) is rejected before any preview work.
  if (candidate.sourceKind === "local" && candidate.mimeType !== null && candidate.mimeType !== expected) {
    return { supported: false, reasonKey: "music.reference.reason.mimeMismatch", extension: candidate.extension };
  }
  if (candidate.sourceKind === "stored_asset") {
    if (candidate.mimeType !== expected) return { supported: false, reasonKey: "music.reference.reason.mimeMismatch", extension: candidate.extension };
    if (!candidate.assetId) return { supported: false, reasonKey: "music.reference.reason.empty", extension: candidate.extension };
  }
  return { supported: true, reasonKey: null, extension: candidate.extension };
}

export function isReferenceAudioCandidate(candidate: ReferenceAudioCandidate | null): boolean {
  return referenceAudioSupportCheck(candidate).supported;
}

export function storedCandidateFromAsset(asset: Asset): ReferenceAudioCandidate | null {
  if (asset.status !== "Active" || !asset.hasFile || !asset.canPreview) return null;
  if (asset.assetType !== "music" && asset.assetType !== "audio") return null;
  const mimeType = (asset.mimeType ?? "").trim().toLowerCase();
  if (!mimeType.startsWith("audio/")) return null;
  const extension = extensionForAudioMime(mimeType) || extensionOf(asset.name);
  // Music-specific containers only; other audio codecs are not previewable
  // references here and never reach a provider.
  if (REFERENCE_AUDIO_EXTENSION_MIME_TYPES[extension] !== mimeType) return null;
  return {
    id: `asset:${asset.id}`,
    sourceKind: "stored_asset",
    displayName: asset.name,
    extension,
    mimeType,
    sizeBytes: asset.fileSizeBytes,
    assetId: asset.id,
    durationSeconds: null,
  };
}

/**
 * Unclaimed = not attached to any project. The workspace asset query is
 * already membership-scoped server-side, so only the caller's own private,
 * project-free audio assets surface here. Newest first, deterministic on
 * equal timestamps via id tie-break, bounded for the panel.
 */
export function selectUnclaimedReferenceAudioCandidates(assets: Asset[], limit = 20): ReferenceAudioCandidate[] {
  const seen = new Set<string>();
  const unclaimed: Array<{ candidate: ReferenceAudioCandidate; createdAt: number }> = [];
  for (const asset of assets) {
    if (asset.projectId !== null) continue;
    const candidate = storedCandidateFromAsset(asset);
    if (!candidate || seen.has(candidate.id)) continue;
    seen.add(candidate.id);
    unclaimed.push({ candidate, createdAt: Date.parse(asset.createdAt) || 0 });
  }
  // Newest first; the ascending id tie-break keeps the order stable for equal
  // timestamps and independent of the incoming array order.
  return unclaimed
    .sort((left, right) => right.createdAt - left.createdAt || left.candidate.id.localeCompare(right.candidate.id))
    .map((entry) => entry.candidate)
    .slice(0, Math.max(1, limit));
}

export function referenceAudioPreviewUrl(
  candidate: ReferenceAudioCandidate | null,
  assetFileUrl: (assetId: string, inline?: boolean) => string,
): string | null {
  if (!isReferenceAudioCandidate(candidate) || !candidate) return null;
  if (candidate.sourceKind === "stored_asset" && candidate.assetId) {
    return assetFileUrl(candidate.assetId, true);
  }
  return null;
}

/** Revokes a previously created object URL exactly once; never throws. */
export function revokeReferenceAudioObjectUrl(objectUrl: string | null): void {
  if (!objectUrl || !objectUrl.startsWith("blob:")) return;
  if (typeof URL === "undefined" || typeof URL.revokeObjectURL !== "function") return;
  URL.revokeObjectURL(objectUrl);
}

export type ReferenceAudioSelectionResult = {
  state: ReferenceAudioState;
  /** Object URL created for this selection, if any. */
  createdObjectUrl: string | null;
  /** Previous object URL that the caller must revoke, if any. */
  staleObjectUrl: string | null;
  selected: boolean;
  rejected: boolean;
};

/**
 * Single-selection reducer: selecting an unsupported candidate keeps the
 * previous selection untouched and only records the rejection; selecting the
 * current candidate again clears it; selecting a different supported
 * candidate replaces it.
 */
export function selectReferenceAudio(
  previous: ReferenceAudioState,
  candidate: ReferenceAudioCandidate | null,
  createObjectUrl: (candidate: ReferenceAudioCandidate) => string | null,
): ReferenceAudioSelectionResult {
  const staleObjectUrl = previous.previewObjectUrl;
  if (!candidate) {
    return { state: emptyReferenceAudioState, createdObjectUrl: null, staleObjectUrl, selected: false, rejected: false };
  }
  const check = referenceAudioSupportCheck(candidate);
  if (!check.supported) {
    return {
      state: { ...previous, rejectedReasonKey: check.reasonKey },
      createdObjectUrl: null,
      staleObjectUrl: null,
      selected: false,
      rejected: true,
    };
  }
  if (previous.candidate?.id === candidate.id) {
    return { state: emptyReferenceAudioState, createdObjectUrl: null, staleObjectUrl, selected: false, rejected: false };
  }
  if (candidate.sourceKind === "local") {
    const createdObjectUrl = createObjectUrl(candidate);
    return {
      state: { candidate, previewObjectUrl: createdObjectUrl, rejectedReasonKey: null },
      createdObjectUrl,
      staleObjectUrl,
      selected: true,
      rejected: false,
    };
  }
  return { state: { candidate, previewObjectUrl: null, rejectedReasonKey: null }, createdObjectUrl: null, staleObjectUrl, selected: true, rejected: false };
}

export function clearReferenceAudioSelection(previous: ReferenceAudioState): ReferenceAudioSelectionResult {
  return { state: emptyReferenceAudioState, createdObjectUrl: null, staleObjectUrl: previous.previewObjectUrl, selected: false, rejected: false };
}

export function formatReferenceAudioSize(sizeBytes: number | null): string | null {
  if (sizeBytes === null || !Number.isFinite(sizeBytes) || sizeBytes <= 0) return null;
  const mib = sizeBytes / (1024 * 1024);
  if (mib >= 10) return `${Math.round(mib)} MiB`;
  if (mib >= 1) return `${mib.toFixed(1)} MiB`;
  return `${Math.max(1, Math.round(sizeBytes / 1024))} KiB`;
}
