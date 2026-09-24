import { describe, expect, it, vi } from "vitest";
import { GET, POST } from "./route";

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
});
