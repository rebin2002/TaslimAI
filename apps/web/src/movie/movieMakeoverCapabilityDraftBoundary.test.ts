import { describe, expect, it } from "vitest";
import {
  MOVIE_VIDEO_MAKEOVER_FEATURE_KEYS,
  admitMovieVideoMakeoverDraft,
  createMovieVideoMakeoverDraft,
  movieVideoMakeoverDraftMayRequestEstimate,
} from "@/lib/movieVideoMakeoverDraftState";
import type {
  MovieVideoMakeoverAdmission,
  MovieVideoMakeoverCapabilityInput,
  MovieVideoMakeoverDraftInput,
} from "@/lib/movieVideoMakeoverDraftState";

/**
 * Cross-slice boundary regression for issue #698 (Movie Studio AI Video Makeover).
 *
 * What this pins is the seam between two unmerged slices, not either slice alone:
 *
 *   - PR #771 `feat/movie-video-edit-capability-vocabulary-20261113` adds the provider-neutral
 *     edit vocabulary (`ProviderCapabilityKinds.VideoEditing`, and the six
 *     `ProviderCapabilityFeatures` keys `edit-in-place`, `region-window-edit`,
 *     `source-audio-passthrough`, `identity-preserve`, `wardrobe-transform`, `frame-rate-conform`)
 *     to `apps/api/Movies/ProviderCapabilityRegistry.cs`. That same file's existing
 *     `ProviderCapabilityRegistryOptionsValidator.ValidateProvenance` requires a provenance
 *     `CapturedAtUtc` that `DateTime.TryParse(..., RoundtripKind)` accepts, i.e. an ISO-8601 instant.
 *   - PR #775 `feat/movie-video-makeover-draft-state-20261113` adds the non-billable draft and
 *     admission helper `apps/web/src/lib/movieVideoMakeoverDraftState.ts`, which mirrors those
 *     keys as client-side strings and gates a private source draft on them.
 *   - PR #765 fixes the spike contract those two slices cite: one verification date for every
 *     external claim (`2026-11-12`), a `V`/`C`/`U`/`N` evidence scale, and `honest-UX`
 *     ("never presents an unverified guarantee as a product promise").
 *
 * Existing coverage, and what it leaves unproven:
 *   - `apps/api.Tests/MovieVideoEditCapabilityVocabularyTests.cs` (#771) asserts server vocabulary
 *     only. It never sees a draft, an admission, or a permission bit.
 *   - `apps/web/src/lib/movieVideoMakeoverDraftState.test.ts` (#775) asserts the helper's own
 *     happy/fail-closed cases. Its capability cases use one invented key
 *     (`quantum-wardrobe-swap`), one unavailable report, one missing `sourceUrl`, and one
 *     duplicate. It never feeds a real legacy vocabulary key, the key PR #771 deliberately
 *     deferred (`keyframe-guided-edit`), an evidence label in the spike's single-letter form, a
 *     capture date that is not an ISO-8601 instant, a capture date far outside the verification
 *     date, or a verified `wardrobe-transform` observation attached to a wardrobe mode.
 *   - `apps/web/src/movie/videoMakeoverCapabilityDoc.test.ts` (#765) reads only the markdown.
 *
 * So nothing today proves that a capability observation written in the #771 vocabulary can never
 * widen a private source draft into billable execution, or into a UI permission claim. That is the
 * single gap this file closes. It is test-only: no source, route, provider, or billing change, no
 * network, no timers, no disk fixtures, fully deterministic.
 */

const OWNER = "principal_creator_01";
const WORKSPACE = "workspace_01";
const SOURCE_ASSET = "asset_source_video_01";

/** The Phase 1 editing keys exactly as PR #771 declares them. */
const PHASE_ONE_EDIT_KEYS = [
  "edit-in-place",
  "region-window-edit",
  "source-audio-passthrough",
  "identity-preserve",
  "wardrobe-transform",
  "frame-rate-conform",
] as const;

/** Keys that already exist in the same registry for generation/upscaling/mastering. */
const LEGACY_NON_EDIT_KEYS = [
  "text-to-video",
  "image-to-video",
  "video-to-video",
  "first-frame",
  "last-frame",
  "multi-keyframe",
  "extension",
  "interpolation",
  "native-audio",
  "reference-images",
  "hdr",
  "color-management",
  "denoise",
  "frame-interpolation",
] as const;

