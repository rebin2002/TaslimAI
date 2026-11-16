import type { GlobalSearchGroup } from "./api";

/**
 * Appends the next per-type search page while retaining each group's total
 * count and current hasMore state from the newest response.
 */
export function appendGlobalSearchGroups(existing: GlobalSearchGroup[], incoming: GlobalSearchGroup[]): GlobalSearchGroup[] {
  const merged = new Map(existing.map((group) => [group.type, group]));
  for (const group of incoming) {
    const previous = merged.get(group.type);
    merged.set(group.type, previous ? { ...group, items: [...previous.items, ...group.items] } : group);
  }
  return [...merged.values()];
}
