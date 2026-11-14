import { describe, expect, it } from "vitest";
import {
  applyMovieVideoMakeoverSourceSelection,
  buildMovieVideoMakeoverSourceCatalog,
  clearMovieVideoMakeoverSourceSelection,
  countSelectableMovieVideoMakeoverSources,
  describeMovieVideoMakeoverSource,
  movieVideoMakeoverSourcePendingCopy,
  tallyMovieVideoMakeoverSourceGaps,
  MOVIE_VIDEO_MAKEOVER_READY_CLIP_STATUSES,
  type MovieVideoMakeoverSourceDescriptor,
} from "./movieVideoMakeoverSourceCatalog";
import {
  admitMovieVideoMakeoverDraft,
  createMovieVideoMakeoverDraft,
  type MovieVideoMakeoverBlockCode,
} from "@/lib/movieVideoMakeoverDraftState";
import {
  buildMovieVideoMakeoverDraftInput,
  createEmptyMovieVideoMakeoverSelection,
  type MovieVideoMakeoverCapabilitySelection,
  type MovieVideoMakeoverSelection,
} from "@/components/movie/movieVideoMakeoverInputState";

const PRINCIPAL = "principal_creator_01";
const WORKSPACE = "workspace_01";
const LIMITS_SAFE_BYTES = 50_000_000;

function verifiedSource(overrides: Partial<MovieVideoMakeoverSourceDescriptor> = {}): MovieVideoMakeoverSourceDescriptor {
  return {
    assetId: "asset_talking_head_01",
    name: "Talking head take 03",
    assetType: "video",
    mimeType: "video/mp4",
    clipStatus: "Succeeded",
    workspaceId: WORKSPACE,
    ownerPrincipalId: PRINCIPAL,
    visibility: "private",
    retentionPolicyVerified: true,
    hasFile: true,
    canPreview: true,
    sizeBytes: LIMITS_SAFE_BYTES,
    durationSeconds: 14,
    width: 1080,
    height: 1920,
    frameRate: 30,
    audioPresent: true,
    ...overrides,
  };
}

/** Every source field #784 carries; used to prove switching leaves nothing behind. */
const SOURCE_FIELDS: (keyof MovieVideoMakeoverSelection)[] = [
  "sourceAssetId",
  "sourceOwnerPrincipalId",
  "sourceWorkspaceId",
  "sourceVisibilityPrivate",
  "sourceRetentionVerified",
  "sourceAudioPresent",
  "sourceDurationSeconds",
  "sourceWidth",
  "sourceHeight",
  "sourceFrameRate",
  "sourceSizeBytes",
];

function admittedSelection(): MovieVideoMakeoverSelection {
  const entry = describeMovieVideoMakeoverSource(verifiedSource());
  return applyMovieVideoMakeoverSourceSelection(
    {
      ...createEmptyMovieVideoMakeoverSelection(),
      mode: "studio-swap",
      presetId: "studio.clean-neutral",
      preserveSubjectIdentity: true,
      creatorApproved: true,
      approvalReference: "consent_698_draft",
      capabilities: [
        { feature: "edit-in-place", selected: true, evidenceLabel: "verified", sourceUrl: "https://providers.example.test/docs/video-edit", capturedAtUtc: "2026-11-13T00:00:00Z" },
        { feature: "identity-preserve", selected: true, evidenceLabel: "verified", sourceUrl: "https://providers.example.test/docs/video-edit", capturedAtUtc: "2026-11-13T00:00:00Z" },
      ] satisfies readonly MovieVideoMakeoverCapabilitySelection[],
    },
    entry,
  );
}

function codes(selection: MovieVideoMakeoverSelection): MovieVideoMakeoverBlockCode[] {
  return admitMovieVideoMakeoverDraft(
    createMovieVideoMakeoverDraft(buildMovieVideoMakeoverDraftInput(selection)),
  ).blockedReasons.map((reason) => reason.code);
}

