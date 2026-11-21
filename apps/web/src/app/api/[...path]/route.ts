const configuredApiUrl = process.env.NEXT_PUBLIC_API_URL;
if (process.env.NODE_ENV === "production" && (!configuredApiUrl || !configuredApiUrl.startsWith("https://")))
  throw new Error("NEXT_PUBLIC_API_URL must be an HTTPS URL in production.");
const apiOrigin = (configuredApiUrl ?? "http://localhost:5000").replace(/\/$/, "");
const MAX_PROXY_BODY_BYTES = 25 * 1024 * 1024;
const UPSTREAM_TIMEOUT_MS = 30_000;
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

// RFC 9110 §7.6.1 reserves these fields for a single transport hop, and Proxy-*
// carries that hop's own auth challenge and credentials. The hop the browser
// speaks to is this proxy rather than the API behind it, so an upstream value
// for any of them describes our connection and must never be echoed to a client.
const HOP_BY_HOP_HEADERS: readonly string[] = [
  "connection",
  "keep-alive",
  "proxy-authenticate",
  "proxy-authorization",
  "te",
  "trailer",
  "transfer-encoding",
  "upgrade",
];

function stripHopByHopHeaders(headers: Headers): void {
  const removable = new Set(HOP_BY_HOP_HEADERS);
  // A sender may extend the set by naming fields in its own Connection header,
  // e.g. `Connection: X-Internal-Digest`; honour that or the field leaks through.
  for (const token of (headers.get("connection") ?? "").split(",")) {
    const name = token.trim().toLowerCase();
    if (name) removable.add(name);
  }
  // Snapshot names: deleting during a live Headers iterator can skip the next key.
  for (const name of [...headers.keys()]) {
    if (removable.has(name)) headers.delete(name);
  }
}

type RouteContext = { params: Promise<{ path: string[] }> };

const bodyMethods = new Set(["POST", "PUT", "PATCH", "DELETE"]);

// A Response may only carry an HTTP reason phrase (HTAB / SP / VCHAR / obs-text).
// The upstream builds that byte sequence, so it is hostile-reachable: a status
// line with a raw CR, LF or NUL in the reason phrase makes the Response
// constructor throw inside the route handler instead of degrading the response.
function safeStatusText(value: unknown): string {
  if (typeof value !== "string") return "";
  for (const character of value) {
    const code = character.codePointAt(0) ?? 0;
    if (code !== 0x09 && !(code >= 0x20 && code <= 0x7e) && !(code >= 0x80 && code <= 0xff)) return "";
  }
  return value;
}

// RFC 9110 §6.4.1: these statuses are answered without a body, so a body-bearing
// variant from a non-compliant upstream must be dropped, not proxied verbatim
// (the Response constructor rejects that pairing outright).
const NULL_BODY_STATUSES = new Set([101, 103, 204, 205, 304]);

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
  const target = `${apiOrigin}/api/${path.map((segment) => encodeURIComponent(segment)).join("/")}${new URL(request.url).search}`;
  const headers = new Headers(request.headers);
  headers.delete("host");
  headers.delete("content-length");
  headers.delete("accept-encoding");
  // The API trusts one hosting-proxy hop for the original HTTPS scheme so it
  // can issue Secure cookies. Never let a browser manufacture that metadata
  // through this same-origin proxy.
  stripUntrustedForwardingHeaders(headers);
  // `connection` was already dropped; clear the rest of the reserved namespace
  // too so no client-side hop metadata (or Proxy-Authorization credential)
  // reaches the API regardless of how the fetch implementation filters it.
  stripHopByHopHeaders(headers);

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

  // From here the upstream owns the response head, so assembling the client
  // response must not be able to throw: a rejected Response escapes as a
  // framework 500 whose error surface is outside this contract, and leaves the
  // upstream stream unread and its connection pinned.
  try {
    const responseHeaders = new Headers(upstream.headers);
    responseHeaders.delete("content-length");
    responseHeaders.delete("content-encoding");
    stripHopByHopHeaders(responseHeaders);

    const setCookies = getSetCookieHeaders(responseHeaders);
    responseHeaders.delete("set-cookie");
    if (setCookies.length > 0) {
      for (const cookie of setCookies) responseHeaders.append("set-cookie", cookie);
    }

    return new Response(NULL_BODY_STATUSES.has(upstream.status) ? null : upstream.body, {
      status: upstream.status,
      statusText: safeStatusText(upstream.statusText),
      headers: responseHeaders,
    });
  } catch {
    try {
      await upstream.body?.cancel();
    } catch {
      // The stream is already errored; nothing left to release.
    }
    return Response.json(
      { error: { code: "API_UNAVAILABLE", message: "The service is temporarily unavailable. Please try again." } },
      { status: 503 },
    );
  }
}

export const GET = forward;
export const POST = forward;
export const PUT = forward;
export const PATCH = forward;
export const DELETE = forward;
export const OPTIONS = forward;
