import { afterEach, describe, expect, it, vi } from "vitest";
import { GET, POST } from "./route";

// Narrowly scoped positive/negative-control harness for the production
// containment fix that flips the red-by-design contract test in
// route.scanner-probes.test.ts (draft PR #1105). Synthetic, in-process only:
// fetch is stubbed, hosts and cookies are fake, nothing reaches a network.
//
// Review follow-on (review #5969689743 on #1107): dropping a relative segment
// and forwarding the remainder still invoked a *different* valid endpoint with
// the caller's session cookie, so the handler now refuses the request outright
// with a non-disclosing 400 and the fetch spy below must never be called for a
// traversal spelling. route.traversal-refusal.test.ts carries the dedicated
// negative controls; this file keeps the spelling-by-spelling contract.

type RouteContext = { params: Promise<{ path: string[] }> };

function context(path: string[]): RouteContext {
  return { params: Promise.resolve({ path }) };
}

const API_ORIGIN = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000";

function recordingFetch() {
  return vi.fn().mockImplementation(async (target: string) => {
    // Mirror the platform URL parser the real fetch() uses, so the test
    // observes the request line the upstream would actually receive.
    const resolved = new Request(target).url;
    return new Response(JSON.stringify({ synthetic: true, resolved }), { status: 200 });
  });
}

