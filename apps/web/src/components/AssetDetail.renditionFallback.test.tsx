import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import type { Asset } from "../lib/api";
import { assetFileUrl } from "../lib/apiBase";
import { AssetDetail, type AssetDetailLabels } from "./AssetDetail";

// The detail view is the Assets library surface a customer reaches for a rendition that has since
// expired or been pruned. Its media element and its file actions are driven purely by
// `hasFile`/`canPreview`, so it must fall back rather than paint an image that will never load and
// link at a download that dead-ends. Deterministic static markup: no jsdom, no network, no provider.
const labels: AssetDetailLabels = {
  detailEyebrow: "Asset detail", close: "Close", structuredTitle: "Structured asset",
  structuredDescription: "Structured output", previewLabel: "Preview", summaryLabel: "Summary",
  download: "Download", open: "Open", edit: "Edit", type: "Type", typeValue: "Image",
  created: "Created", project: "Project", noProject: "Workspace", file: "File",
  notAvailable: "Not available", source: "Source", manual: "Manual upload",
  description: "Description", representations: "Representations", studios: { image: "Image Studio" },
};

const imageAsset: Asset = {
  id: "9f1c4b2e-6d3a-4f57-8b1e-2c7d9a0e4b31", workspaceId: "workspace-1", projectId: null, projectName: null,
  name: "Poster", description: null, assetType: "image", mimeType: "image/png",
  status: "Active", hasFile: true, canPreview: true, fileSizeBytes: 2048,
  sourceStudio: "image", sourceJobTitle: null,
  createdAt: "2026-11-17T00:00:00Z", updatedAt: "2026-11-17T00:00:00Z", archivedAt: null, representations: [],
};

function render(asset: Asset) {
  return renderToStaticMarkup(<AssetDetail asset={asset} locale="en" labels={labels} onClose={vi.fn()} onEdit={vi.fn()} />);
}

describe("AssetDetail rendition availability", () => {
  it("shows the inline preview and both file actions while the rendition is available", () => {
    const html = render(imageAsset);
    expect(html).toContain(`src="${assetFileUrl(imageAsset.id, true)}"`);
    expect(html).toContain(`href="${assetFileUrl(imageAsset.id)}"`);
    expect(html).toContain(" Download");
    expect(html).toContain(" Open");
    expect(html).toContain("2.0 KB");
  });

  it("withholds the preview and the file actions once the stored rendition is gone", () => {
    const html = render({ ...imageAsset, hasFile: false, canPreview: false, fileSizeBytes: null });
    expect(html).not.toContain(assetFileUrl(imageAsset.id));
    expect(html).not.toContain("<video");
    expect(html).not.toContain("<audio");
    expect(html).not.toContain(" Download");
    expect(html).not.toContain(" Open");
    expect(html).toContain("Not available");
    // The panel stays usable: the dialog identity and the metadata edit action survive.
    expect(html).toContain('role="dialog"');
    expect(html).toContain("Poster");
    expect(html).toContain(" Edit");
  });

  it("does not offer a dead preview when the stored file cannot be previewed", () => {
    const html = render({ ...imageAsset, canPreview: false });
    expect(html).not.toContain(`src="${assetFileUrl(imageAsset.id, true)}"`);
    // Downloading the original is still correct while the stored file exists.
    expect(html).toContain(`href="${assetFileUrl(imageAsset.id)}"`);
  });
});
