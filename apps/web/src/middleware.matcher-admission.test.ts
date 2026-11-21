import { describe, expect, it } from "vitest";
import { NextRequest } from "next/server";
import middleware, {
  PRIVATE_DOC_CACHE_CONTROL,
  PRIVATE_DOC_ROBOTS,
  config,
  isPublicDocPath,
} from "./middleware";

// Provider-free, network-free, credential-free: a synthetic loopback origin and
// reserved route names only. Nothing here opens a socket, reads an environment
// credential or reaches a model provider.
//
// SCOPE - the one property the two existing #1038 suites never assert together.
//
//   * `middleware.test.ts` (lane file) asserts the *policy* for escape shapes
//     by calling `middleware(requestFor(path))` directly. That runs the function
//     no matter whether the middleware would have been reached at all, so it
//     cannot see a gating miss.
//   * `middleware.anonymous-warmup.test.ts` (sibling lane #1089, draft) asserts
//     `matcherAdmits(new URL(...).pathname)` - always the ALREADY-NORMALIZED
//     pathname - so the raw-versus-resolved distinction cannot surface there
//     either. Its traversal case, `/login/../projects`, normalizes to
//     `/projects`, which admits on both readings.
//
// `config.matcher` gates on the incoming request pathname, while
// `middleware()` reads `new URL(request.url).pathname`. Those are not guaranteed
// to be the same string: dot-segment removal, `%2E` decoding and case handling
// happen at different layers depending on what sits in front of the origin. When
// the incoming shape is skipped by every matcher entry but the path the router
// actually answers is a guarded private document, the fence is never invoked and
// the prerendered `s-maxage=31536000` is what a shared cache stores.
//
// That is the fail-closed claim in the middleware header comment - "Unknown and
// new routes fail closed" - evaluated one layer earlier than anywhere it is
// currently tested. This file is a reproduction, not a fix: it owns no lane
// file and asserts nothing about anonymous-vs-session parity (#1089) or about
// the policy directive strings themselves (#1038).

function admits(pathname: string): boolean {
  // Mirrors how Next evaluates `config.matcher`: the middleware runs when ANY
  // entry matches the whole pathname.
  return config.matcher.some((pattern) => new RegExp(`^${pattern}$`).test(pathname));
}

function resolved(pathWithQuery: string): string {
  return new URL(`http://localhost:3000${pathWithQuery}`).pathname;
}

function policyFor(pathWithQuery: string): { cacheControl: string | null; robots: string | null } {
  const headers = middleware(new NextRequest(`http://localhost:3000${pathWithQuery}`)).headers;
  return { cacheControl: headers.get("Cache-Control"), robots: headers.get("X-Robots-Tag") };
}

// Shapes that must be gated under the lane's own fail-closed rule: a pathname is
// only allowed to skip the fence when it is provably an allowlisted public
// credential form, an API proxy call, a framework asset, or a real public static
// file. Anything else resolves to a document the fence owns.
const MUST_BE_GATED = [
  // (A) Dot-segment traversal behind a skipped dotted first segment. Entry 1's
  // static-file carve-out keys on the shape of the FIRST segment only
  // (`[^/]+\.[\w-]+`), so the incoming pathname is skipped even though the
  // answered target is a guarded private route.
  "/favicon.ico/../chat",
  "/robots.txt/../projects",
  "/manifest.webmanifest/../account/billing",
  "/login.rsc/../projects",
  "/favicon.ico/%2E%2E/chat",
  // (B) Payload suffix that is not exactly lowercase `.rsc`. Entry 2 is
  // case-sensitive, while entry 1's `[\w-]+` happily swallows `RSC`, so these
  // match neither entry. Next resolves the RSC suffix case-sensitively, so the
  // URL answers a private-namespace document rather than an asset - the mirror
  // image of the case-fold exemption the lane closed at the policy layer in
  // 042594ee: folding case there would have widened the public allowlist, while
  // refusing to fold case here narrows the guard.
  "/chat.RSC",
  "/projects.RSC",
  "/activity.Rsc",
];

