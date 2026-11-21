/**
 * Provider-free pins for the site-wide search box's own surface contract.
 *
 * Scope: what the search page is allowed to claim about a search while it is
 * running, after it is cancelled, after it finds nothing, and after it fails -
 * plus the announcement that carries each of those four answers to assistive
 * technology. The Chat search backend, the Research report's citations, asset
 * deep links and per-type pagination semantics are separate lanes and are not
 * pinned here. No provider and no network is touched: the decision is a pure
 * function of (a read outstanding, a failure, the term it belongs to, rows on
 * screen), so the outcomes below are asserted rather than raced.
 */
import { readFileSync } from "node:fs";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import { locales, translate, type Locale } from "@/lib/i18n";
import { GlobalSearchFailurePanel, GlobalSearchView } from "./GlobalSearchView";
import {
  clearedSearchResults,
  searchStatusAnnouncement,
  searchSurfaceStatus,
  settleSearchCancellation,
  settleSearchFailure,
  type SearchSurfaceInput,
  type SearchSurfaceStatus,
} from "@/lib/searchSurfaceState";

const { urlQuery } = vi.hoisted(() => ({ urlQuery: { current: "" } }));

vi.mock("next/navigation", () => ({
  useSearchParams: () => new URLSearchParams(urlQuery.current ? [["q", urlQuery.current]] : []),
  useRouter: () => ({ push: vi.fn(), replace: vi.fn() }),
}));
vi.mock("@/components/LocaleProvider", () => ({
  useLocale: () => ({ locale: "en", t: (key: string, variables?: Record<string, string>) => translate("en", key, variables) }),
}));

const searchViewSource = readFileSync(new URL("./GlobalSearchView.tsx", import.meta.url), "utf8");

/** Reads a status sentence out of the catalog the way the view does. */
const announce = (status: SearchSurfaceStatus, input: Pick<SearchSurfaceInput, "submittedQuery"> & { count?: number }, locale: Locale = "en") => {
  const announcement = searchStatusAnnouncement(status, input.submittedQuery, input.count ?? 0);
  return announcement ? translate(locale, announcement.key, announcement.variables) : "";
};

const pageHtml = (q: string) => {
  urlQuery.current = q;
  return renderToStaticMarkup(<GlobalSearchView />);
};

/** Every polite status region on the page, in document order. */
const statusRegions = (html: string) => html.match(/<[^>]*role="status"[^>]*>/g) ?? [];
/** The element carrying that role, with its announced text. */
function statusRegionContent(html: string): string[] {
  return [...html.matchAll(/<[^>]*role="status"[^>]*>([\s\S]*?)<\/(?:p|div|section)>/g)].map((match) => match[1].replace(/<[^>]*>/g, "").trim());
}

