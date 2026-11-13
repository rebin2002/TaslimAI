import { describe, expect, it } from "vitest";
import {
  MOVIE_VIDEO_MAKEOVER_FEATURE_KEYS,
  MOVIE_VIDEO_MAKEOVER_INPUT_LIMITS,
  MOVIE_VIDEO_MAKEOVER_MODES,
  admitMovieVideoMakeoverDraft,
  createMovieVideoMakeoverDraft,
  isMovieVideoMakeoverMode,
  movieVideoMakeoverDraftMayRequestEstimate,
} from "@/lib/movieVideoMakeoverDraftState";
import type {
  MovieVideoMakeoverBlockCode,
  MovieVideoMakeoverDraftInput,
} from "@/lib/movieVideoMakeoverDraftState";

/**
 * Deterministic unit tests for the provider-neutral AI Video Makeover draft and
 * admission helper (issue #698). Every case is a pure function call: no network,
 * no provider SDK, no timers, no fixtures loaded from disk, no billing.
 *
 * The suite deliberately separates three things that are easy to conflate:
 *   - a draft that may be estimated (non-billable),
 *   - a preview that may be rendered,
 *   - a job that may be launched (never permitted by this helper).
 */

const OWNER = "principal_creator_01";
const OTHER = "principal_someone_else";
const WORKSPACE = "workspace_01";
const ASSET = "asset_source_video_01";

function capability(
  feature: string,
  overrides: Record<string, unknown> = {},
) {
  return {
    feature,
    available: true,
    evidenceLabel: "verified",
    sourceUrl: "https://provider.example/docs/" + feature,
    capturedAtUtc: "2026-11-12",
    ...overrides,
  };
}

/** Baseline input that is genuinely complete for every Phase A mode. */
function validInput(overrides: Partial<MovieVideoMakeoverDraftInput> = {}): MovieVideoMakeoverDraftInput {
  return {
    mode: "studio-swap",
    presetId: "soft-studio-wide",
    prompt: "warm key light, neutral backdrop",
    source: {
      assetId: ASSET,
      ownerPrincipalId: OWNER,
      workspaceId: WORKSPACE,
      visibility: "private",
      retentionPolicyVerified: true,
      durationSeconds: 42,
      width: 1920,
      height: 1080,
      frameRate: 30,
      audioPresent: true,
      sizeBytes: 268435456,
    },
    consent: {
      creatorApproved: true,
      approvedByPrincipalId: OWNER,
      approvalReference: "consent_2026_11_12_0001",
    },
    preservation: { preserveOriginalAudio: true, preserveSubjectIdentity: true },
    capabilities: [
      capability("edit-in-place"),
      capability("identity-preserve"),
      capability("source-audio-passthrough"),
      capability("frame-rate-conform"),
    ],
    ...overrides,
  };
}

function admit(input: MovieVideoMakeoverDraftInput) {
  return admitMovieVideoMakeoverDraft(createMovieVideoMakeoverDraft(input));
}

function codes(result: ReturnType<typeof admit>): MovieVideoMakeoverBlockCode[] {
  return result.blockedReasons.map((reason) => reason.code);
}

describe("movieVideoMakeoverDraftState constants", () => {
  it("exposes exactly the three Phase A modes and never wardrobe", () => {
    expect(MOVIE_VIDEO_MAKEOVER_MODES).toEqual(["studio-swap", "relight", "restyle"]);
    expect(MOVIE_VIDEO_MAKEOVER_FEATURE_KEYS).not.toContain("wardrobe");
    expect(isMovieVideoMakeoverMode("wardrobe-change")).toBe(false);
    expect(isMovieVideoMakeoverMode("relight")).toBe(true);
  });
});

