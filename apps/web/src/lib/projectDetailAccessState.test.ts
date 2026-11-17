import { describe, expect, it } from "vitest";
import { resolveProjectDetailSnapshotAfterFailure } from "./projectDetailAccessState";

function apiLikeError(status: number, message = "Something went wrong.") {
  return Object.assign(new Error(message), { status });
}

/**
 * The project detail page pins a membership-scoped workspace role badge from
 * the last overview response. A revoked member must not keep that claim on
 * screen after the server has given a definitive answer, while a transient
 * hiccup must not flicker the badge away. These pin the boundary the page
 * delegates to, provider-free and deterministic.
 */
describe("project detail snapshot disposition", () => {
  it("invalidates the snapshot when the session ended", () => {
    expect(resolveProjectDetailSnapshotAfterFailure(apiLikeError(401, "Session expired."))).toBe("invalidate");
  });

  it("invalidates the snapshot when a read says membership was revoked", () => {
    // The overview route answers 403 Forbid once the WorkspaceMember row is
    // gone, so the displayed role is no longer the server's answer.
    expect(resolveProjectDetailSnapshotAfterFailure(apiLikeError(403))).toBe("invalidate");
  });

  it("invalidates the snapshot when the server can no longer resolve the project", () => {
    // Archive and restore fold "removed member" and "gone project" into the
    // same definitive PROJECT_NOT_FOUND answer; either way the cached
    // membership snapshot, role badge included, must not stay displayed.
    expect(resolveProjectDetailSnapshotAfterFailure(apiLikeError(404, "Project not found."))).toBe("invalidate");
  });

  it("keeps the snapshot for transient server hiccups", () => {
    for (const status of [429, 500, 502, 503]) {
      expect(resolveProjectDetailSnapshotAfterFailure(apiLikeError(status))).toBe("keep");
    }
  });

  it("keeps the snapshot for validation failures that are not access answers", () => {
    expect(resolveProjectDetailSnapshotAfterFailure(apiLikeError(400, "Please provide a valid name."))).toBe("keep");
  });

  it("keeps the snapshot for network failures without a status", () => {
    expect(resolveProjectDetailSnapshotAfterFailure(new TypeError("Failed to fetch"))).toBe("keep");
    expect(resolveProjectDetailSnapshotAfterFailure(new Error("boom"))).toBe("keep");
    expect(resolveProjectDetailSnapshotAfterFailure(undefined)).toBe("keep");
    expect(resolveProjectDetailSnapshotAfterFailure("403")).toBe("keep");
  });
});
