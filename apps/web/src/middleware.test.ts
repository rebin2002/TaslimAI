import { describe, expect, it } from "vitest";
import { NextRequest } from "next/server";
import middleware, {
  PRIVATE_DOC_CACHE_CONTROL,
  PRIVATE_DOC_ROBOTS,
  config,
  isPublicDocPath,
} from "./middleware";

// Provider-free by construction: nothing here reads environment credentials,
// opens a socket, or reaches an AI provider. A `NextRequest` is built from a
// loopback URL so the assertions are pure header-policy arithmetic that the
// `Frontend checks` gate can run offline.
function requestFor(pathWithQuery: string): NextRequest {
  return new NextRequest(`http://localhost:3000${pathWithQuery}`);
}

// Mirrors how Next evaluates `config.matcher`: a request runs the middleware
// when ANY entry matches the whole pathname.
function matcherAdmits(pathname: string): boolean {
  return config.matcher.some((pattern) => new RegExp(`^${pattern}$`).test(pathname));
}

function policyFor(pathWithQuery: string): { cacheControl: string | null; robots: string | null } {
  const headers = middleware(requestFor(pathWithQuery)).headers;
  return {
    cacheControl: headers.get("Cache-Control"),
    robots: headers.get("X-Robots-Tag"),
  };
}

describe("SSR cache isolation middleware", () => {
  it("forces the workspace home out of shared caches", () => {
    const response = middleware(requestFor("/chat"));
    expect(response.headers.get("Cache-Control")).toBe(PRIVATE_DOC_CACHE_CONTROL);
    expect(response.headers.get("X-Robots-Tag")).toBe(PRIVATE_DOC_ROBOTS);
    expect(PRIVATE_DOC_CACHE_CONTROL).not.toMatch(/s-maxage|public/);
  });

  it("covers the root document and deep dynamic segments", () => {
    for (const path of ["/", "/projects", "/projects/9f2c", "/chat/550e8400", "/search?q=quarterly"]) {
      expect(middleware(requestFor(path)).headers.get("Cache-Control")).toBe(PRIVATE_DOC_CACHE_CONTROL);
    }
  });

  it("applies the same policy to RSC payload variants, which share the document cache key", () => {
    const response = middleware(requestFor("/projects?_rsc=0123"));
    expect(response.headers.get("Cache-Control")).toBe(PRIVATE_DOC_CACHE_CONTROL);
  });

  it("applies the same policy to `<route>.rsc` payload URLs at every depth", () => {
    // The App Router answers these with `Content-Type: text/x-component` for
    // the same route. Production probes showed a single top-level segment
    // (`/search.rsc`) falling into the static-file carve-out while a deeper
    // segment (`/create/image.rsc`) was guarded, so the exemption was an
    // accident of shape rather than policy.
    for (const path of [
      "/search.rsc",
      "/projects.rsc",
      "/chat.rsc",
      "/notifications.rsc",
      "/create/image.rsc",
      "/projects/9f2c.rsc",
      "/account/billing.rsc",
    ]) {
      const { cacheControl, robots } = policyFor(path);
      expect(cacheControl, path).toBe(PRIVATE_DOC_CACHE_CONTROL);
      expect(robots, path).toBe(PRIVATE_DOC_ROBOTS);
      expect(matcherAdmits(path), path).toBe(true);
    }
  });

  it("keeps the public credential forms untouched", () => {
    for (const path of ["/login", "/register", "/login?next=%2Fprojects"]) {
      const response = middleware(requestFor(path));
      expect(response.headers.get("Cache-Control")).toBeNull();
      expect(response.headers.get("X-Robots-Tag")).toBeNull();
    }
  });

  it("does not exempt a payload URL of a public form", () => {
    // This pins the policy layer only: given a pathname the allowlist does not
    // name, the function refuses to emit a public lifetime. At the deployed
    // layer Next resolves `<form>.rsc` back to the form page before invoking
    // the middleware, so the live credential-form payload keeps its framework
    // policy - verified in both `next dev` and `next start`, and asserted there
    // by e2e/private-route-cache-isolation.spec.ts.
    for (const path of ["/login.rsc", "/register.rsc"]) {
      expect(isPublicDocPath(path)).toBe(false);
      expect(policyFor(path).cacheControl).toBe(PRIVATE_DOC_CACHE_CONTROL);
    }
  });

  it("normalizes trailing slashes without widening the allowlist by case", () => {
    expect(isPublicDocPath("/login/")).toBe(true);
    expect(isPublicDocPath("/register/")).toBe(true);
    // App Router routes are case-sensitive: `/Login` renders the private
    // not-found document and must stay inside the guarded set.
    expect(isPublicDocPath("/Login")).toBe(false);
    expect(isPublicDocPath("/LOGIN")).toBe(false);
    expect(isPublicDocPath("/Register")).toBe(false);
    expect(isPublicDocPath("/chat/")).toBe(false);
    expect(isPublicDocPath("/account")).toBe(false);
  });

  it("cannot be escaped by path shape, encoding or traversal", () => {
    for (const path of [
      "/login/../projects",
      "/login/%2e%2e/projects",
      "//login",
      "/login/extra",
      "/register/dashboard",
      "/login.json",
      "/api/../projects",
    ]) {
      expect(isPublicDocPath(new URL(`http://localhost:3000${path}`).pathname), path).toBe(false);
      expect(policyFor(path).cacheControl, path).toBe(PRIVATE_DOC_CACHE_CONTROL);
    }
  });

  it("fails closed for unknown routes", () => {
    const response = middleware(requestFor("/some-future-private-surface"));
    expect(response.headers.get("Cache-Control")).toBe(PRIVATE_DOC_CACHE_CONTROL);
  });

  it("writes a header value that cannot carry a second directive or a control character", () => {
    expect(PRIVATE_DOC_CACHE_CONTROL).toMatch(/^private, no-cache, no-store, must-revalidate, max-age=0$/);
    expect(PRIVATE_DOC_ROBOTS).toMatch(/^noindex, nofollow, noarchive, nosnippet$/);
    for (const path of ["/chat", "/projects.rsc", "/a/../b", "/search?q=%0D%0Ax-robots-tag%3Aall"]) {
      const headers = middleware(requestFor(path)).headers;
      for (const value of [headers.get("Cache-Control"), headers.get("X-Robots-Tag")]) {
        expect(value).not.toMatch(/[\r\n]/);
      }
    }
  });

  it("is idempotent and never emits a cookie on a guarded document", () => {
    const first = policyFor("/projects");
    const second = policyFor("/projects");
    expect(second).toEqual(first);
    expect(middleware(requestFor("/projects")).headers.get("set-cookie")).toBeNull();
  });

  it("excludes the API proxy, framework assets, and extension-bearing files from every matcher entry", () => {
    for (const skipped of [
      "/api/auth/me",
      "/api/projects/1/files",
      "/api/x.rsc",
      "/_next/static/chunks/main.js",
      "/_next/static/css/app.css",
      "/_next/image?url=x",
      "/_next/data/build/projects.json",
      "/robots.txt",
      "/favicon.ico",
      "/manifest.webmanifest",
    ]) {
      expect(matcherAdmits(skipped), skipped).toBe(false);
    }
    for (const guarded of [
      "/",
      "/chat",
      "/fonts/Inter.woff2",
      "/projects/1",
      "/account/billing",
      "/development/build",
      "/personal/health",
      "/search.rsc",
      "/some-future-route",
    ]) {
      expect(matcherAdmits(guarded), guarded).toBe(true);
    }
  });
});
