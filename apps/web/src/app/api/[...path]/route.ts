const configuredApiUrl = process.env.NEXT_PUBLIC_API_URL;
if (process.env.NODE_ENV === "production" && (!configuredApiUrl || !configuredApiUrl.startsWith("https://")))
  throw new Error("NEXT_PUBLIC_API_URL must be an HTTPS URL in production.");
const apiOrigin = (configuredApiUrl ?? "http://localhost:5000").replace(/\/$/, "");
// The forwarding namespace the proxy is allowed to reach. Derived from the
// configured origin so a deployment whose API base URL carries a path prefix
// keeps forwarding exactly as before.
const apiNamespacePathname = new URL(`${apiOrigin}/api/`).pathname;
const MAX_PROXY_BODY_BYTES = 25 * 1024 * 1024;
const UPSTREAM_TIMEOUT_MS = 30_000;
// Bounded percent-decode rounds used to recognize a dot-relative segment in
// any repeated-encoding spelling (%2e%2e, %252e%252e, %25252e%25252e, ...).
const MAX_RELATIVE_DECODE_ROUNDS = 4;
// Remove the entire client-controlled forwarding namespace, not just today's
// commonly recognized names (e.g. X-Forwarded-Client-Cert and future variants).
// The upstream HTTPS/cookie trust boundary must never depend on client metadata.
function stripUntrustedForwardingHeaders(headers: Headers): void {
  // Snapshot names: deleting during a live Headers iterator can skip the next key.
  for (const name of [...headers.keys()]) {
    if (name === "forwarded" || name === "via" || name === "x-real-ip" || name.startsWith("x-forwarded-")) {
      headers.delete(name);
    }
  }
}

type RouteContext = { params: Promise<{ path: string[] }> };

const bodyMethods = new Set(["POST", "PUT", "PATCH", "DELETE"]);

// Containment helper for the same-origin /api/[...path] proxy (draft PR #1105
// red-by-design handler contract). encodeURIComponent does not encode "." or
// "..", and the router decodes spellings such as %2e%2e before invoking the
// handler, so a relative segment reaching here composes a target whose request
// line the WHATWG URL parser collapses out of the /api/ namespace
// (e.g. /api/v1/../../admin -> /admin) while the caller's session cookie keeps
// traveling with it. Relative segments are refused outright, before the target
// is composed, before any credential header is copied, and before the request
// body is read; a percent-decode failure is treated as an ordinary
// (non-relative) segment because the encoded form cannot collapse.
function isRelativePathSegment(segment: string): boolean {
  let candidate = segment;
  // Bounded fixed-point decode: every additional percent-encoding round is
  // another spelling of the same relative segment (%2e%2e, %252e%252e,
  // %25252e%25252e, ...), and all of them must be refused rather than
  // forwarded as encoded noise. A segment whose decoding reaches a fixed point
  // without becoming exactly "." or ".." (an ordinary name such as "...env" or
  // a dot-prefixed name with encoded separators) exits immediately.
  for (let round = 0; round < MAX_RELATIVE_DECODE_ROUNDS; round++) {
    if (candidate === "." || candidate === "..") return true;
    let decoded: string;
    try {
      decoded = decodeURIComponent(candidate);
    } catch {
      return false;
    }
    if (decoded === candidate) return false;
    candidate = decoded;
  }
  return candidate === "." || candidate === "..";
}

function splitCombinedSetCookieHeader(value: string): string[] {
  return value.split(/,(?=\s*[^;,=\s]+=[^;,]*)/).map(cookie => cookie.trim()).filter(Boolean);
}

function getSetCookieHeaders(headers: Headers): string[] {
  const headersWithSetCookie = headers as Headers & { getSetCookie?: () => string[] };
  const setCookies = headersWithSetCookie.getSetCookie?.call(headersWithSetCookie);
  if (setCookies && setCookies.length > 0) return setCookies;
  const combined = headers.get("set-cookie");
  return combined ? splitCombinedSetCookieHeader(combined) : [];
}

