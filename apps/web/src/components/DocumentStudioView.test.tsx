import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import { DocumentPreviewTable, DocumentProgressMeter, DocumentStudioView } from "./DocumentStudioView";

vi.mock("next/navigation", () => ({
  useSearchParams: () => new URLSearchParams(),
}));

vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => ({ workspace: { id: "workspace-1" } }),
}));

vi.mock("@/components/LocaleProvider", () => ({
  useLocale: () => ({ locale: "en", t: (key: string) => key }),
}));

vi.mock("@/lib/api", () => ({
  api: {
    listProjects: vi.fn(),
    listFiles: vi.fn(),
    listAssets: vi.fn(),
    createDocumentGenerationJob: vi.fn(),
    getGenerationJob: vi.fn(),
    cancelGenerationJob: vi.fn(),
    downloadAssetRepresentation: vi.fn(),
  },
}));

describe("DocumentStudioView", () => {
  it("exposes an announced, bounded progress meter for assistive technology", () => {
    const html = renderToStaticMarkup(<DocumentProgressMeter progress={140} label="Progress" announcement="Report: running. 100% complete." />);

    expect(html).toContain('role="status"');
    expect(html).toContain('role="progressbar"');
    expect(html).toContain('aria-valuenow="100"');
    expect(html).toContain('aria-valuetext="100%"');
    expect(html).toContain("Report: running. 100% complete.");
  });

  it("exposes generated table previews with row and cell semantics", () => {
    const html = renderToStaticMarkup(<DocumentPreviewTable rows={[{ cells: ["Metric", "Value"] }, { cells: ["Revenue", "$10,000"] }]} label="Document preview" />);

    expect(html).toContain('role="table"');
    expect(html).toContain('aria-label="Document preview"');
    expect(html).toContain('role="row"');
    expect(html).toContain('role="cell"');
    expect(html).toContain("$10,000");
  });

  it("renders a compact guided compose workspace without provider details", () => {
    const html = renderToStaticMarkup(<DocumentStudioView />);

    expect(html).toContain("document-compose-layout");
    expect(html).toContain("document-field-primary");
    expect(html).toContain("document-advanced");
    expect(html).toContain("document-recent-card");
    expect(html).toContain('aria-labelledby="document-create-title"');
    expect(html).toContain('aria-describedby="document-brief-count"');
    expect(html).toContain('aria-labelledby="document-sources-title"');
    expect(html).toContain('id="document-project"');
    expect(html).toContain("document.type");
    expect(html).toContain("document.language");
    expect(html).not.toContain("provider");
    expect(html).not.toContain("model");
    expect(html).not.toContain("resultJson");
  });

  it("keeps the empty source and recent-document states explicit", () => {
    const html = renderToStaticMarkup(<DocumentStudioView />);

    expect(html).toContain("document.loadingSources");
    expect(html).toContain("document.loading");
    expect(html).toContain("document.sources");
    expect(html).toContain("document.recentTitle");
    expect(html).toContain('aria-live="polite"');
  });
});