// The exact composition the fail-closed guard asserts on: the resolved
// request line must stay inside the /api/ namespace before fetch() runs.
function composedRequestLine(target: string): string {
  return new URL(new Request(target).url).pathname;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("literal dot-segment containment at the proxy handler contract", () => {
  it("refuses every relative-segment spelling instead of rewriting it into another endpoint", async () => {
    const fetchMock = recordingFetch();
    vi.stubGlobal("fetch", fetchMock);
    const cases: string[][] = [
      ["v1", "..", "..", "admin"],
      [".."],
      [".", "health"],
      ["health", ".."],
      ["..", "..", "..", "admin"],
      ["%2e", "%2e", "%2e%2e", ".env"],
      ["v1", "%2e%2e", "%2e%2e", ".env"],
      ["auth", "..", "me"],
    ];
    for (const path of cases) {
      const request = new Request(`http://localhost:3000/api/${path.join("/")}`, {
        headers: { cookie: "taslim.auth=synthetic-session" },
      });
      const response = await GET(request, context(path));
      // The old contract - containment by deletion - turned ["v1","..","..",
      // "admin"] into /api/v1/admin and authenticated it. Deletion is not a
      // refusal, so every spelling here must answer 400.
      expect(response.status, path.join("/")).toBe(400);
      await expect(response.json()).resolves.toEqual({
        error: { code: "INVALID_PATH", message: "The requested path is not valid." },
      });
    }
    expect(fetchMock).toHaveBeenCalledTimes(0);
  });

  it("carries no session material anywhere for a direct relative-segment injection", async () => {
    const fetchMock = recordingFetch();
    vi.stubGlobal("fetch", fetchMock);
    const request = new Request("http://localhost:3000/api/x", {
      headers: { cookie: "taslim.auth=synthetic-session" },
    });
    const response = await GET(request, context(["v1", "..", "..", "admin"]));
    // Without containment this exact spelling collapses to
    // ${API_ORIGIN}/admin - out of the namespace, session cookie attached -
    // and rewriting it to /api/v1/admin would still authenticate a different
    // endpoint. Refusing it means no request line and no cookie ever leaves.
    expect(response.status).toBe(400);
    expect(fetchMock).toHaveBeenCalledTimes(0);
  });

  it("keeps every legitimate spelling byte-identical, including dot-adjacent names and encoded slashes", async () => {
    const fetchMock = recordingFetch();
    vi.stubGlobal("fetch", fetchMock);
    const cases: [string[], string][] = [
      [["auth", "csrf"], `${API_ORIGIN}/api/auth/csrf`],
      [["v1", "documents", "doc-1", "download"], `${API_ORIGIN}/api/v1/documents/doc-1/download`],
      // Relative-adjacent but non-relative names are ordinary upstream paths.
      [["v1", "...", "admin"], `${API_ORIGIN}/api/v1/.../admin`],
      [["v1", "..admin"], `${API_ORIGIN}/api/v1/..admin`],
      [["%2e%2e%2f%2e%2e%2f.env"], `${API_ORIGIN}/api/%252e%252e%252f%252e%252e%252f.env`],
      [["..%2f..%2f.env"], `${API_ORIGIN}/api/..%252f..%252f.env`],
      [["v1", "..%5c..%5c.env"], `${API_ORIGIN}/api/v1/..%255c..%255c.env`],
      [["a.dot.com", "x"], `${API_ORIGIN}/api/a.dot.com/x`],
      // Malformed percent-escapes are ordinary names, never relative segments.
      [["%zz", "health"], `${API_ORIGIN}/api/%25zz/health`],
    ];
    for (const [path] of cases) {
      await GET(new Request(`http://localhost:3000/api/${path.join("/")}`), context(path));
    }
    expect(fetchMock.mock.calls.map((call) => call[0])).toEqual(cases.map(([, expected]) => expected));
    for (const [target] of fetchMock.mock.calls as [string, RequestInit][]) {
      expect(composedRequestLine(target).startsWith("/api/")).toBe(true);
    }
  });

  it("refuses double- and repeatedly-encoded relative spellings too", async () => {
    const fetchMock = recordingFetch();
    vi.stubGlobal("fetch", fetchMock);
    // Dropping "%252e%252e" rewrote this into /api/v1/admin: a different
    // endpoint, requested with credentials. Repeated percent-encoding of the
    // same relative segment is refused the same way; the surrounding
    // legitimate segments are never forwarded either, because the whole
    // request is refused.
    for (const path of [["v1", "%252e%252e", "admin"], ["v1", "%25252e%25252e", "admin"]]) {
      const response = await GET(
        new Request(`http://localhost:3000/api/${path.join("/")}`, {
          headers: { cookie: "taslim.auth=synthetic-session" },
        }),
        context(path),
      );
      expect(response.status, path.join("/")).toBe(400);
    }
    expect(fetchMock).toHaveBeenCalledTimes(0);
  });

  it("preserves query strings for valid paths and refuses them for relative ones", async () => {
    const fetchMock = recordingFetch();
    vi.stubGlobal("fetch", fetchMock);
    await GET(
      new Request("http://localhost:3000/api/v1/documents?mode=list&flag=1", {
        headers: { cookie: "taslim.auth=synthetic-session" },
      }),
      context(["v1", "documents"]),
    );
    const [target] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(target.endsWith("?mode=list&flag=1")).toBe(true);
    expect(composedRequestLine(target).startsWith("/api/")).toBe(true);
    // The same query string arriving with relative segments is refused rather
    // than forwarded to whatever endpoint the rewrite would have produced.
    const refused = await GET(
      new Request("http://localhost:3000/api/v1/admin?mode=list&flag=1", {
        headers: { cookie: "taslim.auth=synthetic-session" },
      }),
      context(["v1", "..", "..", "admin"]),
    );
    expect(refused.status).toBe(400);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("still forwards valid authenticated POST bodies with cookies and CSRF material", async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({ ok: true }), { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);
    const body = JSON.stringify({ email: "owner@example.com", synthetic: "value" });
    const request = new Request("http://localhost:3000/api/auth/login?mode=normal", {
      method: "POST",
      headers: {
        cookie: "taslim.auth=synthetic-session",
        "content-type": "application/json",
        "x-csrf-token": "request-token",
      },
      body,
    });
    const response = await POST(request, context(["auth", "login"]));
    const [target, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    const forwardedHeaders = new Headers(init.headers);
    const forwardedBody = await new Response(init.body).text();

    expect(target).toBe(`${API_ORIGIN}/api/auth/login?mode=normal`);
    expect(forwardedHeaders.get("cookie")).toBe("taslim.auth=synthetic-session");
    expect(forwardedHeaders.get("x-csrf-token")).toBe("request-token");
    expect(forwardedBody).toBe(body);
    expect(response.status).toBe(200);
  });

  it("refuses the whole request rather than reaching upstream when composition escapes /api/", async () => {
    const fetchMock = recordingFetch();
    vi.stubGlobal("fetch", fetchMock);
    // Independent fail-closed assertion control: whatever spelling is used,
    // the composed request line handed to fetch() must satisfy the guard, so
    // a request whose namespace path would normalize away is never sent.
    await GET(new Request("http://localhost:3000/api/..%2f..%2fadmin"), context(["..%2f..%2fadmin"]));
    const [target] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(composedRequestLine(target)).toBe("/api/..%252f..%252fadmin");
    // Contrast: the same leaf supplied as a genuine relative segment is
    // refused outright, not deleted-then-forwarded as /api/admin.
    fetchMock.mockClear();
    const response = await GET(
      new Request("http://localhost:3000/api/../../admin", {
        headers: { cookie: "taslim.auth=synthetic-session" },
      }),
      context(["..", "..", "admin"]),
    );
    expect(response.status).toBe(400);
    expect(fetchMock).toHaveBeenCalledTimes(0);
  });
});
