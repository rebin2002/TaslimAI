import { beforeEach, describe, expect, it, vi } from "vitest";
import { cookies } from "next/headers";
import { redirect } from "next/navigation";
import { requireAuthenticatedPage } from "./serverAuth";

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
    vi.stubGlobal("fetch", vi.fn());
  });

  it("redirects before contacting the API when the auth cookie is absent", async () => {
    cookiesMock.mockResolvedValue({ get: vi.fn().mockReturnValue(undefined) } as never);

    await expect(requireAuthenticatedPage("/personal/health")).rejects.toThrow("REDIRECT:/login?next=%2Fpersonal%2Fhealth");
    expect(redirectMock).toHaveBeenCalledWith("/login?next=%2Fpersonal%2Fhealth");
    expect(fetch).not.toHaveBeenCalled();
  });

  it("validates the auth cookie through the existing session endpoint", async () => {
    cookiesMock.mockResolvedValue({ get: vi.fn().mockReturnValue({ value: "session-cookie" }) } as never);
    const fetchMock = vi.mocked(fetch).mockResolvedValue(new Response(null, { status: 200 }));

    await expect(requireAuthenticatedPage("/personal/health")).resolves.toBeUndefined();
    expect(fetchMock).toHaveBeenCalledWith("http://localhost:5000/api/auth/me", {
      headers: { cookie: "taslim.auth=session-cookie" },
      cache: "no-store",
    });
    expect(redirectMock).not.toHaveBeenCalled();
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
});
