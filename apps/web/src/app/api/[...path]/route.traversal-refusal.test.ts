import { afterEach, describe, expect, it, vi } from "vitest";
import { GET, POST } from "./route";

// Negative-control harness for the review finding on draft PR #1107
// (review #5969689743): a dot-relative segment must NOT be silently dropped
// and forwarded, because that rewrites a request that was meant to be rejected
// into a *different* valid endpoint (["v1","..","..","admin"] -> /api/v1/admin)
// while the caller's session cookie and CSRF material keep traveling with it.
// The proxy must instead answer a deterministic, non-disclosing 400 before it
// composes the target, before it copies any header, and before it reads any
// request body - so the fetch spy below must be invoked zero times.
//
// Synthetic and in-process only: fetch is stubbed, hosts, cookies, tokens and
// bodies are fake, nothing here reaches a network, a provider or real data.

type RouteContext = { params: Promise<{ path: string[] }> };

function context(path: string[]): RouteContext {
  return { params: Promise.resolve({ path }) };
}

const API_ORIGIN = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000";
const REFUSAL_ENVELOPE = { error: { code: "INVALID_PATH", message: "The requested path is not valid." } };

// A fetch spy that never resolves successfully: any call at all is a failure,
// and the recorded call list is the evidence the guard ran before forwarding.
function zeroToleranceFetch() {
  return vi.fn().mockImplementation(() => {
    throw new Error("synthetic upstream must not be reached");
  });
}

const SESSION = { cookie: "taslim.auth=synthetic-session", "x-csrf-token": "synthetic-csrf" };

