/**
 * Issue #1267 slice 1 — scholarly search transport and lifecycle tests.
 *
 * These are the guarantees the student cannot see from a screenshot: a refused search is never
 * shown as "no results", a rate limit keeps the server's own wait, an abandoned request can never
 * overwrite a newer one, and no discovered record is ever dressed up as a work that was read. All
 * fixtures are synthetic; no real workspace, account or record is involved.
 */
import { describe, expect, it, vi } from "vitest";
import { ApiError, type AcademicSourceSearchResponse } from "@/lib/api";
import { discoveredPage, discoveredRecord } from "./academicTestHelpers";
import {
  classifyScholarlySearchFailure,
  createScholarlySearchController,
  displayableProviderUrl,
  maxScholarlySearchPage,
  narrowSourceType,
  scholarlyAccessNoteKey,
  scholarlyDiscoveryNotice,
  scholarlyDoiCheckKey,
  scholarlyRowToSource,
  scholarlySearchFailureIsRetryable,
  scholarlySearchFailureKey,
  scholarlySearchMaxDepth,
  scholarlySearchPageInfo,
  scholarlySearchPageSize,
  scholarlySearchQueryParams,
  scholarlySearchScopeKey,
  searchScholarlySources,
  splitScholarlyIdentifiers,
  toScholarlySearchRows,
} from "./academicScholarlySearch";

const workspaceId = "1f2e3d4c-b5a6-4789-abcd-0123456789ab";

/** Local aliases so the cases read as data, not as fixture plumbing. */
const source = discoveredRecord;
const response = (overrides: Partial<AcademicSourceSearchResponse> = {}) => discoveredPage({ workspaceId, ...overrides });

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

describe("failure classification", () => {
  it("keeps every refusal a distinct, honest state", () => {
    const signal = new AbortController().signal;
    const classify = (status: number, code?: string, retryAfter?: number) =>
      classifyScholarlySearchFailure(new ApiError(status, "The search did not complete.", undefined, code, retryAfter), signal);
    expect(classify(401).kind).toBe("unauthenticated");
    expect(classify(403).kind).toBe("forbidden");
    expect(classify(429, "RATE_LIMITED", 45).kind).toBe("rate_limited");
    expect(classify(429, "RATE_LIMITED", 45).retryAfterSeconds).toBe(45);
    expect(classify(503, "ACADEMIC_SEARCH_UNAVAILABLE").kind).toBe("unavailable");
    expect(classify(500).kind).toBe("failed");
    expect(classify(400).kind).toBe("rejected");
  });

  it("tells the two 503s apart, because only one of them is about the deployment", () => {
    // The shared transport wraps a dead connection as 503 NETWORK_ERROR; the endpoint's own refusal
    // is 503 ACADEMIC_SEARCH_UNAVAILABLE. Same status, opposite sentences: the first is "we could
    // not reach the search", the second is "this deployment has not turned search on". Reading the
    // status instead of the code would tell an offline student something about their own product
    // that this client never observed.
    const signal = new AbortController().signal;
    const classify = (status: number, code?: string) =>
      classifyScholarlySearchFailure(new ApiError(status, "The search did not complete.", undefined, code), signal);
    expect(classify(503, "NETWORK_ERROR").kind).toBe("failed");
    expect(scholarlySearchFailureKey(classify(503, "NETWORK_ERROR"))).toBe("sources.findErrFailed");
    // An unanswered 503 with no code the client recognises is still reported as unreachable rather
    // than as a configuration fact: a proxy can produce it, and the client cannot tell the two apart.
    expect(classify(503).kind).toBe("failed");
    expect(classify(503, "SOMETHING_ELSE").kind).toBe("failed");
    // And the real refusal keeps its precise wording, so this is not "never say unavailable".
    const off = classify(503, "ACADEMIC_SEARCH_UNAVAILABLE");
    expect(off.kind).toBe("unavailable");
    expect(scholarlySearchFailureKey(off)).toBe("sources.findErrUnavailable");
    expect(scholarlyDiscoveryNotice({ state: "failed", failureKind: off.kind }).state).toBe("disabled");
    expect(scholarlyDiscoveryNotice({ state: "failed", failureKind: classify(503, "NETWORK_ERROR").kind }).state).not.toBe("disabled");
  });

  it("reads an aborted request as stopped, not as a failure to reach records", () => {
    const controller = new AbortController();
    controller.abort();
    const failure = classifyScholarlySearchFailure(new Error("listener died"), controller.signal);
    expect(failure.kind).toBe("cancelled");
    expect(scholarlySearchFailureIsRetryable(failure)).toBe(false);
  });

  it("classifies a non-HTTP throw as unreachable", () => {
    expect(classifyScholarlySearchFailure(new TypeError("network"), new AbortController().signal).kind).toBe("failed");
  });

  it("maps each kind to its own dictionary key and retry affordance", () => {
    expect(scholarlySearchFailureKey({ kind: "rate_limited", retryAfterSeconds: 30 })).toBe("sources.findErrRateLimited");
    expect(scholarlySearchFailureKey({ kind: "forbidden", retryAfterSeconds: null })).toBe("sources.findErrForbidden");
    expect(scholarlySearchFailureKey({ kind: "unavailable", retryAfterSeconds: null })).toBe("sources.findErrUnavailable");
    expect(scholarlySearchFailureKey({ kind: "cancelled", retryAfterSeconds: null })).toBe("sources.findCancelled");
    expect(scholarlySearchFailureIsRetryable({ kind: "rate_limited", retryAfterSeconds: null })).toBe(true);
    expect(scholarlySearchFailureIsRetryable({ kind: "forbidden", retryAfterSeconds: null })).toBe(false);
    expect(scholarlySearchFailureIsRetryable({ kind: "unauthenticated", retryAfterSeconds: null })).toBe(false);
  });
});

