import { describe, expect, it } from "vitest";
import { buildHomeRecentItems, hasInFlightActivity } from "./homeDashboardState";

const baseProject = {
  id: "project-1", workspaceId: "workspace-1", name: "Launch plan", description: null, instructions: null, contextNotes: null, type: "Marketing", status: "Active" as const, createdAt: "2026-09-20T12:00:00.000Z", updatedAt: "2026-09-22T12:00:00.000Z", archivedAt: null,
};

const baseConversation = {
  id: "conversation-1", workspaceId: "workspace-1", projectId: null, title: "Campaign angles", status: "Active" as const, createdAt: "2026-09-20T12:00:00.000Z", updatedAt: "2026-09-21T12:00:00.000Z", lastMessageAt: "2026-09-23T12:00:00.000Z",
};

const baseActivity = {
  jobId: "job-1", workspaceId: "workspace-1", projectId: null, jobType: "image" as const, title: "Product hero", status: "Running" as const, progressPercent: 42, createdAt: "2026-09-24T12:00:00.000Z", completedAt: null, isRead: false, safeFailureMessage: null, assetId: null,
};

const baseAsset = {
  id: "asset-1", workspaceId: "workspace-1", projectId: null, projectName: null, name: "Company overview", description: null, assetType: "document" as const, mimeType: "application/pdf", status: "Active" as const, hasFile: true, canPreview: true, createdAt: "2026-09-19T12:00:00.000Z", updatedAt: "2026-09-22T18:00:00.000Z", archivedAt: null, representations: [],
};

describe("homeDashboardState", () => {
  it("creates only real, most-recent workspace records with safe destinations", () => {
    const result = buildHomeRecentItems({ projects: [baseProject], conversations: [baseConversation], activity: [baseActivity], assets: [baseAsset] });

    expect(result.map((item) => item.id)).toEqual(["activity:job-1", "conversation:conversation-1", "asset:asset-1", "project:project-1"]);
    expect(result[0]).toMatchObject({ href: "/notifications", status: "Running", progressPercent: 42 });
    expect(result[1]).toMatchObject({ href: "/chat/conversation-1" });
  });

  it("links completed activity with an asset to the private Asset Library search", () => {
    const result = buildHomeRecentItems({
      projects: [], conversations: [], assets: [], activity: [{ ...baseActivity, status: "Completed", assetId: "asset-1" }],
    });

    expect(result[0]?.href).toBe("/assets?search=Product%20hero");
  });

  it("limits the continuation list and recognizes in-flight work", () => {
    const result = buildHomeRecentItems({
      projects: [baseProject, { ...baseProject, id: "project-2", updatedAt: "2026-09-25T12:00:00.000Z" }],
      conversations: [baseConversation], activity: [baseActivity], assets: [baseAsset],
    }, 2);

    expect(result).toHaveLength(2);
    expect(hasInFlightActivity([{ ...baseActivity, status: "Queued" }])).toBe(true);
    expect(hasInFlightActivity([{ ...baseActivity, status: "Completed" }])).toBe(false);
  });
});
