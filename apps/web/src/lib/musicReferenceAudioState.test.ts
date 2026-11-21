import { describe, expect, it, vi } from "vitest";
import type { Asset } from "./api";
import {
  MAX_REFERENCE_AUDIO_BYTES,
  REFERENCE_AUDIO_ACCEPT,
  REFERENCE_AUDIO_EXTENSION_MIME_TYPES,
  REFERENCE_AUDIO_MIME_ALIASES,
  clearReferenceAudioSelection,
  emptyReferenceAudioState,
  extensionForAudioMime,
  extensionOf,
  formatReferenceAudioSize,
  isReferenceAudioCandidate,
  isReferenceAudioMimeForExtension,
  localCandidateFromFile,
  localReferenceAudioId,
  referenceAudioPreviewUrl,
  referenceAudioSupportCheck,
  revokeReferenceAudioObjectUrl,
  selectReferenceAudio,
  selectUnclaimedReferenceAudioCandidates,
  storedCandidateFromAsset,
  type ReferenceAudioCandidate,
} from "./musicReferenceAudioState";

function asset(overrides: Partial<Asset> = {}): Asset {
  return {
    id: "asset-1",
    workspaceId: "workspace-1",
    projectId: null,
    projectName: null,
    name: "demo-loop.mp3",
    description: null,
    assetType: "music",
    mimeType: "audio/mpeg",
    status: "Active",
    hasFile: true,
    canPreview: true,
    fileSizeBytes: 4_200_000,
    sourceStudio: "music",
    sourceJobTitle: null,
    createdAt: "2026-11-01T10:00:00Z",
    updatedAt: "2026-11-01T10:00:00Z",
    archivedAt: null,
    representations: [],
    ...overrides,
  };
}

function localFile(name: string, size: number, mimeType: string): ReferenceAudioCandidate {
  return localCandidateFromFile({ name, size, mimeType });
}

