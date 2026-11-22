import { resolveProjectDetailSnapshotAfterFailure } from "./projectDetailAccessState";

/**
 * Ordering fence for the project detail page.
 *
 * `resolveProjectDetailSnapshotAfterFailure` (#1044) decides what a failed
 * request does to the snapshot that is already on screen. It answers a
 * question about the *present*; it says nothing about the requests that were
 * already in flight when the answer arrived.
 *
 * A definitive access-ended answer (401, 403, 404) is a statement about the
 * principal, not about one response: every snapshot the server captured
 * BEFORE that answer is now known to be stale, including the ones still on
 * the wire. A role refresh in flight during a revocation therefore must not
 * be allowed to commit - it carries the membership the server read before the
 * revoke committed (a cached project response, or a read issued a few
 * milliseconds earlier), and applying it re-pins the workspace member role
 * badge, the metadata role claim and the edit/archive affordances for someone
 * the server has just refused.
 *
 * This predicate deliberately delegates to #1044's disposition instead of
 * re-listing the statuses, so the two rules cannot drift apart: exactly the
 * failures that take the visible snapshot down are the ones that also retire
 * every older response.
 *
 * Provider-free and side-effect-free, like the module it delegates to.
 */
export function hasProjectDetailAccessEnded(error: unknown): boolean {
  return resolveProjectDetailSnapshotAfterFailure(error) === "invalidate";
}

/**
 * Whether an overview response that resolved now may still be applied.
 *
 * `requestEpoch` is the access-ended count observed when the read was issued;
 * `accessEndedCount` is the count when its promise settles. They differ only
 * if a definitive access answer landed in between, which is precisely the
 * window in which the response is known to describe a membership that no
 * longer exists. A read issued after that answer sees the current count and
 * is still applied: a retry, a language-pack refresh or a re-admission must
 * keep showing the server's live role, so the fence cannot simply suppress
 * every late response.
 */
export function canApplyProjectDetailSnapshot(requestEpoch: number, accessEndedCount: number): boolean {
  return requestEpoch === accessEndedCount;
}
