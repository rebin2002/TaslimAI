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
//  3. If such a segment reaches the handler, `${origin}/api/${seg}` is
//     collapsed by the URL parser ("Request.url" below) and the request line
//     leaves the /api/ namespace, e.g. /api/v1/../../admin -> /admin.
//
// Contract strengthened after review: the handler must REFUSE a dot-relative
// segment, not silently drop it. Dropping kept the request inside /api/ but
// rewrote it onto a different live endpoint (/api/users/../delete ->
// /api/users/delete), which for a state-changing method means an authenticated
// request executed against a resource the caller never addressed. Every case
// below therefore asserts the disjunction "refused with no upstream call" OR
// "forwarded byte-exactly", and never "forwarded to a rewritten path".

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

// The path the caller actually addressed, after per-segment encoding. A
// compliant handler either never reaches fetch() or reaches it with exactly
// this path — any other value means the request was silently retargeted.
function addressedPathname(path: string[]): string {
  return `/api/${path.map((segment) => encodeURIComponent(segment)).join("/")}`;
}

function isRefusedPath(path: string[]): boolean {
  // Mirrors the handler's rule, expressed independently of it: a segment is
  // relative when it, or any bounded percent-decode of it, is exactly "." or
  // "..". Encoded spellings that merely *contain* dots are ordinary names.
  return path.some((segment) => {
    let candidate = segment;
    for (let round = 0; round < 4; round++) {
      if (candidate === "." || candidate === "..") return true;
      try {
        const decoded = decodeURIComponent(candidate);
        if (decoded === candidate) return false;
        candidate = decoded;
      } catch {
        return false;
      }
    }
    return candidate === "." || candidate === "..";
  });
}

