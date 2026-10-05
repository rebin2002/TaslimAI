import { beforeEach, describe, expect, it, vi } from "vitest";

function response(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

describe("api.listNotifications", () => {
  beforeEach(() => {
    vi.resetModules();
    vi.unstubAllGlobals();
  });

  it("requests the selected workspace page", async () => {
    const fetchMock = vi.fn().mockResolvedValue(response({ items: [], page: 3, pageSize: 20, totalCount: 41, totalPages: 3, unreadCount: 2 }));
    vi.stubGlobal("fetch", fetchMock);
    const { api } = await import("./api");

    await expect(api.listNotifications("workspace-7", 3, 20, false)).resolves.toMatchObject({ page: 3, totalPages: 3 });
    const [url] = fetchMock.mock.calls[0] as [string];
    expect(url).toContain("/api/notifications?");
    expect(url).toContain("workspaceId=workspace-7");
    expect(url).toContain("page=3");
    expect(url).toContain("pageSize=20");
    expect(url).toContain("unreadOnly=false");
  });

  it("encodes unread-only pagination without leaking another workspace identifier", async () => {
    const fetchMock = vi.fn().mockResolvedValue(response({ items: [], page: 1, pageSize: 10, totalCount: 0, totalPages: 0, unreadCount: 0 }));
    vi.stubGlobal("fetch", fetchMock);
    const { api } = await import("./api");

    await api.listNotifications("private workspace", 1, 10, true);
    const [url] = fetchMock.mock.calls[0] as [string];
    expect(url).toContain("workspaceId=private+workspace");
    expect(url).toContain("pageSize=10");
    expect(url).toContain("unreadOnly=true");
    expect(url).not.toContain("workspace-7");
  });
});
