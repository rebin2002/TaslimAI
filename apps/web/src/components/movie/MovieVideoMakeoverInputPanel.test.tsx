import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import {
  MovieVideoMakeoverInputPanel,
  type MovieVideoMakeoverInputPanelProps,
} from "./MovieVideoMakeoverInputPanel";
import {
  MOVIE_VIDEO_MAKEOVER_UI_MODE_CHOICES,
  buildMovieVideoMakeoverDraftInput,
  createEmptyMovieVideoMakeoverSelection,
  movieVideoMakeoverSelectedRequiredFeatures,
  type MovieVideoMakeoverCapabilitySelection,
  type MovieVideoMakeoverSelection,
} from "./movieVideoMakeoverInputState";
import { admitMovieVideoMakeoverDraft, createMovieVideoMakeoverDraft } from "@/lib/movieVideoMakeoverDraftState";

const noChange: MovieVideoMakeoverInputPanelProps["onChange"] = () => {
  throw new Error("static render must not invoke onChange");
};

function render(selection: MovieVideoMakeoverSelection): string {
  return renderToStaticMarkup(<MovieVideoMakeoverInputPanel selection={selection} onChange={noChange} />);
}

const provenance = {
  sourceUrl: "https://providers.example.test/docs/video-edit",
  capturedAtUtc: "2026-11-13T00:00:00Z",
};

function capability(
  feature: MovieVideoMakeoverCapabilitySelection["feature"],
  evidenceLabel: MovieVideoMakeoverCapabilitySelection["evidenceLabel"] = "verified",
): MovieVideoMakeoverCapabilitySelection {
  return { feature, selected: true, evidenceLabel, ...provenance };
}

function completeSelection(): MovieVideoMakeoverSelection {
  return {
    ...createEmptyMovieVideoMakeoverSelection(),
    mode: "studio-swap",
    presetId: "studio.clean-neutral",
    prompt: "neutral cyclorama, soft key",
    sourceAssetId: "asset-77",
    sourceOwnerPrincipalId: "principal-9",
    sourceWorkspaceId: "workspace-3",
    sourceVisibilityPrivate: true,
    sourceRetentionVerified: true,
    sourceAudioPresent: true,
    sourceDurationSeconds: 42,
    sourceWidth: 1920,
    sourceHeight: 1080,
    sourceFrameRate: 30,
    sourceSizeBytes: 52428800,
    preserveOriginalAudio: true,
    preserveSubjectIdentity: true,
    creatorApproved: true,
    approvalReference: "consent-record-12",
    capabilities: [
      capability("edit-in-place"),
      capability("identity-preserve"),
      capability("source-audio-passthrough"),
    ],
  };
}

describe("movieVideoMakeoverInputState", () => {
  it("offers exactly the three Phase A modes plus one honest future-feature entry", () => {
    expect(MOVIE_VIDEO_MAKEOVER_UI_MODE_CHOICES.map((choice) => choice.value)).toEqual([
      "studio-swap",
      "relight",
      "restyle",
      "wardrobe-change",
    ]);
    const future = MOVIE_VIDEO_MAKEOVER_UI_MODE_CHOICES.filter((choice) => !choice.supported);
    expect(future).toHaveLength(1);
    expect(future[0].value).toBe("wardrobe-change");
  });

  it("derives required features from the shared mode requirements without inventing keys", () => {
    const restyle = { ...createEmptyMovieVideoMakeoverSelection(), mode: "restyle" as const };
    expect(movieVideoMakeoverSelectedRequiredFeatures(restyle)).toEqual([
      "edit-in-place",
      "identity-preserve",
      "frame-rate-conform",
    ]);
    const wardrobe = { ...createEmptyMovieVideoMakeoverSelection(), mode: "wardrobe-change" as const };
    expect(movieVideoMakeoverSelectedRequiredFeatures(wardrobe)).toEqual([]);
  });

  it("emits no capability observation for rows the creator did not select", () => {
    const selection = {
      ...createEmptyMovieVideoMakeoverSelection(),
      capabilities: [{ ...capability("edit-in-place"), selected: false }],
    };
    expect(buildMovieVideoMakeoverDraftInput(selection).capabilities).toEqual([]);
  });
});

