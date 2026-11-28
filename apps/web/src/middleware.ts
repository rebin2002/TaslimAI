import { NextResponse, type NextRequest } from "next/server";

// A production `next start` serves every statically prerendered route with
// `Cache-Control: s-maxage=31536000` plus an ETag and no session awareness.
// For the authenticated workspace surface those one-year, URL-keyed documents
// are exactly what RFC 9111 tells shared caches (CDNs, hosting edges,
// corporate forward proxies) to store and replay across sessions: user B can
// be served user A's cached workspace document, and a pre-gating shell can
// outlive the server-side auth gate that later lands on the same URL.
//
// This middleware is the SSR cache-isolation layer for that defect class:
// every document or RSC payload route that is not an explicit public form
// page is forced to the same private-response policy the API already applies
// to its authentication endpoints. Unknown and new routes fail closed.
const PUBLIC_DOC_PATHNAMES = new Set(["/login", "/register"]);

export const PRIVATE_DOC_CACHE_CONTROL = "private, no-cache, no-store, must-revalidate, max-age=0";
export const PRIVATE_DOC_ROBOTS = "noindex, nofollow, noarchive, nosnippet";

export function isPublicDocPath(pathname: string): boolean {
  const normalized = pathname.length > 1 ? pathname.replace(/\/+$/, "") : pathname;
  // Exact, case-sensitive membership after trailing-slash normalization. App
  // Router route registration is case-sensitive, so only the real public paths
  // may be exempted: folding case would widen the exemption onto surfaces that
  // render the private not-found document (`/Login`, `/LOGIN`). Normalization
  // is therefore allowed to err only in the fail-closed direction.
  return PUBLIC_DOC_PATHNAMES.has(normalized);
}

export default function middleware(request: NextRequest): NextResponse {
  const response = NextResponse.next();
  const { pathname } = new URL(request.url);
  if (isPublicDocPath(pathname)) return response;
  // Overrides the prerendered `s-maxage=31536000` in production and keeps
  // public crawlers and shared caches out of authenticated route responses.
  response.headers.set("Cache-Control", PRIVATE_DOC_CACHE_CONTROL);
  response.headers.set("X-Robots-Tag", PRIVATE_DOC_ROBOTS);
  return response;
}

export const config = {
  // API proxy responses carry upstream-owned headers, and Next-owned
  // `/_next/*` assets and extension-bearing public files (robots.txt, icons,
  // fonts) keep their framework cache policies. Everything else runs here.
  //
  // That static-file carve-out is deliberately paired with a second entry.
  // The App Router also answers `<route>.rsc` with that route's serialized
  // payload (`Content-Type: text/x-component`), the same cache-key surface as
  // the `?_rsc=` document variant. A top-level segment such as `/search.rsc`
  // or `/chat.rsc` otherwise satisfied `[^/]+\.[\w-]+` and kept only the
  // framework default, while the deeper `/create/image.rsc` was guarded: an
  // asymmetry, not a design decision. The second entry closes it. The first
  // entry exempts only terminal dotted files (not `/favicon.ico/../chat`), and
  // the second admits mixed-case RSC-like document suffixes. API and framework
  // assets remain excluded from both entries.
  matcher: ["/((?!api/|_next/|[^/]+\\.[\\w-]+$).*)", "/((?!api/|_next/).+\\.[rR][sS][cC])"],
};
