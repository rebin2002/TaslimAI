export function resolveNotificationUnreadCount(currentCount: number, nextCount: number | null): number {
  if (nextCount === null || !Number.isFinite(nextCount)) return Math.max(0, currentCount);
  return Math.max(0, Math.trunc(nextCount));
}
