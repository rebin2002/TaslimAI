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
  return PUBLIC_DOC_PATHNAMES.has(normalized.toLowerCase());
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
  matcher: ["/((?!api/|_next/|[^/]+\\.[\\w-]+).*)"],
};
