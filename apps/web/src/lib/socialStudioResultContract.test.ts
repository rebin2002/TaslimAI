import { describe, expect, it } from "vitest";
import type { GenerationJob } from "./api";
import { parseSocialJobResult, socialStudioState } from "./socialStudioState";

/**
 * Acceptance coverage for issue #350: a successfully persisted Social generation
 * must travel `persisted assetId -> authorized API ResultJson -> socialStudioState
 * "succeeded" with a preview-ready post set` without ever landing a real success in
 * the `completed-unavailable` fallback.
 *
 * Everything below is a synthetic, provider-free fixture. The canonical document
 * mirrors the exact wire shape recorded for a real job run on main (issue #350
 * re-diagnosis, 2026-11-14): root members are camelCase, `assetId` is the
 * persisted `Asset.Id` appended by `GenerationJobExecution.AddPublishedAssetReference`,
 * and nested `posts` members must carry the canonical camelCase names produced by
 * `SocialGenerationJobHandler` once the producer-side fix (#878, owner lanes #299/#305)
 * lands. All identifiers are fake test constants; no real user data is used.
 *
 * This file is test-only and deliberately disjoint from every owned path:
 * `SocialGenerationJobHandler.cs` (#299/#305, #860, #878),
 * `SocialGenerationResultPayloadTests.cs` (#878),
 * `SocialPersistedAssetResultTests.cs` (#380/#425),
 * `socialStudioState.ts` (#340/#860), `SocialStudioView.tsx` (#251),
 * and `socialStudioState.test.ts`. It imports them, never edits them.
 */

const job = (overrides: Partial<GenerationJob> = {}): GenerationJob => ({
  id: "11111111-1111-4111-8111-111111111111",
  workspaceId: "workspace-1",
  projectId: null,
  jobType: "social.generate",
  status: "Running",
  title: null,
  progressPercent: 100,
  resultJson: null,
  errorCode: null,
  errorMessage: null,
  cancellationRequested: false,
  createdAt: "2026-01-01T00:00:00Z",
  queuedAt: null,
  startedAt: null,
  completedAt: null,
  failedAt: null,
  cancelledAt: null,
  outputs: [],
  ...overrides,
});

const persistedAssetId = "0e949132-005e-4029-9adf-0b90517274ec";

const canonicalPost = {
  order: 1,
  hook: "Our community workshop is now open for founders.",
  body: "Bring one idea and leave with a plan, a peer group, and a follow-up session booked.",
  callToAction: "Reserve a seat",
  hashtags: ["#taslim", "#founders"],
  altText: "Workshop announcement card",
  visualDirection: "Bold type on the brand gradient",
  assetRefs: [],
};

/** Root shape of `GenerationJobDto.ResultJson` for a successful Social job on main. */
const canonicalResultJson = JSON.stringify({
  socialType: "announcement",
  platform: "linkedin",
  title: "Community workshop announcement",
  language: "en",
  postCount: 1,
  posts: [canonicalPost],
  assetId: persistedAssetId,
  representations: [
    { id: "22222222-2222-4222-8222-222222222222", type: "json", fileName: "community-workshop-announcement.json", contentType: "application/json" },
  ],
});

/**
 * The failure shape currently persisted by `main`: the handler serializes
 * `SocialDraft.Posts` with default CLR member casing, so every nested key is
 * PascalCase while `assetId` is already correct. This is the exact payload that
 * makes Social Studio render `completed-unavailable` for a real success.
 */
const pascalCasePostsResultJson = JSON.stringify({
  socialType: "announcement",
  platform: "linkedin",
  title: "Community workshop announcement",
  language: "en",
  postCount: 1,
  posts: [
    {
      Order: canonicalPost.order,
      Hook: canonicalPost.hook,
      Body: canonicalPost.body,
      CallToAction: canonicalPost.callToAction,
      Hashtags: canonicalPost.hashtags,
      AltText: canonicalPost.altText,
      VisualDirection: canonicalPost.visualDirection,
      AssetRefs: canonicalPost.assetRefs,
    },
  ],
  assetId: persistedAssetId,
  representations: [],
});