describe("MovieVideoMakeoverInputPanel admission wiring", () => {
  it("keeps a complete selection ready-for-estimate while safety flags stay literal false", () => {
    const admission = admitMovieVideoMakeoverDraft(
      createMovieVideoMakeoverDraft(buildMovieVideoMakeoverDraftInput(completeSelection())),
    );
    expect(admission.status).toBe("ready-for-estimate");
    expect(admission.launchPermitted).toBe(false);
    expect(admission.billable).toBe(false);
    expect(admission.providerJobCreated).toBe(false);
    expect(admission.uiEnabled).toBe(false);
  });

  it("reports wardrobe honestly as an unsupported future feature", () => {
    const admission = admitMovieVideoMakeoverDraft(
      createMovieVideoMakeoverDraft(
        buildMovieVideoMakeoverDraftInput({
          ...completeSelection(),
          mode: "wardrobe-change",
          preserveOriginalAudio: false,
        }),
      ),
    );
    expect(admission.status).toBe("blocked");
    expect(admission.blockedReasons.map((reason) => reason.code)).toContain("wardrobe_unsupported_future_feature");
  });

  it("blocks vendor-claim evidence for a required feature as unverified", () => {
    const admission = admitMovieVideoMakeoverDraft(
      createMovieVideoMakeoverDraft(
        buildMovieVideoMakeoverDraftInput({
          ...completeSelection(),
          capabilities: [
            capability("edit-in-place"),
            capability("identity-preserve"),
            capability("source-audio-passthrough", "vendor-claim"),
          ],
        }),
      ),
    );
    expect(admission.status).toBe("blocked");
    expect(admission.blockedReasons.map((reason) => reason.code)).toContain("capability_unverified");
  });

  it("blocks audio preservation when the source audio state is unknown", () => {
    const admission = admitMovieVideoMakeoverDraft(
      createMovieVideoMakeoverDraft(
        buildMovieVideoMakeoverDraftInput({ ...completeSelection(), sourceAudioPresent: null }),
      ),
    );
    expect(admission.status).toBe("blocked");
    expect(admission.blockedReasons.map((reason) => reason.code)).toContain("source_audio_state_unknown");
  });
});

describe("MovieVideoMakeoverInputPanel rendering", () => {
  it("renders disabled estimate/start controls and the false safety flags on an empty draft", () => {
    const html = render(createEmptyMovieVideoMakeoverSelection());
    expect(html).toContain('aria-label="AI Video Makeover inputs"');
    expect(html).toContain("Draft input only. No provider is wired");
    expect(html).toContain("Select studio swap, relight, or restyle.");
    expect(html.match(/<button[^>]*disabled=""/g)?.length).toBeGreaterThanOrEqual(2);
    expect(html).toContain("title=\"Not wired yet\"");
    expect(html).toContain("launchPermitted: false · billable: false · providerJobCreated: false · uiEnabled: false");
  });

  it("labels the wardrobe option as a future feature instead of hiding it", () => {
    const html = render({ ...createEmptyMovieVideoMakeoverSelection(), mode: "wardrobe-change" });
    expect(html).toContain("Future feature - not supported by any verified route yet");
    expect(html).toContain("Wardrobe change is a future feature and is not supported by any verified route.");
  });

  it("shows a resolved status but still no enabled action on a complete draft", () => {
    const html = render(completeSelection());
    expect(html).toContain("Draft complete - ready for a non-billable estimate when one is wired.");
    expect(html).toContain("Estimate is not wired yet");
    expect(html).toContain('<button type="button" disabled="" title="Not wired yet">Start makeover</button>');
  });
});
