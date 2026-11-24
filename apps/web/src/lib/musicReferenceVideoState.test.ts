import { describe, expect, it } from "vitest";
import type { Asset } from "./api";
import {
  MAX_REFERENCE_VIDEO_BYTES,
  MAX_REFERENCE_VIDEO_DURATION_SECONDS,
  REFERENCE_VIDEO_ACCEPT,
  clearReferenceVideo,
  describeReferenceVideoRejection,
  emptyReferenceVideoState,
  extensionForVideoMime,
  extensionOf,
  isReferenceVideoCandidate,
  localCandidateFromFile,
  referenceVideoSupportCheck,
  selectReferenceVideo,
  storedCandidateFromAsset,
  type ReferenceVideoCandidate,
  type ReferenceVideoFileHandle,
} from "./musicReferenceVideoState";

function localFile(name: string, sizeBytes: number, mimeType: string): ReferenceVideoFileHandle {
  return { name, size: sizeBytes, mimeType };
}

function candidate(overrides: Partial<ReferenceVideoCandidate> = {}): ReferenceVideoCandidate {
  return {
    id: "local-video:1000:clip.mp4",
    sourceKind: "local",
    displayName: "clip.mp4",
    extension: ".mp4",
    mimeType: "video/mp4",
    sizeBytes: 1000,
    assetId: null,
    durationSeconds: null,
    ...overrides,
  };
}

function asset(overrides: Partial<Asset> = {}): Asset {
  return {
    id: "asset-1", workspaceId: "workspace-1", projectId: null, projectName: null,
    name: "reference.mp4", description: null, assetType: "video", mimeType: "video/mp4",
    status: "Active", hasFile: true, canPreview: true, fileSizeBytes: 1000,
    sourceStudio: null, sourceJobTitle: null, createdAt: "2026-01-01T00:00:00Z",
    updatedAt: "2026-01-01T00:00:00Z", archivedAt: null, representations: [], ...overrides,
  };
}

describe("reference video accept surface", () => {
  it("offers exactly the MP4/MOV containers the server extraction contract names", () => {
    expect(REFERENCE_VIDEO_ACCEPT).toBe(".mp4,.mov,.m4v");
  });

  it("maps container extensions to MIME types both ways without inventing codecs", () => {
    expect(extensionOf("Concert.MOV")).toBe(".mov");
    expect(extensionOf("no-extension")).toBe("");
    expect(extensionForVideoMime("video/quicktime")).toBe(".mov");
    expect(extensionForVideoMime("video/mp4")).toBe(".mp4");
    // .m4v shares the MP4 container MIME; the canonical container must win.
    expect(extensionForVideoMime("VIDEO/MP4 ")).toBe(".mp4");
    expect(extensionForVideoMime("video/webm")).toBe("");
    expect(extensionForVideoMime(null)).toBe("");
  });

  it("derives deterministic candidate ids from name and size only", () => {
    const first = localCandidateFromFile(localFile("Concert MP4", 42, "video/mp4"));
    const second = localCandidateFromFile(localFile("concert mp4", 42, "video/mp4"));
    expect(first.id).toBe(second.id);
    expect(localCandidateFromFile(localFile("concert.mp4", 43, "video/mp4")).id).not.toBe(first.id);
  });
});

