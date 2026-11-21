import { describe, expect, it, vi } from "vitest";
import { GET, POST } from "./route";

function context(path: string[]) {
  return { params: Promise.resolve({ path }) };
}

// A stalled upstream: fetch settles only when its signal aborts, the way undici
// behaves for a socket that is accepted but never answered.
function stalledFetch() {
  return vi.fn((_target: string, init: RequestInit) =>
    new Promise<Response>((_resolve, reject) => {
      const signal = init.signal as AbortSignal;
      if (signal.aborted) reject(new DOMException("The operation was aborted.", "AbortError"));
      else signal.addEventListener("abort", () => reject(new DOMException("The operation was aborted.", "AbortError")));
    }),
  );
}

describe("same-origin API proxy response boundary", () => {
  it("does not echo upstream hop-by-hop or Proxy-* fields to the browser", async () => {
    const upstream = new Response("{}", {
      status: 200,
      headers: {
        "content-type": "application/json",
        connection: "keep-alive, Upgrade, X-Internal-Digest",
        "keep-alive": "timeout=5",
        upgrade: "websocket",
        te: "trailers",
        trailer: "X-Internal-Digest",
        "x-internal-digest": "storage-etag-internal",
        "proxy-authenticate": 'Basic realm="internal-api"',
        "proxy-authorization": "Bearer rotated-by-internal-hop",
        "transfer-encoding": "chunked",
        // End-to-end fields the browser legitimately needs must survive.
        "www-authenticate": 'Bearer realm="api"',
        location: "/api/auth/login",
      },
    });
    upstream.headers.append("set-cookie", "taslim.csrf=csrf-cookie; Path=/; Secure; SameSite=None");
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(upstream));

    const response = await GET(new Request("http://localhost:3000/api/auth/me"), context(["auth", "me"]));

    // Single-hop metadata describes the proxy's own upstream connection, and
    // `Connection` extends that set, so none of it may reach the client.
    for (const header of ["connection", "keep-alive", "upgrade", "te", "trailer", "transfer-encoding", "proxy-authenticate", "proxy-authorization", "x-internal-digest"]) {
      expect(response.headers.get(header)).toBeNull();
    }
    expect(response.status).toBe(200);
    expect(response.headers.get("www-authenticate")).toBe('Bearer realm="api"');
    expect(response.headers.get("location")).toBe("/api/auth/login");
    expect(response.headers.get("set-cookie")).toContain("taslim.csrf=csrf-cookie");
    await expect(response.json()).resolves.toEqual({});
    vi.unstubAllGlobals();
  });

  it("does not forward client hop-by-hop or Proxy-Authorization fields to the API", async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response("{}", { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);
    // Undici filters these itself, but the proxy must not depend on that: the
    // Node Request constructor keeps them, so they do reach this code path.
    const request = new Request("http://localhost:3000/api/auth/me", {
      headers: {
        connection: "keep-alive, Upgrade",
        "keep-alive": "timeout=2",
        te: "trailers",
        trailer: "X-Client-Digest",
        upgrade: "websocket",
        "proxy-authorization": "Basic browser-supplied-credentials",
        "transfer-encoding": "chunked",
        "x-csrf-token": "legitimate-csrf",
        cookie: "taslim.auth=synthetic-session",
      },
    });

    await GET(request, context(["auth", "me"]));
    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    const forwarded = new Headers(init.headers);

    for (const header of ["connection", "keep-alive", "te", "trailer", "upgrade", "proxy-authorization", "transfer-encoding"]) {
      expect(forwarded.get(header)).toBeNull();
    }
    expect(forwarded.get("x-csrf-token")).toBe("legitimate-csrf");
    expect(forwarded.get("cookie")).toBe("taslim.auth=synthetic-session");
    vi.unstubAllGlobals();
  });

  it("degrades an upstream reason phrase containing illegal bytes instead of throwing", async () => {
    // Wire-reachable: a raw status line such as `HTTP/1.1 500 BAD\x01REASON`
    // surfaces verbatim in Response.statusText, and those illegal bytes
    // previously made the Response constructor throw inside the route handler.
    const smuggled = "internal stack trace\nSet-Cookie: taslim.auth=session";
    const upstream = {
      status: 500,
      statusText: smuggled,
      headers: new Headers({ "content-type": "application/json" }),
      body: new Response("{}").body,
    } as unknown as Response;
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(upstream));

    const response = await GET(new Request("http://localhost:3000/api/auth/me"), context(["auth", "me"]));
    const text = await response.text();

    expect(response.status).toBe(500);
    expect(response.statusText).toBe("");
    expect(text).toBe("{}");
    expect(text).not.toContain("stack trace");
    expect(response.headers.get("set-cookie")).toBeNull();
    vi.unstubAllGlobals();
  });

  it("proxies a body-less status without crashing the route handler", async () => {
    // `new Response(body, { status: 204 })` throws outright, so a non-compliant
    // upstream that pairs a null-body status with a body must be dropped, not proxied.
    const upstream = {
      status: 204,
      statusText: "No Content",
      headers: new Headers({ "content-type": "application/json" }),
      body: new Response("unexpected").body,
    } as unknown as Response;
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(upstream));

    const response = await GET(new Request("http://localhost:3000/api/jobs/1"), context(["jobs", "1"]));

    expect(response.status).toBe(204);
    expect(response.body).toBeNull();
    vi.unstubAllGlobals();
  });

  it("returns the safe error contract and releases the stream for an unusable upstream head", async () => {
    let cancelled = false;
    const body = new ReadableStream<Uint8Array>({
      cancel() { cancelled = true; },
    });
    const upstream = {
      status: 200,
      statusText: "OK",
      // A header value carrying a control byte is rejected outright by Headers,
      // which previously escaped the handler as an unhandled TypeError.
      headers: [["content-type", "application/json"], ["set-cookie", "session\u0000id=abc"]],
      body,
    } as unknown as Response;
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(upstream));

    const response = await GET(new Request("http://localhost:3000/api/auth/me"), context(["auth", "me"]));
    const text = await response.text();

    expect(response.status).toBe(503);
    expect(text).toBe(JSON.stringify({ error: { code: "API_UNAVAILABLE", message: "The service is temporarily unavailable. Please try again." } }));
    expect(text).not.toContain("session");
    expect(text).not.toContain("set-cookie");
    expect(response.headers.get("set-cookie")).toBeNull();
    expect(cancelled).toBe(true);
    vi.unstubAllGlobals();
  });

  it("never surfaces the upstream origin or failure detail for an unreachable API", async () => {
    const failure = new TypeError("fetch failed to http://127.0.0.1:5000/api/auth/me") as TypeError & { cause?: unknown };
    failure.cause = new Error("connect ECONNREFUSED 10.0.0.7:5000 password=synthetic-secret");
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(failure));

    const response = await GET(new Request("http://localhost:3000/api/auth/me"), context(["auth", "me"]));
    const text = await response.text();

    expect(response.status).toBe(503);
    for (const secret of ["10.0.0.7", "127.0.0.1", "localhost:5000", "ECONNREFUSED", "synthetic-secret", "fetch failed"]) {
      expect(text).not.toContain(secret);
    }
    vi.unstubAllGlobals();
  });

  it("keeps a stalled upstream bounded to one attempt per request", async () => {
    // Large-batch deployment invariant: a stalled API must not fan one client
    // request out into extra upstream attempts, and a side-effecting POST must
    // never be replayed (a duplicate submit would double-charge a generation).
    const stalledGet = new Request("http://localhost:3000/api/jobs/1");
    const stalledPostRequest = new Request("http://localhost:3000/api/generate", {
      method: "POST",
      body: JSON.stringify({ prompt: "synthetic" }),
      headers: { "content-type": "application/json" },
    });
    const getMock = stalledFetch();
    const postMock = stalledFetch();
    vi.useFakeTimers();
    try {
      vi.stubGlobal("fetch", getMock);
      const timedOut = GET(stalledGet, context(["jobs", "1"]));
      await vi.advanceTimersByTimeAsync(30_001);
      const response = await timedOut;
      expect(response.status).toBe(504);
      await expect(response.json()).resolves.toEqual({ error: { code: "API_TIMEOUT", message: "The service took too long to respond. Please try again." } });
      expect(getMock).toHaveBeenCalledTimes(1);

      vi.stubGlobal("fetch", postMock);
      const posted = POST(stalledPostRequest, context(["generate"]));
      await vi.advanceTimersByTimeAsync(30_001);
      expect((await posted).status).toBe(504);
      expect(postMock).toHaveBeenCalledTimes(1);
      expect(postMock.mock.calls[0]?.[1]?.body).toBeInstanceOf(ArrayBuffer);
    } finally {
      vi.useRealTimers();
      vi.unstubAllGlobals();
    }
  });

  it("settles a client cancellation in one attempt instead of hanging", async () => {
    const controller = new AbortController();
    controller.abort();
    const fetchMock = stalledFetch();
    vi.stubGlobal("fetch", fetchMock);

    const response = await GET(new Request("http://localhost:3000/api/jobs/1", { signal: controller.signal }), context(["jobs", "1"]));

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(response.status).toBe(504);
    vi.unstubAllGlobals();
  });
});
