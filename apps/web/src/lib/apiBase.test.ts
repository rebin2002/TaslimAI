import { describe, expect, it } from "vitest";
import { API_URL, assetFileUrl, assetRepresentationUrl } from "./apiBase";

describe("asset download URL construction", () => {
  it("keeps ordinary opaque identifiers byte-for-byte in the path", () => {
    expect(assetFileUrl("5f4dcc3b5aa765d61d8327deb882cf99")).toBe(
      `${API_URL}/api/assets/5f4dcc3b5aa765d61d8327deb882cf99/download`,
    );
    expect(assetRepresentationUrl("asset-1", "rep-1")).toBe(
      `${API_URL}/api/assets/asset-1/representations/rep-1/download`,
    );
  });

  it("preserves the inline query flag", () => {
    expect(assetFileUrl("asset-1", true)).toBe(`${API_URL}/api/assets/asset-1/download?inline=true`);
    expect(assetFileUrl("asset-1", false)).toBe(`${API_URL}/api/assets/asset-1/download`);
  });

  it("encodes path, query and fragment characters so an opaque id cannot split the segment", () => {
    expect(assetFileUrl("a/b")).toBe(`${API_URL}/api/assets/a%2Fb/download`);
    expect(assetFileUrl("a?b")).toBe(`${API_URL}/api/assets/a%3Fb/download`);
    expect(assetFileUrl("a#b")).toBe(`${API_URL}/api/assets/a%23b/download`);
    expect(assetFileUrl("a b", true)).toBe(`${API_URL}/api/assets/a%20b/download?inline=true`);
    expect(assetRepresentationUrl("a/b", "c?d")).toBe(
      `${API_URL}/api/assets/a%2Fb/representations/c%3Fd/download`,
    );
  });

  it("keeps the whole identifier inside one path segment", () => {
    for (const id of ["a/b", "a?b", "a#b", "a%b", "a&b", "a=b", "a+b"]) {
      const url = assetFileUrl(id, true);
      const [path, query] = url.split("?");
      // The identifier never contributes extra path segments or a second query.
      expect(path).toBe(`${API_URL}/api/assets/${encodeURIComponent(id)}/download`);
      expect(path).not.toContain("#");
      expect(query).toBe("inline=true");
    }
  });
});