describe("scanner probes at the same-origin API proxy boundary", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("keeps every scanner-shaped request contained: refused, or forwarded byte-exactly, never rewritten", async () => {
    const fetchMock = recordingFetch();
    vi.stubGlobal("fetch", fetchMock);
    // Segments exactly as the App Router delivers them for the probe spellings
    // that survive edge normalization, plus the sensitive leaf names verbatim.
    const cases: string[][] = [
      ["%2e%2e.env"],
      ["%2E%2E%2F%2E%2E%2F.env"],
      ["..%2f..%2f.env"],
      ["v1", "..%5c..%5c.env"],
      [".", "health"],
      ["%2e", "%2e", "%2e%2e", ".env"],
      ["auth", ".env"],
      ["health", ".."],
    ];
    let forwarded = 0;
    let refused = 0;
    for (const path of cases) {
      fetchMock.mockClear();
      const request = new Request(`http://localhost:3000/api/${path.join("/")}`, {
        headers: { cookie: "taslim.auth=synthetic-session" },
      });
      const response = await GET(request, context(path));
      if (isRefusedPath(path)) {
        // Fail closed: no upstream call at all, so no rewritten path and no
        // cookie egress is possible for this spelling.
        refused += 1;
        expect(response.status, `expected refusal for ${path.join("/")}`).toBe(400);
        expect(fetchMock).not.toHaveBeenCalled();
        continue;
      }
      forwarded += 1;
      expect(response.status, `expected forward for ${path.join("/")}`).toBe(200);
      expect(fetchMock.mock.calls.length).toBe(1);
      const [target] = fetchMock.mock.calls[0] as [string, RequestInit];
      const resolved = new Request(target).url;
      const pathname = new URL(resolved).pathname;
      // Containment contract: under /api/, with no dot segment left standing.
      expect(pathname.startsWith("/api/"), `escaped namespace: ${pathname}`).toBe(true);
      expect(pathname.split("/").filter((s) => s !== "" && s !== "api")).not.toContain("..");
      expect(pathname.split("/")).not.toContain(".");
      // Non-rewrite contract: exactly the addressed path, no segment dropped.
      expect(pathname, `silently retargeted: ${pathname}`).toBe(addressedPathname(path));
    }
    // Guards against the loop degrading into a no-op if the case list changes.
    expect(refused).toBeGreaterThan(0);
    expect(forwarded).toBeGreaterThan(0);
  });

  it("refuses a literal relative segment outright instead of dropping it and retargeting", async () => {
    const fetchMock = recordingFetch();
    vi.stubGlobal("fetch", fetchMock);
    // The reviewer-reported case: dropping ".." forwards /api/users/delete, a
    // different endpoint, with the caller's session cookie attached.
    const request = new Request("http://localhost:3000/api/users/../delete", {
      method: "DELETE",
      headers: { cookie: "taslim.auth=synthetic-session" },
    });
    const response = await GET(request, context(["users", "..", "delete"]));
    expect(response.status).toBe(400);
    // Nothing reached the upstream, so nothing was retargeted and no
    // credential travelled with it.
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("refuses the collapsing traversal spelling before any upstream call", async () => {
    const fetchMock = recordingFetch();
    vi.stubGlobal("fetch", fetchMock);
    const request = new Request("http://localhost:3000/api/v1/../../admin");
    const response = await GET(request, context(["v1", "..", "..", "admin"]));
    expect(response.status).toBe(400);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("returns a fixed non-disclosing refusal that echoes no request material", async () => {
    const fetchMock = recordingFetch();
    vi.stubGlobal("fetch", fetchMock);
    const request = new Request("http://localhost:3000/api/v1/../../admin");
    const response = await GET(request, context(["v1", "..", "..", "admin"]));
    expect(response.status).toBe(400);
    const body = await response.json();
    expect(body).toEqual({ error: { code: "INVALID_PATH", message: "The requested path is not valid." } });
    const raw = JSON.stringify(body);
    // Must not leak the upstream origin, the traversal segment, or the leaf.
    expect(raw).not.toContain(API_ORIGIN);
    expect(raw).not.toContain("admin");
    expect(raw).not.toContain("..");
  });

  it("returns a non-disclosing unavailable envelope when a contained probe target cannot be served", async () => {
    const fetchMock = vi.fn().mockRejectedValue(new Error("getaddrinfo ENOTFOUND synthetic-upstream.invalid"));
    vi.stubGlobal("fetch", fetchMock);
    // "%2e%2e.env" decodes to "...env": an ordinary single segment, so it is
    // forwarded (contained) and the upstream-failure envelope still applies.
    expect(isRefusedPath(["%2e%2e.env"])).toBe(false);
    const request = new Request("http://localhost:3000/api/%2e%2e.env");
    const response = await GET(request, context(["%2e%2e.env"]));
    expect(response.status).toBe(503);
    await expect(response.json()).resolves.toEqual({
      error: { code: "API_UNAVAILABLE", message: "The service is temporarily unavailable. Please try again." },
    });
  });

  it("keeps session material off any non-namespace or rewritten target", async () => {
    const fetchMock = recordingFetch();
    vi.stubGlobal("fetch", fetchMock);
    const cookie = { cookie: "taslim.auth=synthetic-session" };
    // Refused class: encoded relative segments carry nothing upstream.
    for (const path of [["v1", "%2e%2e", "%2e%2e", ".env"], ["health", ".."], [".", "health"]]) {
      fetchMock.mockClear();
      const request = new Request(`http://localhost:3000/api/${path.join("/")}`, { headers: cookie });
      const response = await GET(request, context(path));
      expect(response.status, `expected refusal for ${path.join("/")}`).toBe(400);
      expect(fetchMock, `cookie egress risk for ${path.join("/")}`).not.toHaveBeenCalled();
    }
    // Forwarded class: the only place a cookie may land is the addressed,
    // in-namespace path.
    for (const path of [["auth", ".env"], ["%2e%2e.env"]]) {
      fetchMock.mockClear();
      const request = new Request(`http://localhost:3000/api/${path.join("/")}`, { headers: cookie });
      await GET(request, context(path));
      expect(fetchMock.mock.calls.length).toBe(1);
      const [target, init] = fetchMock.mock.calls[0] as [string, RequestInit];
      expect(target.startsWith(`${API_ORIGIN}/api/`)).toBe(true);
      expect(new URL(new Request(target).url).pathname).toBe(addressedPathname(path));
      expect(new Headers(init.headers).get("cookie")).toBe("taslim.auth=synthetic-session");
    }
  });
});
