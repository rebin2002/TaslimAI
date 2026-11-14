import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import {
  MovieVideoMakeoverStudio,
  reduceMovieVideoMakeoverStudioSource,
  type MovieVideoMakeoverStudioProps,
} from "./MovieVideoMakeoverStudio";
import {
  describeMovieVideoMakeoverProjectSources,
  type MovieVideoMakeoverAssetSource,
  type MovieVideoMakeoverProjectSource,
} from "@/lib/movieVideoMakeoverSourceCatalog";
import {
  admitMovieVideoMakeoverDraft,
  createMovieVideoMakeoverDraft,
} from "@/lib/movieVideoMakeoverDraftState";
import {
  buildMovieVideoMakeoverDraftInput,
  createEmptyMovieVideoMakeoverSelection,
  type MovieVideoMakeoverSelection,
} from "./movieVideoMakeoverInputState";

const PRINCIPAL = "principal_creator_01";
const WORKSPACE = "workspace_01";

function project(overrides: Partial<MovieVideoMakeoverProjectSource> = {}): MovieVideoMakeoverProjectSource {
  return {
    workspaceId: WORKSPACE,
    clips: [
      { id: "clip_1", assetId: "asset_take_01", status: "Succeeded", durationSeconds: 12, metadataJson: null },
      { id: "clip_2", assetId: "asset_take_02", status: "Running", durationSeconds: null, metadataJson: null },
      { id: "clip_3", assetId: null, status: "Failed", durationSeconds: null, metadataJson: null },
    ],
    ...overrides,
  };
}

function assets(): readonly MovieVideoMakeoverAssetSource[] {
  return [
    {
      id: "asset_take_01",
      name: "Interview room take 01",
      workspaceId: WORKSPACE,
      assetType: "video",
      mimeType: "video/mp4",
      status: "Active",
      hasFile: true,
      canPreview: true,
      fileSizeBytes: 48_000_000,
    },
    {
      id: "asset_take_02",
      name: "Reel take 02",
      workspaceId: WORKSPACE,
      assetType: "video",
      mimeType: "video/mp4",
      status: "Active",
      hasFile: true,
      canPreview: true,
      fileSizeBytes: 20_000_000,
    },
  ];
}

function catalogue() {
  return describeMovieVideoMakeoverProjectSources(project(), assets(), { principalId: PRINCIPAL, workspaceId: WORKSPACE });
}

/**
 * The browser read models carry no owner, visibility, retention, dimension, frame
 * rate or audio fact, so a caller that wants to complete a draft must supply those
 * from a server-side source descriptor. This is the realistic "everything else is
 * settled" selection the tests need in order to show what the bridge alone changes.
 */
function settledDraftParts(): MovieVideoMakeoverSelection {
  return {
    ...createEmptyMovieVideoMakeoverSelection(),
    mode: "studio-swap",
    presetId: "studio.clean-neutral",
    preserveSubjectIdentity: true,
    creatorApproved: true,
    approvalReference: "consent_698_source_bridge",
    sourceOwnerPrincipalId: PRINCIPAL,
    sourceVisibilityPrivate: true,
    sourceRetentionVerified: true,
    sourceWidth: 1080,
    sourceHeight: 1920,
    sourceFrameRate: 30,
    capabilities: [
      { feature: "edit-in-place", selected: true, evidenceLabel: "verified", sourceUrl: "https://providers.example.test/docs/video-edit", capturedAtUtc: "2026-11-13T00:00:00Z" },
      { feature: "identity-preserve", selected: true, evidenceLabel: "verified", sourceUrl: "https://providers.example.test/docs/video-edit", capturedAtUtc: "2026-11-13T00:00:00Z" },
    ],
  };
}

function admission(selection: MovieVideoMakeoverSelection) {
  return admitMovieVideoMakeoverDraft(createMovieVideoMakeoverDraft(buildMovieVideoMakeoverDraftInput(selection)));
}

/**
 * Button labels in rendered markup. Written as a split rather than a `/gs` regex
 * because this project typechecks against an ES2017 target, where the `s` flag is
 * not available.
 */
function buttonLabels(markup: string): string[] {
  return markup
    .split("<button")
    .slice(1)
    .map((fragment) => {
      const open = fragment.indexOf(">");
      const close = fragment.indexOf("</button>");
      return open === -1 || close === -1 ? "" : fragment.slice(open + 1, close);
    });
}

const inertHandlers: Pick<MovieVideoMakeoverStudioProps, "onChange"> = {
  onChange: () => {
    throw new Error("static render must not change the draft");
  },
};

function render(props: Pick<MovieVideoMakeoverStudioProps, "project" | "assets" | "principalId" | "workspaceId" | "selection">): string {
  return renderToStaticMarkup(<MovieVideoMakeoverStudio {...props} {...inertHandlers} />);
}

