import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import type { Asset } from "../lib/api";
import { AssetCard, type AssetCardLabels } from "./AssetCard";

const labels: AssetCardLabels = { project: "Project", workspace: "Workspace asset", rename: "Edit", archive: "Archive", restore: "Restore", download: "Download", open: "Open", type: "File", fileUnavailable: "No file" };
const asset: Asset = {
  id: "3ca880c9-a8e3-4534-8e78-a39d2e3aed74", workspaceId: "workspace-1", projectId: "project-1", projectName: "Launch",
  name: "Campaign result", description: "Reusable output", assetType: "file", mimeType: "application/json",
  status: "Active", hasFile: true, canPreview: false, fileSizeBytes: 120, sourceStudio: "system", sourceJobTitle: "Test", createdAt: "2026-09-22T00:00:00Z",
  updatedAt: "2026-09-22T00:00:00Z", archivedAt: null, representations: [],
};

describe("AssetCard", () => {
  it("renders user-facing metadata and authenticated asset actions without provider internals", () => {
    const html = renderToStaticMarkup(<AssetCard asset={asset} labels={labels} locale="en" onOpen={vi.fn()} onEdit={vi.fn()} onArchive={vi.fn()} onRestore={vi.fn()} />);
    expect(html).toContain("Campaign result");
    expect(html).toContain("Reusable output");
    expect(html).toContain("Launch");
    expect(html).toContain("Download");
    expect(html).toContain("Archive");
    expect(html).toContain(`/api/assets/${asset.id}/download`);
    expect(html).not.toContain("storageProvider");
    expect(html).not.toContain("storageKey");
    expect(html).not.toContain("sourceGenerationJobId");
    expect(html).not.toContain("model");
  });

  it("shows restore instead of archive for archived assets", () => {
    const html = renderToStaticMarkup(<AssetCard asset={{ ...asset, status: "Archived", archivedAt: "2026-09-22T01:00:00Z" }} labels={labels} locale="en" onOpen={vi.fn()} onEdit={vi.fn()} onArchive={vi.fn()} onRestore={vi.fn()} />);
    expect(html).toContain("Restore");
    expect(html).not.toContain(">Archive</button>");
  });
});