describe("reference video support checks", () => {
  it("accepts clean MP4, QuickTime, and M4V selections", () => {
    expect(referenceVideoSupportCheck(candidate())).toEqual({ supported: true, reasonKey: null, extension: ".mp4" });
    expect(referenceVideoSupportCheck(candidate({ extension: ".mov", mimeType: "video/quicktime", displayName: "clip.mov" })).supported).toBe(true);
    expect(referenceVideoSupportCheck(candidate({ extension: ".m4v", displayName: "clip.m4v" })).supported).toBe(true);
    // Browsers report no MIME for some containers; the extension still stands.
    expect(referenceVideoSupportCheck(candidate({ mimeType: null })).supported).toBe(true);
    // An unreadable header probe (null) stays non-committal; the server re-checks.
    expect(referenceVideoSupportCheck(candidate({ header: { detected: null } })).supported).toBe(true);
    expect(isReferenceVideoCandidate(candidate())).toBe(true);
  });

  it("fails closed on missing or non-video extensions before any upload work", () => {
    expect(referenceVideoSupportCheck(null).reasonKey).toBe("music.referenceVideo.reason.empty");
    expect(referenceVideoSupportCheck(candidate({ extension: "" })).reasonKey).toBe("music.referenceVideo.reason.extensionUnsupported");
    for (const extension of [".avi", ".mkv", ".webm", ".mp3", ".m4a", ".wav", ".txt"]) {
      expect(referenceVideoSupportCheck(candidate({ extension, displayName: `clip${extension}` })).reasonKey, extension)
        .toBe("music.referenceVideo.reason.extensionUnsupported");
    }
  });

  it("rejects empty and oversized local selections against the shared upload boundary", () => {
    expect(referenceVideoSupportCheck(candidate({ sizeBytes: 0 })).reasonKey).toBe("music.referenceVideo.reason.empty");
    expect(referenceVideoSupportCheck(candidate({ sizeBytes: null })).reasonKey).toBe("music.referenceVideo.reason.empty");
    expect(referenceVideoSupportCheck(candidate({ sizeBytes: MAX_REFERENCE_VIDEO_BYTES + 1 })).reasonKey).toBe("music.referenceVideo.reason.oversized");
    expect(referenceVideoSupportCheck(candidate({ sizeBytes: MAX_REFERENCE_VIDEO_BYTES })).supported).toBe(true);
  });

  it("rejects declared MIME types that disagree with the container", () => {
    expect(referenceVideoSupportCheck(candidate({ mimeType: "video/webm" })).reasonKey).toBe("music.referenceVideo.reason.mimeMismatch");
    expect(referenceVideoSupportCheck(candidate({ mimeType: "audio/mpeg" })).reasonKey).toBe("music.referenceVideo.reason.mimeMismatch");
    // A video MIME that belongs to a different container than the extension.
    expect(referenceVideoSupportCheck(candidate({ extension: ".mov", mimeType: "video/mp4", displayName: "clip.mov" })).reasonKey)
      .toBe("music.referenceVideo.reason.mimeMismatch");
  });

  it("rejects local files whose probed header is not an MP4/QuickTime container", () => {
    // A renamed MP3 disguised as .mp4 is caught by the header probe even when
    // the browser-declared MIME matches the extension.
    expect(referenceVideoSupportCheck(candidate({ header: { detected: false } })).reasonKey).toBe("music.referenceVideo.reason.headerMismatch");
    expect(referenceVideoSupportCheck(candidate({ header: { detected: true } })).supported).toBe(true);
  });

  it("rejects unparseable or overlong durations inside the ingest window", () => {
    expect(referenceVideoSupportCheck(candidate({ durationSeconds: Number.NaN })).reasonKey).toBe("music.referenceVideo.reason.durationUnknown");
    expect(referenceVideoSupportCheck(candidate({ durationSeconds: -4 })).reasonKey).toBe("music.referenceVideo.reason.durationUnknown");
    expect(referenceVideoSupportCheck(candidate({ durationSeconds: 0 })).reasonKey).toBe("music.referenceVideo.reason.durationUnknown");
    expect(referenceVideoSupportCheck(candidate({ durationSeconds: MAX_REFERENCE_VIDEO_DURATION_SECONDS + 1 })).reasonKey).toBe("music.referenceVideo.reason.durationTooLong");
    expect(referenceVideoSupportCheck(candidate({ durationSeconds: MAX_REFERENCE_VIDEO_DURATION_SECONDS })).supported).toBe(true);
    expect(referenceVideoSupportCheck(candidate({ durationSeconds: 47.5 })).supported).toBe(true);
  });
});

