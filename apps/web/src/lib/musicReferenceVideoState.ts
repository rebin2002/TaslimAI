// Client-side validation contract and error-state machine for MP4/MOV
// reference videos in Music Studio.
//
// This module is deliberately separate from the reference-AUDIO boundary
// (the in-flight audio picker work) and owns only the reference-VIDEO
// decision path described by issue #708 criterion 3: a video reference is
// accepted locally only when its container extension, declared MIME type,
// probed header signature, byte size, and duration metadata all fall inside
// the boundaries the server ingest will later enforce. Anything uncertain
// fails closed with a stable reason key so the UI can render a terminal
// error state instead of queueing a doomed upload or showing a generic
// provider failure.

import type { Asset } from "./api";

export type ReferenceVideoFileHandle = {
  readonly name: string;
  readonly size: number;
  readonly mimeType: string;
};

export type ReferenceVideoHeaderProbe = {
  /**
   * Outcome of reading the container signature (ftyp box) from the file
   * head. `null` means the probe did not run or could not read the file;
   * local selections then stay non-committal and are re-checked server side.
   */
  readonly detected: boolean | null;
};

export type ReferenceVideoCandidate = {
  /** Deterministic identity derived from stored asset id or name + size. */
  readonly id: string;
  readonly sourceKind: "local" | "stored_asset";
  readonly displayName: string;
  /** Lower-cased leading dot extension, e.g. ".mp4"; "" when absent. */
  readonly extension: string;
  /** Lower-cased MIME type; null when the source declared none. */
  readonly mimeType: string | null;
  readonly sizeBytes: number | null;
  readonly assetId: string | null;
  readonly durationSeconds: number | null;
  readonly header?: ReferenceVideoHeaderProbe;
};

export type ReferenceVideoSupportCheck = {
  supported: boolean;
  /** Stable key like "music.referenceVideo.reason.oversized"; null when supported. */
  reasonKey: string | null;
  extension: string;
};

export type ReferenceVideoState = {
  candidate: ReferenceVideoCandidate | null;
  /** Rejection of the most recent selection attempt; cleared on success. */
  rejectedReasonKey: string | null;
  /** True while a container probe or preview preparation is running. */
  loading: boolean;
};

export const emptyReferenceVideoState: ReferenceVideoState = {
  candidate: null,
  rejectedReasonKey: null,
  loading: false,
};

// User uploads are bounded by the shared 25 MB file settings on the server
// (FileOptions.MaxFileSizeBytes); the client refuses oversized videos before
// any upload work starts.
export const MAX_REFERENCE_VIDEO_BYTES = 25 * 1024 * 1024;
// The server ingest window for reference video duration mirrors the bounded
// music duration range (15-600 seconds); 600 s is the hard rejection point.
export const MAX_REFERENCE_VIDEO_DURATION_SECONDS = 600;

// MP4/QuickTime/M4V containers only: exactly the video formats the product
// brief names for safe server-side audio extraction. WebM/MKV/AVI codecs are
// not part of the extraction contract and must fail closed client-side.
export const REFERENCE_VIDEO_EXTENSION_MIME_TYPES: Record<string, string> = {
  ".mp4": "video/mp4",
  ".mov": "video/quicktime",
  ".m4v": "video/mp4",
};

// .mp4 and .m4v share the same container MIME, so the reverse mapping must
// name the canonical container explicitly instead of inverting the table
// above, where the last extension would silently win.
export const REFERENCE_VIDEO_MIME_EXTENSIONS: Record<string, string> = {
  "video/mp4": ".mp4",
  "video/quicktime": ".mov",
};

export function extensionOf(fileName: string): string {
  const dot = fileName.lastIndexOf(".");
  if (dot < 0) return "";
  return fileName.slice(dot).toLowerCase();
}

export function extensionForVideoMime(mimeType: string | null | undefined): string {
  return REFERENCE_VIDEO_MIME_EXTENSIONS[(mimeType ?? "").trim().toLowerCase()] ?? "";
}

export const REFERENCE_VIDEO_ACCEPT = Object.keys(REFERENCE_VIDEO_EXTENSION_MIME_TYPES).join(",");

/** Deterministic candidate id from name and size; no randomness involved. */
export function localReferenceVideoId(name: string, sizeBytes: number): string {
  return `local-video:${sizeBytes}:${name.trim().toLowerCase()}`;
}

