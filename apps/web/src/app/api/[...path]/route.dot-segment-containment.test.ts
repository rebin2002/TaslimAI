import { afterEach, describe, expect, it, vi } from "vitest";
import { GET, POST } from "./route";

// Narrowly scoped positive/negative-control harness for the production
// containment fix that flips the red-by-design contract test in
// route.scanner-probes.test.ts (draft PR #1105). Synthetic, in-process only:
// fetch is stubbed, hosts and cookies are fake, nothing reaches a network.

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
  it("keeps every relative-segment spelling inside /api/ while still forwarding", async () => {
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
      await GET(request, context(path));
    }
    expect(fetchMock.mock.calls.length).toBe(cases.length);
    for (const [target] of fetchMock.mock.calls as [string, RequestInit][]) {
      expect(composedRequestLine(target).startsWith("/api/")).toBe(true);
    }
  });

  it("carries no session material out of the namespace for a direct relative-segment injection", async () => {
    const fetchMock = recordingFetch();
    vi.stubGlobal("fetch", fetchMock);
    const request = new Request("http://localhost:3000/api/x", {
      headers: { cookie: "taslim.auth=synthetic-session" },
    });
    await GET(request, context(["v1", "..", "..", "admin"]));
    const [target] = fetchMock.mock.calls[0] as [string, RequestInit];
    // Without containment this exact spelling collapses to
    // ${API_ORIGIN}/admin — out of the namespace, session cookie attached.
    // The fix must keep the request line under /api/ rather than let it
    // normalize away, and must not send it to any other origin either.
    expect(new Request(target).url).not.toBe(`${API_ORIGIN}/admin`);
    expect(composedRequestLine(target)).not.toBe("/admin");
    expect(target.startsWith(`${API_ORIGIN}/api/`)).toBe(true);
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

  it("refuses double-encoded relative spellings too, keeping the namespace path identical", async () => {
    const fetchMock = recordingFetch();
    vi.stubGlobal("fetch", fetchMock);
    // Double-encoded dots cannot collapse at the URL parser either, so
    // refusing them is strictly safer than forwarding the encoded noise; the
    // surrounding legitimate segments are untouched.
    await GET(new Request("http://localhost:3000/api/v1/%252e%252e/admin"), context(["v1", "%252e%252e", "admin"]));
    expect(fetchMock.mock.calls[0][0]).toBe(`${API_ORIGIN}/api/v1/admin`);
    expect(composedRequestLine(fetchMock.mock.calls[0][0] as string)).toBe("/api/v1/admin");
  });

  it("preserves query strings when relative segments are removed from the namespace path", async () => {
    const fetchMock = recordingFetch();
    vi.stubGlobal("fetch", fetchMock);
    await GET(
      new Request("http://localhost:3000/api/v1/admin?mode=list&flag=1", {
        headers: { cookie: "taslim.auth=synthetic-session" },
      }),
      context(["v1", "..", "..", "admin"]),
    );
    const [target] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(target.endsWith("?mode=list&flag=1")).toBe(true);
    expect(composedRequestLine(target).startsWith("/api/")).toBe(true);
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
    // removed instead of collapsing the request line out of /api/.
    fetchMock.mockClear();
    await GET(new Request("http://localhost:3000/api/../../admin"), context(["..", "..", "admin"]));
    const [refused] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(composedRequestLine(refused)).toBe("/api/admin");
  });
});
