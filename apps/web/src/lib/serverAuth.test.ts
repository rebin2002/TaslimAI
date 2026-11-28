import { beforeEach, describe, expect, it, vi } from "vitest";
import { cookies } from "next/headers";
import { redirect } from "next/navigation";
import { internalReturnPath, requireAuthenticatedPage } from "./serverAuth";

const cookiesMock = vi.mocked(cookies);
const redirectMock = vi.mocked(redirect);

vi.mock("next/headers", () => ({
  cookies: vi.fn(),
}));
vi.mock("next/navigation", () => ({
  redirect: vi.fn((destination: string) => {
    throw new Error(`REDIRECT:${destination}`);
  }),
}));

describe("server-side page authentication", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.unstubAllGlobals();
    vi.stubGlobal("fetch", vi.fn());
  });

  it("redirects before contacting the API when the auth cookie is absent", async () => {
    cookiesMock.mockResolvedValue({ get: vi.fn().mockReturnValue(undefined) } as never);

    await expect(requireAuthenticatedPage("/personal/health")).rejects.toThrow("REDIRECT:/login?next=%2Fpersonal%2Fhealth");
    expect(redirectMock).toHaveBeenCalledWith("/login?next=%2Fpersonal%2Fhealth");
    expect(fetch).not.toHaveBeenCalled();
  });

  it("validates the auth cookie through the existing session endpoint without caching", async () => {
    cookiesMock.mockResolvedValue({ get: vi.fn().mockReturnValue({ value: "session-cookie" }) } as never);
    const fetchMock = vi.mocked(fetch).mockResolvedValue(new Response(JSON.stringify({
      user: { id: "user-id", personalWorkspaceId: "workspace-id" },
      personalWorkspace: { id: "workspace-id", type: "Personal", role: "Owner" },
    }), { status: 200, headers: { "content-type": "application/json" } }));

    await expect(requireAuthenticatedPage("/personal/health")).resolves.toBeUndefined();
    expect(fetchMock).toHaveBeenCalledWith("http://localhost:5000/api/auth/me", {
      headers: { cookie: "taslim.auth=session-cookie" },
      cache: "no-store",
      signal: expect.any(AbortSignal),
    });
    expect(redirectMock).not.toHaveBeenCalled();
  });

  it("fails closed when an upstream returns success without the session contract", async () => {
    cookiesMock.mockResolvedValue({ get: vi.fn().mockReturnValue({ value: "session-cookie" }) } as never);
    vi.mocked(fetch).mockResolvedValue(new Response(JSON.stringify({ ok: true }), {
      status: 200,
      headers: { "content-type": "application/json" },
    }));

    await expect(requireAuthenticatedPage("/personal/health")).rejects.toThrow("REDIRECT:/login?next=%2Fpersonal%2Fhealth");
    expect(redirectMock).toHaveBeenCalledWith("/login?next=%2Fpersonal%2Fhealth");
  });

  it("fails closed when a non-JSON response carries a session-shaped body", async () => {
    cookiesMock.mockResolvedValue({ get: vi.fn().mockReturnValue({ value: "session-cookie" }) } as never);
    vi.mocked(fetch).mockResolvedValue(new Response(JSON.stringify({
      user: { id: "user-id", personalWorkspaceId: "workspace-id" },
      personalWorkspace: { id: "workspace-id", type: "Personal", role: "Owner" },
    }), {
      status: 200,
      headers: { "content-type": "text/html" },
    }));

    await expect(requireAuthenticatedPage("/personal/health")).rejects.toThrow("REDIRECT:/login?next=%2Fpersonal%2Fhealth");
    expect(redirectMock).toHaveBeenCalledWith("/login?next=%2Fpersonal%2Fhealth");
  });

  it.each([
    {
      name: "a mismatched personal workspace identity",
      payload: {
        user: { id: "user-id", personalWorkspaceId: "workspace-one" },
        personalWorkspace: { id: "workspace-two", type: "Personal", role: "Owner" },
      },
    },
    {
      name: "a business workspace identity",
      payload: {
        user: { id: "user-id", personalWorkspaceId: "workspace-id" },
        personalWorkspace: { id: "workspace-id", type: "Business", role: "Member" },
      },
    },
  ])("fails closed on $name", async ({ payload }) => {
    cookiesMock.mockResolvedValue({ get: vi.fn().mockReturnValue({ value: "session-cookie" }) } as never);
    vi.mocked(fetch).mockResolvedValue(new Response(JSON.stringify(payload), {
      status: 200,
      headers: { "content-type": "application/json" },
    }));

    await expect(requireAuthenticatedPage("/personal/health")).rejects.toThrow("REDIRECT:/login?next=%2Fpersonal%2Fhealth");
    expect(redirectMock).toHaveBeenCalledWith("/login?next=%2Fpersonal%2Fhealth");
  });

  it("uses the same safe login redirect for rejected or unavailable sessions", async () => {
    cookiesMock.mockResolvedValue({ get: vi.fn().mockReturnValue({ value: "stale-cookie" }) } as never);
    const fetchMock = vi.mocked(fetch);
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 401 }));

    await expect(requireAuthenticatedPage("/personal/health")).rejects.toThrow("REDIRECT:/login?next=%2Fpersonal%2Fhealth");

    vi.clearAllMocks();
    cookiesMock.mockResolvedValue({ get: vi.fn().mockReturnValue({ value: "unavailable-cookie" }) } as never);
    fetchMock.mockRejectedValueOnce(new Error("private upstream detail"));

    await expect(requireAuthenticatedPage("/personal/health")).rejects.toThrow("REDIRECT:/login?next=%2Fpersonal%2Fhealth");
    expect(redirectMock).toHaveBeenCalledWith("/login?next=%2Fpersonal%2Fhealth");
  });

  it("redirects safely when the cookie store fails or the cookie contains header delimiters", async () => {
    cookiesMock.mockRejectedValueOnce(new Error("private cookie-store detail"));
    await expect(requireAuthenticatedPage("/personal/health")).rejects.toThrow("REDIRECT:/login?next=%2Fpersonal%2Fhealth");

    vi.clearAllMocks();
    cookiesMock.mockResolvedValue({ get: vi.fn().mockReturnValue({ value: "session-cookie; injected=true" }) } as never);
    await expect(requireAuthenticatedPage("/personal/health")).rejects.toThrow("REDIRECT:/login?next=%2Fpersonal%2Fhealth");
    expect(fetch).not.toHaveBeenCalled();
  });

  it("rejects backslash-prefixed return paths that browsers normalize into external URLs", async () => {
    cookiesMock.mockRejectedValueOnce(new Error("private cookie-store detail"));

    await expect(requireAuthenticatedPage("/\\evil.example/collect")).rejects.toThrow("REDIRECT:/login?next=%2F");
    expect(redirectMock).toHaveBeenCalledWith("/login?next=%2F");
    expect(fetch).not.toHaveBeenCalled();
  });

  it("bounds a hanging session check and keeps the return path local", async () => {
    vi.useFakeTimers();
    try {
      cookiesMock.mockResolvedValue({ get: vi.fn().mockReturnValue({ value: "session-cookie" }) } as never);
      const fetchMock = vi.mocked(fetch).mockImplementation((_input, init) => new Promise((_resolve, reject) => {
        init?.signal?.addEventListener("abort", () => reject(new DOMException("timeout", "AbortError")), { once: true });
      }));

      const pending = requireAuthenticatedPage("//evil.example/health");
      const redirectAssertion = expect(pending).rejects.toThrow("REDIRECT:/login?next=%2F");
      await vi.advanceTimersByTimeAsync(5_000);

      await redirectAssertion;
      expect(fetchMock).toHaveBeenCalledOnce();
      expect((fetchMock.mock.calls[0][1] as RequestInit).signal).toBeDefined();
    } finally {
      vi.useRealTimers();
    }
  });
});