describe("music reference audio state", () => {
  it("mirrors the music-specific supported containers in the accept filter", () => {
    expect(REFERENCE_AUDIO_ACCEPT).toBe(".mp3,.wav,.m4a,.ogg");
    expect(REFERENCE_AUDIO_EXTENSION_MIME_TYPES[".mp3"]).toBe("audio/mpeg");
    expect(extensionOf("Take ONE.MP3")).toBe(".mp3");
    expect(extensionOf("no-extension")).toBe("");
  });

  it("builds deterministic local candidate ids without randomness", () => {
    const first = localFile("Loop.MP3", 1234, "audio/mpeg");
    const second = localFile("loop.mp3", 1234, "audio/mpeg");
    expect(first.id).toBe(localReferenceAudioId("loop.mp3", 1234));
    expect(second.id).toBe(first.id);
    expect(localFile("loop.mp3", 1235, "audio/mpeg").id).not.toBe(first.id);
  });

  it("rejects the shared upload boundary reasons before any preview work", () => {
    expect(referenceAudioSupportCheck(localFile("song.flac", 1000, "audio/flac")).reasonKey)
      .toBe("music.reference.reason.extensionUnsupported");
    expect(isReferenceAudioCandidate(localFile("notes.txt", 1000, "audio/mpeg"))).toBe(false);
    expect(referenceAudioSupportCheck(localFile("notes.txt", 1000, "audio/mpeg")).reasonKey)
      .toBe("music.reference.reason.extensionUnsupported");
    expect(referenceAudioSupportCheck(localFile("loop.mp3", 1000, "video/mp4")).reasonKey)
      .toBe("music.reference.reason.mimeMismatch");
    expect(referenceAudioSupportCheck(localFile("loop.mp3", 0, "audio/mpeg")).reasonKey)
      .toBe("music.reference.reason.empty");
    expect(referenceAudioSupportCheck(localFile("loop.mp3", MAX_REFERENCE_AUDIO_BYTES + 1, "audio/mpeg")).reasonKey)
      .toBe("music.reference.reason.oversized");
    expect(isReferenceAudioCandidate(localFile("loop.wav", 1000, "audio/wav"))).toBe(true);
    // An undeclared MIME is accepted like the API does for octet-stream uploads.
    expect(isReferenceAudioCandidate(localFile("loop.wav", 1000, ""))).toBe(true);
  });

  // Synthetic handles only: nothing here is a real recording, and no file
  // leaves the browser, so the whole boundary is reproducible in Node.
  it("accepts every supported container declared with a browser's own alias type", () => {
    // Each accepted format, asserted through both the canonical MIME and the
    // aliases browsers and shared MIME databases actually report for it.
    const declared = [
      ["song.mp3", "audio/mpeg"],
      ["song.MP3", "audio/mp3"],
      ["song.mp3", "audio/x-mpeg"],
      ["drum.wav", "audio/wav"],
      ["drum.wav", "audio/x-wav"],
      ["drum.wav", "audio/wave"],
      ["voice.m4a", "audio/mp4"],
      // Safari/WebKit on names an .m4a this way; canonical-only matching made
      // it an unsupported file the user could not preview.
      ["voice.m4a", "audio/x-m4a"],
      ["loop.ogg", "audio/ogg"],
      ["loop.ogg", "audio/vorbis"],
      ["loop.ogg", "audio/opus"],
      // An opaque declaration names no other content, so it must not be
      // contradicted here any more than the API contradicts it on upload.
      ["loop.mp3", "application/octet-stream"],
    ];
    for (const [name, mimeType] of declared) {
      const candidate = localFile(name, 1000, mimeType);
      expect([name, mimeType, referenceAudioSupportCheck(candidate).reasonKey]).toEqual([name, mimeType, null]);
      expect(isReferenceAudioCandidate(candidate)).toBe(true);
    }
  });

  it("still contradicts a declared type that names different content", () => {
    // An alias never widens the boundary past the container itself.
    for (const [name, mimeType] of [
      ["loop.mp3", "video/mp4"],
      ["loop.mp3", "text/plain"],
      ["loop.mp3", "audio/x-m4a"],
      ["loop.wav", "audio/mpeg"],
      ["clip.m4a", "audio/wav"],
      ["clip.ogg", "audio/mp4"],
      ["clip.ogg", "image/png"],
    ]) {
      expect(referenceAudioSupportCheck(localFile(name, 1000, mimeType)).reasonKey)
        .toBe("music.reference.reason.mimeMismatch");
    }
    expect(isReferenceAudioMimeForExtension(".m4a", "audio/x-m4a")).toBe(true);
    expect(isReferenceAudioMimeForExtension(".m4a", "audio/mpeg")).toBe(false);
    expect(isReferenceAudioMimeForExtension(".flac", "audio/flac")).toBe(false);
  });

  it("keeps the alias table and the accept filter describing one supported set", () => {
    // The picker filter, the hint copy's formats and the alias table must stay
    // the same four containers, so a file the dialog offers is never refused
    // for a reason the announced alert cannot name.
    expect(Object.keys(REFERENCE_AUDIO_MIME_ALIASES)).toEqual(Object.keys(REFERENCE_AUDIO_EXTENSION_MIME_TYPES));
    expect(REFERENCE_AUDIO_ACCEPT.split(",")).toEqual(Object.keys(REFERENCE_AUDIO_EXTENSION_MIME_TYPES));
    for (const [extension, mime] of Object.entries(REFERENCE_AUDIO_EXTENSION_MIME_TYPES)) {
      expect(REFERENCE_AUDIO_MIME_ALIASES[extension]).toContain(mime);
      expect(REFERENCE_AUDIO_MIME_ALIASES[extension][0]).toBe(mime);
    }
  });

  it("announces a selectable or rejected outcome for every supported container", () => {
    // The panel renders a rejection as a live alert and a selection inside the
    // polite region, so each attempt must resolve to exactly one of the two:
    // never a silent refusal with no key, and never a selection that also
    // carries a stale rejection reason the reader would announce.
    for (const mimeType of ["audio/mp4", "audio/x-m4a"]) {
      const candidate = localFile("take.m4a", 2048, mimeType);
      const result = selectReferenceAudio(emptyReferenceAudioState, candidate, () => "blob:take");
      expect(result.selected).toBe(true);
      expect(result.rejected).toBe(false);
      expect(result.state.candidate?.id).toBe(candidate.id);
      expect(result.state.rejectedReasonKey).toBeNull();
    }
    const refused = selectReferenceAudio(emptyReferenceAudioState, localFile("take.m4a", 2048, "video/mp4"), () => "blob:take");
    expect(refused.selected).toBe(false);
    expect(refused.rejected).toBe(true);
    expect(refused.state.rejectedReasonKey).toBe("music.reference.reason.mimeMismatch");
    expect(refused.createdObjectUrl).toBeNull();
  });

  it("accepts only own private, previewable audio assets as stored candidates", () => {
    expect(storedCandidateFromAsset(asset())).toMatchObject({ sourceKind: "stored_asset", assetId: "asset-1" });
    expect(storedCandidateFromAsset(asset({ assetType: "image" }))).toBeNull();
    expect(storedCandidateFromAsset(asset({ mimeType: "video/mp4" }))).toBeNull();
    expect(storedCandidateFromAsset(asset({ canPreview: false }))).toBeNull();
    expect(storedCandidateFromAsset(asset({ hasFile: false }))).toBeNull();
    expect(storedCandidateFromAsset(asset({ status: "Archived" }))).toBeNull();
    expect(storedCandidateFromAsset(asset({ assetType: "audio", name: "field.wav", mimeType: "audio/wav" })))
      .toMatchObject({ extension: ".wav", mimeType: "audio/wav" });
    // Generated music assets usually have no filename extension: the MIME wins.
    expect(storedCandidateFromAsset(asset({ name: "calm-bed" }))).toMatchObject({ extension: ".mp3" });
    expect(extensionForAudioMime("audio/mpeg")).toBe(".mp3");
    expect(extensionForAudioMime("audio/flac")).toBe("");
    // Codecs outside the music-specific containers stay out of the picker.
    expect(storedCandidateFromAsset(asset({ name: "field.flac", mimeType: "audio/flac" }))).toBeNull();
    expect(storedCandidateFromAsset(asset({ name: "voice.opus", mimeType: "audio/opus" }))).toBeNull();
  });

  it("lists only unclaimed (project-free) candidates, newest first with stable ties", () => {
    const claimed = asset({ id: "asset-claimed", name: "score.mp3", projectId: "project-1", createdAt: "2026-12-01T00:00:00Z" });
    const older = asset({ id: "asset-old", name: "old-loop.mp3", createdAt: "2026-10-01T00:00:00Z" });
    const newer = asset({ id: "asset-new", name: "new-loop.mp3", createdAt: "2026-11-15T00:00:00Z" });
    const tiedA = asset({ id: "asset-a", name: "tie-a.mp3", createdAt: "2026-11-20T00:00:00Z" });
    const tiedB = asset({ id: "asset-b", name: "tie-b.mp3", createdAt: "2026-11-20T00:00:00Z" });
    const candidates = selectUnclaimedReferenceAudioCandidates([older, claimed, tiedA, tiedB, newer]);
    expect(candidates.map((candidate) => candidate.assetId)).toEqual(["asset-a", "asset-b", "asset-new", "asset-old"]);
    expect(candidates.some((candidate) => candidate.assetId === "asset-claimed")).toBe(false);
    // Deterministic for repeated inputs, including duplicate asset entries.
    expect(selectUnclaimedReferenceAudioCandidates([older, claimed, tiedA, tiedB, newer])).toEqual(candidates);
    expect(selectUnclaimedReferenceAudioCandidates([newer, newer]).map((candidate) => candidate.id)).toEqual(["asset:asset-new"]);
    const many = Array.from({ length: 30 }, (_, index) => asset({ id: `bulk-${index}`, name: `bulk-${index}.mp3`, createdAt: `2026-11-0${index % 9 + 1}T00:00:00Z` }));
    expect(selectUnclaimedReferenceAudioCandidates(many, 20)).toHaveLength(20);
  });

  it("previews stored candidates only through the authenticated inline asset URL", () => {
    const assetFileUrl = vi.fn((id: string, inline = false) => `https://api.example.invalid/api/assets/${id}/download${inline ? "?inline=true" : ""}`);
    const stored = storedCandidateFromAsset(asset())!;
    expect(referenceAudioPreviewUrl(stored, assetFileUrl)).toBe("https://api.example.invalid/api/assets/asset-1/download?inline=true");
    expect(assetFileUrl).toHaveBeenCalledWith("asset-1", true);
    const local = localFile("loop.mp3", 1000, "audio/mpeg");
    expect(referenceAudioPreviewUrl(local, assetFileUrl)).toBeNull();
    expect(referenceAudioPreviewUrl(null, assetFileUrl)).toBeNull();
    expect(referenceAudioPreviewUrl(localFile("song.flac", 1000, "audio/flac"), assetFileUrl)).toBeNull();
    // A stored asset whose MIME does not match its extension is refused even if listed.
    const mismatched: ReferenceAudioCandidate = { ...stored, mimeType: "audio/wav" };
    expect(referenceAudioPreviewUrl(mismatched, assetFileUrl)).toBeNull();
  });

  it("keeps single selection with safe replace, toggle, and clear transitions", () => {
    const createObjectUrl = vi.fn(() => "blob:preview-1");
    const local = localFile("loop.mp3", 1000, "audio/mpeg");
    const stored = storedCandidateFromAsset(asset())!;

    const first = selectReferenceAudio(emptyReferenceAudioState, local, createObjectUrl);
    expect(first.selected).toBe(true);
    expect(first.state.previewObjectUrl).toBe("blob:preview-1");
    expect(first.staleObjectUrl).toBeNull();

    const replaced = selectReferenceAudio(first.state, stored, createObjectUrl);
    expect(replaced.state.candidate?.id).toBe(stored.id);
    expect(replaced.state.previewObjectUrl).toBeNull();
    expect(replaced.staleObjectUrl).toBe("blob:preview-1");
    expect(createObjectUrl).toHaveBeenCalledTimes(1);

    const toggled = selectReferenceAudio(replaced.state, stored, createObjectUrl);
    expect(toggled.selected).toBe(false);
    expect(toggled.state).toEqual(emptyReferenceAudioState);
    expect(toggled.staleObjectUrl).toBeNull();

    const rejected = selectReferenceAudio(replaced.state, localFile("movie.mp4", 1000, "video/mp4"), createObjectUrl);
    expect(rejected.rejected).toBe(true);
    expect(rejected.state.candidate).toBe(stored);
    expect(rejected.state.rejectedReasonKey).toBe("music.reference.reason.extensionUnsupported");
    expect(rejected.staleObjectUrl).toBeNull();

    const cleared = clearReferenceAudioSelection(first.state);
    expect(cleared.state).toEqual(emptyReferenceAudioState);
    expect(cleared.staleObjectUrl).toBe("blob:preview-1");

    const nullSelection = selectReferenceAudio(first.state, null, createObjectUrl);
    expect(nullSelection.state).toEqual(emptyReferenceAudioState);
    expect(nullSelection.staleObjectUrl).toBe("blob:preview-1");
  });

  it("revokes only blob object URLs and never throws elsewhere", () => {
    const calls: string[] = [];
    const previous = URL.revokeObjectURL;
    URL.revokeObjectURL = ((url: string) => { calls.push(url); }) as typeof URL.revokeObjectURL;
    revokeReferenceAudioObjectUrl("https://api.example.invalid/file");
    expect(calls).toEqual([]);
    revokeReferenceAudioObjectUrl(null);
    expect(calls).toEqual([]);
    revokeReferenceAudioObjectUrl("blob:preview-1");
    expect(calls).toEqual(["blob:preview-1"]);
    if (previous) URL.revokeObjectURL = previous; else delete (URL as { revokeObjectURL?: unknown }).revokeObjectURL;
  });

  it("formats sizes without leaking raw byte precision", () => {
    expect(formatReferenceAudioSize(null)).toBeNull();
    expect(formatReferenceAudioSize(0)).toBeNull();
    expect(formatReferenceAudioSize(-5)).toBeNull();
    expect(formatReferenceAudioSize(2048)).toBe("2 KiB");
    expect(formatReferenceAudioSize(1_572_864)).toBe("1.5 MiB");
    expect(formatReferenceAudioSize(26_214_400)).toBe("25 MiB");
  });
});
