import { afterEach, describe, expect, it, vi } from "vitest";
import { GET } from "./route";

// Provider-free, network-free synthetic regression harness for the
// internet-scanner probe classes observed against the deployed Web service
// (read attempts for /.env, /.git/*, /.vscode/*, /wp-content/debug.log,
// /.config credential paths, and traversal spellings aimed at the
// same-origin /api/[...path] proxy). Every request below is a synthetic
// in-process Request object; nothing here touches a real network.
//
// Threat boundary established locally (disposable `next start` + a recorded
// fake upstream on loopback only):
//  1. The public directory ships no assets and there are no rewrites, so
//     every scanner dot-path lands on the App Router not-found document.
//  2. The edge normalizes unencoded dot segments and rejects encoded
//     multi-segment traversals before the proxy handler runs, so the only
//     spelling that reaches the handler with a literal ".." segment is one
//     supplied directly to the handler contract tested here.
//  3. If such a segment ever reaches the handler, `${origin}/api/${seg}`
//     is collapsed by the URL parser ("Request.url" below) and the request
//     line leaves the /api/ namespace, e.g. /api/v1/../../admin -> /admin,
//     which can reach API surfaces mounted outside /api (health endpoints).

type RouteContext = { params: Promise<{ path: string[] }> };

function context(path: string[]): RouteContext {
  return { params: Promise.resolve({ path }) };
}

function recordingFetch() {
  return vi.fn().mockImplementation(async (target: string) => {
    // Mirror the platform URL parser the real fetch() uses, so the test
    // observes the request line the upstream would actually receive.
    const resolved = new Request(target).url;
    return new Response(JSON.stringify({ synthetic: true, resolved }), { status: 200 });
  });
}

const API_ORIGIN = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000";

describe("scanner probes at the same-origin API proxy boundary", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("keeps every ordinary scanner spelling inside /api/ and refuses the relative ones", async () => {
    const fetchMock = recordingFetch();
    vi.stubGlobal("fetch", fetchMock);
    // Ordinary (non-relative) scanner noise keeps forwarding byte-identically.
    const cases: string[][] = [
      ["%2e%2e.env"],
      ["%2E%2E%2F%2E%2E%2F.env"],
      ["..%2f..%2f.env"],
      ["v1", "..%5c..%5c.env"],
      ["auth", ".env"],
    ];
    for (const path of cases) {
      const request = new Request(`http://localhost:3000/api/${path.join("/")}`, {
        headers: { cookie: "taslim.auth=synthetic-session" },
      });
      await GET(request, context(path));
    }
    expect(fetchMock.mock.calls.length).toBe(cases.length);
    for (const call of fetchMock.mock.calls) {
      const [target] = call as [string, RequestInit];
      const resolved = new Request(target).url;
      const pathname = new URL(resolved).pathname;
      // Containment contract: the upstream request line must stay under /api/
      // and must never contain a bare "." or ".." segment that the URL parser
      // could collapse onto a sibling namespace.
      expect(pathname.startsWith("/api/")).toBe(true);
      expect(pathname.split("/").filter((s) => s !== "" && s !== "api")).not.toContain("..");
      expect(pathname.split("/")).not.toContain(".");
    }
    // A genuine relative segment is now refused with a non-disclosing 400
    // instead of being deleted from a request that still gets forwarded.
    const refusedCases: string[][] = [
      [".", "health"],
      ["%2e", "%2e", "%2e%2e", ".env"],
      ["health", ".."],
    ];
    for (const path of refusedCases) {
      const response = await GET(
        new Request(`http://localhost:3000/api/${path.join("/")}`, {
          headers: { cookie: "taslim.auth=synthetic-session" },
        }),
        context(path),
      );
      expect(response.status, path.join("/")).toBe(400);
    }
    expect(fetchMock).toHaveBeenCalledTimes(cases.length);
  });

  it("fails closed on a literal relative segment instead of collapsing or rewriting it", async () => {
    const fetchMock = recordingFetch();
    vi.stubGlobal("fetch", fetchMock);
    // Direct handler-level injection of a resolved ".." segment: the spellings
    // the deployed edge rejects before routing, pinned here as the containment
    // contract for the handler itself (RED by design until route.ts refuses
    // relative segments). Refusal, not deletion, is the contract: dropping the
    // segment produced /api/v1/admin and authenticated it with the caller's
    // cookie, so the spy below must record zero upstream calls.
    const request = new Request("http://localhost:3000/api/x", {
      headers: { cookie: "taslim.auth=synthetic-session" },
    });
    const response = await GET(request, context(["v1", "..", "..", "admin"]));
    expect(response.status).toBe(400);
    await expect(response.json()).resolves.toEqual({
      error: { code: "INVALID_PATH", message: "The requested path is not valid." },
    });
    expect(fetchMock).toHaveBeenCalledTimes(0);
  });

  it("returns a non-disclosing unavailable envelope when the probe target cannot be served", async () => {
    const fetchMock = vi.fn().mockRejectedValue(new Error("getaddrinfo ENOTFOUND synthetic-upstream.invalid"));
    vi.stubGlobal("fetch", fetchMock);
    const request = new Request("http://localhost:3000/api/%2e%2e.env");
    const response = await GET(request, context(["%2e%2e.env"]));
    expect(response.status).toBe(503);
    await expect(response.json()).resolves.toEqual({
      error: { code: "API_UNAVAILABLE", message: "The service is temporarily unavailable. Please try again." },
    });
  });

  it("does not forward session material when the containment contract holds", async () => {
    const fetchMock = recordingFetch();
    vi.stubGlobal("fetch", fetchMock);
    // The synthetic cookie exists only to prove what would travel if a probe
    // escaped the namespace. A relative segment now never reaches the
    // upstream at all, rewritten or otherwise, so no cookie can travel.
    const request = new Request("http://localhost:3000/api/v1/%2e%2e/%2e%2e/.env", {
      headers: { cookie: "taslim.auth=synthetic-session" },
    });
    const response = await GET(request, context(["v1", "%2e%2e", "%2e%2e", ".env"]));
    expect(response.status).toBe(400);
    expect(fetchMock).toHaveBeenCalledTimes(0);
    // Nothing about the probe, the namespace, or the fixed API origin is
    // reflected back to the scanner.
    await expect(response.clone().text()).resolves.not.toContain(API_ORIGIN);
  });
});
