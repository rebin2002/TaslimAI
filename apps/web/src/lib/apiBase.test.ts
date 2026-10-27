import { describe, expect, it } from "vitest";
import { API_URL, assetFileUrl, assetRepresentationUrl } from "./apiBase";

describe("asset URL helpers", () => {
  it("keeps opaque asset identifiers inside one URL path segment", () => {
    const assetId = "asset/with?query#fragment";
    const encodedAssetId = encodeURIComponent(assetId);

    expect(assetFileUrl(assetId)).toBe(
      `${API_URL}/api/assets/${encodedAssetId}/download`,
    );
    expect(assetFileUrl(assetId, true)).toBe(
      `${API_URL}/api/assets/${encodedAssetId}/download?inline=true`,
    );
  });

  it("keeps opaque representation identifiers inside one URL path segment", () => {
    const assetId = "asset-id";
    const representationId = "representation/with?query#fragment";

    expect(assetRepresentationUrl(assetId, representationId)).toBe(
      `${API_URL}/api/assets/${encodeURIComponent(assetId)}/representations/${encodeURIComponent(representationId)}/download`,
    );
  });
});
