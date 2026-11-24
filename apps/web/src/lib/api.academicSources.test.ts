/**
 * Issue #1267 slice 1 — the scholarly search request the web client makes.
 *
 * Pinned here: it is a GET against the workspace-scoped route with no CSRF token and no body
 * (so it cannot create state, spend anything or need an idempotency key), the abort signal is
 * carried through to `fetch`, bounded parameters are sent as the server expects them, and the
 * throttle contract this repo already has for reads applies to this one too. All responses are
 * synthetic fixtures; no real workspace or record is involved.
 */
import { beforeEach, describe, expect, it, vi } from "vitest";

const workspaceId = "1f2e3d4c-b5a6-4789-abcd-0123456789ab";
const route = `/api/workspaces/${workspaceId}/academic-sources/search`;

function pageBody(overrides: Record<string, unknown> = {}) {
  return {
    workspaceId,
    page: 1,
    pageSize: 10,
    totalMatched: 1,
    hasMore: false,
    partial: false,
    partialNotice: null,
    sources: [
      {
        title: "Synthetic governance of synthetic data",
        authors: ["Rebin Khoshnaw"],
        publicationYear: 2024,
        sourceType: "journal_article",
        containerTitle: "Journal of Synthetic Studies",
        publisher: "Synthetic Press",
        volume: "12",
        issue: "3",
        pageRange: "41-58",
        pageCount: 18,
        language: "en",
        citationCount: 7,
        doi: "10.5555/synth.2024.0001",
        isbn13: null,
        isbn10: null,
        abstract: null,
        publisherDescription: null,
        landingUrl: "https://synth.example/records/1",
        fullTextUrl: null,
        accessLevel: "metadata",
        fullTextIndicated: false,
        provenance: { basis: "scholarly_catalog_metadata", retrievedAtUtc: "2026-11-24T07:00:00Z", accessDescription: "Bibliographic record found." },
        relevance: 0.5,
      },
    ],
    doiChecks: [],
    ...overrides,
  };
}

function jsonResponse(body: unknown, status = 200, headers: Record<string, string> = {}) {
  return new Response(typeof body === "string" ? body : JSON.stringify(body), { status, headers: { "Content-Type": "application/json", ...headers } });
}

async function clientWith(fetchMock: ReturnType<typeof vi.fn>) {
  vi.stubGlobal("fetch", fetchMock);
  return await import("./api");
}

beforeEach(() => {
  vi.resetModules();
  vi.unstubAllGlobals();
});