describe("studio: what the real Movie read models can and cannot say", () => {
  it("builds one row per clip in project order", () => {
    expect(catalogue().map((entry) => entry.label)).toEqual([
      "Interview room take 01",
      "Reel take 02",
      "clip_3",
    ]);
  });

  it("permits exactly the finished clip with a published video asset", () => {
    const entries = catalogue();
    expect(entries.filter((entry) => entry.selectable).map((entry) => entry.assetId)).toEqual(["asset_take_01"]);
    const running = entries.find((entry) => entry.assetId === "asset_take_02");
    expect(running?.blockers.map((blocker) => blocker.code)).toContain("source_clip_not_ready");
    const noAsset = entries.find((entry) => entry.assetId === "");
    expect(noAsset?.blockers.map((blocker) => blocker.code)).toContain("source_asset_missing");
  });

  it("reports the facts the asset read model does not carry instead of guessing them", () => {
    const attached = catalogue().find((entry) => entry.assetId === "asset_take_01");
    expect(attached?.gaps.map((gap) => gap.code).sort()).toEqual([
      "audio_state_unknown",
      "dimensions_unknown",
      "frame_rate_unknown",
      "ownership_unverified",
      "retention_unverified",
      "visibility_unverified",
    ]);
    // Size and duration do come from the read models.
    expect(attached?.source.sizeBytes).toBe(48_000_000);
    expect(attached?.source.durationSeconds).toBe(12);
  });

  it("falls back to clip metadata duration and stays unknown on a malformed bag", () => {
    const withMeta = project({
      clips: [{ id: "clip_m", assetId: "asset_take_01", status: "Ready", durationSeconds: null, metadataJson: "{\"durationMilliseconds\":8400}" }],
    });
    const parsed = describeMovieVideoMakeoverProjectSources(withMeta, assets(), {});
    expect(parsed[0].source.durationSeconds).toBe(8.4);

    const corrupt = project({
      clips: [{ id: "clip_c", assetId: "asset_take_01", status: "Ready", durationSeconds: null, metadataJson: "{not json" }],
    });
    expect(describeMovieVideoMakeoverProjectSources(corrupt, assets(), {})[0].source.durationSeconds).toBeNull();

    const absent = project({
      clips: [{ id: "clip_n", assetId: "asset_unlisted", status: "Succeeded", durationSeconds: null, metadataJson: null }],
    });
    const orphan = describeMovieVideoMakeoverProjectSources(absent, assets(), {})[0];
    expect(orphan.gaps.map((gap) => gap.code)).toContain("size_unknown");
    expect(orphan.blockers.map((blocker) => blocker.code)).toContain("source_asset_not_video");
  });

  it("ignores an absent project and duplicate asset rows", () => {
    expect(describeMovieVideoMakeoverProjectSources(null, assets(), {})).toEqual([]);
    const doubled = [...assets(), assets()[0]];
    const oneClip = project({ clips: [{ id: "clip_1", assetId: "asset_take_01", status: "Succeeded", durationSeconds: 12, metadataJson: null }] });
    expect(describeMovieVideoMakeoverProjectSources(oneClip, doubled, {})).toHaveLength(1);
  });
});