/** Proposed by the #765 spike and explicitly deferred by #771, so never admissible. */
const DEFERRED_EDIT_KEYS = ["keyframe-guided-edit"] as const;

/** Rejected by design in #698, not deferred: substitution and wardrobe-adjacent names. */
const OUT_OF_SCOPE_KEYS = [
  "wardrobe-change",
  "outfit-change",
  "person-replace",
  "face-swap",
  "voice-clone",
  "identity-replace",
] as const;

/**
 * The complete permission-and-safety shape the admission type may ever expose. Pinned so a new
 * execution-shaped field cannot be added to the boundary without failing this file first.
 */
const ADMISSION_KEYS = [
  "billable",
  "blockedReasons",
  "estimatePermitted",
  "launchPermitted",
  "mode",
  "previewPermitted",
  "providerJobCreated",
  "requiredFeatures",
  "status",
  "uiEnabled",
] as const;

function observation(
  feature: string,
  overrides: Record<string, unknown> = {},
): MovieVideoMakeoverCapabilityInput {
  return {
    feature,
    available: true,
    evidenceLabel: "verified",
    sourceUrl: `https://provider.example/docs/${feature}`,
    capturedAtUtc: "2026-11-12",
    ...overrides,
  } as unknown as MovieVideoMakeoverCapabilityInput;
}

/** A complete, honestly-consented draft over one private, owner-held source clip. */
function draftInput(overrides: Record<string, unknown> = {}): MovieVideoMakeoverDraftInput {
  return {
    mode: "studio-swap",
    presetId: "soft-studio-wide",
    prompt: "warm key light, neutral backdrop",
    source: {
      assetId: SOURCE_ASSET,
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
      observation("edit-in-place"),
      observation("identity-preserve"),
      observation("source-audio-passthrough"),
    ],
    ...overrides,
  } as unknown as MovieVideoMakeoverDraftInput;
}

function admit(overrides: Record<string, unknown> = {}): MovieVideoMakeoverAdmission {
  const input = draftInput(overrides);
  return admitMovieVideoMakeoverDraft(createMovieVideoMakeoverDraft(input));
}

function blockCodes(admission: MovieVideoMakeoverAdmission): string[] {
  return admission.blockedReasons.map((reason) => reason.code);
}

/**
 * The hard ceiling. Whatever the helper decides about capability currency, an observation can
 * never produce a billing, job, launch, or UI-enablement affordance, and the admission object can
 * never grow a field that claims one.
 */
function expectNonBillable(admission: MovieVideoMakeoverAdmission): void {
  expect(admission.launchPermitted).toBe(false);
  expect(admission.providerJobCreated).toBe(false);
  expect(admission.billable).toBe(false);
  expect(admission.uiEnabled).toBe(false);
  expect(Object.keys(admission).sort()).toEqual([...ADMISSION_KEYS].sort());
}

/** The two permissions this slice may ever grant, and nothing else, on any path. */
function truthyPermissions(admission: MovieVideoMakeoverAdmission): string[] {
  return Object.entries(admission)
    .filter(([, value]) => value === true)
    .map(([key]) => key)
    .sort();
}

const REQUIRED_OBSERVATIONS: readonly MovieVideoMakeoverCapabilityInput[] = [
  observation("identity-preserve"),
  observation("source-audio-passthrough"),
];

