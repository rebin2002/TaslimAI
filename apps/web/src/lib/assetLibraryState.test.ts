import { describe, expect, it } from "vitest";
import type { Asset, AssetList } from "./api";
import { reduceAssetLibrary, safeAssetDisplay, type AssetLibraryState } from "./assetLibraryState";

const asset: Asset = {
  id: "asset-1", workspaceId: "workspace-1", projectId: "project-1", projectName: "Launch",
  name: "Campaign result", description: "Reusable output", assetType: "file", mimeType: "application/json",
  status: "Active", hasFile: true, canPreview: false, fileSizeBytes: 120, sourceStudio: "system", sourceJobTitle: "Test", createdAt: "2026-09-22T00:00:00Z",
  updatedAt: "2026-09-22T00:00:00Z", archivedAt: null, representations: [],
};
const result: AssetList = { items: [asset], page: 1, pageSize: 12, totalCount: 1, totalPages: 1 };
const initial: AssetLibraryState = { loading: false, error: "", result: null };

describe("Asset Library state", () => {
  it("handles loading, loaded, empty, and safe failures", () => {
    expect(reduceAssetLibrary(initial, { type: "loading" })).toEqual({ loading: true, error: "", result: null });
    expect(reduceAssetLibrary(initial, { type: "loaded", result }).result?.items).toEqual([asset]);
    expect(reduceAssetLibrary(initial, { type: "loaded", result: { ...result, items: [], totalCount: 0, totalPages: 0 } }).result?.items).toEqual([]);
    expect(reduceAssetLibrary(initial, { type: "failed", message: "Could not load assets" }).error).toBe("Could not load assets");
  });

  it("applies rename, project movement, archive, and restore responses", () => {
    let state = reduceAssetLibrary(initial, { type: "loaded", result });
    state = reduceAssetLibrary(state, { type: "updated", asset: { ...asset, name: "Renamed", projectId: null, projectName: null } });
    expect(state.result?.items[0]).toMatchObject({ name: "Renamed", projectId: null });
    state = reduceAssetLibrary(state, { type: "updated", asset: { ...asset, status: "Archived", archivedAt: "2026-09-22T01:00:00Z" } });
    expect(state.result?.items[0].status).toBe("Archived");
    state = reduceAssetLibrary(state, { type: "updated", asset });
    expect(state.result?.items[0].status).toBe("Active");
  });

  it("keeps provider, storage key, model, and source internals out of display data", () => {
    const display = safeAssetDisplay(asset) as Record<string, unknown>;
    expect(display.name).toBe("Campaign result");
    expect(display).not.toHaveProperty("storageProvider");
    expect(display).not.toHaveProperty("storageKey");
    expect(display).not.toHaveProperty("provider");
    expect(display).not.toHaveProperty("model");
    expect(display).not.toHaveProperty("sourceGenerationJobId");
  });
});
