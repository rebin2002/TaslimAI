import { describe, expect, it } from "vitest";
import type { NotificationList } from "./api";
import { applyAllNotificationsRead, applyNotificationRead } from "./notificationReadState";

const list: NotificationList = {
  items: [
    {
      id: "notification-1",
      workspaceId: "workspace-1",
      projectId: null,
      generationJobId: null,
      assetId: null,
      type: "generation.completed",
      resourceTitle: "First result",
      createdAt: "2026-01-01T00:00:00Z",
      readAt: null,
      isRead: false,
      destination: "/activity",
    },
    {
      id: "notification-2",
      workspaceId: "workspace-1",
      projectId: null,
      generationJobId: null,
      assetId: null,
      type: "generation.failed",
      resourceTitle: "Second result",
      createdAt: "2026-01-01T00:01:00Z",
      readAt: null,
      isRead: false,
      destination: "/activity",
    },
  ],
  page: 1,
  pageSize: 6,
  totalCount: 2,
  totalPages: 1,
  unreadCount: 2,
};

describe("notification read state transitions", () => {
  it("does not decrement the unread count when a duplicate read response arrives", () => {
    const first = applyNotificationRead(list, "notification-1", "2026-01-01T00:02:00Z");
    const duplicate = applyNotificationRead(first.next, "notification-1", "2026-01-01T00:03:00Z");

    expect(first.unreadCountChanged).toBe(true);
    expect(first.next.unreadCount).toBe(1);
    expect(duplicate.unreadCountChanged).toBe(false);
    expect(duplicate.next).toEqual(first.next);
  });

  it("keeps separate successful reads accurate regardless of response order", () => {
    const firstOrder = applyNotificationRead(list, "notification-1", "2026-01-01T00:02:00Z");
    const secondOrder = applyNotificationRead(firstOrder.next, "notification-2", "2026-01-01T00:03:00Z");
    const reverseFirst = applyNotificationRead(list, "notification-2", "2026-01-01T00:03:00Z");
    const reverseSecond = applyNotificationRead(reverseFirst.next, "notification-1", "2026-01-01T00:02:00Z");

    expect(secondOrder.next.unreadCount).toBe(0);
    expect(reverseSecond.next).toEqual(secondOrder.next);
    expect(secondOrder.next.items.every((item) => item.isRead)).toBe(true);
  });

  it("makes mark-all idempotent after individual and bulk responses overlap", () => {
    const individual = applyNotificationRead(list, "notification-1", "2026-01-01T00:02:00Z");
    const all = applyAllNotificationsRead(individual.next, "2026-01-01T00:03:00Z");
    const duplicateIndividual = applyNotificationRead(all.next, "notification-1", "2026-01-01T00:04:00Z");
    const duplicateAll = applyAllNotificationsRead(duplicateIndividual.next, "2026-01-01T00:05:00Z");

    expect(all.next.unreadCount).toBe(0);
    expect(all.unreadCountChanged).toBe(true);
    expect(duplicateIndividual.unreadCountChanged).toBe(false);
    expect(duplicateAll.unreadCountChanged).toBe(false);
    expect(duplicateAll.next).toEqual(all.next);
  });
});