describe("query-preserving return paths for server-side gates", () => {
  it("keeps the bare pathname when no query was requested", () => {
    expect(internalReturnPath("/assets")).toBe("/assets");
    expect(internalReturnPath("/assets", {})).toBe("/assets");
    expect(internalReturnPath("/assets", { assetId: undefined })).toBe("/assets");
    expect(internalReturnPath("/assets", new URLSearchParams())).toBe("/assets");
  });

  it("rebuilds the deep link the protected view hydrates from", () => {
    expect(internalReturnPath("/assets", { assetId: "asset-1", status: "Archived" }))
      .toBe("/assets?assetId=asset-1&status=Archived");
    expect(internalReturnPath("/create/presentation", { projectId: "project-1" }))
      .toBe("/create/presentation?projectId=project-1");
  });

  it("re-encodes decoded values exactly as the app-encoded the original link", () => {
    // assetDestination() emits /assets?assetId=asset%2Farchived&status=Archived;
    // Next.js hands the page the decoded value, so the gate must restore the
    // same wire form instead of a bare or double-decoded slash.
    expect(internalReturnPath("/assets", { assetId: "asset/archived", status: "Archived" }))
      .toBe("/assets?assetId=asset%2Farchived&status=Archived");
    // encodeURIComponent uses %20 for spaces, never the + that a
    // URLSearchParams.toString() rewrite would produce.
    expect(internalReturnPath("/assets", { search: "quarterly report" }))
      .toBe("/assets?search=quarterly%20report");
    // A stored value that was never a valid escape stays opaque.
    expect(internalReturnPath("/assets", { search: "100%zz" }))
      .toBe("/assets?search=100%25zz");
  });

  it("keeps repeated parameters, their order, and non-string values inside one value", () => {
    expect(internalReturnPath("/assets", { tag: ["image", "document"], status: "Active" }))
      .toBe("/assets?tag=image&tag=document&status=Active");
    // A value that looks like structure must not become structure.
    expect(internalReturnPath("/assets", { assetId: "a&status=Archived" }))
      .toBe("/assets?assetId=a%26status%3DArchived");
    expect(internalReturnPath("/assets", { "&": "1" })).toBe("/assets?%26=1");
  });

  it("accepts the URLSearchParams form as well as the page object form", () => {
    const params = new URLSearchParams([["assetId", "asset/archived"], ["tag", "a"], ["tag", "b"]]);
    expect(internalReturnPath("/assets", params))
      .toBe("/assets?assetId=asset%2Farchived&tag=a&tag=b");
  });

  it.each([
    ["a protocol-relative pathname", "//evil.example/assets", {}],
    ["a backslash pathname", "/\\evil.example/assets", {}],
    ["a scheme pathname", "https://evil.example/assets", {}],
    ["a pathname with control characters", "/assets\nHost: evil.example", {}],
    ["an unsafe pathname carrying a query", "//evil.example/x", { assetId: "asset-1" }],
  ])("falls back to the origin root for %s", (_name, pathname, searchParams) => {
    const result = internalReturnPath(pathname, searchParams);
    expect(result === "/" || result.startsWith("/?")).toBe(true);
    expect(result.startsWith("//")).toBe(false);
  });

  it("neutralises control characters smuggled through a query value", () => {
    const result = internalReturnPath("/assets", { search: "a\r\nLocation: https://evil.example" });
    expect(result).not.toMatch(/[\u0000-\u001f\u007f]/);
    expect(result).toBe(
      "/assets?search=a%0D%0ALocation%3A%20https%3A%2F%2Fevil.example",
    );
  });

  it("drops the query, not the destination, when the rebuild would overflow", () => {
    const result = internalReturnPath("/assets", { search: "x".repeat(4_096) });
    expect(result).toBe("/assets");
  });

  it("promises back the full deep link when a gate redirects", async () => {
    cookiesMock.mockResolvedValue({ get: vi.fn().mockReturnValue(undefined) } as never);
    const returnPath = internalReturnPath("/assets", { assetId: "asset/archived", status: "Archived" });

    await expect(requireAuthenticatedPage(returnPath)).rejects.toThrow(
      "REDIRECT:/login?next=%2Fassets%3FassetId%3Dasset%252Farchived%26status%3DArchived",
    );
    expect(redirectMock).toHaveBeenCalledWith(
      "/login?next=%2Fassets%3FassetId%3Dasset%252Farchived%26status%3DArchived",
    );
  });

  it("still refuses an external target that only looks internal once reassembled", async () => {
    cookiesMock.mockResolvedValue({ get: vi.fn().mockReturnValue(undefined) } as never);

    await expect(requireAuthenticatedPage(internalReturnPath("//evil.example/assets", { a: "b" })))
      .rejects.toThrow("REDIRECT:/login?next=%2F");
  });
});
