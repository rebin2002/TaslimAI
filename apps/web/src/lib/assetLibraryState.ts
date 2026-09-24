import type { Asset, AssetList } from "./api";

export type AssetLibraryState = {
  loading: boolean;
  error: string;
  result: AssetList | null;
};

export type AssetLibraryEvent =
  | { type: "loading" }
  | { type: "loaded"; result: AssetList }
  | { type: "failed"; message: string }
  | { type: "updated"; asset: Asset };

export function reduceAssetLibrary(state: AssetLibraryState, event: AssetLibraryEvent): AssetLibraryState {
  if (event.type === "loading") return { ...state, loading: true, error: "" };
  if (event.type === "loaded") return { loading: false, error: "", result: event.result };
  if (event.type === "failed") return { ...state, loading: false, error: event.message };
  if (!state.result) return state;
  return {
    ...state,
    result: {
      ...state.result,
      items: state.result.items.map((item) => item.id === event.asset.id ? event.asset : item),
    },
  };
}

export function safeAssetDisplay(asset: Asset) {
  return {
    id: asset.id,
    name: asset.name,
    description: asset.description,
    assetType: asset.assetType,
    status: asset.status,
    projectName: asset.projectName,
    mimeType: asset.mimeType,
    hasFile: asset.hasFile,
    canPreview: asset.canPreview,
    fileSizeBytes: asset.fileSizeBytes,
    sourceStudio: asset.sourceStudio,
    sourceJobTitle: asset.sourceJobTitle,
    createdAt: asset.createdAt,
  };
}