describe("admission: missing or absent inputs fail closed", () => {
  it("blocks when no source video is attached", () => {
    const input = validInput();
    delete (input as { source?: unknown }).source;
    const result = admit(input);
    expect(result.status).toBe("blocked");
    expect(codes(result)).toContain("source_missing");
    expect(result.estimatePermitted).toBe(false);
  });

  it("blocks when the source identifier is blank rather than inventing one", () => {
    const result = admit(validInput({
      source: { ...validInput().source, assetId: "   " },
    }));
    expect(codes(result)).toContain("source_missing");
  });

  it("blocks when the creator has not explicitly approved", () => {
    const result = admit(validInput({
      consent: { creatorApproved: false, approvedByPrincipalId: OWNER, approvalReference: "x_1" },
    }));
    expect(result.status).toBe("blocked");
    expect(codes(result)).toContain("consent_missing");
    expect(codes(result)).not.toContain("source_missing");
  });

  it("blocks when approval is granted but names no approving creator", () => {
    const result = admit(validInput({
      consent: { creatorApproved: true, approvedByPrincipalId: null, approvalReference: "consent_1" },
    }));
    expect(codes(result)).toContain("consent_reference_invalid");
  });

  it("blocks when the approving creator does not own the source video", () => {
    const result = admit(validInput({
      consent: { creatorApproved: true, approvedByPrincipalId: OTHER, approvalReference: "consent_1" },
    }));
    expect(codes(result)).toContain("consent_principal_mismatch");
  });

  it("blocks when source ownership or retention is unverified", () => {
    const unowned = admit(validInput({
      source: { ...validInput().source, ownerPrincipalId: null },
    }));
    expect(codes(unowned)).toContain("source_ownership_unverified");

    const unretained = admit(validInput({
      source: { ...validInput().source, retentionPolicyVerified: null },
    }));
    expect(codes(unretained)).toContain("source_retention_unverified");
  });

  it("blocks when the source is not private to its owner", () => {
    const result = admit(validInput({
      source: { ...validInput().source, visibility: "workspace-shared" },
    }));
    expect(codes(result)).toContain("source_visibility_not_private");
  });

  it("blocks when identity preservation is switched off", () => {
    const result = admit(validInput({
      preservation: { preserveOriginalAudio: true, preserveSubjectIdentity: false },
    }));
    expect(result.status).toBe("blocked");
    expect(codes(result)).toContain("identity_preservation_required");
  });
});

describe("admission: invalid mode and preset", () => {
  it("blocks an absent mode", () => {
    const result = admit(validInput({ mode: null }));
    expect(codes(result)).toContain("mode_missing");
    expect(result.mode).toBeNull();
  });

  it("blocks a whitespace mode as absent, not as a supported mode", () => {
    const result = admit(validInput({ mode: "   " }));
    expect(codes(result)).toContain("mode_missing");
  });

  it("blocks an unrecognised mode without guessing a supported one", () => {
    const result = admit(validInput({ mode: "talking-head-reinvent" }));
    expect(result.status).toBe("blocked");
    expect(codes(result)).toContain("mode_unsupported");
    expect(result.mode).toBeNull();
  });

  it("blocks a mode that matches a supported name only case-insensitively", () => {
    const result = admit(validInput({ mode: "Relight" }));
    expect(codes(result)).toContain("mode_unsupported");
  });

  it("blocks wardrobe change as an unsupported future feature, never as relight or restyle", () => {
    for (const mode of ["wardrobe-change", "outfit-change"]) {
      const result = admit(validInput({ mode }));
      expect(result.status).toBe("blocked");
      expect(codes(result)).toContain("wardrobe_unsupported_future_feature");
      expect(result.mode).toBeNull();
      expect(codes(result)).not.toContain("mode_unsupported");
    }
  });

  it("blocks a missing preset", () => {
    const result = admit(validInput({ presetId: null }));
    expect(codes(result)).toContain("preset_missing");
  });

  it("blocks a preset identifier that is not bounded and path-safe", () => {
    expect(codes(admit(validInput({ presetId: "Studio One" })))).toContain("preset_invalid");
    expect(codes(admit(validInput({ presetId: "-leading-dash" })))).toContain("preset_invalid");
    expect(codes(admit(validInput({ presetId: "a".repeat(400) })))).toContain("preset_invalid");
  });

  it("blocks an over-length optional prompt", () => {
    const result = admit(validInput({ prompt: "p".repeat(MOVIE_VIDEO_MAKEOVER_INPUT_LIMITS.maxPromptLength + 1) }));
    expect(codes(result)).toContain("prompt_invalid");
  });
});