describe("identifier-shaped queries", () => {
  it("sends a pasted DOI as an identifier, not as a keyword", () => {
    const split = splitScholarlyIdentifiers("10.5555/synth.2024.0001 accountability");
    expect(split.dois).toEqual(["10.5555/synth.2024.0001"]);
    expect(split.text).toBe("accountability");
    expect(split.isbns).toEqual([]);
  });

  it("normalises ISBN-10 to ISBN-13 and refuses a bad check digit", () => {
    expect(splitScholarlyIdentifiers("0306406152").isbns).toEqual(["9780306406157"]);
    // 9780306406111 fails its own check digit, so it stays a search term.
    const bad = splitScholarlyIdentifiers("9780306406111");
    expect(bad.isbns).toEqual([]);
    expect(bad.text).toBe("9780306406111");
  });

  it("carries both identifier families into the query string", () => {
    const query = scholarlySearchQueryParams({ term: "ethics", page: 2, pageSize: 10, yearFrom: 2019, yearTo: 2024, openAccessOnly: true, dois: ["10.5555/a"], isbns: ["9780306406157"] });
    expect(query).toContain("q=ethics");
    expect(query).toContain("page=2");
    expect(query).toContain("yearFrom=2019");
    expect(query).toContain("openAccessOnly=true");
    expect(query).toContain("doi=10.5555%2Fa");
    expect(query).toContain("isbn=9780306406157");
  });
});