describe("studio: attaching a source is a draft edit and nothing more", () => {
  it("writes the clip facts into the #784 selection through the bridge", () => {
    const next = reduceMovieVideoMakeoverStudioSource(settledDraftParts(), "asset_take_01", catalogue());
    expect(next.sourceAssetId).toBe("asset_take_01");
    expect(next.sourceSizeBytes).toBe(48_000_000);
    expect(next.sourceDurationSeconds).toBe(12);
    expect(next.mode).toBe("studio-swap");
    expect(next.presetId).toBe("studio.clean-neutral");
  });

  it("refuses to attach a clip the catalogue blocked", () => {
    const before = settledDraftParts();
    expect(reduceMovieVideoMakeoverStudioSource(before, "asset_take_02", catalogue())).toBe(before);
    expect(reduceMovieVideoMakeoverStudioSource(before, "asset_not_in_project", catalogue())).toBe(before);
  });

  it("keeps the draft non-billable while source facts are still unverified", () => {
    const selection = reduceMovieVideoMakeoverStudioSource(settledDraftParts(), "asset_take_01", catalogue());
    const result = admission(selection);
    // Ownership, visibility, retention and media facts came from the caller's
    // settled draft, and the bridge attached the clip's own facts on top.
    expect(result.status).toBe("blocked");
    expect(result.blockedReasons.map((reason) => reason.code)).toContain("source_metadata_missing");
    expect(result.launchPermitted).toBe(false);
    expect(result.billable).toBe(false);
    expect(result.estimatePermitted).toBe(false);
  });

  it("reaches ready-for-estimate only once every source fact is supplied", () => {
    const full = describeMovieVideoMakeoverProjectSources(
      project({ clips: [{ id: "clip_1", assetId: "asset_take_01", status: "Succeeded", durationSeconds: 12, metadataJson: null }] }),
      [{ ...assets()[0], status: "Active" }],
      { principalId: PRINCIPAL, workspaceId: WORKSPACE },
    );
    // The catalogue alone can never get there: the read model has no owner/visibility
    // /retention/dimension/frame-rate/audio fields, so the gaps stay open.
    const viaBridge = reduceMovieVideoMakeoverStudioSource(settledDraftParts(), "asset_take_01", full);
    expect(admission(viaBridge).blockedReasons.map((reason) => reason.code)).toContain("source_metadata_missing");

    // With those facts settled by a caller, the same draft admits and still grants
    // no execution, proving the bridge neither blocks nor enables wrongly.
    const settled = {
      ...viaBridge,
      sourceWidth: 1080,
      sourceHeight: 1920,
      sourceFrameRate: 30,
      sourceAudioPresent: false,
      sourceOwnerPrincipalId: PRINCIPAL,
      sourceVisibilityPrivate: true,
      sourceRetentionVerified: true,
    };
    const result = admission(settled);
    expect(result.status).toBe("ready-for-estimate");
    expect(result.uiEnabled).toBe(false);
    expect(result.providerJobCreated).toBe(false);
    expect(result.previewPermitted).toBe(true);
  });

  it("detaching removes the source and returns the draft to its blocking state", () => {
    const attached = reduceMovieVideoMakeoverStudioSource(settledDraftParts(), "asset_take_01", catalogue());
    const detached = reduceMovieVideoMakeoverStudioSource(attached, null, catalogue());
    expect(detached.sourceAssetId).toBeNull();
    expect(admission(detached).blockedReasons.map((reason) => reason.code)).toContain("source_missing");
  });

  it("drops a stale audio promise from the previous clip on switch", () => {
    const first = describeMovieVideoMakeoverProjectSources(
      project({ clips: [{ id: "clip_1", assetId: "asset_take_01", status: "Succeeded", durationSeconds: 12, metadataJson: null }] }),
      assets(),
      {},
    );
    const withAudio = { ...settledDraftParts(), sourceAudioPresent: true, preserveOriginalAudio: true };
    const next = reduceMovieVideoMakeoverStudioSource(withAudio, "asset_take_01", first);
    // The catalogue says nothing about audio, so the promise cannot survive.
    expect(next.sourceAudioPresent).toBeNull();
    expect(next.preserveOriginalAudio).toBe(false);
  });
});

describe("studio: markup composes the source step with the #784 panel", () => {
  it("renders both steps and the pending-fact count together", () => {
    const markup = render({
      project: project(),
      assets: assets(),
      principalId: PRINCIPAL,
      workspaceId: WORKSPACE,
      selection: reduceMovieVideoMakeoverStudioSource(settledDraftParts(), "asset_take_01", catalogue()),
    });
    expect(markup).toContain("movie-makeover-source-panel");
    expect(markup).toContain("movie-makeover-input-panel");
    expect(markup).toContain('data-pending-source-facts="6"');
    expect(markup).toMatch(/Attached, but 6 source facts still need verification/);
  });

  it("surfaces the future-feature answer for an outfit request even with a clip attached", () => {
    const markup = render({
      project: project(),
      assets: assets(),
      principalId: PRINCIPAL,
      workspaceId: WORKSPACE,
      selection: { ...reduceMovieVideoMakeoverStudioSource(settledDraftParts(), "asset_take_01", catalogue()), mode: "wardrobe-change" },
    });
    expect(markup).toMatch(/Outfit change is a future feature/i);
    // #784's own vocabulary still carries the same block, unchanged.
    expect(markup).toMatch(/wardrobe/i);
  });

  it("renders no execution control of its own", () => {
    const markup = render({
      project: project(),
      assets: assets(),
      principalId: PRINCIPAL,
      workspaceId: WORKSPACE,
      selection: settledDraftParts(),
    });
    const labels = buttonLabels(markup);
    // Exactly two enabled controls exist in this composition, and both are draft
    // edits: this slice's "Attach as source" and #784's pre-existing placeholder
    // source button. Every action control stays disabled, and a draft with no
    // source attached keeps #784's blocked wording rather than an estimate wording.
    expect(labels.filter((label) => label === "Attach as source")).toHaveLength(1);
    expect(markup).toContain('disabled=""');
    expect(markup).toContain('title="Not wired yet"');
    expect(markup).toContain("Resolve the items above first");
    expect(markup).toContain("launchPermitted: false");
    expect(labels.some((label) => label.startsWith("Estimate"))).toBe(false);
    const enabled = markup
      .split("<button")
      .slice(1)
      .filter((fragment) => !fragment.slice(0, fragment.indexOf(">")).includes("disabled"));
    expect(enabled.length).toBe(2);
  });

  it("says there is nothing to pick when the project has no clips", () => {
    const markup = render({
      project: project({ clips: [] }),
      assets: [],
      principalId: PRINCIPAL,
      workspaceId: WORKSPACE,
      selection: createEmptyMovieVideoMakeoverSelection(),
    });
    expect(markup).toMatch(/Nothing to show yet/);
    expect(markup).toMatch(/No source attached/);
  });
});
