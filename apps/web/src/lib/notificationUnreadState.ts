export function resolveNotificationUnreadCount(currentCount: number, nextCount: number | null): number {
  if (nextCount === null || !Number.isFinite(nextCount)) return Math.max(0, currentCount);
  return Math.max(0, Math.trunc(nextCount));
}

/**
 * Decides what a failed unread-count poll does to the badge.
 *
 * A network blip or a server-side hiccup (5xx, 429) is transient: the last
 * observed count stays displayed so the badge does not flicker away. A
 * definitive 401 or 403 is not transient - the server has stated that inbox
 * access ended (the session expired or the workspace membership was revoked).
 * The read-receipt aggregate then fails closed to zero instead of pinning a
 * membership-scoped count for a principal that can no longer read it.
 */
export function resolveNotificationUnreadCountAfterPollFailure(currentCount: number, error: unknown): number {
  const status = error instanceof Object && "status" in error ? (error as { status: unknown }).status : undefined;
  if (status === 401 || status === 403) return 0;
  return Number.isFinite(currentCount) ? Math.max(0, Math.trunc(currentCount)) : 0;
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
