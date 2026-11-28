import { describe, expect, it } from "vitest";
import { NextRequest } from "next/server";
import middleware, {
  PRIVATE_DOC_CACHE_CONTROL,
  PRIVATE_DOC_ROBOTS,
  config,
} from "./middleware";
// Edge not pinned by apps/web/src/middleware.test.ts or
// e2e/private-route-cache-isolation.spec.ts: those suites are purely
// path-keyed. Neither one distinguishes an ANONYMOUS request from an
// AUTHENTICATED request on the same URL, and neither pins that the fence is
// a pass-through whose headers survive on response variants the app itself
// produces (the 3xx redirect of an unauthenticated hit on an auth-gated
// route, the 404 not-found document, the RSC payload variant). That is the
// cache-warming sequence this lane exists to defend: a crawler or attacker
// GETs `/chat` with no cookies first, and a shared cache must then hold
// nothing for the real session's response to be replayed from - or into.
//
// Provider-free by construction: only a loopback NextRequest is built, no
// socket is opened, and the session material is the synthetic constant below
// (never a real credential, matching the `taslim.auth` cookie name used by
// src/lib/serverAuth.ts).
const SYNTHETIC_SESSION_COOKIE = "taslim.auth=synthetic-session-fixture-00000000";
interface RequestOptions {
  session?: boolean;
}
function requestFor(pathWithQuery: string, options: RequestOptions = {}): NextRequest {
  const headers = new Headers();
  if (options.session) headers.set("cookie", SYNTHETIC_SESSION_COOKIE);
  return new NextRequest(`http://localhost:3000${pathWithQuery}`, { headers });
}
// Mirrors how Next evaluates `config.matcher`: a request runs the middleware
// when ANY entry matches the whole pathname.
function matcherAdmits(pathname: string): boolean {
  return config.matcher.some((pattern) => new RegExp(`^${pattern}$`).test(pathname));
}
function policyFor(pathWithQuery: string, options: RequestOptions = {}) {
  const headers = middleware(requestFor(pathWithQuery, options)).headers;
  return {
    cacheControl: headers.get("Cache-Control"),
    robots: headers.get("X-Robots-Tag"),
    setCookie: headers.get("set-cookie"),
    location: headers.get("location"),
  };
}
function expectSharedCacheRefusal(cacheControl: string | null, label: string): void {
  expect(cacheControl, label).toBe(PRIVATE_DOC_CACHE_CONTROL);
  expect(cacheControl ?? "", label).toMatch(/no-store/);
  expect(cacheControl ?? "", label).not.toMatch(/s-maxage|public|max-age=(?!0[,\s]|0$)/);
}
describe("anonymous cache-warm-up vs authenticated response (SSR cache isolation)", () => {
  it("gives an anonymous warm-up request and an authenticated request the identical private policy on auth-gated documents", () => {
    // The fence must not be conditioned on session material: if only the
    // authenticated variant were fenced, the preceding anonymous response
    // would remain share-cacheable under the same URL key, and if only the
    // anonymous variant were fenced, the private response would inherit a
    // stored shared entry. Both directions are refused here.
    for (const path of ["/", "/chat", "/projects", "/account/billing", "/personal/health"]) {
      const warmUp = policyFor(path);
      const authenticated = policyFor(path, { session: true });
      expect(authenticated, path).toEqual(warmUp);
      expectSharedCacheRefusal(warmUp.cacheControl, `${path} anonymous`);
      expectSharedCacheRefusal(authenticated.cacheControl, `${path} authenticated`);
      expect(warmUp.robots, `${path} anonymous`).toBe(PRIVATE_DOC_ROBOTS);
      expect(authenticated.robots, `${path} authenticated`).toBe(PRIVATE_DOC_ROBOTS);
      // A warm-up response that itself plants a cookie would seed the cache
      // entry with session material; the fence must never mint one.
      expect(warmUp.setCookie, path).toBeNull();
      expect(authenticated.setCookie, path).toBeNull();
    }
  });
  it("stays a pass-through so redirect and error variants the app produces inherit the fenced headers", () => {
    // An unauthenticated hit on an auth-gated route is answered by the app
    // with a redirect to /login (src/lib/serverAuth.ts), and unknown or
    // case-folded surfaces are answered with a 404 not-found document. The
    // middleware must not terminate the response itself: a middleware-owned
    // redirect/rewritten response would carry its own header set and shadow
    // the upstream status variant. Pass-through shape guarantees the 3xx/4xx
    // response the app returns is emitted under this same header policy.
    for (const path of ["/chat", "/account/billing", "/personal/health"]) {
      for (const variant of [undefined, { session: true }]) {
        const response = middleware(requestFor(path, variant ?? {}));
        expect(response.status, path).toBeLessThan(300);
        expect(response.headers.get("location"), path).toBeNull();
        expectSharedCacheRefusal(response.headers.get("Cache-Control"), path);
      }
    }
  });
  it("fences the RSC payload variants identically under warm-up and authenticated requests, which share the document cache key", () => {
    for (const path of ["/chat.rsc", "/projects.rsc", "/projects?_rsc=0123"]) {
      const warmUp = policyFor(path);
      const authenticated = policyFor(path, { session: true });
      expect(matcherAdmits(new URL(`http://localhost:3000${path}`).pathname), path).toBe(true);
      expect(authenticated, path).toEqual(warmUp);
      expectSharedCacheRefusal(warmUp.cacheControl, `${path} anonymous`);
      expectSharedCacheRefusal(authenticated.cacheControl, `${path} authenticated`);
    }
  });
  it("fences error-document variants identically for the warm-up and the authenticated hit", () => {
    // Case-folded public paths render the private not-found document (404)
    // and traversal shapes miss every public allowlist entry; these error
    // responses are URL-keyed too and a warm-up request must not leave them
    // share-cacheable for the session that follows.
    for (const path of ["/Login", "/LOGIN", "/some-future-private-surface", "/login/../projects"]) {
      const warmUp = policyFor(path);
      const authenticated = policyFor(path, { session: true });
      expect(matcherAdmits(new URL(`http://localhost:3000${path}`).pathname), path).toBe(true);
      expect(authenticated, path).toEqual(warmUp);
      expectSharedCacheRefusal(warmUp.cacheControl, `${path} anonymous`);
      expectSharedCacheRefusal(authenticated.cacheControl, `${path} authenticated`);
      expect(warmUp.setCookie, path).toBeNull();
      expect(authenticated.setCookie, path).toBeNull();
    }
  });
  it("cannot be re-ordered: a preceding anonymous pass-through leaves no policy residue on the authenticated follow-up", () => {
    // Deterministic sequence probe for one URL: warm-up, then two
    // authenticated follow-ups. Any per-request state (session-dependent
    // branching, header accumulation on a reused response) would surface as
    // divergence between these three evaluations.
    const warmUp = policyFor("/chat");
    const first = policyFor("/chat", { session: true });
    const second = policyFor("/chat", { session: true });
    expect(first).toEqual(warmUp);
    expect(second).toEqual(first);
    expectSharedCacheRefusal(second.cacheControl, "/chat follow-up");
  });
  it("keeps the login redirect target itself outside the fence while the redirected origin stays fenced", () => {
    // An anonymous warm-up on /chat is answered by the app with a redirect to
    // the public credential form; the fence must cover the private origin of
    // that chain and must not silently widen the exemption onto the deep-link
    // shape of the form. The allowlisted form itself stays framework-default
    // (out of scope here, asserted by the lane's own suites).
    expectSharedCacheRefusal(policyFor("/chat?next=%2Fprojects").cacheControl, "/chat?next=%2Fprojects");
    expect(policyFor("/login?next=%2Fchat").cacheControl).toBeNull();
  });
});