describe("vocabulary boundary: a registry key is not makeover support unless #775 admits it", () => {
  it("keeps the client mirror exactly equal to the Phase 1 edit vocabulary", () => {
    expect([...MOVIE_VIDEO_MAKEOVER_FEATURE_KEYS].sort()).toEqual([...PHASE_ONE_EDIT_KEYS].sort());
  });

  it("absorbs no legacy generation key, no deferred key, and no rejected substitution key", () => {
    const clientKeys = MOVIE_VIDEO_MAKEOVER_FEATURE_KEYS as readonly string[];
    for (const legacy of LEGACY_NON_EDIT_KEYS) {
      expect(clientKeys, `legacy key ${legacy} leaked into the edit mirror`).not.toContain(legacy);
    }
    for (const deferred of [...DEFERRED_EDIT_KEYS, ...OUT_OF_SCOPE_KEYS]) {
      expect(clientKeys, `${deferred} leaked into the edit mirror`).not.toContain(deferred);
    }
    expect(clientKeys).not.toContain("video-editing");
  });

  it.each([...LEGACY_NON_EDIT_KEYS, ...DEFERRED_EDIT_KEYS, ...OUT_OF_SCOPE_KEYS])(
    "treats a verified-looking %s observation as unknown, never as support",
    (feature) => {
      const admission = admit({
        capabilities: [
          observation("edit-in-place"),
          ...REQUIRED_OBSERVATIONS,
          observation(feature),
        ],
      });
      expect(blockCodes(admission)).toContain("capability_unknown");
      expect(admission.status).toBe("blocked");
      expect(admission.previewPermitted).toBe(false);
      expect(admission.estimatePermitted).toBe(false);
      expect(truthyPermissions(admission)).toEqual([]);
      expectNonBillable(admission);
      expect(movieVideoMakeoverDraftMayRequestEstimate(
        createMovieVideoMakeoverDraft(draftInput({
          capabilities: [
            observation("edit-in-place"),
            ...REQUIRED_OBSERVATIONS,
            observation(feature),
          ],
        })),
      )).toBe(false);
    },
  );

  it("cannot be coaxed by key case or padding into treating a foreign key as ours", () => {
    for (const feature of ["EDIT-IN-PLACE", " Edit-In-Place ", "identity-Preserve", "wardrobe_transform"]) {
      const admission = admit({
        capabilities: [observation(feature), ...REQUIRED_OBSERVATIONS],
      });
      expect(admission.status).toBe("blocked");
      expect(admission.estimatePermitted).toBe(false);
      expectNonBillable(admission);
    }
  });
});

describe("capability currency: stale, future-dated, unparseable, or absent evidence buys nothing", () => {
  it.each([
    ["blank", "   "],
    ["over the client bound", "x".repeat(41)],
  ])("blocks a verified claim whose capture date is %s", (_label, capturedAtUtc) => {
    const admission = admit({
      capabilities: [
        observation("edit-in-place", { capturedAtUtc }),
        ...REQUIRED_OBSERVATIONS,
      ],
    });
    expect(blockCodes(admission)).toContain("capability_unverified");
    expect(admission.status).toBe("blocked");
    expect(admission.previewPermitted).toBe(false);
    expect(admission.estimatePermitted).toBe(false);
    expectNonBillable(admission);
  });

  it.each([
    ["null", null],
    ["undefined", undefined],
    ["a number", 1763000000000],
    ["an object", { year: 2026 }],
    ["a boolean", true],
  ])("rejects a capture date supplied as %s instead of guessing one", (_label, capturedAtUtc) => {
    const admission = admit({
      capabilities: [observation("edit-in-place", { capturedAtUtc }), ...REQUIRED_OBSERVATIONS],
    });
    expect(blockCodes(admission)).toContain("capability_unverified");
    expect(admission.status).toBe("blocked");
    expect(admission.previewPermitted).toBe(false);
    expect(admission.estimatePermitted).toBe(false);
    expectNonBillable(admission);
  });

  /**
   * Characterization of the live divergence, stated as a ceiling rather than as approval.
   *
   * `admitMovieVideoMakeoverDraft` compares `capturedAtUtc` only for presence and length, while
   * the server-side registry validator that PR #771 extends requires an ISO-8601 instant, and the
   * #765 spike fixes a single verification date for every claim. So a claim captured years before
   * that date, or dated in the future, or written free-form, is still reported
   * `ready-for-estimate` by the draft slice. This file cannot fix that without a source change it
   * is not permitted to make; what it does prove is that the leniency cannot escalate: such a
   * draft never becomes billable, never becomes a job, and never becomes a UI affordance beyond
   * the estimate-only permissions this slice already owns. If #775 aligns admission with the
   * registry's provenance rule, the `ready-for-estimate` branch below must be flipped to
   * `blocked` — which is exactly the kind of change this pin is designed to surface.
   */
  it.each([
    ["stale by seven years", "2019-03-04"],
    ["stale by three decades", "1996-01-01"],
    ["future-dated", "2099-12-31"],
    ["free-form, not ISO-8601", "last tuesday"],
    ["impossible calendar day", "2026-13-45"],
    ["non-UTC bare month", "2026-11"],
  ] as const)("contains a %s capture date inside the non-billable ceiling", (_label, capturedAtUtc) => {
    const overrides = {
      capabilities: [
        observation("edit-in-place", { capturedAtUtc }),
        ...REQUIRED_OBSERVATIONS,
      ],
    };
    const admission = admit(overrides);
    expectNonBillable(admission);
    expect(admission.requiredFeatures).not.toContain("wardrobe-transform");
    expect(admission.requiredFeatures).not.toContain("keyframe-guided-edit");
    expect(admission.requiredFeatures).toContain("identity-preserve");
    if (admission.status === "ready-for-estimate") {
      expect(truthyPermissions(admission)).toEqual(["estimatePermitted", "previewPermitted"]);
    } else {
      expect(blockCodes(admission)).toContain("capability_unverified");
      expect(truthyPermissions(admission)).toEqual([]);
    }
  });

  it.each(["V", "C", "U", "N", "VERIFIED", " verified", "verified ", true, 1, ["verified"], { label: "verified" }])(
    "refuses the spike's %s evidence form as a verified label",
    (evidenceLabel) => {
      const admission = admit({
        capabilities: [observation("edit-in-place", { evidenceLabel }), ...REQUIRED_OBSERVATIONS],
      });
      expect(blockCodes(admission)).toContain("capability_unverified");
      expect(admission.status).toBe("blocked");
      expect(admission.previewPermitted).toBe(false);
      expect(admission.estimatePermitted).toBe(false);
      expectNonBillable(admission);
    },
  );

  it("blocks a verified claim that carries no citable provenance reference", () => {
    for (const sourceUrl of [null, undefined, "   ", "x".repeat(513)]) {
      const admission = admit({
        capabilities: [observation("edit-in-place", { sourceUrl }), ...REQUIRED_OBSERVATIONS],
      });
      expect(blockCodes(admission)).toContain("capability_unverified");
      expect(admission.status).toBe("blocked");
      expect(admission.estimatePermitted).toBe(false);
      expectNonBillable(admission);
    }
  });

  it.each(["trust me", "javascript:alert(1)", "file:///etc/passwd", "http://e.example/quick"])(
    "holds the ceiling for an unvalidated provenance reference %s",
    (sourceUrl) => {
      const admission = admit({
        capabilities: [observation("edit-in-place", { sourceUrl }), ...REQUIRED_OBSERVATIONS],
      });
      expectNonBillable(admission);
      expect(admission.requiredFeatures).not.toContain("wardrobe-transform");
      expect(admission.requiredFeatures).not.toContain("keyframe-guided-edit");
    },
  );
});