describe("dot-relative segment refusal before any forwarding side effect", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("answers a non-disclosing 400 for every relative-segment GET spelling and never calls fetch", async () => {
    const fetchMock = zeroToleranceFetch();
    vi.stubGlobal("fetch", fetchMock);
    const traversalPaths: string[][] = [
      ["v1", "..", "..", "admin"],
      [".."],
      [".", "health"],
      ["health", ".."],
      ["auth", "..", "me"],
      ["..", "..", "..", "admin"],
      // Single-encoded dots.
      ["%2e", "%2e", "%2e%2e", ".env"],
      ["v1", "%2e%2e", "%2e%2e", ".env"],
      ["%2E%2E", "%2E%2E", "admin"],
      // Double-encoded dots.
      ["v1", "%252e%252e", "admin"],
      ["%252E%252E", "%252E%252E", ".env"],
      // Repeated (triple) percent encoding of the same relative segment.
      ["v1", "%25252e%25252e", "admin"],
      // A relative segment mixed into an otherwise valid namespace path.
      ["v1", "documents", "..", "doc-1", "download"],
    ];
    for (const path of traversalPaths) {
      const request = new Request(`http://localhost:3000/api/${path.join("/")}`, { headers: SESSION });
      const response = await GET(request, context(path));
      expect(response.status, path.join("/")).toBe(400);
      await expect(response.json()).resolves.toEqual(REFUSAL_ENVELOPE);
    }
    expect(fetchMock).toHaveBeenCalledTimes(0);
  });

  it("answers a non-disclosing 400 for traversal POST payloads and never reads the body or calls fetch", async () => {
    const fetchMock = zeroToleranceFetch();
    vi.stubGlobal("fetch", fetchMock);
    const traversalPaths: string[][] = [
      ["v1", "..", "..", "admin"],
      ["auth", "..", "login"],
      ["v1", "%2e%2e", "%2e%2e", "admin"],
      ["v1", "%252e%252e", "admin"],
      ["v1", "%25252e%25252e", "admin"],
    ];
    for (const path of traversalPaths) {
      const body = JSON.stringify({ synthetic: "value", secret: "not-read" });
      const request = new Request(`http://localhost:3000/api/${path.join("/")}`, {
        method: "POST",
        headers: { ...SESSION, "content-type": "application/json" },
        body,
      });
      const response = await POST(request, context(path));
      expect(response.status, path.join("/")).toBe(400);
      await expect(response.json()).resolves.toEqual(REFUSAL_ENVELOPE);
      // The refusal happens before body consumption: the request stream is
      // still untouched when the 400 is returned.
      expect(request.bodyUsed, path.join("/")).toBe(false);
    }
    expect(fetchMock).toHaveBeenCalledTimes(0);
  });

  it("counts zero stream pulls when a streamed traversal body is refused", async () => {
    const fetchMock = zeroToleranceFetch();
    vi.stubGlobal("fetch", fetchMock);
    let pulls = 0;
    const encoder = new TextEncoder();
    const stream = new ReadableStream<Uint8Array>({
      pull(controller) {
        pulls += 1;
        controller.enqueue(encoder.encode("synthetic-body-chunk"));
        controller.close();
      },
    });
    const request = new Request("http://localhost:3000/api/v1/../../admin", {
      method: "POST",
      headers: { ...SESSION, "content-type": "application/octet-stream" },
      body: stream,
      duplex: "half",
    } as RequestInit & { duplex: "half" });
    // The platform Request constructor primes a stream body before the
    // handler ever runs, so the baseline for "the route read nothing" is the
    // state at handler entry, not zero.
    await new Promise((resolve) => setTimeout(resolve, 0));
    const pullsBeforeHandler = pulls;
    const bodyUsedBeforeHandler = request.bodyUsed;
    const response = await POST(request, context(["v1", "..", "..", "admin"]));
    expect(response.status).toBe(400);
    await expect(response.json()).resolves.toEqual(REFUSAL_ENVELOPE);
    // No additional pull of the caller's body stream, and no body consumption.
    expect(pulls).toBe(pullsBeforeHandler);
    expect(request.bodyUsed).toBe(bodyUsedBeforeHandler);
    expect(request.bodyUsed).toBe(false);
    expect(fetchMock).toHaveBeenCalledTimes(0);
  });

  it("refuses a traversal path before the body-size limit rather than answering 413", async () => {
    const fetchMock = zeroToleranceFetch();
    vi.stubGlobal("fetch", fetchMock);
    const response = await POST(
      new Request("http://localhost:3000/api/v1/../../admin", {
        method: "POST",
        headers: { ...SESSION, "content-length": String(25 * 1024 * 1024 + 1) },
        body: "small-body",
      }),
      context(["v1", "..", "..", "admin"]),
    );
    expect(response.status).toBe(400);
    await expect(response.json()).resolves.toEqual(REFUSAL_ENVELOPE);
    expect(fetchMock).toHaveBeenCalledTimes(0);
  });

  it("discloses nothing about the path, the segment, or the upstream origin", async () => {
    const fetchMock = zeroToleranceFetch();
    vi.stubGlobal("fetch", fetchMock);
    const path = ["v1", "..", "..", "admin"];
    const response = await GET(
      new Request("http://localhost:3000/api/v1/../../admin", { headers: SESSION }),
      context(path),
    );
    const text = await response.clone().text();
    expect(response.status).toBe(400);
    expect(text).not.toContain("admin");
    expect(text).not.toContain("..");
    expect(text).not.toContain(API_ORIGIN);
    expect(text).not.toContain("/api/");
    expect(text).not.toContain("synthetic-session");
    expect(response.headers.get("content-type")).toContain("application/json");
    expect(fetchMock).toHaveBeenCalledTimes(0);
  });

  it("keeps the refusal deterministic across repeated identical requests", async () => {
    const fetchMock = zeroToleranceFetch();
    vi.stubGlobal("fetch", fetchMock);
    const envelopes: string[] = [];
    for (let attempt = 0; attempt < 3; attempt++) {
      const response = await GET(
        new Request("http://localhost:3000/api/v1/../../admin", { headers: SESSION }),
        context(["v1", "..", "..", "admin"]),
      );
      expect(response.status).toBe(400);
      envelopes.push(await response.clone().text());
    }
    expect(new Set(envelopes).size).toBe(1);
    expect(JSON.parse(envelopes[0])).toEqual(REFUSAL_ENVELOPE);
    expect(fetchMock).toHaveBeenCalledTimes(0);
  });

  it("still forwards ordinary authenticated GET and POST traffic with cookies and CSRF unchanged", async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({ ok: true }), { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);

    const getResponse = await GET(
      new Request("http://localhost:3000/api/v1/documents?mode=list&flag=1", { headers: SESSION }),
      context(["v1", "documents"]),
    );
    expect(getResponse.status).toBe(200);

    const body = JSON.stringify({ email: "owner@example.com", synthetic: "value" });
    const postResponse = await POST(
      new Request("http://localhost:3000/api/auth/login?mode=normal", {
        method: "POST",
        headers: { ...SESSION, "content-type": "application/json" },
        body,
      }),
      context(["auth", "login"]),
    );
    expect(postResponse.status).toBe(200);

    expect(fetchMock).toHaveBeenCalledTimes(2);
    const [getTarget, getInit] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(getTarget).toBe(`${API_ORIGIN}/api/v1/documents?mode=list&flag=1`);
    expect(new Headers(getInit.headers).get("cookie")).toBe("taslim.auth=synthetic-session");
    expect(new Headers(getInit.headers).get("x-csrf-token")).toBe("synthetic-csrf");

    const [postTarget, postInit] = fetchMock.mock.calls[1] as [string, RequestInit];
    expect(postTarget).toBe(`${API_ORIGIN}/api/auth/login?mode=normal`);
    expect(postInit.method).toBe("POST");
    expect(new Headers(postInit.headers).get("cookie")).toBe("taslim.auth=synthetic-session");
    expect(new Headers(postInit.headers).get("x-csrf-token")).toBe("synthetic-csrf");
    await expect(new Response(postInit.body).text()).resolves.toBe(body);
  });

  it("keeps every dot-adjacent and non-relative spelling forwarded byte-identically", async () => {
    const fetchMock = vi.fn().mockImplementation(async (target: string) => {
      const resolved = new Request(target).url;
      return new Response(JSON.stringify({ synthetic: true, resolved }), { status: 200 });
    });
    vi.stubGlobal("fetch", fetchMock);
    const cases: [string[], string][] = [
      [["v1", "...", "admin"], `${API_ORIGIN}/api/v1/.../admin`],
      [["v1", "..admin"], `${API_ORIGIN}/api/v1/..admin`],
      [["a.dot.com", "x"], `${API_ORIGIN}/api/a.dot.com/x`],
      [["....", "health"], `${API_ORIGIN}/api/..../health`],
      [["%2e%2e%2f%2e%2e%2f.env"], `${API_ORIGIN}/api/%252e%252e%252f%252e%252e%252f.env`],
      [["..%2f..%2f.env"], `${API_ORIGIN}/api/..%252f..%252f.env`],
      [["v1", "..%5c..%5c.env"], `${API_ORIGIN}/api/v1/..%255c..%255c.env`],
      [["%2e%2e.env"], `${API_ORIGIN}/api/%252e%252e.env`],
      [["%zz", "health"], `${API_ORIGIN}/api/%25zz/health`],
      [["auth", "csrf"], `${API_ORIGIN}/api/auth/csrf`],
    ];
    for (const [path] of cases) {
      const response = await GET(new Request(`http://localhost:3000/api/${path.join("/")}`, { headers: SESSION }), context(path));
      expect(response.status, path.join("/")).toBe(200);
    }
    expect(fetchMock.mock.calls.map((call) => call[0])).toEqual(cases.map(([, expected]) => expected));
    for (const [target] of fetchMock.mock.calls as [string, RequestInit][]) {
      expect(new URL(new Request(target).url).pathname.startsWith("/api/")).toBe(true);
    }
  });
});
