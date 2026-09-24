const apiOrigin = (process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000").replace(/\/$/, "");
const MAX_PROXY_BODY_BYTES = 25 * 1024 * 1024;
const UPSTREAM_TIMEOUT_MS = 30_000;

type RouteContext = { params: Promise<{ path: string[] }> };

const bodyMethods = new Set(["POST", "PUT", "PATCH", "DELETE"]);

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
  headers.delete("connection");

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
  try {
    upstream = await fetch(target, { ...init, signal: controller.signal });
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

  const getSetCookie = (responseHeaders as Headers & { getSetCookie?: () => string[] }).getSetCookie;
  const setCookies = getSetCookie?.call(responseHeaders) ?? [];
  responseHeaders.delete("set-cookie");
  if (setCookies.length > 0) {
    for (const cookie of setCookies) responseHeaders.append("set-cookie", cookie);
  } else {
    const setCookie = upstream.headers.get("set-cookie");
    if (setCookie) responseHeaders.append("set-cookie", setCookie);
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