describe("Global search surface outcomes", () => {
  it("ranks the five outcomes so a failure can never borrow the empty copy", () => {
    expect(searchSurfaceStatus({ loading: true, error: "", submittedQuery: "cat", groupCount: 0 })).toBe("searching");
    expect(searchSurfaceStatus({ loading: false, error: "", submittedQuery: "cat", groupCount: 3 })).toBe("populated");
    expect(searchSurfaceStatus({ loading: false, error: "", submittedQuery: "cat", groupCount: 0 })).toBe("noResults");
    expect(searchSurfaceStatus({ loading: false, error: "boom", submittedQuery: "cat", groupCount: 0 })).toBe("failed");
    // Nothing was searched at all: the prompt, whichever flags arrive from the reset.
    expect(searchSurfaceStatus({ loading: false, error: "", submittedQuery: "", groupCount: 0 })).toBe("idle");
  });

  it("keeps a failed append on top of the pages it was extending", () => {
    // Pages already read for the *same* term still answer that term; only its next
    // page is lost, so dropping them would destroy work the reader can keep using.
    const settlement = settleSearchFailure("cat", true);
    expect(settlement.retainResults).toBe(true);
    expect(searchSurfaceStatus({ loading: false, error: "boom", submittedQuery: "cat", groupCount: 2 })).toBe("populated");
  });

  it("refuses to leave an earlier term's rows under the term that failed to be searched", () => {
    // Repro: searching "cat" then "dog" and having "dog" fail used to set only the
    // error, so the page kept showing cat's titles, cat's count and a query chip
    // naming cat, while the box read "dog". The rows were never read for dog.
    const settlement = settleSearchFailure("dog", false);
    expect(settlement.retainResults).toBe(false);
    expect(settlement.attemptedQuery).toBe("dog");
    expect(settlement.results).toEqual(clearedSearchResults);
    const shown = searchSurfaceStatus({ loading: false, error: "boom", submittedQuery: settlement.attemptedQuery, groupCount: settlement.results.groups.length });
    expect(shown, "a failed read must not fall through to the no-matches copy").toBe("failed");
  });

  it("retires the busy flag that cancelling fences away", () => {
    // runSearch's finally only clears `loading` for the request that is still
    // current, so the request cancelled by the clear gesture can never settle it.
    // Without this the page keeps aria-busy, keeps showing the spinner instead of
    // the prompt, and loadMore stays blocked by a read that no longer exists.
    const cancelled = settleSearchCancellation();
    expect(cancelled.loading, "a cancelled search must not stay busy").toBe(false);
    expect(cancelled.loadingMore).toBe(false);
    expect(cancelled.submittedQuery).toBe("");
    expect(cancelled.error).toBe("");
    expect(cancelled.groups).toEqual([]);
    expect(cancelled.hasMore).toBe(false);
    expect(searchSurfaceStatus({ loading: cancelled.loading, error: cancelled.error, submittedQuery: cancelled.submittedQuery, groupCount: cancelled.groups.length })).toBe("idle");
  });

  it("wires both gestures through those settlements instead of half-resetting by hand", () => {
    // Static rendering cannot reach an event handler, so the wiring itself is
    // pinned here, exactly as the Image Studio selector's loading pin does.
    expect(searchViewSource).toMatch(/function clear\(\)[\s\S]{0,400}?applySettlement\(settleSearchCancellation\(\)\)/);
    expect(searchViewSource).toMatch(/if \(!settlement\.retainResults\) applySettlement\(\{ \.\.\.settlement\.results, submittedQuery: settlement\.attemptedQuery \}\)/);
    // The old hand-written reset in clear() omitted `loading`; both paths must now
    // come from one settlement so no flag can be forgotten again.
    expect(searchViewSource, "clear() must not hand-write a partial reset").not.toMatch(/setGroups\(\[\]\); setTotalCount\(0\); setLoadingMore\(false\)/);
  });
});

