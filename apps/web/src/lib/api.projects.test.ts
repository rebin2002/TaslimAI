import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Project } from "./api";

// Synthetic client-side regression for the projects task-list pagination lane.
// The endpoint answers with a bare page: 20 rows by default, never more than
// 100, and no total. A caller that issues one unparameterized request
// therefore renders a silently truncated list, so `listAllProjects` must pin
// its own page size and keep requesting pages until one comes back short.

function response(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

const project = (index: number, overrides: Partial<Project> = {}): Project => ({
  id: `project-${index}`,
  workspaceId: "workspace-1",
  name: `Project ${index}`,
  description: null,
  instructions: null,
  contextNotes: null,
  type: "General",
  status: "Active",
  createdAt: "2026-11-16T00:00:00Z",
  updatedAt: "2026-11-16T00:00:00Z",
  archivedAt: null,
  ...overrides,
});

const items = (start: number, count: number): Project[] =>
  Array.from({ length: count }, (_, offset) => project(start + offset));

describe("api.listAllProjects", () => {
  beforeEach(() => {
    vi.resetModules();
    vi.unstubAllGlobals();
  });

  it("requests its own page size instead of accepting the 20-row server default", async () => {
    const fetchMock = vi.fn().mockResolvedValue(response(items(1, 12)));
    vi.stubGlobal("fetch", fetchMock);
    const { api } = await import("./api");
    await api.listAllProjects("workspace-1", "Active");
    const [url] = fetchMock.mock.calls[0] as [string];
    expect(url).toContain("/api/workspaces/workspace-1/projects?status=Active");
    expect(url).toContain("page=1");
    expect(url).toContain("pageSize=100");
  });

  it("walks every page and publishes the whole list", async () => {
    const fetchMock = vi.fn(async (input: string) => {
      const pageNumber = Number(new URL(input).searchParams.get("page"));
      // 137 projects: a full 100-row page, then a short final page.
      return response(pageNumber === 1 ? items(1, 100) : items(101, 37));
    });
    vi.stubGlobal("fetch", fetchMock);
    const { api } = await import("./api");
    const collected = await api.listAllProjects("workspace-1", "Active");
    expect(collected).toHaveLength(137);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    const [secondUrl] = fetchMock.mock.calls[1] as [string];
    expect(secondUrl).toContain("page=2");
    expect(secondUrl).toContain("pageSize=100");
    expect(collected[100]!.id).toBe("project-101");
    expect(new Set(collected.map((item) => item.id)).size).toBe(137);
  });

  it("keeps each tab scoped to its own status on every page", async () => {
    const fetchMock = vi.fn(async (input: string) => {
      const pageNumber = Number(new URL(input).searchParams.get("page"));
      return response(pageNumber <= 2 ? items(pageNumber, 100) : []);
    });
    vi.stubGlobal("fetch", fetchMock);
    const { api } = await import("./api");
    await api.listAllProjects("workspace-1", "Archived");
    expect(fetchMock).toHaveBeenCalledTimes(3);
    for (const call of fetchMock.mock.calls) {
      const url = new URL(call[0] as string);
      expect(url.searchParams.get("status")).toBe("Archived");
      expect(url.searchParams.get("pageSize")).toBe("100");
    }
  });

  it("forwards the caller's abort signal to every page request", async () => {
    const fetchMock = vi.fn(async (input: string, init?: RequestInit) => {
      const pageNumber = Number(new URL(input).searchParams.get("page"));
      // A replacement load aborts the walk part-way through, exactly as the
      // browser would reject an aborted fetch.
      init?.signal?.throwIfAborted();
      return response(pageNumber === 1 ? items(1, 100) : []);
    });
    vi.stubGlobal("fetch", fetchMock);
    const { api } = await import("./api");
    const controller = new AbortController();
    controller.abort(new Error("superseded"));
    await expect(api.listAllProjects("workspace-1", "Active", controller.signal)).rejects.toThrow();
    const signals = fetchMock.mock.calls.map((call) => (call[1] as RequestInit | undefined)?.signal);
    expect(signals.length).toBeGreaterThan(0);
    expect(signals.every((signal) => signal === controller.signal)).toBe(true);
  });

  it("surfaces a failed page instead of publishing a partial list", async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(items(1, 100)))
      .mockResolvedValueOnce(response({ error: { code: "SERVER_ERROR", message: "db unavailable" } }, 500));
    vi.stubGlobal("fetch", fetchMock);
    const { api } = await import("./api");
    await expect(api.listAllProjects("workspace-1", "Active")).rejects.toThrow();
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });
});