describe("source catalogue: a real finished clip becomes an attachable source", () => {
  it("attaches a fully described video asset and admits a draft end to end", () => {
    const entry = describeMovieVideoMakeoverSource(verifiedSource(), { principalId: PRINCIPAL, workspaceId: WORKSPACE });
    expect(entry.selectable).toBe(true);
    expect(entry.blockers).toEqual([]);
    expect(entry.gaps).toEqual([]);

    const selection = admittedSelection();
    const admission = admitMovieVideoMakeoverDraft(createMovieVideoMakeoverDraft(buildMovieVideoMakeoverDraftInput(selection)));
    expect(admission.status).toBe("ready-for-estimate");
    // Integration must not smuggle execution: the ceiling is #775's, unchanged.
    expect(admission.launchPermitted).toBe(false);
    expect(admission.billable).toBe(false);
    expect(admission.providerJobCreated).toBe(false);
    expect(admission.uiEnabled).toBe(false);
  });

  it("carries the descriptor facts into #784's source fields without inventing any", () => {
    const selection = admittedSelection();
    expect(selection.sourceAssetId).toBe("asset_talking_head_01");
    expect(selection.sourceOwnerPrincipalId).toBe(PRINCIPAL);
    expect(selection.sourceWorkspaceId).toBe(WORKSPACE);
    expect(selection.sourceVisibilityPrivate).toBe(true);
    expect(selection.sourceRetentionVerified).toBe(true);
    expect(selection.sourceDurationSeconds).toBe(14);
    expect(selection.sourceWidth).toBe(1080);
    expect(selection.sourceHeight).toBe(1920);
    expect(selection.sourceFrameRate).toBe(30);
    expect(selection.sourceAudioPresent).toBe(true);
    expect(selection.sourceSizeBytes).toBe(LIMITS_SAFE_BYTES);
  });

  it("lists a whole catalogue in the caller's order", () => {
    const catalogue = buildMovieVideoMakeoverSourceCatalog([
      verifiedSource({ assetId: "asset_a" }),
      verifiedSource({ assetId: "asset_b", clipStatus: "Running" }),
      verifiedSource({ assetId: "asset_c" }),
    ], { principalId: PRINCIPAL, workspaceId: WORKSPACE });
    expect(catalogue.map((entry) => entry.assetId)).toEqual(["asset_a", "asset_b", "asset_c"]);
    expect(catalogue.filter((entry) => !entry.selectable).map((entry) => entry.assetId)).toEqual(["asset_b"]);
    expect(countSelectableMovieVideoMakeoverSources(catalogue)).toBe(2);
  });

  it("treats every documented finished-clip status as attachable and nothing else", () => {
    for (const status of MOVIE_VIDEO_MAKEOVER_READY_CLIP_STATUSES) {
      expect(describeMovieVideoMakeoverSource(verifiedSource({ clipStatus: status })).selectable).toBe(true);
    }
    for (const status of ["Running", "Pending", "Failed", "Cancelled", "", null, undefined]) {
      const entry = describeMovieVideoMakeoverSource(verifiedSource({ clipStatus: status as string | null | undefined }));
      expect(entry.selectable).toBe(false);
      expect(entry.blockers.map((blocker) => blocker.code)).toContain("source_clip_not_ready");
    }
  });
});

describe("source catalogue: blocked clips are named, not silently dropped", () => {
  it("refuses a non-video asset", () => {
    const entry = describeMovieVideoMakeoverSource(verifiedSource({ assetType: "image", mimeType: "image/png" }));
    expect(entry.blockers.map((blocker) => blocker.code)).toContain("source_asset_not_video");
  });

  it("falls back to the mime type when the asset type is absent, and blocks when neither is known", () => {
    expect(describeMovieVideoMakeoverSource(verifiedSource({ assetType: null, mimeType: "video/mp4" })).selectable).toBe(true);
    const unknown = describeMovieVideoMakeoverSource(verifiedSource({ assetType: null, mimeType: null }));
    expect(unknown.blockers.map((blocker) => blocker.code)).toContain("source_asset_not_video");
  });

  it("refuses a clip with no asset identity", () => {
    for (const assetId of [null, undefined, "", "   "]) {
      const entry = describeMovieVideoMakeoverSource(verifiedSource({ assetId: assetId as string | null | undefined }));
      expect(entry.blockers.map((blocker) => blocker.code)).toContain("source_asset_missing");
      expect(entry.selectable).toBe(false);
    }
  });

  it("refuses an asset whose stored file or preview is missing", () => {
    expect(describeMovieVideoMakeoverSource(verifiedSource({ hasFile: false })).blockers.map((b) => b.code))
      .toContain("source_file_missing");
    expect(describeMovieVideoMakeoverSource(verifiedSource({ canPreview: false })).blockers.map((b) => b.code))
      .toContain("source_preview_unavailable");
  });

  it("refuses an archived asset and stays open about an unstated lifecycle", () => {
    const archived = describeMovieVideoMakeoverSource(verifiedSource({ assetStatus: "Archived" }));
    expect(archived.blockers.map((blocker) => blocker.code)).toContain("source_asset_unavailable");
    expect(archived.selectable).toBe(false);
    for (const assetStatus of ["Active", null, undefined]) {
      expect(describeMovieVideoMakeoverSource(verifiedSource({ assetStatus: assetStatus as string | null | undefined }))
        .blockers.map((blocker) => blocker.code)).not.toContain("source_asset_unavailable");
    }
  });

  it("tallies outstanding verifications across a catalogue", () => {
    const catalogue = buildMovieVideoMakeoverSourceCatalog([
      verifiedSource({ assetId: "asset_a", frameRate: null }),
      verifiedSource({ assetId: "asset_b", frameRate: null, audioPresent: null }),
    ]);
    const tally = tallyMovieVideoMakeoverSourceGaps(catalogue);
    expect(tally.frame_rate_unknown).toBe(2);
    expect(tally.audio_state_unknown).toBe(1);
    expect(tally.ownership_unverified).toBe(0);
    expect(Object.keys(tally)).toHaveLength(8);
    expect(countSelectableMovieVideoMakeoverSources(catalogue)).toBe(2);
  });

  it("refuses a clip from another workspace once the caller states the current one", () => {
    const entry = describeMovieVideoMakeoverSource(verifiedSource({ workspaceId: "workspace_other" }), { workspaceId: WORKSPACE });
    expect(entry.blockers.map((blocker) => blocker.code)).toContain("source_cross_workspace");
  });

  it("leaves workspace comparison alone when the caller does not state a workspace", () => {
    const entry = describeMovieVideoMakeoverSource(verifiedSource({ workspaceId: "workspace_other" }), {});
    expect(entry.blockers.map((blocker) => blocker.code)).not.toContain("source_cross_workspace");
  });

  it("applies no mutation when a blocked entry is handed to the selection bridge", () => {
    const selection = createEmptyMovieVideoMakeoverSelection();
    const blocked = describeMovieVideoMakeoverSource(verifiedSource({ clipStatus: "Failed" }));
    expect(applyMovieVideoMakeoverSourceSelection(selection, blocked)).toBe(selection);
    expect(codes(selection)).toContain("source_missing");
  });
});

