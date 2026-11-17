import { describe, expect, it } from "vitest";
import { NextRequest } from "next/server";
import middleware, {
  PRIVATE_DOC_CACHE_CONTROL,
  PRIVATE_DOC_ROBOTS,
  config,
  isPublicDocPath,
} from "./middleware";

function requestFor(pathWithQuery: string): NextRequest {
  return new NextRequest(`http://localhost:3000${pathWithQuery}`);
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

  it("keeps the public credential forms untouched", () => {
    for (const path of ["/login", "/register", "/login?next=%2Fprojects"]) {
      const response = middleware(requestFor(path));
      expect(response.headers.get("Cache-Control")).toBeNull();
      expect(response.headers.get("X-Robots-Tag")).toBeNull();
    }
  });

  it("normalizes trailing slashes and casing before the public allowlist", () => {
    expect(isPublicDocPath("/register/")).toBe(true);
    expect(isPublicDocPath("/Login")).toBe(true);
    expect(isPublicDocPath("/chat/")).toBe(false);
    expect(isPublicDocPath("/account")).toBe(false);
  });

  it("fails closed for unknown routes", () => {
    const response = middleware(requestFor("/some-future-private-surface"));
    expect(response.headers.get("Cache-Control")).toBe(PRIVATE_DOC_CACHE_CONTROL);
  });

  it("excludes the API proxy, framework assets, and extension-bearing files from the matcher", () => {
    const pattern = new RegExp(`^${config.matcher[0]}$`);
    for (const skipped of ["/api/auth/me", "/api/projects/1/files", "/_next/static/chunks/main.js", "/_next/image?url=x", "/robots.txt", "/favicon.ico", "/manifest.webmanifest"]) {
      expect(pattern.test(skipped), skipped).toBe(false);
    }
    for (const guarded of ["/", "/chat", "/projects/1", "/account/billing", "/development/build", "/personal/health"]) {
      expect(pattern.test(guarded), guarded).toBe(true);
    }
  });
});