async function readBoundedBody(request: Request): Promise<ArrayBuffer | Response> {
  const declaredLength = request.headers.get("content-length");
  if (declaredLength && Number.isFinite(Number(declaredLength)) && Number(declaredLength) > MAX_PROXY_BODY_BYTES)
    return Response.json({ error: { code: "REQUEST_TOO_LARGE", message: "The request body is too large." } }, { status: 413 });
  if (!request.body) return new ArrayBuffer(0);

  const reader = request.body.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      total += value.byteLength;
      if (total > MAX_PROXY_BODY_BYTES)
        return Response.json({ error: { code: "REQUEST_TOO_LARGE", message: "The request body is too large." } }, { status: 413 });
      chunks.push(value);
    }
  } finally {
    reader.releaseLock();
  }

  const body = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) {
    body.set(chunk, offset);
    offset += chunk.byteLength;
  }
  return body.buffer;
}

async function forward(request: Request, context: RouteContext): Promise<Response> {
  const { path } = await context.params;
  // Fail closed on a dot-relative segment instead of dropping it: removing the
  // segment silently rewrote a request that was meant to be rejected into a
  // *different* valid endpoint (["v1","..","..","admin"] -> /api/v1/admin) and
  // still forwarded the caller's session cookie and CSRF material to it. This
  // refusal happens before the target is composed, before any header is copied
  // and before the request body is read, and its envelope echoes neither the
  // path, the offending segment, nor the upstream origin.
  if (path.some(isRelativePathSegment))
    return Response.json(
      { error: { code: "INVALID_PATH", message: "The requested path is not valid." } },
      { status: 400 },
    );
  const target = `${apiOrigin}/api/${path.map((segment) => encodeURIComponent(segment)).join("/")}${new URL(request.url).search}`;
  // Second, independent fail-closed assertion: if the composed target would
  // ever normalize outside the forwarding namespace for any reason, refuse it
  // before fetch() so neither the request line nor any credential header
  // (session cookie included) can leave /api/. Valid paths, auth forwarding,
  // and the 503/504/413 error envelopes are unchanged.
  let targetPathname: string;
  try {
    targetPathname = new URL(target).pathname;
  } catch {
    return Response.json({ error: { code: "INVALID_PATH", message: "The requested path is not valid." } }, { status: 400 });
  }
  if (!targetPathname.startsWith(apiNamespacePathname))
    return Response.json({ error: { code: "INVALID_PATH", message: "The requested path is not valid." } }, { status: 400 });
  const headers = new Headers(request.headers);
  headers.delete("host");
  headers.delete("content-length");
  headers.delete("accept-encoding");
  headers.delete("connection");
  // The API trusts one hosting-proxy hop for the original HTTPS scheme so it
  // can issue Secure cookies. Never let a browser manufacture that metadata
  // through this same-origin proxy.
  stripUntrustedForwardingHeaders(headers);

  const init: RequestInit & { duplex?: "half" } = {
    method: request.method,
    headers,
    redirect: "manual",
    cache: "no-store",
  };
  if (bodyMethods.has(request.method)) {
    const body = await readBoundedBody(request);
    if (body instanceof Response) return body;
    init.body = body;
  }

  let upstream: Response;
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), UPSTREAM_TIMEOUT_MS);
  const upstreamSignal = AbortSignal.any([request.signal, controller.signal]);
  try {
    upstream = await fetch(target, { ...init, signal: upstreamSignal });
  } catch (error) {
    if (error instanceof DOMException && error.name === "AbortError")
      return Response.json({ error: { code: "API_TIMEOUT", message: "The service took too long to respond. Please try again." } }, { status: 504 });
    return Response.json(
      { error: { code: "API_UNAVAILABLE", message: "The service is temporarily unavailable. Please try again." } },
      { status: 503 },
    );
  } finally {
    clearTimeout(timeout);
  }

  const responseHeaders = new Headers(upstream.headers);
  responseHeaders.delete("content-length");
  responseHeaders.delete("content-encoding");
  responseHeaders.delete("transfer-encoding");

  const setCookies = getSetCookieHeaders(responseHeaders);
  responseHeaders.delete("set-cookie");
  if (setCookies.length > 0) {
    for (const cookie of setCookies) responseHeaders.append("set-cookie", cookie);
  }

  return new Response(upstream.body, {
    status: upstream.status,
    statusText: upstream.statusText,
    headers: responseHeaders,
  });
}

export const GET = forward;
export const POST = forward;
export const PUT = forward;
export const PATCH = forward;
export const DELETE = forward;
export const OPTIONS = forward;