describe("source facts the read model lacks stay gaps, never guesses", () => {
  it("records one gap per missing fact with the #775 code it will produce", () => {
    const entry = describeMovieVideoMakeoverSource(verifiedSource({
      ownerPrincipalId: null,
      visibility: null,
      retentionPolicyVerified: null,
      durationSeconds: null,
      width: null,
      height: null,
      frameRate: null,
      audioPresent: null,
      sizeBytes: null,
    }));
    expect(entry.selectable).toBe(true);
    expect(entry.gaps.map((row) => row.code).sort()).toEqual([
      "audio_state_unknown",
      "dimensions_unknown",
      "duration_unknown",
      "frame_rate_unknown",
      "ownership_unverified",
      "retention_unverified",
      "size_unknown",
      "visibility_unverified",
    ]);
    for (const row of entry.gaps) {
      expect(row.message.length).toBeGreaterThan(0);
      expect(row.admissionBlockCode).not.toBe("capability_unverified");
    }
  });

  it("blocks an otherwise complete draft on exactly those source codes", () => {
    const entry = describeMovieVideoMakeoverSource(verifiedSource({
      visibility: null,
      retentionPolicyVerified: null,
      frameRate: null,
      audioPresent: null,
    }));
    const found = codes(applyMovieVideoMakeoverSourceSelection({
      ...createEmptyMovieVideoMakeoverSelection(),
      mode: "studio-swap",
      presetId: "studio.clean-neutral",
      preserveSubjectIdentity: true,
      creatorApproved: true,
      approvalReference: "consent_698_draft",
    }, entry));
    expect(found).toContain("source_visibility_not_private");
    expect(found).toContain("source_retention_unverified");
    expect(found).toContain("source_metadata_missing");
    expect(found).not.toContain("source_missing");
  });

  it("passes an out-of-bounds duration through so #775 still calls it out of bounds", () => {
    const entry = describeMovieVideoMakeoverSource(verifiedSource({ durationSeconds: 9000 }));
    expect(entry.source.durationSeconds).toBe(9000);
    expect(entry.gaps.map((row) => row.code)).not.toContain("duration_unknown");
    expect(codes(applyMovieVideoMakeoverSourceSelection(createEmptyMovieVideoMakeoverSelection(), entry)))
      .toContain("source_metadata_out_of_bounds");
  });

  it("drops a stale audio promise when the new source cannot confirm audio", () => {
    const withAudio = { ...admittedSelection(), preserveOriginalAudio: true };
    // Audio preserved on a clip whose read model reports audio, so the draft asks
    // for the source-audio-passthrough feature.
    expect(withAudio.sourceAudioPresent).toBe(true);
    expect(codes(withAudio)).toContain("capability_missing");

    const silentSource = describeMovieVideoMakeoverSource(verifiedSource({ assetId: "asset_no_audio", audioPresent: null }));
    const next = applyMovieVideoMakeoverSourceSelection(withAudio, silentSource);
    expect(next.sourceAudioPresent).toBeNull();
    expect(next.preserveOriginalAudio).toBe(false);
    expect(codes(next)).not.toContain("source_audio_state_unknown");
    expect(codes(next)).not.toContain("capability_missing");
  });
});