// Correctly outside the fence today. Pinned so a matcher fix cannot widen the
// guard onto the public or framework surface while closing (A) and (B).
//
// The credential forms are deliberately absent: the matcher DOES admit them and
// they are exempted one layer later by `isPublicDocPath` returning early, so
// they are pinned as a policy-layer control instead.
const MUST_STAY_SKIPPED = [
  "/api/auth/me",
  "/api/projects/1/files",
  "/_next/static/chunks/main.js",
  "/_next/image?url=x",
  "/robots.txt",
  "/favicon.ico",
  "/manifest.webmanifest",
];

describe("#1038 matcher admission - the guard must fire on the shape it is handed", () => {
  it("admits the private shapes the reviewed matcher already covers", () => {
    // Positive control: without it the contract test could pass vacuously.
    for (const path of ["/", "/chat", "/projects", "/account/billing", "/chat.rsc", "/create/image.rsc"]) {
      expect(admits(path), path).toBe(true);
    }
  });

  it("keeps the public and framework surface outside the fence", () => {
    for (const path of MUST_STAY_SKIPPED) {
      expect(admits(path), path).toBe(false);
    }
  });

  it("lets the credential forms reach the fence and leave it header-free", () => {
    // Exempted by policy, not by gating: a matcher fix must not silently start
    // stamping a private lifetime onto the public sign-up and sign-in pages.
    for (const path of ["/login", "/register", "/login/"]) {
      expect(admits(path), path).toBe(true);
      expect(isPublicDocPath(path), path).toBe(true);
      expect(policyFor(path).cacheControl, path).toBeNull();
      expect(policyFor(path).robots, path).toBeNull();
    }
  });

  it("pins that policy-layer coverage is not admission-layer coverage", () => {
    // The reviewed suite proves these shapes would be fenced IF reached.
    // Recording that here isolates the finding: the miss is in gating, not in
    // `middleware()`.
    for (const path of ["/chat.RSC", "/favicon.ico/../chat", "/robots.txt/../projects"]) {
      expect(isPublicDocPath(resolved(path)), path).toBe(false);
      expect(policyFor(path).cacheControl, path).toBe(PRIVATE_DOC_CACHE_CONTROL);
    }
  });

  it("gates every shape whose resolved target is a private document", () => {
    // THE CONTRACT ASSERTION. Deliberately expected RED on head 2a4438d3: each
    // reported shape is skipped by both matcher entries while its target is a
    // guarded, non-public document, i.e. the shared-cacheable private-HTML
    // surface the lane exists to fence. A fix in either layer closes it -
    // widening entry 2 to `(?i)\\.rsc`, anchoring entry 1's carve-out to the
    // resolved pathname, or dropping the dotted-segment exemption in favour of
    // an explicit asset allowlist.
    const ungated = MUST_BE_GATED.filter((path) => !admits(path));
    expect(ungated, `shapes that skip the fence: ${ungated.join(", ")}`).toEqual([]);
  });

  it("never lets the gating string change the outcome for a private target", () => {
    // The invariant the fence rests on: whichever string the matcher is given,
    // a private target must be gated. Reported per shape so a red run names the
    // exact escape rather than a count.
    for (const path of MUST_BE_GATED) {
      expect(admits(path) || isPublicDocPath(resolved(path)), `${path} -> ${resolved(path)}`).toBe(true);
    }
  });

  it("applies the same private directive pair to every gated document", () => {
    // Belt for the reader: for the targets behind (A) and (B) the policy that
    // would apply once gated is the reviewed private pair, which carries no
    // shared-cache lifetime and no indexability.
    for (const path of ["/chat", "/projects", "/account/billing"]) {
      const { cacheControl, robots } = policyFor(path);
      expect(cacheControl, path).toBe(PRIVATE_DOC_CACHE_CONTROL);
      expect(robots, path).toBe(PRIVATE_DOC_ROBOTS);
      expect(PRIVATE_DOC_CACHE_CONTROL).not.toMatch(/s-maxage|public/);
    }
  });
});
