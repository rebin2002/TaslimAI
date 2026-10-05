import { describe, expect, it } from "vitest";
import type { GlobalSearchResult } from "./api";
import { globalSearchResultDestination } from "./searchNavigation";

const result = (overrides: Partial<GlobalSearchResult>): GlobalSearchResult => ({
  type: "assets",
  id: "asset-1",
  title: "Campaign result",
  description: null,
  projectId: null,
  conversationId: null,
  assetId: null,
  projectName: null,
  status: "Active",
  metadata: null,
  createdAt: "2026-10-05T00:00:00Z",
  updatedAt: null,
  ...overrides,
});

describe("globalSearchResultDestination", () => {
  it("opens the exact asset instead of searching by a potentially duplicated title", () => {
    expect(globalSearchResultDestination(result({ id: "asset/with spaces", title: "Shared name" }))).toBe(
      "/assets?assetId=asset%2Fwith%20spaces&status=Active",
    );
  });

  it("opens the asset produced by a generation result directly", () => {
    expect(globalSearchResultDestination(result({ type: "generation", id: "job-1", assetId: "asset-2" }))).toBe(
      "/assets?assetId=asset-2&status=Active",
    );
  });

  it("keeps project, conversation, and file fallbacks stable", () => {
    expect(globalSearchResultDestination(result({ type: "projects", id: "project-1" }))).toBe("/projects/project-1");
    expect(globalSearchResultDestination(result({ type: "conversations", id: "conversation-1" }))).toBe("/chat/conversation-1");
    expect(globalSearchResultDestination(result({ type: "files", id: "file-1", projectId: "project-1" }))).toBe("/projects/project-1");
    expect(globalSearchResultDestination(result({ type: "files", id: "file-2", conversationId: "conversation-1" }))).toBe("/chat/conversation-1");
  });
});
