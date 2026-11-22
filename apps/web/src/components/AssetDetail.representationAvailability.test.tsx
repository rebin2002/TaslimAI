import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import type { Asset } from "../lib/api";
import { assetRepresentationUrl } from "../lib/apiBase";
import { AssetDetail, type AssetDetailLabels } from "./AssetDetail";

// Issue #1030: the asset-level fallback from PR #1004 fixed `hasFile`; the per-representation row
// still rendered a download anchor whenever the representation record existed, even though the
// download route dead-ends in `409 ASSET_FILE_UNAVAILABLE` for a pruned or processing stored file.
// The read model now carries `canDownload` per representation; the detail view must withhold the
// anchor for a false flag while keeping the format's identity and metadata, and keep serving the
// anchor for `true` — and for older payloads that carry no flag at all (backward compatibility).
// Deterministic static markup: no jsdom, no network, no provider.
const labels: AssetDetailLabels = {
  detailEyebrow: "Asset detail", close: "Close", structuredTitle: "Structured asset",
  structuredDescription: "Structured output", previewLabel: "Preview", summaryLabel: "Summary",
  download: "Download", open: "Open", edit: "Edit", type: "Type", typeValue: "Document",
  created: "Created", project: "Project", noProject: "Workspace", file: "File",
  notAvailable: "Not available", source: "Source", manual: "Manual upload",
  description: "Description", representations: "Representations", studios: { document: "Document Studio" },
};
const readyRepresentation = {
  id: "a1000000-0000-4000-8000-000000000001", representationType: "pdf", fileName: "report.pdf",
  contentType: "application/pdf", sizeBytes: 4096, createdAt: "2026-11-17T00:00:00Z", canDownload: true,
};
const unavailableRepresentation = { ...readyRepresentation, id: "a1000000-0000-4000-8000-000000000002", fileName: "stale.pdf", canDownload: false };
const legacyRepresentation = { ...readyRepresentation, id: "a1000000-0000-4000-8000-000000000003", fileName: "legacy.pdf" };
delete (legacyRepresentation as Partial<typeof readyRepresentation>).canDownload;
const documentAsset: Asset = {
  id: "b2000000-0000-4000-8000-000000000000", workspaceId: "workspace-1", projectId: null, projectName: null,
  name: "Quarterly report", description: null, assetType: "document",
  mimeType: "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
  status: "Active", hasFile: true, canPreview: false, fileSizeBytes: 2048,
  sourceStudio: "document", sourceJobTitle: null,
  createdAt: "2026-11-17T00:00:00Z", updatedAt: "2026-11-17T00:00:00Z", archivedAt: null,
  representations: [readyRepresentation],
};
function render(asset: Asset) {
  return renderToStaticMarkup(<AssetDetail asset={asset} locale="en" labels={labels} onClose={vi.fn()} onEdit={vi.fn()} />);
}
describe("AssetDetail representation download availability (#1030)", () => {
  it("keeps the download anchor for an advertised representation", () => {
    const html = render(documentAsset);
    expect(html).toContain(`href="${assetRepresentationUrl(documentAsset.id, readyRepresentation.id)}"`);
  });
  it("withholds the 409-bound anchor but keeps identity and metadata for an unavailable representation", () => {
    const html = render({ ...documentAsset, representations: [unavailableRepresentation] });
    expect(html).not.toContain(assetRepresentationUrl(documentAsset.id, unavailableRepresentation.id));
    // The unavailable row itself is not an anchor (the asset's own download/open anchors remain).
    expect(html).not.toContain('class="asset-representation" href');
    expect(html).not.toContain('<a class="asset-representation"');
    // The row itself survives: the format listing stays informative (backward-compatible identity).
    expect(html).toContain("stale.pdf");
    expect(html).toContain("PDF");
    expect(html).toContain("4.0 KB");
    expect(html).toContain("Not available");
    // Panel and the asset's own affordances stay usable.
    expect(html).toContain('role="dialog"');
    expect(html).toContain("Quarterly report");
  });
  it("hides only the unavailable row when a ready sibling remains advertised", () => {
    const html = render({ ...documentAsset, representations: [readyRepresentation, unavailableRepresentation] });
    expect(html).toContain(`href="${assetRepresentationUrl(documentAsset.id, readyRepresentation.id)}"`);
    expect(html).not.toContain(assetRepresentationUrl(documentAsset.id, unavailableRepresentation.id));
  });
  it("treats a payload without the flag as advertised (older API backward compatibility)", () => {
    const html = render({ ...documentAsset, representations: [legacyRepresentation] });
    expect(html).toContain(`href="${assetRepresentationUrl(documentAsset.id, legacyRepresentation.id)}"`);
  });
});
