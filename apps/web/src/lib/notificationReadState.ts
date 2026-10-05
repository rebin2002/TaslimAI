import type { NotificationList } from "./api";

export type NotificationReadUpdate = {
  next: NotificationList;
  unreadCountChanged: boolean;
};

/**
 * Applies one successful read response to the latest visible notification list.
 * Repeated or out-of-order successful responses are intentionally no-ops after
 * the item has already been marked read, keeping the badge count authoritative.
 */
export function applyNotificationRead(result: NotificationList, notificationId: string, readAt: string): NotificationReadUpdate {
  const item = result.items.find((entry) => entry.id === notificationId);
  if (!item || item.isRead) return { next: result, unreadCountChanged: false };

  return {
    next: {
      ...result,
      unreadCount: Math.max(0, result.unreadCount - 1),
      items: result.items.map((entry) => entry.id === notificationId ? { ...entry, isRead: true, readAt: entry.readAt ?? readAt } : entry),
    },
    unreadCountChanged: true,
  };
}

/**
 * Applies a successful mark-all response once. The total unread count can
 * include records on other pages, so it is authoritative when it is non-zero
 * even if the current page happens to contain only read records.
 */
export function applyAllNotificationsRead(result: NotificationList, readAt: string): NotificationReadUpdate {
  if (result.unreadCount === 0 && !result.items.some((item) => !item.isRead)) return { next: result, unreadCountChanged: false };

  return {
    next: {
      ...result,
      unreadCount: 0,
      items: result.items.map((item) => item.isRead ? item : { ...item, isRead: true, readAt: item.readAt ?? readAt }),
    },
    unreadCountChanged: result.unreadCount !== 0,
  };
}
