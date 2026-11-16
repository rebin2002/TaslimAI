export function resolveNotificationUnreadCount(currentCount: number, nextCount: number | null): number {
  if (nextCount === null || !Number.isFinite(nextCount)) return Math.max(0, currentCount);
  return Math.max(0, Math.trunc(nextCount));
}

export type NotificationBadgeSession = {
  panelOpen: boolean;
  panelLoading: boolean;
  panelUnreadCount: number | null;
  liveUnreadCount: number;
};

/**
 * Decides which unread count the header badge shows.
 *
 * A panel snapshot only owns the badge while that panel session is open and
 * fully loaded. Once the panel closes - or while a new session is still
 * fetching - the snapshot stops refreshing, so the live header count (from
 * workspace-scoped polls and same-workspace read events) is authoritative
 * again. This keeps the badge from freezing at a stale panel count after a
 * popover visit.
 */
export function resolveNotificationBadgeCount(session: NotificationBadgeSession): number {
  if (!session.panelOpen || session.panelLoading) return resolveNotificationUnreadCount(0, session.liveUnreadCount);
  return resolveNotificationUnreadCount(session.liveUnreadCount, session.panelUnreadCount);
}