describe("admission: source metadata is validated and bounded", () => {
  it("blocks incomplete source metadata", () => {
    const result = admit(validInput({
      source: { ...validInput().source, durationSeconds: null },
    }));
    expect(codes(result)).toContain("source_metadata_missing");
  });

  it("blocks source metadata outside the bounds", () => {
    const overlong = admit(validInput({
      source: { ...validInput().source, durationSeconds: MOVIE_VIDEO_MAKEOVER_INPUT_LIMITS.maxDurationSeconds + 1 },
    }));
    expect(codes(overlong)).toContain("source_metadata_out_of_bounds");

    const overSized = admit(validInput({
      source: { ...validInput().source, sizeBytes: MOVIE_VIDEO_MAKEOVER_INPUT_LIMITS.maxSourceBytes + 1 },
    }));
    expect(codes(overSized)).toContain("source_metadata_out_of_bounds");

    const nonFinite = admit(validInput({
      source: { ...validInput().source, frameRate: Number.NaN },
    }));
    expect(codes(nonFinite)).toContain("source_metadata_out_of_bounds");
  });

  it("blocks audio preservation when the source audio state is unknown", () => {
    const result = admit(validInput({
      source: { ...validInput().source, audioPresent: null },
    }));
    expect(result.status).toBe("blocked");
    expect(codes(result)).toContain("source_audio_state_unknown");
    expect(codes(result)).not.toContain("source_metadata_missing");
  });
});

describe("admission: unverified capability never becomes support", () => {
  it("blocks a required feature with no observation at all", () => {
    const result = admit(validInput({
      capabilities: [capability("identity-preserve")],
    }));
    expect(codes(result)).toContain("capability_missing");
  });

  it("blocks a required feature reported unavailable", () => {
    const result = admit(validInput({
      capabilities: [
        capability("edit-in-place", { available: false }),
        capability("identity-preserve"),
        capability("source-audio-passthrough"),
        capability("frame-rate-conform"),
      ],
    }));
    expect(codes(result)).toContain("capability_unavailable");
  });

  it.each(["vendor-claim", "unverified", "unknown"] as const)(
    "blocks a verified-looking feature carrying only a %s label",
    (evidenceLabel) => {
      const result = admit(validInput({
        capabilities: [
          capability("edit-in-place", { evidenceLabel }),
          capability("identity-preserve"),
          capability("source-audio-passthrough"),
          capability("frame-rate-conform"),
        ],
      }));
      expect(codes(result)).toContain("capability_unverified");
    },
  );

  it("blocks a feature labelled verified but lacking provenance", () => {
    const result = admit(validInput({
      capabilities: [
        capability("edit-in-place", { sourceUrl: null }),
        capability("identity-preserve"),
        capability("source-audio-passthrough"),
        capability("frame-rate-conform"),
      ],
    }));
    expect(codes(result)).toContain("capability_unverified");
  });

  it("blocks an unrecognised capability key instead of treating it as support", () => {
    const result = admit(validInput({
      capabilities: [
        capability("edit-in-place"),
        capability("identity-preserve"),
        capability("source-audio-passthrough"),
        capability("frame-rate-conform"),
        capability("quantum-wardrobe-swap"),
      ],
    }));
    expect(codes(result)).toContain("capability_unknown");
    expect(result.status).toBe("blocked");
  });

  it("blocks wardrobe-transform even when reported as verified, and dedupes the reason", () => {
    const result = admit(validInput({
      capabilities: [
        capability("edit-in-place"),
        capability("identity-preserve"),
        capability("source-audio-passthrough"),
        capability("frame-rate-conform"),
        capability("wardrobe-transform"),
      ],
    }));
    expect(codes(result)).toContain("wardrobe_unsupported_future_feature");
    expect(codes(result).filter((code) => code === "wardrobe_unsupported_future_feature")).toHaveLength(1);
  });

  it("blocks a duplicate observation for the same feature", () => {
    const result = admit(validInput({
      capabilities: [capability("edit-in-place"), capability("edit-in-place")],
    }));
    expect(codes(result)).toContain("capability_input_invalid");
  });
});