describe("searchScholarlySources", () => {
  it("refuses an empty term before any request is made", async () => {
    const search = vi.fn();
    const outcome = await searchScholarlySources({ workspaceId, term: "   ", page: 1, pageSize: 10, signal: new AbortController().signal }, search);
    expect(outcome).toEqual({ ok: false, failure: { kind: "rejected", retryAfterSeconds: null } });
    expect(search).not.toHaveBeenCalled();
  });

  it("refuses to search without a workspace scope", async () => {
    const search = vi.fn();
    const outcome = await searchScholarlySources({ workspaceId: "  ", term: "ethics", page: 1, pageSize: 10, signal: new AbortController().signal }, search);
    expect(outcome.ok).toBe(false);
    if (!outcome.ok) expect(outcome.failure.kind).toBe("forbidden");
    expect(search).not.toHaveBeenCalled();
  });

  it("stops paging at the server-side browse bound", async () => {
    const search = vi.fn();
    const beyond = maxScholarlySearchPage(scholarlySearchPageSize);
    expect(scholarlySearchMaxDepth / scholarlySearchPageSize).toBe(beyond);
    const outcome = await searchScholarlySources({ workspaceId, term: "ethics", page: beyond + 1, pageSize: scholarlySearchPageSize, signal: new AbortController().signal }, search);
    expect(outcome.ok).toBe(false);
    expect(search).not.toHaveBeenCalled();
  });

  it("trims the term and forwards identifiers, bounds and paging", async () => {
    const search = vi.fn().mockResolvedValue(response());
    const outcome = await searchScholarlySources(
      { workspaceId, term: "  data ethics 10.5555/synth.2024.0001 ", page: 3, pageSize: 10, yearFrom: 2018, yearTo: null, openAccessOnly: false, signal: new AbortController().signal },
      search,
    );
    expect(outcome.ok).toBe(true);
    const [sentWorkspace, params, signal] = search.mock.calls[0];
    expect(sentWorkspace).toBe(workspaceId);
    expect(params.term).toBe("data ethics");
    expect(params.dois).toEqual(["10.5555/synth.2024.0001"]);
    expect(params.page).toBe(3);
    expect(params.pageSize).toBe(scholarlySearchPageSize);
    expect(params.yearFrom).toBe(2018);
    expect(params.yearTo).toBeNull();
    expect(signal).toBeInstanceOf(AbortSignal);
  });

  it("never throws: a rate limit comes back classified", async () => {
    const search = vi.fn().mockRejectedValue(new ApiError(429, "Slow down", undefined, "RATE_LIMITED", 20));
    const outcome = await searchScholarlySources({ workspaceId, term: "ethics", page: 1, pageSize: 10, signal: new AbortController().signal }, search);
    expect(outcome).toEqual({ ok: false, failure: { kind: "rate_limited", retryAfterSeconds: 20 } });
  });

  it("reports a disabled deployment as unavailable", async () => {
    const search = vi.fn().mockRejectedValue(new ApiError(503, "Off", undefined, "ACADEMIC_SEARCH_UNAVAILABLE"));
    const outcome = await searchScholarlySources({ workspaceId, term: "ethics", page: 1, pageSize: 10, signal: new AbortController().signal }, search);
    if (outcome.ok) throw new Error("expected a failure");
    expect(outcome.failure.kind).toBe("unavailable");
  });
});

describe("row narrowing", () => {
  it("keeps metadata as metadata and abstract as abstract", () => {
    expect(scholarlyAccessNoteKey("metadata", false)).toBe("sources.accessNoteMetadata");
    expect(scholarlyAccessNoteKey("abstract", false)).toBe("sources.accessNoteAbstract");
    expect(scholarlyAccessNoteKey("metadata", true)).toBe("sources.accessNoteIndicated");
    expect(scholarlyAccessNoteKey("abstract", true)).toBe("sources.accessNoteIndicated");
  });

  it("never promotes an indicated location into read content", () => {
    const [row] = toScholarlySearchRows([source({ accessLevel: "metadata", fullTextIndicated: true, fullTextUrl: "https://synth.example/full/1" })]);
    expect(row.access).toBe("metadata");
    expect(row.fullTextIndicated).toBe(true);
    expect(row.accessNoteKey).toBe("sources.accessNoteIndicated");
    expect(row.url).toBe("https://synth.example/records/1");
  });

  it("withholds abstract text the catalogue did not return", () => {
    const [metadata] = toScholarlySearchRows([source({ accessLevel: "metadata", abstract: null })]);
    expect(metadata.abstract).toBeNull();
    const [withAbstract] = toScholarlySearchRows([source({ accessLevel: "abstract", abstract: "Synthetic abstract." })]);
    expect(withAbstract.abstract).toBe("Synthetic abstract.");
  });

  it("collapses an unknown classification to other rather than guessing", () => {
    expect(narrowSourceType("journal_article")).toBe("journal_article");
    expect(narrowSourceType("web_page")).toBe("other");
    expect(toScholarlySearchRows([source({ sourceType: "weird" })])[0].sourceType).toBe("other");
  });

  it("keeps the retrieval timestamp as data, not as prose", () => {
    const [row] = toScholarlySearchRows([source()]);
    expect(row.retrievedAt).toBe("2026-11-24T07:00:00Z");
  });

  it("maps a DOI check outcome to a dictionary key without naming a service", () => {
    expect(scholarlyDoiCheckKey("10.5555/x", true, null)).toBe("sources.doiCheckRegistered");
    expect(scholarlyDoiCheckKey("10.5555/x", false, "not_registered")).toBe("sources.doiCheckNotRegistered");
    expect(scholarlyDoiCheckKey("10.5555/x", false, "invalid_syntax")).toBe("sources.doiCheckInvalidSyntax");
    expect(scholarlyDoiCheckKey("10.5555/x", false, "rate_limited")).toBe("sources.doiCheckRateLimited");
    expect(scholarlyDoiCheckKey("10.5555/x", false, "timeout")).toBe("sources.doiCheckTimeout");
    expect(scholarlyDoiCheckKey("10.5555/x", false, "malformed_response")).toBe("sources.doiCheckMalformed");
    expect(scholarlyDoiCheckKey("10.5555/x", false, "configuration")).toBe("sources.doiCheckConfiguration");
    expect(scholarlyDoiCheckKey("10.5555/x", false, null)).toBe("sources.doiCheckUnavailable");
  });
});

