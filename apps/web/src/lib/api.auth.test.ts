import { beforeEach, describe, expect, it, vi } from "vitest";

const authResponse = {
  user: {
    id: "user-1",
    email: "owner@example.com",
    displayName: "Owner",
    preferredLanguage: "en",
    personalWorkspaceId: "workspace-1",
    createdAt: "2026-09-22T00:00:00Z",
  },
  personalWorkspace: {
    id: "workspace-1",
    name: "Owner's Workspace",
    slug: "owner-workspace",
    type: "Personal",
    role: "Owner",
  },
};

function response(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function csrfResponse(token: string) {
  return response({ token });
}

describe("api.login", () => {
  beforeEach(() => {
    vi.resetModules();
    vi.unstubAllGlobals();
  });

  it("performs login and refreshes CSRF state after authentication", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(csrfResponse("before-login"))
      .mockResolvedValueOnce(response(authResponse))
      .mockResolvedValueOnce(csrfResponse("after-login"));
    vi.stubGlobal("fetch", fetchMock);
    const { api } = await import("./api");

    await expect(api.login({ email: "owner@example.com", password: "StrongPassword!123" })).resolves.toEqual(authResponse);
    expect(fetchMock).toHaveBeenCalledTimes(3);
    expect(fetchMock.mock.calls.map((call) => (call[1] as RequestInit | undefined)?.method ?? "GET")).toEqual(["GET", "POST", "GET"]);
    expect(fetchMock.mock.calls[0][1].credentials).toBe("include");
    expect(fetchMock.mock.calls[1][1].credentials).toBe("include");
  });

  it("surfaces invalid credentials as a typed safe API error", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(csrfResponse("token"))
      .mockResolvedValueOnce(response({ error: { code: "INVALID_CREDENTIALS", message: "Invalid email or password." } }, 401));
    vi.stubGlobal("fetch", fetchMock);
    const { api, ApiError } = await import("./api");

    const failure = await api.login({ email: "owner@example.com", password: "wrong" }).catch((error) => error);
    expect(failure).toBeInstanceOf(ApiError);
    expect(failure.code).toBe("INVALID_CREDENTIALS");
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it("refreshes CSRF once and retries a login rejected by a stale token", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(csrfResponse("stale"))
      .mockResolvedValueOnce(response({ error: { code: "CSRF_VALIDATION_FAILED", message: "Request validation failed." } }, 400))
      .mockResolvedValueOnce(csrfResponse("fresh"))
      .mockResolvedValueOnce(response(authResponse))
      .mockResolvedValueOnce(csrfResponse("post-login"));
    vi.stubGlobal("fetch", fetchMock);
    const { api } = await import("./api");

    await expect(api.login({ email: "owner@example.com", password: "StrongPassword!123" })).resolves.toEqual(authResponse);
    expect(fetchMock).toHaveBeenCalledTimes(5);
    expect(fetchMock.mock.calls.map((call) => (call[1] as RequestInit | undefined)?.method ?? "GET")).toEqual(["GET", "POST", "GET", "POST", "GET"]);
    expect(fetchMock.mock.calls[0][1].credentials).toBe("include");
    expect(fetchMock.mock.calls[1][1].credentials).toBe("include");
    expect(fetchMock.mock.calls[3][1].credentials).toBe("include");
    expect(new Headers(fetchMock.mock.calls[1][1].headers).get("X-CSRF-TOKEN")).toBe("stale");
    expect(new Headers(fetchMock.mock.calls[3][1].headers).get("X-CSRF-TOKEN")).toBe("fresh");
  });

  it("does not retry a second time when the refreshed CSRF attempt also fails", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(csrfResponse("stale"))
      .mockResolvedValueOnce(response({ error: { code: "CSRF_VALIDATION_FAILED", message: "Request validation failed." } }, 400))
      .mockResolvedValueOnce(csrfResponse("fresh"))
      .mockResolvedValueOnce(response({ error: { code: "CSRF_VALIDATION_FAILED", message: "Request validation failed." } }, 400));
    vi.stubGlobal("fetch", fetchMock);
    const { api, ApiError } = await import("./api");

    const failure = await api.login({ email: "owner@example.com", password: "StrongPassword!123" }).catch((error) => error);
    expect(failure).toBeInstanceOf(ApiError);
    expect(failure.code).toBe("CSRF_VALIDATION_FAILED");
    expect(fetchMock).toHaveBeenCalledTimes(4);
  });
});
