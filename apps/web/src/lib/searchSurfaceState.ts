import type { GlobalSearchGroup } from "./api";

/**
 * The five things the site-wide search results area can honestly say about
 * itself while a search is running, was cancelled, found nothing, found
 * something, or could not be answered at all.
 *
 * The view used to derive this from the raw `loading`/`error`/`groups` state
 * inside one render expression, which let two wrong answers through: a failed
 * read fell through to the "no matches" copy - the same false claim the
 * Research report's evidence panel was fixed for - and the rows an earlier
 * term had loaded stayed on screen under a later term's error.
 */
export type SearchSurfaceStatus = "searching" | "populated" | "noResults" | "failed" | "idle";

export type SearchSurfaceInput = {
  /** A read for the term the reader last submitted is still in flight. */
  loading: boolean;
  /** The message for a read that failed, empty when nothing failed. */
  error: string;
  /** The term the rows on screen were read for, empty when nothing was searched. */
  submittedQuery: string;
  /** How many result groups are rendered. */
  groupCount: number;
};

/**
 * Rows outrank the failure that failed to add to them: an append that breaks
 * still shows valid pages for the same term, so only a failure with nothing to
 * show is reported as failed. A reset outranks both, because after a reset no
 * term has been searched at all.
 */
export function searchSurfaceStatus(input: SearchSurfaceInput): SearchSurfaceStatus {
  if (input.loading) return "searching";
  if (input.groupCount > 0) return "populated";
  if (input.error) return "failed";
  if (!input.submittedQuery) return "idle";
  return "noResults";
}

/** The result-bearing part of the surface, as one object instead of four states. */
export type SearchResultsReset = { groups: GlobalSearchGroup[]; totalCount: number; page: number; hasMore: boolean };

export const clearedSearchResults: SearchResultsReset = { groups: [], totalCount: 0, page: 1, hasMore: false };

/** Everything the surface must report once the reader cancels the search. */
export type SearchCancellationSettlement = SearchResultsReset & {
  submittedQuery: string;
  loading: boolean;
  loadingMore: boolean;
  error: string;
};

/**
 * Any answer the surface can be settled to. Every field is optional because a
 * failed append deliberately keeps the rows it already had, while a
 * cancellation drops all of them; the view writes whichever fields are present
 * and falls back to {@link clearedSearchResults} for the rest.
 */
export type SearchSettlementFields = Partial<SearchCancellationSettlement>;

/**
 * Cancelling fences the in-flight read, and a fenced read deliberately applies
 * no state of its own - including the busy flag it had set. So the gesture
 * that cancels a search has to retire that busy flag itself, or the page keeps
 * announcing a read that no longer exists and never returns to the prompt.
 */
export function settleSearchCancellation(): SearchCancellationSettlement {
  return { ...clearedSearchResults, submittedQuery: "", loading: false, loadingMore: false, error: "" };
}

/** What a failed read leaves the surface showing. */
export type SearchFailureSettlement = {
  /** Whether the rows already on screen still answer the term that failed. */
  retainResults: boolean;
  /** The term the failure belongs to, so the surface can name and retry it. */
  attemptedQuery: string;
  results: SearchResultsReset;
};

/**
 * A read that fails answers the term the reader just submitted with nothing,
 * so the rows on screen - which were read for an *earlier* term - cannot stay
 * under it. Dropping them is what keeps "0 results" and the earlier term's
 * titles from being read as the outcome of the search that never completed.
 * A failed append is the exception: those pages were read for the same term
 * and stay valid, so only its retry is lost.
 */
export function settleSearchFailure(attemptedQuery: string, append: boolean): SearchFailureSettlement {
  return { retainResults: append, attemptedQuery, results: { ...clearedSearchResults } };
}

/** A status sentence for the page's live region, or none when it would duplicate one. */
export type SearchAnnouncement = { key: string; variables?: Record<string, string> };

/**
 * The live region is mounted once and never unmounted, so changing the outcome
 * is what announces it. An empty string for `failed` is deliberate: the failure
 * is already read out by the visible `role="alert"`, and a second region would
 * announce the same sentence twice.
 */
export function searchStatusAnnouncement(status: SearchSurfaceStatus, query: string, count: number): SearchAnnouncement | null {
  switch (status) {
    case "searching":
      return { key: "search.statusSearching", variables: { query } };
    case "populated":
      return { key: "search.statusResults", variables: { count: String(count), query } };
    case "noResults":
      return { key: "search.statusNoResults", variables: { query } };
    case "failed":
      return null;
    case "idle":
      return { key: "search.statusIdle" };
  }
}
