import { describe, expect, it } from "vitest";
import type { ActivityItem, ActivityList } from "./api";
import { activityStatusKey, activityTypeKey, hasActiveActivity, mergeActivityList, safeActivityFailure } from "./activityCenter";

function item(overrides: Partial<ActivityItem> = {}): ActivityItem {
  return {
    jobId: "job-1", workspaceId: "workspace-1", projectId: null, jobType: "document", title: "Brief", status: "Queued", progressPercent: 0,
    createdAt: "2026-09-23T00:00:00Z", completedAt: null, isRead: false, safeFailureMessage: null, assetId: null, ...overrides,
  };
}

describe("activity center state", () => {
  it("normalizes safe display keys and active statuses", () => {
    expect(activityStatusKey("Completed")).toBe("completed");
    expect(activityTypeKey("presentation")).toBe("presentation");
    expect(hasActiveActivity([item({ status: "Running" })])).toBe(true);
    expect(hasActiveActivity([item({ status: "Completed" })])).toBe(false);
  });

  it("keeps safe failure copy and never needs provider fields", () => {
    const failed = item({ status: "Failed", safeFailureMessage: "This activity could not be completed. Please try again." });
    expect(safeActivityFailure(failed)).toContain("could not be completed");
    expect(failed).not.toHaveProperty("provider");
    expect(failed).not.toHaveProperty("model");
  });

  it("merges refreshed activity without duplicating jobs", () => {
    const current: ActivityList = { items: [item()], page: 1, pageSize: 50, totalCount: 1, totalPages: 1, unreadCount: 1 };
    const next: ActivityList = { ...current, items: [item({ status: "Completed", progressPercent: 100, isRead: true })] };
    const merged = mergeActivityList(current, next);
    expect(merged.items).toHaveLength(1);
    expect(merged.items[0]).toMatchObject({ status: "Completed", progressPercent: 100, isRead: true });
  });
});


