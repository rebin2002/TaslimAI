import { readFileSync } from "node:fs";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import { authSuccessPath } from "@/lib/authRedirect";
import { ProtectedPage, PROTECTED_PAGE_AUTH_TIMEOUT_MS, protectedPageLoginPath } from "./ProtectedPage";

// Hydration harness: ProtectedPage reads the auth state through useAuth(), so
// the gate can be exercised deterministically with every post-login hydration
// state, without a transport, a browser, or a provider.
const authHarness = vi.hoisted(() => ({ state: { user: null as unknown, loading: true } }));
vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => authHarness.state,
}));
vi.mock("next/navigation", () => ({
  useRouter: () => ({ push: vi.fn(), replace: vi.fn() }),
  usePathname: () => "/projects",
}));

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

const protectedPageSource = readFileSync(new URL("./ProtectedPage.tsx", import.meta.url), "utf8");


describe("protected page loading boundary", () => {
  it("uses a finite session bootstrap timeout", () => {
    expect(PROTECTED_PAGE_AUTH_TIMEOUT_MS).toBe(10_000);
    expect(PROTECTED_PAGE_AUTH_TIMEOUT_MS).toBeGreaterThan(0);
  });

  it("announces the session bootstrap boundary and hides its decorative spinner", () => {
    expect(protectedPageSource).toContain('aria-live="polite"');
    expect(protectedPageSource).toContain('aria-hidden="true"');
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

  describe("post-login session hydration persistence", () => {
    const hydratedUser = { id: "user-1", email: "owner@example.test", onboardingCompletedAt: null };

    function renderGate(user: unknown, loading: boolean): string {
      authHarness.state = { user, loading };
      return renderToStaticMarkup(<ProtectedPage><div id="projects-view">projects</div></ProtectedPage>);
    }

    it("renders the authenticated content for a hydrated session even while the bootstrap flag is unresolved", () => {
      // Reproduction of the eviction defect: completing sign-in (or
      // registration) while the session read issued at mount is still in
      // flight makes that transition the current one, so the stale read
      // never clears the provider's `loading` flag. The page is then left
      // with a hydrated session plus an unresolved bootstrap flag, and the
      // first-run onboarding flow inside the protected content must stay
      // mounted instead of collapsing into the bootstrap spinner.
      const html = renderGate(hydratedUser, true);
      expect(html).toContain("projects-view");
      expect(html).not.toContain("loading-state");
    });

    it("still holds the bootstrap spinner until a session hydrates", () => {
      const html = renderGate(null, true);
      expect(html).toContain("loading-state");
      expect(html).not.toContain("projects-view");
    });

    it("arms the bootstrap watchdog only while no session has hydrated", async () => {
      const gate = await import("./ProtectedPage") as typeof import("./ProtectedPage");
      expect(gate.shouldArmBootstrapWatchdog(true, false)).toBe(true);
      // The defect: a stale bootstrap flag used to arm the recovery timer
      // next to a hydrated session, evicting the signed-in user - mid
      // onboarding for a first-login account - to /login after the timeout
      // despite a valid session.
      expect(gate.shouldArmBootstrapWatchdog(true, true)).toBe(false);
      // Absence of a settled session is handled by the direct redirect.
      expect(gate.shouldArmBootstrapWatchdog(false, false)).toBe(false);
      expect(gate.shouldArmBootstrapWatchdog(false, true)).toBe(false);
    });

    it("recognizes only a resolved session object as hydrated", async () => {
      const gate = await import("./ProtectedPage") as typeof import("./ProtectedPage");
      expect(gate.protectedPageHasHydratedSession(null)).toBe(false);
      expect(gate.protectedPageHasHydratedSession(undefined)).toBe(false);
      expect(gate.protectedPageHasHydratedSession(hydratedUser)).toBe(true);
    });

    it("denies a malformed session payload instead of trusting any non-null value", async () => {
      // Security contract: a missing, expired, malformed, or unhydrated
      // session must deny - never allow. A truthy-but-malformed payload (a
      // misrouted proxy body, a placeholder object, a non-string id) is not
      // proof of a resolved session, so it must read as unhydrated and hold
      // the bootstrap gate shut, exactly like a signed-out state.
      const gate = await import("./ProtectedPage") as typeof import("./ProtectedPage");
      for (const malformed of [
        {}, // object without the stable identity field
        { id: "" }, // empty id
        { id: "   " }, // whitespace-only id
        { id: 123 }, // non-string id
        { id: null }, // null id
        "session-token", // primitive masquerading as a session
        42,
        true,
        [],
      ]) {
        expect(gate.protectedPageHasHydratedSession(malformed)).toBe(false);
      }
      expect(gate.protectedPageHasHydratedSession({ id: " user-1 " })).toBe(true);
    });

    it("holds the bootstrap gate for a malformed session, loading or settled", () => {
      // The render gate must consult the hardened predicate, not the raw
      // truthiness of `user`, so malformed payloads never mount protected
      // children.
      for (const loading of [true, false]) {
        const html = renderGate({ email: "owner@example.test" }, loading);
        expect(html).toContain("loading-state");
        expect(html).not.toContain("projects-view");
      }
    });

    it("wires the component render and watchdog through the session-aware gate", () => {
      // The render guard must defer to the hydrated session rather than to
      // the bootstrap flag, and the watchdog must consult the same
      // session-aware predicate.
      expect(protectedPageSource).toContain("if (!hasHydratedSession) return");
      expect(protectedPageSource).toContain("shouldArmBootstrapWatchdog(loading, hasHydratedSession)");
    });
  });
});
