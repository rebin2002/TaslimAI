import { describe, expect, it } from "vitest";
import {
  describeVerifiedMovieMakeoverServerSources,
  type MovieMakeoverServerSourceResponse,
  type MovieMakeoverServerSourceRow,
} from "./movieVideoMakeoverServerSourceAdapter";

const context = { principalId: "creator_a", workspaceId: "workspace_a" };
const projectId = "project_a";
const row: MovieMakeoverServerSourceRow = {
  clipId: "clip_1",
  movieProjectId: projectId,
  assetId: "asset_1",
  clipStatus: "Ready",
  assetType: "video",
  assetStatus: "active",
  contentType: "video/mp4",
  sizeBytes: 123456,
  durationSeconds: 12.5,
  width: 1920,
  height: 1080,
  frameRate: null,
  frameRateVerified: false,
  audioState: null,
  audioStateVerified: false,
  ownershipVerified: true,
  ownerIsRequester: true,
  visibilityPrivate: true,
  retentionVerified: true,
  storageAvailable: true,
  attachPermitted: true,
  blockers: [],
  pendingFacts: ["frame_rate", "audio_state"],
};
const response: MovieMakeoverServerSourceResponse = {
  module: "video-makeover-sources",
  movieProjectId: projectId,
  workspaceId: context.workspaceId,
  sources: [row],
};
const map = (
  value: MovieMakeoverServerSourceResponse | null = response,
  scope: { principalId: string | null; workspaceId: string | null } = context,
) =>
  describeVerifiedMovieMakeoverServerSources(value, projectId, scope);

describe("authenticated source metadata adapter for provider-free Movie Makeover", () => {
  it("maps authenticated source facts without inventing audio or frame rate", () => {
    const entry = expectOne(map());
    expect(entry.assetId).toBe("asset_1");
    expect(entry.selectable).toBe(true);
    expect(entry.source.ownerPrincipalId).toBe(context.principalId);
    expect(entry.source.workspaceId).toBe(context.workspaceId);
    expect(entry.source.visibility).toBe("private");
    expect(entry.source.retentionPolicyVerified).toBe(true);
    expect(entry.source.width).toBe(1920);
    expect(entry.source.durationSeconds).toBe(12.5);
    expect(entry.source.frameRate).toBeNull();
    expect(entry.source.audioPresent).toBeNull();
    expect(entry.gaps.map((gap) => gap.code)).toContain("frame_rate_unknown");
    expect(entry.gaps.map((gap) => gap.code)).toContain("audio_state_unknown");
  });

  it("never elevates a server-declined source to attachable", () => {
    const entry = expectOne(map({ ...response, sources: [{ ...row, attachPermitted: false }] }));
    expect(entry.selectable).toBe(false);
    expect(entry.blockers.map((item) => item.code)).toContain("source_asset_unavailable");
  });

  it("fails closed when the server returns a blocker together with attachPermitted=true", () => {
    const conflicting = expectOne(map({
      ...response,
      sources: [{ ...row, attachPermitted: true, blockers: ["source_storage_unavailable"] }],
    }));
    expect(conflicting.selectable).toBe(false);
    expect(conflicting.blockers.map((item) => item.code)).toContain("source_asset_unavailable");

    // The runtime boundary must also refuse a malformed response missing the
    // server's blocker array, rather than assuming its absence means approval.
    const malformed = expectOne(map({
      ...response,
      sources: [{ ...row, blockers: undefined as unknown as string[] }],
    }));
    expect(malformed.selectable).toBe(false);
  });

  it("refuses mismatched or unverified source ownership even when the server exposes a row", () => {
    for (const override of [
      { ownerIsRequester: false },
      { ownershipVerified: false },
      { ownerIsRequester: false, ownershipVerified: false },
    ]) {
      const entry = expectOne(map({ ...response, sources: [{ ...row, ...override }] }));
      expect(entry.selectable).toBe(false);
      expect(entry.source.ownerPrincipalId).toBeNull();
      expect(entry.blockers.map((item) => item.code)).toContain("source_cross_principal");
    }
  });

  it("rejects wrong project/workspace/anonymous and mismatched row identifiers", () => {
    expect(map(null)).toEqual([]);
    expect(map(response, { ...context, principalId: null })).toEqual([]);
    expect(map(response, { ...context, workspaceId: "other" })).toEqual([]);
    expect(map({ ...response, movieProjectId: "other" })).toEqual([]);
    expect(map({ ...response, module: "unexpected" })).toEqual([]);
    expect(map({ ...response, sources: [{ ...row, movieProjectId: "other" }] })).toEqual([]);
  });

  it("deduplicates clips, retaining the first authorized descriptor only", () => {
    const entries = map({
      ...response,
      sources: [
        row,
        { ...row, assetId: "not_a_second_asset", attachPermitted: false },
        { ...row, clipId: "clip_2", assetId: "asset_2" },
      ],
    });
    expect(entries).toHaveLength(2);
    expect(entries[0].assetId).toBe("asset_1");
    expect(entries[1].assetId).toBe("asset_2");
  });
});

function expectOne(entries: readonly ReturnType<typeof map>[number][]) {
  expect(entries).toHaveLength(1);
  return entries[0];
}
