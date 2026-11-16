import { describe, expect, it } from "vitest";
import type { GlobalSearchGroup, GlobalSearchResult } from "./api";
import { appendGlobalSearchGroups } from "./searchPagination";

const group = (overrides: Partial<GlobalSearchGroup>): GlobalSearchGroup => ({
  type: "projects",
  count: 3,
  hasMore: true,
  items: [],
  ...overrides,
});
const result = (id: string): GlobalSearchResult => ({
  type: "projects",
  id,
  title: id,
  description: null,
  projectId: id,
  conversationId: null,
  assetId: null,
  projectName: null,
  status: "Active",
  metadata: null,
  createdAt: "2026-10-05T00:00:00Z",
  updatedAt: null,
});

describe("appendGlobalSearchGroups", () => {
  it("appends a later page without losing the server's total count or hasMore state", () => {
    const merged = appendGlobalSearchGroups(
      [group({ items: [result("project-1")] })],
      [group({ count: 3, hasMore: false, items: [result("project-2")] })],
    );

    expect(merged).toHaveLength(1);
    expect(merged[0].count).toBe(3);
    expect(merged[0].hasMore).toBe(false);
    expect(merged[0].items.map((item) => item.id)).toEqual(["project-1", "project-2"]);
  });

  it("retains result types that are absent from a later page", () => {
    const merged = appendGlobalSearchGroups(
      [group({ items: [result("project-1")] }), group({ type: "assets", items: [result("asset-1")] })],
      [group({ items: [result("project-2")] })],
    );

    expect(merged.map((item) => item.type)).toEqual(["projects", "assets"]);
    expect(merged[1].items[0].id).toBe("asset-1");
  });
});
