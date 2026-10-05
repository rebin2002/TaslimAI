import { describe, expect, it } from "vitest";
import type { ActivityList } from "./api";
import { applyActivityRead, applyAllActivityRead } from "./activityCenter";

const list: ActivityList = {
  items: [
    {
      jobId: "job-1",
      workspaceId: "workspace-1",
      projectId: null,
      jobType: "document",
      title: "Brief",
      status: "Completed",
      progressPercent: 100,
      createdAt: "2026-09-23T00:00:00Z",
      completedAt: "2026-09-23T00:01:00Z",
      isRead: false,
      safeFailureMessage: null,
      assetId: null,
    },
    {
      jobId: "job-2",
      workspaceId: "workspace-1",
      projectId: null,
      jobType: "image",
      title: "Cover",
      status: "Running",
      progressPercent: 42,
      createdAt: "2026-09-22T00:00:00Z",
      completedAt: null,
      isRead: true,
      safeFailureMessage: null,
      assetId: null,
    },
  ],
  page: 1,
  pageSize: 50,
  totalCount: 2,
  totalPages: 1,
  unreadCount: 1,
};

describe("activity read state", () => {
  it("marks one job read once without double-decrementing the unread count", () => {
    const first = applyActivityRead(list, "job-1");
    const second = applyActivityRead(first.next, "job-1");

    expect(first.unreadCountChanged).toBe(true);
    expect(first.next.unreadCount).toBe(0);
    expect(second.unreadCountChanged).toBe(false);
    expect(second.next).toEqual(first.next);
  });

  it("marks all activities read idempotently", () => {
    const first = applyAllActivityRead(list);
    const second = applyAllActivityRead(first.next);

    expect(first.unreadCountChanged).toBe(true);
    expect(first.next.items.every((item) => item.isRead)).toBe(true);
    expect(second.unreadCountChanged).toBe(false);
    expect(second.next).toEqual(first.next);
  });
});
