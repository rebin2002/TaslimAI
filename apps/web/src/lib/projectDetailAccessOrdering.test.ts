import { describe, expect, it } from "vitest";
import { canApplyProjectDetailSnapshot, hasProjectDetailAccessEnded } from "./projectDetailAccessOrdering";
import { resolveProjectDetailSnapshotAfterFailure } from "./projectDetailAccessState";

// Pure, provider-free contract of the project-detail ordering fence. The mounted
// behaviour is pinned by ProjectDetailView.accessEndedOrdering.test.tsx; this file
// pins the two rules themselves, in the same style as #1044's
// projectDetailAccessState.test.ts. All fixtures are synthetic.
function withStatus(status: number | undefined) {
  return Object.assign(new Error("synthetic"), status === undefined ? {} : { status });
}

describe("project detail access-ended ordering", () => {
  it("fires on exactly the answers that retire the visible snapshot", () => {
    // The whole point of delegating to #1044's disposition: one status table, two
    // consequences. If these ever disagree, a role badge is either resurrectable
    // or the page over-suppresses live reads.
    for (const status of [401, 403, 404]) {
      expect(hasProjectDetailAccessEnded(withStatus(status))).toBe(true);
      expect(resolveProjectDetailSnapshotAfterFailure(withStatus(status))).toBe("invalidate");
    }
    for (const status of [400, 408, 429, 500, 502, 503, 504]) {
      expect(hasProjectDetailAccessEnded(withStatus(status))).toBe(false);
      expect(resolveProjectDetailSnapshotAfterFailure(withStatus(status))).toBe("keep");
    }
  });

  it("fires on nothing at all for a transport failure or a status-less error", () => {
    // "Failed to fetch" carries no access answer, so it must not fence anything.
    expect(hasProjectDetailAccessEnded(new TypeError("Failed to fetch"))).toBe(false);
    expect(hasProjectDetailAccessEnded(new Error("synthetic"))).toBe(false);
    expect(hasProjectDetailAccessEnded(undefined)).toBe(false);
    expect(hasProjectDetailAccessEnded("403")).toBe(false);
  });

  it("applies a response only while its issuing epoch still holds", () => {
    expect(canApplyProjectDetailSnapshot(0, 0)).toBe(true); // nothing superseded it
    expect(canApplyProjectDetailSnapshot(3, 3)).toBe(true); // issued after three answers
    expect(canApplyProjectDetailSnapshot(2, 3)).toBe(false); // issued before the third
    expect(canApplyProjectDetailSnapshot(4, 3)).toBe(false); // monotonic: never re-authorized
  });

  it("is monotonic, so an older response cannot be revalidated by a later one", () => {
    // A fix that compared wall-clock settle times instead of an issuing marker
    // would let a slow pre-revocation read win against a fast fresh one.
    for (const accessEndedCount of [1, 2, 3]) {
      const epochs = [0, 1, 2, 3, 4];
      expect(epochs.map((epoch) => canApplyProjectDetailSnapshot(epoch, accessEndedCount)))
        .toEqual(epochs.map((epoch) => epoch === accessEndedCount));
      // Once the count moves forward, no earlier epoch can become applicable
      // again: only a read issued under the new count is current.
      expect(epochs.map((epoch) => canApplyProjectDetailSnapshot(epoch, accessEndedCount + 1)))
        .toEqual(epochs.map((epoch) => epoch === accessEndedCount + 1));
    }
  });
});