describe("discovered record into the library", () => {
  it("records only what the catalogue returned", () => {
    const [row] = toScholarlySearchRows([source({ accessLevel: "metadata", fullTextIndicated: true })]);
    const record = scholarlyRowToSource(row, { id: "src-test-1", addedAt: "2026-11-24T07:05:00Z", accessNote: "Metadata only." });
    expect(record.access).toBe("metadata");
    expect(record.peerReviewed).toBe(false);
    expect(record.retractionStatus).toBe("unknown");
    expect(record.pages).toBe("41-58");
    expect(record.edition).toBeNull();
    expect(record.doi).toBe("10.5555/synth.2024.0001");
    expect(record.relevanceNote).toBe("Metadata only.");
  });
});

describe("paging facts", () => {
  function pageInfoFor(result: AcademicSourceSearchResponse) {
    return scholarlySearchPageInfo(result, toScholarlySearchRows(result.sources));
  }

  it("offers both directions in the middle of a long result", () => {
    const info = pageInfoFor(response({ page: 3, totalMatched: 150, hasMore: true }));
    expect(info.canPrev).toBe(true);
    expect(info.canNext).toBe(true);
    expect(info.pageCount).toBe(15);
  });

  it("refuses to page past the bound the server enforces", () => {
    const info = pageInfoFor(response({ page: 15, totalMatched: 150, hasMore: true }));
    expect(info.canNext).toBe(false);
    expect(info.canPrev).toBe(true);
  });

  it("stops at the end of a short result", () => {
    const info = pageInfoFor(response({ page: 1, totalMatched: 1, hasMore: false }));
    expect(info.canNext).toBe(false);
    expect(info.canPrev).toBe(false);
    expect(info.shown).toBe(1);
  });

  // The defect this slice had: the API used to size its fetch to page × pageSize, so page 1 of
  // size 10 could report at most totalMatched 10 — one page — and Next was unreachable even while
  // hasMore said otherwise. With the pool now fetched page-independently, a longer pool must open
  // page 2 from page 1 whether or not the pool was truncated.
  it("opens the next page from page 1 whenever the pool holds more than one page", () => {
    const pool = Array.from({ length: 24 }, (_, i) => source({ title: `Synthetic record ${i + 1}`, doi: `10.5555/synth.${i + 1}` }));
    const firstPage = response({ page: 1, totalMatched: pool.length, hasMore: false, sources: pool.slice(0, scholarlySearchPageSize) });
    const info = pageInfoFor(firstPage);
    expect(info.pageCount).toBe(3);
    expect(info.canNext).toBe(true);
    expect(info.hasMore).toBe(false);
    // Second page of the same pool stays navigable in both directions.
    const second = pageInfoFor(response({ page: 2, totalMatched: pool.length, hasMore: false, sources: pool.slice(10, 20) }));
    expect(second.canPrev).toBe(true);
    expect(second.canNext).toBe(true);
    // The last page of the pool closes Next, bound or not.
    const third = pageInfoFor(response({ page: 3, totalMatched: pool.length, hasMore: false, sources: pool.slice(20, 24) }));
    expect(third.canNext).toBe(false);
    expect(third.canPrev).toBe(true);
  });

  it("treats hasMore as the browse bound, not as a next-page signal", () => {
    // A pool truncated at the combined bound still pages normally inside what it returned.
    const truncated = pageInfoFor(response({ page: 2, totalMatched: scholarlySearchMaxDepth, hasMore: true }));
    expect(truncated.canNext).toBe(true);
    const truncatedLast = pageInfoFor(response({ page: maxScholarlySearchPage(scholarlySearchPageSize), totalMatched: scholarlySearchMaxDepth, hasMore: true }));
    expect(truncatedLast.canNext).toBe(false);
  });
});

