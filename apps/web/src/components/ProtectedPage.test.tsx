import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { authSuccessPath } from "@/lib/authRedirect";
import { PROTECTED_PAGE_AUTH_TIMEOUT_MS, protectedPageLoginPath } from "./ProtectedPage";

/**
 * Simulates the login page contract: the browser receives the redirect produced
 * by the expiry gate, Next.js decodes `next`, and AuthForm validates it through
 * authSuccessPath before navigating. Fake and deterministic: no transport, API,
 * or provider is involved.
 */
function followLoginReturnPath(loginUrl: string): string | null {
  const next = new URL(loginUrl, "http://protected.test").searchParams.get("next");
  return authSuccessPath(next);
}

describe("protected page loading boundary", () => {
  it("uses a finite session bootstrap timeout", () => {
    expect(PROTECTED_PAGE_AUTH_TIMEOUT_MS).toBe(10_000);
    expect(PROTECTED_PAGE_AUTH_TIMEOUT_MS).toBeGreaterThan(0);
  });

  it("keeps login return paths local and rejects control characters", () => {
    expect(protectedPageLoginPath("/personal/health")).toBe("/login?next=%2Fpersonal%2Fhealth");
    expect(protectedPageLoginPath("//evil.example/health")).toBe("/login?next=%2F");
    expect(protectedPageLoginPath(`/personal/health${String.fromCharCode(0)}`)).toBe("/login?next=%2F");
  });

  describe("session expiry return path", () => {
    it("preserves the query string that protected views hydrate their state from", () => {
      // window.location.search carries the leading "?", matching the browser shape.
      expect(protectedPageLoginPath("/create/research", "?projectId=project-1"))
        .toBe("/login?next=%2Fcreate%2Fresearch%3FprojectId%3Dproject-1");
      expect(protectedPageLoginPath("/projects", "?create=1"))
        .toBe("/login?next=%2Fprojects%3Fcreate%3D1");
      expect(protectedPageLoginPath("/search", "q=tailored+brief&workspace=1"))
        .toBe("/login?next=%2Fsearch%3Fq%3Dtailored%2Bbrief%26workspace%3D1");
    });

    it("returns the signed-in user to the exact deep link they were on", () => {
      for (const [pathname, search] of [
        ["/create/research", "?projectId=project-1"],
        ["/projects", "?create=1"],
        ["/personal/health", ""],
      ] as const) {
        const expected = `${pathname}${search}`;
        expect(followLoginReturnPath(protectedPageLoginPath(pathname, search))).toBe(expected);
      }
    });

    it("does not change the return path when there is no query string", () => {
      expect(protectedPageLoginPath("/activity", "")).toBe(protectedPageLoginPath("/activity"));
      expect(protectedPageLoginPath("/activity", "?")).toBe(protectedPageLoginPath("/activity"));
    });

    it("still rejects protocol-relative and control-character targets when a query is present", () => {
      // The query is part of the destination, so it cannot widen the validation
      // that already applies to the path: unsafe targets fall back to the root.
      expect(protectedPageLoginPath("//evil.example/health", "?projectId=project-1"))
        .toBe("/login?next=%2F");
      expect(protectedPageLoginPath("/create/research", `?projectId=${String.fromCharCode(7)}`))
        .toBe("/login?next=%2F");
      expect(followLoginReturnPath(protectedPageLoginPath("//evil.example/health", "?x=1"))).toBe("/");
    });

    it("reads the current location search, not only the pathname, when redirecting", () => {
      const source = readFileSync(new URL("./ProtectedPage.tsx", import.meta.url), "utf8");
      expect(source).toContain("usePathname()");
      expect(source).toContain("window.location.search");
      const redirects = source.match(/router\.replace\(protectedPageLoginPath\(/g) ?? [];
      expect(redirects).toHaveLength(2);
      expect(source).not.toMatch(/protectedPageLoginPath\(pathname\)/);
    });
  });
});
