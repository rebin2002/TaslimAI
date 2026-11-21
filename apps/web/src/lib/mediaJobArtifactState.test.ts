import { describe, expect, it } from "vitest";
import { findMediaJobArtifact, mediaJobArtifactDownloadState } from "./mediaJobArtifactState";

// Synthetic rows only: the studio list is the sole authority the client has, so
// these fixtures pin the flag combinations it can actually report.
function artifact(id: string, hasFile: boolean) {
  return { id, hasFile };
}

describe("mediaJobArtifactState", () => {
  it("enables the download while the artifact's stored file is present", () => {
    expect(mediaJobArtifactDownloadState(artifact("asset-1", true))).toEqual({
      downloadable: true,
      reason: "available",
    });
  });

  it("disables the download when the stored file is gone, i.e. missing or expired", () => {
    expect(mediaJobArtifactDownloadState(artifact("asset-1", false))).toEqual({
      downloadable: false,
      reason: "missing",
    });
  });

  it("keeps the download enabled while the asset list has not resolved the artifact", () => {
    const unresolved = mediaJobArtifactDownloadState(null);
    expect(unresolved.downloadable).toBe(true);
    expect(unresolved.reason).toBe("unverified");
    expect(mediaJobArtifactDownloadState(undefined)).toEqual(unresolved);
  });

  it("resolves a job result asset against the loaded rows", () => {
    const rows = [artifact("asset-1", true), artifact("asset-2", false)];
    expect(findMediaJobArtifact(rows, "asset-2")).toEqual(artifact("asset-2", false));
    expect(mediaJobArtifactDownloadState(findMediaJobArtifact(rows, "asset-2")).downloadable).toBe(false);
  });

  it("reports an unknown, blank or whitespace result identity as unresolved", () => {
    const rows = [artifact("asset-1", false)];
    expect(findMediaJobArtifact(rows, "asset-999")).toBeNull();
    expect(findMediaJobArtifact(rows, "")).toBeNull();
    expect(findMediaJobArtifact(rows, "   ")).toBeNull();
    expect(findMediaJobArtifact(rows, null)).toBeNull();
    expect(findMediaJobArtifact(rows, undefined)).toBeNull();
    expect(findMediaJobArtifact([], "asset-1")).toBeNull();
  });

  it("trims a padded result identity so a known artifact is still found", () => {
    expect(findMediaJobArtifact([artifact("asset-1", false)], " asset-1 ")).toEqual(artifact("asset-1", false));
  });
});