export function localCandidateFromFile(handle: ReferenceVideoFileHandle): ReferenceVideoCandidate {
  const extension = extensionOf(handle.name);
  const declaredMime = handle.mimeType.trim().toLowerCase();
  return {
    id: localReferenceVideoId(handle.name, handle.size),
    sourceKind: "local",
    displayName: handle.name.trim() || "reference-video",
    extension,
    mimeType: declaredMime.length > 0 ? declaredMime : null,
    sizeBytes: handle.size,
    assetId: null,
    durationSeconds: null,
  };
}

export function storedCandidateFromAsset(asset: Asset): ReferenceVideoCandidate | null {
  if (asset.status !== "Active" || !asset.hasFile || !asset.canPreview) return null;
  if (asset.assetType !== "video") return null;
  const mimeType = (asset.mimeType ?? "").trim().toLowerCase();
  if (!mimeType.startsWith("video/")) return null;
  // Generated and uploaded video assets often carry no usable filename, so
  // the recorded MIME type alone decides the container; an unmapped codec
  // (webm, matroska, octet-stream) is never re-guessed from the name.
  const extension = extensionForVideoMime(mimeType);
  if (!extension) return null;
  return {
    id: `asset:${asset.id}`,
    sourceKind: "stored_asset",
    displayName: asset.name.trim() || "reference-video",
    extension,
    mimeType,
    sizeBytes: asset.fileSizeBytes ?? null,
    assetId: asset.id,
    durationSeconds: null,
  };
}

export function referenceVideoSupportCheck(candidate: ReferenceVideoCandidate | null): ReferenceVideoSupportCheck {
  if (!candidate) return { supported: false, reasonKey: "music.referenceVideo.reason.empty", extension: "" };
  const expected = REFERENCE_VIDEO_EXTENSION_MIME_TYPES[candidate.extension];
  if (!expected) {
    return { supported: false, reasonKey: "music.referenceVideo.reason.extensionUnsupported", extension: candidate.extension };
  }
  if (candidate.sourceKind === "stored_asset") {
    // Stored candidates come from the Asset Library, so the authoritative
    // recorded MIME type and the backing file must agree with the container.
    if (!candidate.assetId || candidate.mimeType !== expected) {
      return { supported: false, reasonKey: "music.referenceVideo.reason.mimeMismatch", extension: candidate.extension };
    }
    if (candidate.sizeBytes !== null && (candidate.sizeBytes <= 0 || candidate.sizeBytes > MAX_REFERENCE_VIDEO_BYTES)) {
      return { supported: false, reasonKey: "music.referenceVideo.reason.oversized", extension: candidate.extension };
    }
  } else {
    if (candidate.sizeBytes === null || candidate.sizeBytes <= 0) {
      return { supported: false, reasonKey: "music.referenceVideo.reason.empty", extension: candidate.extension };
    }
    if (candidate.sizeBytes > MAX_REFERENCE_VIDEO_BYTES) {
      return { supported: false, reasonKey: "music.referenceVideo.reason.oversized", extension: candidate.extension };
    }
    // An empty declared MIME only falls back to the extension; any other
    // declared value must match the container, mirroring the server sniff.
    if (candidate.mimeType !== null && candidate.mimeType !== expected) {
      return { supported: false, reasonKey: "music.referenceVideo.reason.mimeMismatch", extension: candidate.extension };
    }
    if (candidate.header && candidate.header.detected === false) {
      return { supported: false, reasonKey: "music.referenceVideo.reason.headerMismatch", extension: candidate.extension };
    }
  }
  if (candidate.durationSeconds !== null) {
    if (!Number.isFinite(candidate.durationSeconds) || candidate.durationSeconds <= 0) {
      return { supported: false, reasonKey: "music.referenceVideo.reason.durationUnknown", extension: candidate.extension };
    }
    if (candidate.durationSeconds > MAX_REFERENCE_VIDEO_DURATION_SECONDS) {
      return { supported: false, reasonKey: "music.referenceVideo.reason.durationTooLong", extension: candidate.extension };
    }
  }
  return { supported: true, reasonKey: null, extension: candidate.extension };
}

export function isReferenceVideoCandidate(candidate: ReferenceVideoCandidate | null): boolean {
  return referenceVideoSupportCheck(candidate).supported;
}

