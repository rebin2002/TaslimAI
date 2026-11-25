import type { GlobalSearchGroup } from "./api";

/**
 * Appends the next per-type search page while retaining each group's total
 * count and current hasMore state from the newest response.
 */
export function appendGlobalSearchGroups(existing: GlobalSearchGroup[], incoming: GlobalSearchGroup[]): GlobalSearchGroup[] {
  const merged = new Map(existing.map((group) => [group.type, group]));
  for (const group of incoming) {
    const previous = merged.get(group.type);
    if (!previous) {
      merged.set(group.type, group);
      continue;
    }
    const items = [...previous.items];
    const indexes = new Map(items.map((item, index) => [item.id, index]));
    for (const item of group.items) {
      const index = indexes.get(item.id);
      if (index === undefined) {
        indexes.set(item.id, items.length);
        items.push(item);
      } else {
        items[index] = item;
      }
    }
    merged.set(group.type, { ...group, items });
  }
  return [...merged.values()];
}