describe("discovery capability notice", () => {
  // Copy must not claim a capability the deployment has not demonstrated: before any search, and
  // while the server reports the feature switched off, the studio may only describe the feature
  // generically; "live for your workspace" is earned by an answered search, and an explicit 503
  // gets its own statement.
  it("stays capability-neutral before the first search", () => {
    const notice = scholarlyDiscoveryNotice({ state: "idle", failureKind: null });
    expect(notice.state).toBe("unknown");
    expect(notice.tone).toBe("info");
    expect(notice.bodyKey).toBe("sources.discoveryBody");
  });

  it("says live only after a search actually answered", () => {
    const notice = scholarlyDiscoveryNotice({ state: "done", failureKind: null });
    expect(notice.state).toBe("enabled");
    expect(notice.bodyKey).toBe("sources.discoveryBodyLive");
  });

  it("speaks plainly when the deployment refused with unavailable", () => {
    for (const state of ["failed", "done"] as const) {
      const notice = scholarlyDiscoveryNotice({ state, failureKind: "unavailable" });
      expect(notice.state).toBe("disabled");
      expect(notice.tone).toBe("warning");
      expect(notice.titleKey).toBe("sources.discoveryOffTitle");
      expect(notice.bodyKey).toBe("sources.discoveryOffBody");
    }
  });

  it("keeps a different refusal, such as a rate limit, capability-neutral", () => {
    const notice = scholarlyDiscoveryNotice({ state: "failed", failureKind: "rate_limited" });
    expect(notice.state).toBe("unknown");
    expect(notice.bodyKey).toBe("sources.discoveryBody");
  });
});

