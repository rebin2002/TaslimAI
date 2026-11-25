import { describe, expect, it, vi } from "vitest";
import { GET, PATCH, POST } from "./route";

function context(path: string[]) {
  return { params: Promise.resolve({ path }) };
}

describe("same-origin API proxy", () => {
  it("forwards GET cookies and preserves API Set-Cookie responses", async () => {
    const upstream = new Response(JSON.stringify({ token: "safe-test-token" }), {
      status: 200,
      headers: {
        "content-type": "application/json",
        "set-cookie": "taslim.csrf=csrf-cookie; Path=/; Secure; SameSite=None",
      },
    });
    const fetchMock = vi.fn().mockResolvedValue(upstream);
    vi.stubGlobal("fetch", fetchMock);

    const request = new Request("http://localhost:3000/api/auth/csrf", {
      headers: { cookie: "taslim.csrf=old-cookie", host: "localhost:3000" },
    });
    const response = await GET(request, context(["auth", "csrf"]));
    const [target, init] = fetchMock.mock.calls[0] as [string, RequestInit];

    expect(target).toBe("http://localhost:5000/api/auth/csrf");
    expect(new Headers(init.headers).get("cookie")).toBe("taslim.csrf=old-cookie");
    expect(new Headers(init.headers).get("host")).toBeNull();
    expect(response.status).toBe(200);
    expect(response.headers.get("set-cookie")).toContain("taslim.csrf=csrf-cookie");
    await expect(response.json()).resolves.toEqual({ token: "safe-test-token" });
    vi.unstubAllGlobals();
  });

  it("does not forward client-controlled proxy headers to the API", async () => {
    const upstream = new Response("{}", { status: 200 });
    const fetchMock = vi.fn().mockResolvedValue(upstream);
    vi.stubGlobal("fetch", fetchMock);
    const request = new Request("http://localhost:3000/api/auth/me", {
      headers: {
        forwarded: "for=198.51.100.7;proto=http",
        via: "client-proxy",
        "x-forwarded-for": "198.51.100.7",
        "x-forwarded-host": "attacker.example",
        "x-forwarded-port": "80",
        "x-forwarded-prefix": "/spoofed",
        "x-forwarded-client-cert": "spoofed-client-certificate",
        "x-forwarded-server": "spoofed-upstream",
        "x-forwarded-custom-future-header": "spoofed-extension",
        "x-csrf-token": "legitimate-csrf",
        cookie: "taslim.auth=synthetic-session",
        "x-forwarded-proto": "http",
        "x-real-ip": "198.51.100.7",
      },
    });
    await GET(request, context(["auth", "me"]));
    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    const forwardedHeaders = new Headers(init.headers);
    for (const header of [
      "forwarded",
      "via",
      "x-forwarded-for",
      "x-forwarded-host",
      "x-forwarded-port",
      "x-forwarded-prefix",
      "x-forwarded-client-cert",
      "x-forwarded-server",
      "x-forwarded-custom-future-header",
      "x-forwarded-proto",
      "x-real-ip",
    ]) {
      expect(forwardedHeaders.get(header)).toBeNull();
    }
    expect(forwardedHeaders.get("x-csrf-token")).toBe("legitimate-csrf");
    expect(forwardedHeaders.get("cookie")).toBe("taslim.auth=synthetic-session");
    vi.unstubAllGlobals();
  });

  it("forwards each Set-Cookie value separately when authentication rotates multiple cookies", async () => {
    const upstream = new Response(JSON.stringify({ success: true }), { status: 200 });
    upstream.headers.append("set-cookie", "taslim.auth=session-cookie; Path=/; HttpOnly; Secure; SameSite=None");
    upstream.headers.append("set-cookie", "taslim.csrf=; Path=/; Max-Age=0; Secure; SameSite=None");
    const fetchMock = vi.fn().mockResolvedValue(upstream);
    vi.stubGlobal("fetch", fetchMock);
    const request = new Request("http://localhost:3000/api/auth/login", { method: "POST", body: "{}" });
    const response = await POST(request, context(["auth", "login"]));
    const responseHeaders = response.headers as Headers & { getSetCookie?: () => string[] };
    const cookies = responseHeaders.getSetCookie?.call(responseHeaders) ?? [];
    expect(cookies).toEqual([
      "taslim.auth=session-cookie; Path=/; HttpOnly; Secure; SameSite=None",
      "taslim.csrf=; Path=/; Max-Age=0; Secure; SameSite=None",
    ]);
    vi.unstubAllGlobals();
  });

  it("forwards login body, CSRF header, credentials cookie, and query string", async () => {
    const upstream = new Response(JSON.stringify({ ok: true }), { status: 200 });
    const fetchMock = vi.fn().mockResolvedValue(upstream);
    vi.stubGlobal("fetch", fetchMock);
    const body = JSON.stringify({ email: "owner@example.com", password: "secret" });
    const request = new Request("http://localhost:3000/api/auth/login?mode=normal", {
      method: "POST",
      headers: {
        cookie: "taslim.csrf=csrf-cookie",
        "content-type": "application/json",
        "x-csrf-token": "request-token",
        host: "localhost:3000",
      },
      body,
    });

    const response = await POST(request, context(["auth", "login"]));
    const [target, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    const forwardedBody = await new Response(init.body).text();

    expect(target).toBe("http://localhost:5000/api/auth/login?mode=normal");
    expect(init.method).toBe("POST");
    expect(new Headers(init.headers).get("cookie")).toBe("taslim.csrf=csrf-cookie");
    expect(new Headers(init.headers).get("x-csrf-token")).toBe("request-token");
    expect(forwardedBody).toBe(body);
    expect(response.status).toBe(200);
    vi.unstubAllGlobals();
  });

  it("forwards the incoming abort signal for safe streaming cancellation", async () => {
    const upstream = new Response("event: message.started\n\ndata: {}\n\n", { status: 200, headers: { "content-type": "text/event-stream" } });
    const fetchMock = vi.fn().mockResolvedValue(upstream);
    vi.stubGlobal("fetch", fetchMock);
    const controller = new AbortController();
    const request = new Request("http://localhost:3000/api/conversations/conversation-1/messages/stream", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: "{}",
      signal: controller.signal,
    });

    await POST(request, context(["conversations", "conversation-1", "messages", "stream"]));
    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(init.signal).toBeDefined();
    controller.abort();
    expect(init.signal?.aborted).toBe(true);
    vi.unstubAllGlobals();
  });

  it("returns a safe unavailable response when the API cannot be reached", async () => {
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new Error("private upstream detail")));
    const response = await GET(new Request("http://localhost:3000/api/auth/csrf"), context(["auth", "csrf"]));
    expect(response.status).toBe(503);
    await expect(response.json()).resolves.toEqual({ error: { code: "API_UNAVAILABLE", message: "The service is temporarily unavailable. Please try again." } });
    vi.unstubAllGlobals();
  });

  it("rejects oversized request bodies before contacting the API", async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    const response = await POST(new Request("http://localhost:3000/api/files", {
      method: "POST",
      headers: { "content-length": String(25 * 1024 * 1024 + 1) },
      body: "small-body",
    }), context(["files"]));

    expect(response.status).toBe(413);
    await expect(response.json()).resolves.toEqual({ error: { code: "REQUEST_TOO_LARGE", message: "The request body is too large." } });
    expect(fetchMock).not.toHaveBeenCalled();
    vi.unstubAllGlobals();
  });

  it("returns a safe timeout response for an aborted upstream request", async () => {
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new DOMException("private upstream detail", "AbortError")));
    const response = await GET(new Request("http://localhost:3000/api/auth/me"), context(["auth", "me"]));

    expect(response.status).toBe(504);
    await expect(response.json()).resolves.toEqual({ error: { code: "API_TIMEOUT", message: "The service took too long to respond. Please try again." } });
    vi.unstubAllGlobals();
  });

  // The project revision fence depends on this route: the browser calls the API
  // same-origin in production, so a conditional PATCH has to reach the API with
  // its If-Match intact and the API's answer has to reach the browser with the
  // ETag it published. Anything dropped here turns every fenced save into an
  // unfenced last-write-wins one, or leaves the conflict prompt without the
  // revision it needs to offer.
  it("forwards a fenced PATCH's If-Match upstream and returns the committed ETag", async () => {
    const held = '"2026-11-16T09:00:00.0000123Z"';
    const committed = '"2026-11-16T09:00:00.0000456Z"';
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ id: "project-1", name: "Renamed" }), { status: 200, headers: { ETag: committed } }),
    );
    vi.stubGlobal("fetch", fetchMock);

    const request = new Request("http://localhost:3000/api/projects/project-1", {
      method: "PATCH",
      headers: { "if-match": held, "content-type": "application/json", cookie: "taslim.auth=session" },
      body: JSON.stringify({ name: "Renamed" }),
    });
    const response = await PATCH(request, context(["projects", "project-1"]));
    const [target, init] = fetchMock.mock.calls[0] as [string, RequestInit];

    expect(target).toBe("http://localhost:5000/api/projects/project-1");
    expect(init.method).toBe("PATCH");
    expect(new Headers(init.headers).get("If-Match")).toBe(held);
    expect(response.status).toBe(200);
    expect(response.headers.get("etag")).toBe(committed);
    await expect(response.json()).resolves.toMatchObject({ id: "project-1" });
    vi.unstubAllGlobals();
  });

  it("passes a revision refusal through with the live ETag intact", async () => {
    const live = '"2026-11-16T09:00:00.0000789Z"';
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ error: { code: "PROJECT_REVISION_CONFLICT", message: "This project was changed by someone else." } }), {
        status: 409,
        headers: { ETag: live },
      }),
    );
    vi.stubGlobal("fetch", fetchMock);

    const request = new Request("http://localhost:3000/api/projects/project-1", {
      method: "PATCH",
      headers: { "if-match": '"2026-11-16T09:00:00.0000123Z"', "content-type": "application/json", cookie: "taslim.auth=session" },
      body: JSON.stringify({ name: "Stale" }),
    });
    const response = await PATCH(request, context(["projects", "project-1"]));

    // Not flattened into a generic failure: the client branches on the status and
    // the code, then reads the revision that actually holds the row.
    expect(response.status).toBe(409);
    expect(response.headers.get("etag")).toBe(live);
    await expect(response.json()).resolves.toMatchObject({ error: { code: "PROJECT_REVISION_CONFLICT" } });
    vi.unstubAllGlobals();
  });
});