describe("switching sources leaves no fact from the previous clip", () => {
  it("rewrites all eleven source fields on a switch", () => {
    const first = applyMovieVideoMakeoverSourceSelection(
      createEmptyMovieVideoMakeoverSelection(),
      describeMovieVideoMakeoverSource(verifiedSource()),
    );
    const second = applyMovieVideoMakeoverSourceSelection(
      first,
      describeMovieVideoMakeoverSource(verifiedSource({
        assetId: "asset_second",
        durationSeconds: 22,
        width: 720,
        height: 1280,
        frameRate: 25,
        audioPresent: false,
        sizeBytes: 9_000_000,
        retentionPolicyVerified: false,
        visibility: "unverified",
      })),
    );

    expect(second.sourceAssetId).toBe("asset_second");
    expect(second.sourceDurationSeconds).toBe(22);
    expect(second.sourceWidth).toBe(720);
    expect(second.sourceHeight).toBe(1280);
    expect(second.sourceFrameRate).toBe(25);
    expect(second.sourceSizeBytes).toBe(9_000_000);
    expect(second.sourceRetentionVerified).toBe(false);
    expect(second.sourceVisibilityPrivate).toBe(false);
    expect(second.sourceAudioPresent).toBe(false);

    // Nothing outside the source block moved.
    expect(second.mode).toBe(first.mode);
    expect(second.presetId).toBe(first.presetId);
    expect(second.capabilities).toEqual(first.capabilities);
    expect(second.creatorApproved).toBe(first.creatorApproved);

    const stale = SOURCE_FIELDS.filter((field) => JSON.stringify(first[field]) === JSON.stringify(second[field]));
    expect(stale).toEqual(["sourceOwnerPrincipalId", "sourceWorkspaceId"]);
  });

  it("detaching a source clears every source field", () => {
    const attached = admittedSelection();
    const cleared = clearMovieVideoMakeoverSourceSelection(attached);
    for (const field of SOURCE_FIELDS) {
      const value = cleared[field];
      expect(value === null || value === false).toBe(true);
    }
    expect(cleared.preserveOriginalAudio).toBe(false);
    expect(cleared.mode).toBe(attached.mode);
    expect(codes(cleared)).toContain("source_missing");
  });

  it("returns the same object when there was no source to clear", () => {
    const empty = createEmptyMovieVideoMakeoverSelection();
    expect(clearMovieVideoMakeoverSourceSelection(empty)).toBe(empty);
  });
});

describe("source messaging stays honest about unsupported requests", () => {
  it("calls an outfit change a future feature instead of a source problem", () => {
    const attached = describeMovieVideoMakeoverSource(verifiedSource());
    for (const mode of ["wardrobe-change", "outfit-change"]) {
      const copy = movieVideoMakeoverSourcePendingCopy(attached, mode);
      expect(copy.headline).toMatch(/future feature/i);
      expect(copy.details.join(" ")).toMatch(/blocked/i);
      expect(copy.details.join(" ")).not.toMatch(/attach a different clip/i);
    }
  });

  it("does not claim an upload path exists", () => {
    const copy = movieVideoMakeoverSourcePendingCopy(null, "studio-swap");
    expect(copy.headline).toMatch(/no source attached/i);
    expect(copy.details.join(" ")).toMatch(/not wired/i);
  });

  it("states that a complete source still grants no execution", () => {
    const copy = movieVideoMakeoverSourcePendingCopy(describeMovieVideoMakeoverSource(verifiedSource()), "relight");
    expect(copy.headline).toMatch(/non-billable/i);
    expect(copy.details.join(" ")).toMatch(/never creates a job, reserves credit, or permits a launch/i);
  });

  it("explains each pending fact on an attached clip", () => {
    const copy = movieVideoMakeoverSourcePendingCopy(
      describeMovieVideoMakeoverSource(verifiedSource({ frameRate: null, audioPresent: null })),
      "restyle",
    );
    expect(copy.headline).toMatch(/2 source facts still need verification/i);
    expect(copy.details).toHaveLength(2);
  });

  it("names the blocker when the chosen clip cannot be attached", () => {
    const copy = movieVideoMakeoverSourcePendingCopy(
      describeMovieVideoMakeoverSource(verifiedSource({ hasFile: false })),
      "studio-swap",
    );
    expect(copy.headline).toMatch(/cannot be attached/i);
    expect(copy.details.join(" ")).toMatch(/no stored file/i);
  });
});
