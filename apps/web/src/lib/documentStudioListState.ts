/**
 * Documents-list presentation states for the Document Studio compose rails.
 *
 * The rails read their lists through one `Promise.allSettled` call, so a
 * rejected request and a request that legitimately returned nothing both leave
 * an empty array behind. Presenting from the array alone makes a failure look
 * like an absent library. These helpers keep the settled outcome of each read
 * separate from the number of items it produced, so a rail can tell loading,
 * failure, and a true empty state apart, and so the decision is testable
 * without a browser, a network, or a provider.
 *
 * Scope: the source-document and recent-document lists only. The document
 * renderer, job/draft restoration, and export state are separate lanes.
 */

/** Outcome of the list request that feeds a rail. */
export type DocumentStudioListLoad = "loading" | "ready" | "error";

/** What the rail may legitimately show. */
export type DocumentStudioListStatus = "loading" | "error" | "empty" | "populated";

/**
 * Items already on screen outrank a later request failure: a rail that can
 * still list its documents must keep listing them rather than claim it has
 * none. Only a rail with nothing to show reports loading or failure.
 */
export function documentStudioListStatus(load: DocumentStudioListLoad, itemCount: number): DocumentStudioListStatus {
  if (itemCount > 0) return "populated";
  if (load === "loading") return "loading";
  if (load === "error") return "error";
  return "empty";
}

/** One settled read maps to its own rail status; nothing is inferred across rails. */
export function documentStudioListLoad(outcome: PromiseSettledResult<unknown>): DocumentStudioListLoad {
  return outcome.status === "fulfilled" ? "ready" : "error";
}