describe("stored video asset candidates", () => {
  it("accepts active, previewable MP4/QuickTime video assets keyed on MIME", () => {
    const stored = storedCandidateFromAsset(asset());
    expect(stored).not.toBeNull();
    expect(stored!.id).toBe("asset:asset-1");
    expect(stored!.sourceKind).toBe("stored_asset");
    expect(stored!.assetId).toBe("asset-1");
    expect(referenceVideoSupportCheck(stored)).toEqual({ supported: true, reasonKey: null, extension: ".mp4" });

    const mov = storedCandidateFromAsset(asset({ name: "clip.bin", mimeType: "video/quicktime" }));
    expect(mov!.extension).toBe(".mov");
    expect(referenceVideoSupportCheck(mov).supported).toBe(true);
  });

  it("drops assets that are not usable video references before any check runs", () => {
    expect(storedCandidateFromAsset(asset({ assetType: "music" }))).toBeNull();
    expect(storedCandidateFromAsset(asset({ assetType: "image" }))).toBeNull();
    expect(storedCandidateFromAsset(asset({ status: "Archived" }))).toBeNull();
    expect(storedCandidateFromAsset(asset({ hasFile: false }))).toBeNull();
    expect(storedCandidateFromAsset(asset({ canPreview: false }))).toBeNull();
    // An unmapped codec is dropped by the MIME-keyed mapping, even when the
    // filename looks like a supported container, and never re-guessed.
    expect(storedCandidateFromAsset(asset({ mimeType: "video/webm" }))).toBeNull();
    expect(storedCandidateFromAsset(asset({ name: "clip.mp4", mimeType: "video/x-matroska" }))).toBeNull();
    expect(storedCandidateFromAsset(asset({ mimeType: "application/octet-stream" }))).toBeNull();
    expect(storedCandidateFromAsset(asset({ mimeType: null }))).toBeNull();
    expect(referenceVideoSupportCheck(storedCandidateFromAsset(asset({ mimeType: null, name: "clip.mp4" })))).toEqual({
      supported: false,
      reasonKey: "music.referenceVideo.reason.empty",
      extension: "",
    });
  });

  it("fails stored candidates closed on metadata disagreements", () => {
    expect(referenceVideoSupportCheck(candidate({ sourceKind: "stored_asset", assetId: "asset-1", sizeBytes: null })).supported).toBe(true);
    expect(referenceVideoSupportCheck(candidate({ sourceKind: "stored_asset", assetId: null })).reasonKey).toBe("music.referenceVideo.reason.mimeMismatch");
    expect(referenceVideoSupportCheck(candidate({ sourceKind: "stored_asset", assetId: "asset-1", mimeType: null })).reasonKey).toBe("music.referenceVideo.reason.mimeMismatch");
    expect(referenceVideoSupportCheck(candidate({ sourceKind: "stored_asset", assetId: "asset-1", sizeBytes: MAX_REFERENCE_VIDEO_BYTES + 1 })).reasonKey).toBe("music.referenceVideo.reason.oversized");
  });
});

describe("reference video selection state machine", () => {
  it("keeps only supported candidates and clears stale rejections", () => {
    const rejected = selectReferenceVideo(emptyReferenceVideoState, candidate({ extension: ".avi", displayName: "clip.avi" }));
    expect(rejected.candidate).toBeNull();
    expect(rejected.rejectedReasonKey).toBe("music.referenceVideo.reason.extensionUnsupported");
    expect(rejected.loading).toBe(false);

    const accepted = selectReferenceVideo(rejected, candidate());
    expect(accepted.candidate!.id).toBe("local-video:1000:clip.mp4");
    expect(accepted.rejectedReasonKey).toBeNull();
    expect(accepted.loading).toBe(false);

    expect(selectReferenceVideo(accepted, null)).toEqual({
      candidate: null,
      rejectedReasonKey: "music.referenceVideo.reason.empty",
      loading: false,
    });
    expect(clearReferenceVideo()).toEqual(emptyReferenceVideoState);
  });
});

describe("reference video rejection copy", () => {
  it("renders localized terminal error copy instead of raw keys", () => {
    const en = describeReferenceVideoRejection("music.referenceVideo.reason.oversized", "en");
    const ar = describeReferenceVideoRejection("music.referenceVideo.reason.oversized", "ar");
    const ku = describeReferenceVideoRejection("music.referenceVideo.reason.oversized", "ckb");
    expect(en).toContain("25 MB");
    expect(ar.length).toBeGreaterThan(0);
    expect(ku.length).toBeGreaterThan(0);
    expect(new Set([en, ar, ku]).size).toBe(3);
    expect(describeReferenceVideoRejection(null, "en")).toBe("");
    expect(describeReferenceVideoRejection("music.referenceVideo.reason.invented", "en"))
      .toBe(describeReferenceVideoRejection("music.referenceVideo.reason.extensionUnsupported", "en"));
  });
});