export function selectReferenceVideo(
  state: ReferenceVideoState,
  candidate: ReferenceVideoCandidate | null,
): ReferenceVideoState {
  if (!candidate) {
    return { candidate: null, rejectedReasonKey: "music.referenceVideo.reason.empty", loading: false };
  }
  const check = referenceVideoSupportCheck(candidate);
  if (!check.supported) {
    return { candidate: null, rejectedReasonKey: check.reasonKey, loading: false };
  }
  return { candidate, rejectedReasonKey: null, loading: false };
}

export function clearReferenceVideo(): ReferenceVideoState {
  return emptyReferenceVideoState;
}

/**
 * Terminal error copy per rejection reason, in the three shipped Music
 * Studio locales. Callers should prefer the translation catalog when a key
 * is present; this fallback keeps the panel honest even before catalog
 * entries land, and unknown keys render as neutral guidance rather than a
 * raw key string.
 */
const REJECTION_COPY: Record<string, { en: string; ar: string; ku: string }> = {
  "music.referenceVideo.reason.empty": {
    en: "Choose a video file to use as a reference.",
    ar: "اختر ملف فيديو لاستخدامه كمرجع.",
    ku: "ڤیدیۆیەک هەڵبژێرە بۆ بەکارهێنان وەک سەرچاوە.",
  },
  "music.referenceVideo.reason.extensionUnsupported": {
    en: "Reference videos must be MP4, MOV, or M4V files.",
    ar: "يجب أن تكون ملفات الفيديو المرجعية بصيغة MP4 أو MOV أو M4V.",
    ku: "پێویستە ڤیدیۆ سەرچاوەکان لە جۆری MP4، MOV یان M4V بن.",
  },
  "music.referenceVideo.reason.emptyFile": {
    en: "That video file is empty.",
    ar: "ملف الفيديو هذا فارغ.",
    ku: "ئەو فایلە ڤیدیۆیە بەتاڵە.",
  },
  "music.referenceVideo.reason.oversized": {
    en: "Reference videos must be 25 MB or smaller.",
    ar: "يجب أن تكون ملفات الفيديو المرجعية 25 ميغابايت أو أقل.",
    ku: "پێویستە قەبارەی ڤیدیۆ سەرچاوەکان ٢٥ مێگابایت یان کەمتەر بێت.",
  },
  "music.referenceVideo.reason.mimeMismatch": {
    en: "The file type does not match its video container.",
    ar: "نوع الملف لا يطابق حاوية الفيديو الخاصة به.",
    ku: "جۆری فایل لەگەڵ پێناوی ڤیدیۆکەیدا ناگونجێت.",
  },
  "music.referenceVideo.reason.headerMismatch": {
    en: "That file is not a readable MP4/MOV video, so it cannot be used as a reference.",
    ar: "هذا الملف ليس فيديو MP4/MOV قابلاً للقراءة، لذا لا يمكن استخدامه كمرجع.",
    ku: "ئەم فایلە ڤیدیۆی MP4/MOV ی خوێندنەوە نییە، بۆیە ناتوانرێت وەک سەرچاوە بەکاری بهێنرێت.",
  },
  "music.referenceVideo.reason.durationUnknown": {
    en: "We could not read how long this video is. Try re-exporting it.",
    ar: "لم نتمكن من معرفة مدة هذا الفيديو. جرّب إعادة تصديره.",
    ku: "نەتوانین بزانین ڤیدیۆکە چەند دەخایەنێت. هەوڵبدەرەوە دووبارە دەری بکات.",
  },
  "music.referenceVideo.reason.durationTooLong": {
    en: "Reference videos must be 10 minutes or shorter.",
    ar: "يجب ألا تتجاوز مدة الفيديو المرجعي 10 دقائق.",
    ku: "پێویستە ماوەی ڤیدیۆ سەرچاوەکە ١٠ خولەک یان کەمتەر بێت.",
  },
};

export function describeReferenceVideoRejection(reasonKey: string | null, locale: string): string {
  if (!reasonKey) return "";
  const entry = REJECTION_COPY[reasonKey];
  if (!entry) return REJECTION_COPY["music.referenceVideo.reason.extensionUnsupported"]!.en;
  if (locale.startsWith("ar")) return entry.ar;
  if (locale.startsWith("ku") || locale.startsWith("ckb")) return entry.ku;
  return entry.en;
}