describe("search controller lifecycle", () => {
  it("walks idle → searching → done and keeps the response", async () => {
    const search = vi.fn().mockResolvedValue(response());
    const controller = createScholarlySearchController({ search });
    const states: string[] = [];
    controller.subscribe(() => states.push(controller.read().state));

    expect(controller.read().state).toBe("idle");
    controller.search(1, { workspaceId, term: "ethics" });
    expect(controller.read().state).toBe("searching");

    await vi.waitFor(() => expect(controller.read().state).toBe("done"));
    expect(states).toEqual(["searching", "done"]);
    expect(controller.read().result?.sources).toHaveLength(1);
    expect(controller.read().page).toBe(1);
  });

  it("aborts the previous request when a new page is asked for", async () => {
    const first = deferred<AcademicSourceSearchResponse>();
    const signals: AbortSignal[] = [];
    const search = vi.fn((_workspace: string, _params: unknown, signal: AbortSignal) => {
      signals.push(signal);
      return signals.length === 1 ? first.promise : Promise.resolve(response({ page: 2, sources: [] }));
    });
    const controller = createScholarlySearchController({ search });

    controller.search(1, { workspaceId, term: "ethics" });
    controller.search(2, { workspaceId, term: "ethics" });

    expect(signals).toHaveLength(2);
    expect(signals[0].aborted).toBe(true);

    // The abandoned first request lands late and must not be rendered as current.
    first.resolve(response({ page: 1, totalMatched: 150, hasMore: true, sources: [source({ title: "Late first page" })] }));
    await Promise.resolve();
    await Promise.resolve();
    expect(controller.read().page).toBe(2);
    expect(controller.read().result?.page).toBe(2);
  });

  it("stops on request and reports it as stopped", async () => {
    const pending = deferred<AcademicSourceSearchResponse>();
    let observed: AbortSignal | null = null;
    const search = vi.fn((_workspace: string, _params: unknown, signal: AbortSignal) => {
      observed = signal;
      return pending.promise;
    });
    const controller = createScholarlySearchController({ search });
    controller.search(1, { workspaceId, term: "ethics" });
    controller.stop();

    expect(controller.read().state).toBe("cancelled");
    expect(controller.read().failure?.kind).toBe("cancelled");
    expect(observed).not.toBeNull();
    expect((observed as unknown as AbortSignal).aborted).toBe(true);

    pending.resolve(response());
    await Promise.resolve();
    expect(controller.read().state).toBe("cancelled");
  });

  it("discards the request on dispose and stops notifying", async () => {
    const pending = deferred<AcademicSourceSearchResponse>();
    let signal: AbortSignal | null = null;
    const search = vi.fn((_workspace: string, _params: unknown, aborted: AbortSignal) => {
      signal = aborted;
      return pending.promise;
    });
    const controller = createScholarlySearchController({ search });
    const listener = vi.fn();
    controller.subscribe(listener);

    controller.search(1, { workspaceId, term: "ethics" });
    listener.mockClear();
    controller.dispose();

    expect(signal).not.toBeNull();
    expect((signal as unknown as AbortSignal).aborted).toBe(true);

    pending.resolve(response());
    await Promise.resolve();
    expect(listener).not.toHaveBeenCalled();
  });

  it("keeps a failure and its response separate from an empty result", async () => {
    const search = vi.fn().mockRejectedValue(new ApiError(429, "Slow down", undefined, "RATE_LIMITED", 30));
    const controller = createScholarlySearchController({ search });
    controller.search(2, { workspaceId, term: "ethics" });

    await vi.waitFor(() => expect(controller.read().state).toBe("failed"));
    expect(controller.read().result).toBeNull();
    expect(controller.read().failure).toEqual({ kind: "rate_limited", retryAfterSeconds: 30 });
    expect(controller.read().page).toBe(2);
  });

  it("refuses to search without a workspace instead of showing nothing found", async () => {
    const search = vi.fn();
    const controller = createScholarlySearchController({ search });
    controller.search(1, { workspaceId: null, term: "ethics" });
    expect(controller.read().state).toBe("failed");
    expect(controller.read().failure?.kind).toBe("forbidden");
    expect(search).not.toHaveBeenCalled();
  });

  it("passes the page size it was built with", async () => {
    const search = vi.fn().mockResolvedValue(response());
    const controller = createScholarlySearchController({ search, pageSize: 5 });
    controller.search(1, { workspaceId, term: "ethics" });
    await vi.waitFor(() => expect(controller.read().state).toBe("done"));
    expect(search.mock.calls[0][1].pageSize).toBe(5);
  });
});