describe("academic sources search request", () => {
  it("is a bodyless GET on the workspace-scoped route, with the session and no CSRF token", async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(pageBody()));
    const { api } = await clientWith(fetchMock);

    const page = await api.searchAcademicSources(workspaceId, { term: "data ethics", page: 1, pageSize: 10 });

    expect(page.totalMatched).toBe(1);
    expect(page.sources[0].accessLevel).toBe("metadata");
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(String(url)).toContain(route);
    expect((init.method ?? "GET").toUpperCase()).toBe("GET");
    expect(init.body).toBeUndefined();
    expect(init.credentials).toBe("include");
    const headers = new Headers(init.headers);
    expect(headers.get("X-CSRF-TOKEN")).toBeNull();
    expect(headers.get("Idempotency-Key")).toBeNull();
  });

  it("trims the term and encodes paging, bounds and both identifier families", async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(pageBody()));
    const { api } = await clientWith(fetchMock);

    await api.searchAcademicSources(workspaceId, {
      term: "   market access   ",
      page: 4,
      pageSize: 10,
      yearFrom: 2018,
      yearTo: 2025,
      openAccessOnly: true,
      dois: ["10.5555/synth.2024.0001"],
      isbns: ["9780306406157", "9780306406164"],
    });

    const search = new URL(String(fetchMock.mock.calls[0][0]), "https://synth.example").searchParams;
    expect(search.get("q")).toBe("market access");
    expect(search.get("page")).toBe("4");
    expect(search.get("pageSize")).toBe("10");
    expect(search.get("yearFrom")).toBe("2018");
    expect(search.get("yearTo")).toBe("2025");
    expect(search.get("openAccessOnly")).toBe("true");
    expect(search.getAll("doi")).toEqual(["10.5555/synth.2024.0001"]);
    expect(search.getAll("isbn")).toEqual(["9780306406157", "9780306406164"]);
    // A workspace id is path data, never a query parameter.
    expect(String(fetchMock.mock.calls[0][0])).not.toContain("workspaceId=");
  });

  it("carries the caller's abort signal into fetch", async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(pageBody()));
    const { api } = await clientWith(fetchMock);
    const controller = new AbortController();

    await api.searchAcademicSources(workspaceId, { term: "ethics", page: 1, pageSize: 10 }, controller.signal);

    expect((fetchMock.mock.calls[0][1] as RequestInit).signal).toBe(controller.signal);
  });

  it("rejects with the abort reason when the student stops the search", async () => {
    const controller = new AbortController();
    const fetchMock = vi.fn(
      () =>
        new Promise<Response>((_resolve, reject) => {
          controller.signal.addEventListener("abort", () => reject(new DOMException("Aborted", "AbortError")));
        }),
    );
    const { api } = await clientWith(fetchMock);

    const pending = api.searchAcademicSources(workspaceId, { term: "ethics", page: 1, pageSize: 10 }, controller.signal);
    await Promise.resolve();
    controller.abort();

    await expect(pending).rejects.toBeInstanceOf(DOMException);
  });

  it("keeps the refusal codes distinguishable for the caller", async () => {
    const cases: [number, string][] = [
      [401, "UNAUTHENTICATED"],
      [403, "FORBIDDEN"],
      [400, "VALIDATION_FAILED"],
      [503, "ACADEMIC_SEARCH_UNAVAILABLE"],
    ];
    for (const [status, code] of cases) {
      vi.resetModules();
      const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ error: { code, message: "Synthetic refusal." } }, status));
      const { ApiError, api } = await clientWith(fetchMock);
      const failure = await api.searchAcademicSources(workspaceId, { term: "ethics", page: 1, pageSize: 10 }).catch((error) => error);
      expect(failure, String(status)).toBeInstanceOf(ApiError);
      expect(failure.status).toBe(status);
      expect(failure.code).toBe(code);
    }
  });

  it("repeats a throttled search once the advertised delay passed, like any other read", async () => {
    vi.useFakeTimers();
    try {
      const fetchMock = vi.fn()
        .mockResolvedValueOnce(jsonResponse({ error: { code: "RATE_LIMITED", message: "Too many requests. Please try again later." } }, 429, { "Retry-After": "2" }))
        .mockResolvedValueOnce(jsonResponse(pageBody()));
      const { api } = await clientWith(fetchMock);
      const pending = api.searchAcademicSources(workspaceId, { term: "ethics", page: 1, pageSize: 10 });
      await Promise.resolve();
      expect(fetchMock).toHaveBeenCalledTimes(1);
      // Nothing is sent before the server allowed it.
      await vi.advanceTimersByTimeAsync(1_999);
      expect(fetchMock).toHaveBeenCalledTimes(1);
      await vi.advanceTimersByTimeAsync(1);
      await expect(pending).resolves.toMatchObject({ totalMatched: 1 });
      expect(fetchMock).toHaveBeenCalledTimes(2);
    } finally {
      vi.useRealTimers();
    }
  });

  it("surfaces the shipped 60-second instruction rather than hanging the panel on it", async () => {
    // `RateLimiting.Search` rejects with `Retry-After: 60`, far past the budget one
    // interactive read may spend waiting, so the wait is handed back to the student.
    const fetchMock = vi.fn().mockResolvedValue(
      jsonResponse({ error: { code: "RATE_LIMITED", message: "Too many requests. Please try again later." } }, 429, { "Retry-After": "60" }),
    );
    const { ApiError, api } = await clientWith(fetchMock);

    const failure = await api.searchAcademicSources(workspaceId, { term: "ethics", page: 1, pageSize: 10 }).catch((error) => error);

    expect(failure).toBeInstanceOf(ApiError);
    expect(failure.status).toBe(429);
    expect(failure.code).toBe("RATE_LIMITED");
    expect(failure.retryAfterSeconds).toBe(60);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
});
