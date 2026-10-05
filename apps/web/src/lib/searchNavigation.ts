import type { GlobalSearchResult } from "./api";

/**
 * Keeps search-result links tied to the exact resource the user selected.
 * Asset names are not unique, so title-only links can open the wrong asset.
 */
export function globalSearchResultDestination(result: GlobalSearchResult): string {
  if (result.type === "projects") return `/projects/${result.id}`;
  if (result.type === "conversations") return `/chat/${result.id}`;
  if (result.type === "assets") return `/assets?assetId=${encodeURIComponent(result.id)}&status=${result.status ?? "Active"}`;
  if (result.type === "files") {
    if (result.projectId) return `/projects/${result.projectId}`;
    if (result.conversationId) return `/chat/${result.conversationId}`;
    return "/create/document";
  }
  if (result.assetId) return `/assets?assetId=${encodeURIComponent(result.assetId)}&status=Active`;
  if (result.projectId) return `/projects/${result.projectId}`;
  return "/notifications";
}