describe("Global search surface announcements", () => {
  it("says a search is running instead of reading out the results heading", () => {
    // Before: <span class="sr-only">Search results</span> inside the busy branch,
    // which announced that results exist at the moment they do not.
    const html = pageHtml("cat");
    expect(statusRegions(html), "the busy page must carry exactly one status region").toHaveLength(1);
    expect(statusRegionContent(html)[0]).toBe('Searching for “cat”…');
    expect(html).toContain('aria-busy="true"');
    expect(html, "the spinner itself is decoration; the status region is the announcement").not.toContain('<span class="sr-only">Search results</span>');
  });

  it("announces the prompt once nothing has been searched", () => {
    const html = pageHtml("");
    expect(statusRegions(html)).toHaveLength(1);
    expect(statusRegionContent(html)[0]).toBe(translate("en", "search.statusIdle"));
    expect(html, "an idle page must not claim a read is outstanding").not.toContain('aria-busy="true"');
    expect(html).toContain(translate("en", "search.emptyTitle"));
  });

  it("keeps one live region mounted for every outcome so transitions are spoken", () => {
    // The region used to be the results section itself: reaching zero results
    // unmounted it, and an unmounted live region announces nothing at all.
    const busy = pageHtml("cat");
    const idle = pageHtml("");
    expect(statusRegions(busy)).toHaveLength(1);
    expect(statusRegions(idle)).toHaveLength(1);
    expect(busy).not.toMatch(/<section class="global-search-results"[^>]*aria-live=/);
    expect(idle).not.toMatch(/aria-live=/);
    expect(statusRegionContent(busy)[0]).not.toBe(statusRegionContent(idle)[0]);
  });

  it("speaks each outcome distinctly, naming the term it belongs to", () => {
    expect(announce("searching", { submittedQuery: "dog" })).toBe('Searching for “dog”…');
    expect(announce("populated", { submittedQuery: "dog", count: 7 })).toBe('7 results for “dog”.');
    // Rephrased away from the visible "No matches found" heading on purpose: the
    // status has to say something new, and e2e/navigation.spec.ts resolves that
    // heading with getByText(/no matches found/i), which Playwright strict mode
    // only lets a single element satisfy.
    expect(announce("noResults", { submittedQuery: "zzz" })).toBe('Nothing matched “zzz”.');
    expect(announce("idle", { submittedQuery: "" })).toBe(translate("en", "search.statusIdle"));
    // A failure is spoken by its own role="alert"; a second region would say it twice.
    expect(announce("failed", { submittedQuery: "dog" })).toBe("");
  });

  it("reports a failed search as a failure that can be retried, naming the term", () => {
    const html = renderToStaticMarkup(<GlobalSearchFailurePanel query="dog" error={translate("en", "search.loadError")} onRetry={() => undefined} t={(key, variables) => translate("en", key, variables)} />);
    expect(html).toContain('role="alert"');
    expect(html).toContain(translate("en", "search.failedTitle"));
    expect(html).toContain("dog");
    expect(html, "the retry must be reachable, not just a sentence").toContain(`<button`);
    expect(html).toContain(translate("en", "search.retry"));
  });

  it("leaves the outcome text the browser tests resolve by name unambiguous", () => {
    // e2e/navigation.spec.ts asserts the zero-result outcome with
    // getByText(/no matches found/i) and expects exactly one element, so the
    // announced sentence must describe the same outcome in different words.
    const phrase = /no matches found/i;
    const speaking: string[] = [];
    for (const status of ["searching", "populated", "noResults", "failed", "idle"] as const) {
      const announcement = searchStatusAnnouncement(status, "E2E no-match", 0);
      if (announcement) speaking.push(translate("en", announcement.key, announcement.variables));
    }
    const announced = [...speaking, translate("en", "search.failedTitle"), translate("en", "search.failedHint", { query: "E2E no-match" }), translate("en", "search.retry"), translate("en", "search.loadError")];
    expect(announced.filter((line) => phrase.test(line)), "no status may repeat the heading").toEqual([]);
    expect(translate("en", "search.noResultsTitle")).toMatch(phrase);
  });

  it("names every outcome in English, Arabic and Kurdish Sorani", () => {
    const keys = ["search.statusSearching", "search.statusResults", "search.statusNoResults", "search.statusIdle", "search.failedTitle", "search.failedHint", "search.retry"];
    for (const locale of locales) {
      for (const key of keys) {
        expect(translate(locale, key), `${locale}:${key}`).not.toBe(key);
        expect(translate(locale, key), `${locale}:${key}`).not.toBe("");
      }
      // The interpolated term survives in every locale, so no status can announce
      // a search whose term it cannot name.
      expect(translate(locale, "search.statusNoResults", { query: "kitap" }), locale).toContain("kitap");
      expect(translate(locale, "search.statusResults", { query: "kitap", count: "3" }), locale).toContain("3");
    }
    // The status sentence is localized, not left as a bare key in the region.
    expect(statusRegionContent(pageHtml(""))[0]).not.toBe("search.statusIdle");
  });
});
