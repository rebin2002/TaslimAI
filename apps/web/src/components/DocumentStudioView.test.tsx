import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import { DocumentStudioView } from "./DocumentStudioView";

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
  it("renders a compact guided compose workspace without provider details", () => {
    const html = renderToStaticMarkup(<DocumentStudioView />);

    expect(html).toContain("document-compose-layout");
    expect(html).toContain("document-field-primary");
    expect(html).toContain("document-advanced");
    expect(html).toContain("document-recent-card");
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
  });
});