describe("declaration never implies authority: a capability claim cannot widen a draft", () => {
  it("keeps an over-claimed verified region-window-edit observation out of the required set", () => {
    const baseline = admit();
    const overClaimed = admit({
      capabilities: [
        observation("edit-in-place"),
        ...REQUIRED_OBSERVATIONS,
        observation("region-window-edit"),
      ],
    });
    expect(overClaimed.requiredFeatures).toEqual(baseline.requiredFeatures);
    expect(overClaimed.requiredFeatures).not.toContain("region-window-edit");
    expectNonBillable(overClaimed);
  });

  it("never promotes wardrobe-transform into a required feature, even when reported verified", () => {
    for (const mode of ["studio-swap", "relight", "restyle", null, "wardrobe-change", "outfit-change"]) {
      const admission = admit({
        mode,
        capabilities: [
          observation("edit-in-place"),
          observation("identity-preserve"),
          observation("source-audio-passthrough"),
          observation("frame-rate-conform"),
          observation("wardrobe-transform"),
        ],
      });
      expect(admission.requiredFeatures).not.toContain("wardrobe-transform");
      expectNonBillable(admission);
    }
  });

  it("blocks a wardrobe mode even when a verified wardrobe-transform observation is attached", () => {
    for (const mode of ["wardrobe-change", "outfit-change"]) {
      const admission = admit({
        mode,
        capabilities: [
          observation("edit-in-place"),
          observation("identity-preserve"),
          observation("wardrobe-transform"),
        ],
      });
      const codes = blockCodes(admission);
      expect(codes.filter((code) => code === "wardrobe_unsupported_future_feature")).toHaveLength(2);
      expect(codes).not.toContain("capability_unknown");
      expect(codes).not.toContain("mode_unsupported");
      expect(admission.mode).toBeNull();
      expect(admission.status).toBe("blocked");
      expect(admission.previewPermitted).toBe(false);
      expect(admission.estimatePermitted).toBe(false);
      expect(truthyPermissions(admission)).toEqual([]);
      expectNonBillable(admission);
    }
  });

  it("keeps identity substitution out of every Phase A requirement", () => {
    for (const mode of ["studio-swap", "relight", "restyle"]) {
      const admission = admit({ mode });
      for (const forbidden of ["person-replace", "face-swap", "voice-clone", "identity-replace"]) {
        expect(admission.requiredFeatures).not.toContain(forbidden);
      }
      expect(admission.requiredFeatures).toContain("identity-preserve");
      expectNonBillable(admission);
    }
  });

  it("requires an observation for every required feature and never defaults one to available", () => {
    const empty = admit({ capabilities: [] });
    expect(empty.status).toBe("blocked");
    expect(blockCodes(empty)).toContain("capability_missing");
    expect(empty.requiredFeatures.length).toBeGreaterThan(0);
    expect(empty.previewPermitted).toBe(false);
    expect(empty.estimatePermitted).toBe(false);
    expectNonBillable(empty);

    const unavailable = admit({
      capabilities: [
        observation("edit-in-place", { available: false }),
        observation("identity-preserve", { available: false }),
        observation("source-audio-passthrough", { available: false }),
      ],
    });
    expect(blockCodes(unavailable)).toContain("capability_unavailable");
    expect(unavailable.status).toBe("blocked");
    expectNonBillable(unavailable);
  });
});