describe("social studio result contract (issue #350, consumer acceptance)", () => {
  it("renders the persisted success payload as succeeded with a preview-ready result", () => {
    const succeeded = job({ status: "Succeeded", resultJson: canonicalResultJson });
    const result = parseSocialJobResult(succeeded);

    expect(socialStudioState(succeeded, result)).toBe("succeeded");
    expect(result?.assetId).toBe(persistedAssetId);
    expect(result?.posts).toHaveLength(1);

    // Preview-readiness: the exact members SocialStudioView renders, under the
    // canonical names the draft artifact (#878: result and draft must agree).
    const [post] = result!.posts!;
    expect(post).toMatchObject({
      order: 1,
      hook: canonicalPost.hook,
      body: canonicalPost.body,
      callToAction: canonicalPost.callToAction,
      hashtags: canonicalPost.hashtags,
      altText: canonicalPost.altText,
      visualDirection: canonicalPost.visualDirection,
    });
  });

  it("pins the producer contract that #878 must publish: lowercase post keys succeed, PascalCase post keys fall back", () => {
    // Current main behaviour, kept as an executable regression: PascalCase posts
    // are dropped by parsePosts so a real success renders completed-unavailable.
    const broken = job({ status: "Succeeded", resultJson: pascalCasePostsResultJson });
    const brokenResult = parseSocialJobResult(broken);
    expect(brokenResult?.assetId).toBe(persistedAssetId);
    expect(brokenResult?.posts).toHaveLength(0);
    expect(socialStudioState(broken, brokenResult)).toBe("completed-unavailable");

    // With the identical document except canonical post keys (the #878 wire shape),
    // the same terminal job must render the preview. If the producer casing drifts
    // again, this pair fails in CI instead of silently degrading the studio.
    const fixed = job({ status: "Succeeded", resultJson: canonicalResultJson });
    expect(socialStudioState(fixed, parseSocialJobResult(fixed))).toBe("succeeded");
  });

  it.each([
    ["absent", (payload: Record<string, unknown>) => delete payload.assetId],
    ["empty", (payload: Record<string, unknown>) => { payload.assetId = "   "; }],
    ["non-string", (payload: Record<string, unknown>) => { payload.assetId = 42; }],
  ])("keeps completed-unavailable when the persisted assetId is %s", (_label, mutate) => {
    const payload: Record<string, unknown> = JSON.parse(canonicalResultJson);
    mutate(payload);
    const terminal = job({ status: "Succeeded", resultJson: JSON.stringify(payload) });
    const result = parseSocialJobResult(terminal);
    expect(result?.assetId).toBeUndefined();
    expect(socialStudioState(terminal, result)).toBe("completed-unavailable");
  });

  it("keeps completed-unavailable when posts are empty or malformed alongside a valid assetId", () => {
    for (const posts of [[], [{ order: 2, body: "missing hook" }], "not-an-array", null]) {
      const payload = { ...JSON.parse(canonicalResultJson), posts };
      const terminal = job({ status: "Succeeded", resultJson: JSON.stringify(payload) });
      expect(socialStudioState(terminal, parseSocialJobResult(terminal))).toBe("completed-unavailable");
    }
  });

  it("tolerates the appended representations member without changing the verdict", () => {
    const withoutRepresentations = JSON.parse(canonicalResultJson) as Record<string, unknown>;
    delete withoutRepresentations.representations;
    const terminal = job({ status: "Succeeded", resultJson: JSON.stringify(withoutRepresentations) });
    expect(socialStudioState(terminal, parseSocialJobResult(terminal))).toBe("succeeded");
  });

  it("never treats a non-terminal job as succeeded even with the canonical payload", () => {
    for (const status of ["Pending", "Queued", "Running"] as const) {
      const terminal = job({ status, resultJson: canonicalResultJson });
      expect(["pending", "queued", "running"]).toContain(socialStudioState(terminal, parseSocialJobResult(terminal)));
    }
    for (const status of ["Failed", "Cancelled"] as const) {
      const terminal = job({ status, resultJson: canonicalResultJson });
      expect(socialStudioState(terminal, parseSocialJobResult(terminal))).toBe(status === "Failed" ? "failed" : "cancelled");
    }
  });

  it("holds no secret material in the result contract - only opaque synthetic ids reach the preview", () => {
    // Authorization boundary (consumer side): the contract carries opaque ids only.
    // Whether `assetId` names an asset the caller may read is enforced server-side
    // by the workspace/creator-scoped job and asset endpoints (privacy lanes #353/#361);
    // the frontend must neither fabricate an id nor infer one from other fields.
    const noAsset = { ...JSON.parse(canonicalResultJson), assetId: undefined } as Record<string, unknown>;
    delete noAsset.assetId;
    const terminal = job({ status: "Succeeded", resultJson: JSON.stringify(noAsset) });
    const result = parseSocialJobResult(terminal);
    expect(result?.assetId).toBeUndefined();
    expect(result?.posts).toHaveLength(1);
    expect(socialStudioState(terminal, result)).toBe("completed-unavailable");

    // A storage key, private path, or other-user hint must not be inferred into an id.
    const leaked = job({
      status: "Succeeded",
      resultJson: JSON.stringify({ ...JSON.parse(canonicalResultJson), storageKey: "tenants/other/asset.bin" }),
    });
    const leakedResult = parseSocialJobResult(leaked);
    expect(Object.keys(leakedResult!)).toEqual(["assetId", "socialType", "platform", "title", "language", "postCount", "posts"]);
    expect(leakedResult!.assetId).toBe(persistedAssetId);
  });
});
