import type { ActivityItem, ActivityList } from "./api";

export const activityStatuses = ["All", "Running", "Queued", "Completed", "Failed", "Cancelled"] as const;
export type ActivityStatusFilter = (typeof activityStatuses)[number];

export function activityStatusKey(status: ActivityItem["status"]): string {
  return status.toLowerCase();
}

export function activityTypeKey(type: ActivityItem["jobType"]): string {
  return type.toLowerCase();
}

export function hasActiveActivity(items: ActivityItem[]): boolean {
  return items.some((item) => item.status === "Queued" || item.status === "Running");
}

export function mergeActivityList(current: ActivityList | null, next: ActivityList): ActivityList {
  if (!current) return next;
  const byId = new Map(current.items.map((item) => [item.jobId, item]));
  next.items.forEach((item) => byId.set(item.jobId, item));
  return { ...next, items: [...byId.values()].sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt)) };
}

export function applyActivityRead(current: ActivityList, jobId: string): { next: ActivityList; unreadCountChanged: boolean } {
  const item = current.items.find((entry) => entry.jobId === jobId);
  if (!item || item.isRead) return { next: current, unreadCountChanged: false };
  return {
    next: {
      ...current,
      unreadCount: Math.max(0, current.unreadCount - 1),
      items: current.items.map((entry) => entry.jobId === jobId ? { ...entry, isRead: true } : entry),
    },
    unreadCountChanged: true,
  };
}

export function applyAllActivityRead(current: ActivityList): { next: ActivityList; unreadCountChanged: boolean } {
  if (current.unreadCount === 0 && current.items.every((item) => item.isRead)) return { next: current, unreadCountChanged: false };
  return {
    next: {
      ...current,
      unreadCount: 0,
      items: current.items.map((item) => item.isRead ? item : { ...item, isRead: true }),
    },
    unreadCountChanged: current.unreadCount > 0,
  };
}

export function safeActivityFailure(item: ActivityItem): string | null {
  return item.status === "Failed" || item.status === "Cancelled" ? item.safeFailureMessage : null;
}