describe("permission ceiling: preview and estimate are never execution, on every path", () => {
  it("keeps the private-source requirement blocking regardless of capability strength", () => {
    const admission = admit({
      source: { ...draftInput().source, visibility: "workspace-shared" },
      capabilities: [
        observation("edit-in-place"),
        observation("identity-preserve"),
        observation("source-audio-passthrough"),
        observation("frame-rate-conform"),
      ],
    });
    expect(blockCodes(admission)).toContain("source_visibility_not_private");
    expect(admission.status).toBe("blocked");
    expect(admission.previewPermitted).toBe(false);
    expect(admission.estimatePermitted).toBe(false);
    expectNonBillable(admission);
  });

  it("grants only preview and estimate on the admitted path and never a job or a charge", () => {
    const admitted = admit();
    expect(admitted.status).toBe("ready-for-estimate");
    expect(truthyPermissions(admitted)).toEqual(["estimatePermitted", "previewPermitted"]);
    expectNonBillable(admitted);
  });

  it("holds the ceiling across every defective capability shape exercised here", () => {
    const shapes: Array<Record<string, unknown>> = [
      { capabilities: [observation("edit-in-place", { capturedAtUtc: "2019-03-04" }), ...REQUIRED_OBSERVATIONS] },
      { capabilities: [observation("edit-in-place", { capturedAtUtc: "2099-12-31" }), ...REQUIRED_OBSERVATIONS] },
      { capabilities: [observation("edit-in-place", { capturedAtUtc: "last tuesday" }), ...REQUIRED_OBSERVATIONS] },
      { capabilities: [observation("edit-in-place", { evidenceLabel: "C" }), ...REQUIRED_OBSERVATIONS] },
      { capabilities: [observation("edit-in-place", { sourceUrl: "javascript:alert(1)" }), ...REQUIRED_OBSERVATIONS] },
      { capabilities: [observation("keyframe-guided-edit"), observation("edit-in-place"), ...REQUIRED_OBSERVATIONS] },
      { capabilities: [observation("video-to-video"), observation("edit-in-place"), ...REQUIRED_OBSERVATIONS] },
      { capabilities: [] },
      { mode: "wardrobe-change", capabilities: [observation("wardrobe-transform"), observation("edit-in-place"), observation("identity-preserve")] },
      { mode: "restyle", capabilities: [observation("edit-in-place", { capturedAtUtc: "1996-01-01" }), observation("identity-preserve"), observation("frame-rate-conform")] },
    ];
    for (const shape of shapes) {
      const admission = admit(shape);
      expectNonBillable(admission);
      expect(admission.requiredFeatures).not.toContain("wardrobe-transform");
      expect(admission.requiredFeatures).not.toContain("keyframe-guided-edit");
      expect(admission.requiredFeatures).not.toContain("video-to-video");
      if (admission.status === "blocked") {
        expect(admission.previewPermitted).toBe(false);
        expect(admission.estimatePermitted).toBe(false);
        expect(truthyPermissions(admission)).toEqual([]);
        expect(movieVideoMakeoverDraftMayRequestEstimate(
          createMovieVideoMakeoverDraft(draftInput(shape)),
        )).toBe(false);
      }
    }
  });
});
