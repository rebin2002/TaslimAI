import { describe, expect, it, vi } from "vitest";
import type { Asset } from "./api";
import {
  MAX_REFERENCE_AUDIO_BYTES,
  REFERENCE_AUDIO_ACCEPT,
  REFERENCE_AUDIO_EXTENSION_MIME_TYPES,
  clearReferenceAudioSelection,
  emptyReferenceAudioState,
  extensionForAudioMime,
  extensionOf,
  formatReferenceAudioSize,
  isReferenceAudioCandidate,
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