describe("provider link admission at the display edge", () => {
  // Round 9 of #1267 slice 3: the server refuses machine-local and non-public catalog addresses where
  // it builds the DTO, so the studio normally receives null in those fields. This filter is the second
  // gate over the same data: it can only drop more, never admit more, and it never rewrites an address
  // it keeps. What it does NOT claim: pinning a hostname that resolves differently when clicked — no
  // check on a string controls what the browser dials later.
  const kept = [
    "https://openalex.org/W2741770304",
    "https://books.google.com/books?id=_lYfBgAAQBAJ",
    "https://doi.org/10.1016/j.knr.2021.02.003",
    "https://journal.example.org/articles/45.pdf",
    "http://arxiv.org/pdf/1234",
    "https://synth.example/records/1",
    "https://arxiv1.example.org/x",
    "https://en.m.wikipedia.org/wiki/Kurdistan_Region",
    "https://142.250.80.46/x",
    "  https://journal.example.org/articles/45  ",
  ];
  kept.forEach(url => {
    it(`keeps the public address exactly as it was, trimmed: ${url.trim()}`, () => {
      expect(displayableProviderUrl(url)).toBe(url.trim());
    });
  });

  const refused = [
    // Machine-local names, in the spellings a reader's own network resolves.
    "http://localhost:9200/elastic",
    "http://api.localhost/health",
    "http://host.test/x",
    "http://db.internal/x",
    "http://nas.local:5001/x",
    "http://printer.lan/job",
    "http://router.home/setup",
    "http://staging.intranet/docs",
    "http://x.invalid/",
    "http://box.localdomain/x",
    "http://nas/admin",
    // Non-public addresses, including the obfuscated spellings a browser still dials locally.
    "http://127.0.0.1:8080/stolen",
    "http://[::1]/",
    "http://[::ffff:127.0.0.1]/",
    "http://[64:ff9b::7f00:1]/",
    "http://2130706433/",
    "http://0177.0.0.1/",
    "http://0x7f.0.0.1/",
    "http://127.1/",
    "http://0.0.0.0/",
    "http://169.254.169.254/latest/meta-data/",
    "http://10.20.30.40/report",
    "http://172.16.0.9/x",
    "http://192.168.1.10/router/admin",
    "http://100.64.0.1/x",
    // Not a link this surface should ever make clickable.
    "http://user:pass@openalex.org/W2741770304",
    "javascript:alert(1)",
    "data:text/html;base64,PHNjcmlwdD4=",
    "file:///etc/passwd",
    "not a url",
    "",
    "   ",
  ];
  refused.forEach(url => {
    it(`refuses ${url || "(empty)"}`, () => {
      expect(displayableProviderUrl(url)).toBeNull();
    });
  });

  it("carries the refusal into the row, so no downstream consumer sees the dropped address", () => {
    const [row] = toScholarlySearchRows([source({ landingUrl: "http://db.internal/report", fullTextUrl: null })]);
    expect(row.url).toBeNull();
    const [merged] = toScholarlySearchRows([source({ landingUrl: null, fullTextUrl: "http://nas.local:9200/elastic.pdf" })]);
    expect(merged.url).toBeNull();
    const record = scholarlyRowToSource(merged, { id: "src-refuse-1", addedAt: "2026-11-24T07:05:00Z", accessNote: "Metadata only." });
    expect(record.url).toBeNull();
  });

  it("keeps a public landing page while a local full-text address disappears from the same record", () => {
    const [row] = toScholarlySearchRows([source({ landingUrl: "https://journal.example.org/articles/45", fullTextUrl: "http://nas.local:9200/elastic.pdf" })]);
    expect(row.url).toBe("https://journal.example.org/articles/45");
  });
});

/**
 * The comparison the Sources panel fences its search lifecycle on: what counts as "the same
 * workspace these results were fetched for". Get it wrong in the strict direction and a student
 * loses their own results on every host re-render; get it wrong in the loose direction and the next
 * student sees the records the previous one searched for. `e2e/academic-identity-privacy.spec.ts`
 * pins the outcome in a browser; this pins the rule the fence is built from.
 */
describe("search scope ownership", () => {
  it("names the workspace a search belongs to", () => {
    expect(scholarlySearchScopeKey(workspaceId)).toBe(workspaceId);
  });

  it("treats a re-render of the same workspace as the same owner", () => {
    // Whitespace and repeated renders must not look like a scope change, or the fence would clear a
    // student's own results every time the host rebuilt its props.
    expect(scholarlySearchScopeKey(`  ${workspaceId}  `)).toBe(scholarlySearchScopeKey(workspaceId));
    expect(scholarlySearchScopeKey(scholarlySearchScopeKey(workspaceId))).toBe(scholarlySearchScopeKey(workspaceId));
  });

  it("separates two workspaces, and separates every spelling of no scope from any workspace", () => {
    const other = "9a8b7c6d-5e4f-4a1b-9c8d-7e6f5a4b3c2d";
    expect(scholarlySearchScopeKey(other)).not.toBe(scholarlySearchScopeKey(workspaceId));
    // One clearable value for "no scope", so going from a workspace to none is a change of owner
    // rather than a no-op that leaves the old results on screen after a sign-out.
    for (const empty of [null, undefined, "", "   ", "\t\n"]) {
      expect(scholarlySearchScopeKey(empty)).toBeNull();
    }
    expect(scholarlySearchScopeKey("   ")).not.toBe(scholarlySearchScopeKey(workspaceId));
  });
});
