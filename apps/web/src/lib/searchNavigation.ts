import type { GlobalSearchResult } from "./api";

export function assetDestination(assetId: string, status = "Active"): string {
  return `/assets?assetId=${encodeURIComponent(assetId)}&status=${status}`;
}

/**
 * Keeps search-result links tied to the exact resource the user selected.
 * Asset names are not unique, so title-only links can open the wrong asset.
 */
export function globalSearchResultDestination(result: GlobalSearchResult): string {
  if (result.type === "projects") return `/projects/${result.id}`;
  if (result.type === "conversations") return `/chat/${result.id}`;
  if (result.type === "assets") return assetDestination(result.id, result.status ?? "Active");
  if (result.type === "files") {
    if (result.projectId) return `/projects/${result.projectId}`;
    if (result.conversationId) return `/chat/${result.conversationId}`;
    return "/create/document";
  }
  if (result.assetId) return assetDestination(result.assetId);
  if (result.projectId) return `/projects/${result.projectId}`;
  return "/notifications";
}