describe("admission: valid drafts", () => {
  it("admits each Phase A mode on its own verified features", () => {
    expect(admit(validInput({ mode: "studio-swap" })).status).toBe("ready-for-estimate");
    expect(admit(validInput({ mode: "relight" })).status).toBe("ready-for-estimate");
    expect(admit(validInput({ mode: "restyle" })).status).toBe("ready-for-estimate");
  });

  it("reports the narrowed mode and a deterministic, vocabulary-ordered feature list", () => {
    const result = admit(validInput({ mode: "restyle" }));
    expect(result.status).toBe("ready-for-estimate");
    if (result.status !== "ready-for-estimate") {
      return;
    }
    expect(result.mode).toBe("restyle");
    expect(result.requiredFeatures).toEqual(["edit-in-place", "source-audio-passthrough", "identity-preserve", "frame-rate-conform"]);
    expect(result.blockedReasons).toHaveLength(0);
  });

  it("does not require audio passthrough for a source that has no audio", () => {
    const result = admit(validInput({
      source: { ...validInput().source, audioPresent: false },
      capabilities: [capability("edit-in-place"), capability("identity-preserve")],
    }));
    expect(result.status).toBe("ready-for-estimate");
    if (result.status === "ready-for-estimate") {
      expect(result.requiredFeatures).not.toContain("source-audio-passthrough");
    }
  });

  it("is deterministic: the same draft admits identically on repeated calls", () => {
    const draft = createMovieVideoMakeoverDraft(validInput());
    const first = admitMovieVideoMakeoverDraft(draft);
    const second = admitMovieVideoMakeoverDraft(draft);
    expect(JSON.stringify(first)).toBe(JSON.stringify(second));
    expect(movieVideoMakeoverDraftMayRequestEstimate(draft)).toBe(true);
  });

  it("does not mutate the input it was handed", () => {
    const input = validInput();
    const frozen = JSON.parse(JSON.stringify(input)) as MovieVideoMakeoverDraftInput;
    createMovieVideoMakeoverDraft(input);
    expect(JSON.stringify(input)).toBe(JSON.stringify(frozen));
  });
});

describe("non-billable safety invariant", () => {
  const blockedDraft = admit(validInput({ mode: "wardrobe-change" }));
  const readyDraft = admit(validInput());

  it.each([
    ["blocked draft", blockedDraft],
    ["ready-for-estimate draft", readyDraft],
  ] as const)("%s never permits launch, job creation, billing, or UI enablement", (_label, result) => {
    expect(result.launchPermitted).toBe(false);
    expect(result.providerJobCreated).toBe(false);
    expect(result.billable).toBe(false);
    expect(result.uiEnabled).toBe(false);
  });

  it("keeps preview and estimate permission separate from launch permission", () => {
    expect(readyDraft.status).toBe("ready-for-estimate");
    expect(readyDraft.previewPermitted).toBe(true);
    expect(readyDraft.estimatePermitted).toBe(true);
    expect(readyDraft.launchPermitted).toBe(false);

    expect(blockedDraft.previewPermitted).toBe(false);
    expect(blockedDraft.estimatePermitted).toBe(false);
  });

  it("returns at most one reason per code and field pair", () => {
    const result = admit(validInput({
      presetId: null,
      consent: { creatorApproved: false, approvedByPrincipalId: null, approvalReference: null },
    }));
    const keys = result.blockedReasons.map((reason) => reason.code + "|" + reason.field);
    expect(new Set(keys).size).toBe(keys.length);
    expect(keys.length).toBeGreaterThan(0);
  });
});
