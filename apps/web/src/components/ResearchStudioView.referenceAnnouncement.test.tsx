import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";

vi.mock("next/navigation", () => ({
  useSearchParams: () => new URLSearchParams(),
}));
vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => ({ workspace: { id: "workspace-1" } }),
}));
vi.mock("@/components/LocaleProvider", () => ({
  useLocale: () => ({ locale: "en", t: (key: string) => key }),
}));
vi.mock("@/lib/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api")>()),
  api: {
    listProjects: vi.fn(), listFiles: vi.fn(), listAssets: vi.fn(),
    createResearchGenerationJob: vi.fn(), getGenerationJob: vi.fn(),
    cancelGenerationJob: vi.fn(), getResearchSources: vi.fn(),
    downloadAssetRepresentation: vi.fn(), downloadResearchSourcesExport: vi.fn(),
  },
}));

import { ResearchResultView } from "./ResearchStudioView";
import type { ResearchJobResult, ResearchSource } from "@/lib/api";

// Synthetic, provider-free fixtures. Citation ids are deliberately not in
// alphabetical or numeric order so the assertions prove the view renders the
// server-assigned identifiers verbatim instead of re-keying them in the UI.
function source(citationId: string, title: string): ResearchSource {
  return {
    citationId,
    url: `https://source.example/${citationId.toLowerCase()}`,
    title,
    domain: "source.example",
    publisher: null,
    publishedAt: null,
    retrievedAt: "2026-11-17T00:00:00Z",
    sourceType: "web",
    snippet: null,
    searchQuery: null,
    rank: 0,
    isSelected: false,
  };
}

const RESULT: ResearchJobResult = {
  assetId: "asset-1",
  researchType: "research",
  title: "Redacted report title",
  subtitle: null,
  executiveSummary: "Synthetic summary.",
  keyFindings: [{ type: "text", text: "Synthetic finding.", items: null, rows: null, citationIds: ["S2", "S1"] }],
  sections: [],
  conclusion: "Synthetic conclusion.",
  sourceCount: 2,
  sources: [source("S2", "Second cited page"), source("S1", "First cited page")],
  representations: [{ id: "rep-docx", type: "docx", fileName: "report.docx", contentType: "application/vnd.openxmlformats-officedocument.wordprocessingml.document" }],
};

const identity = (key: string) => key;

function render(overrides: { downloadError?: string; working?: boolean } = {}) {
  return renderToStaticMarkup(
    <ResearchResultView
      result={RESULT}
      working={overrides.working ?? false}
      downloadError={overrides.downloadError ?? ""}
      onDownload={vi.fn()}
      onExport={vi.fn()}
      onCreateAnother={vi.fn()}
      t={identity}
    />,
  );
}

describe("Research Studio result reference announcement", () => {
  it("announces a failed reference or download action to assistive technology", () => {
    const html = render({ downloadError: "research.downloadError" });
    const alert = /<div class="form-error" role="alert">[\s\S]{0,600}?research\.downloadError/.exec(html);
    expect(alert, 'the reference failure message must be exposed with role="alert"').not.toBeNull();
    expect(html).toContain("research.downloadError");
  });

  it("does not keep a stale alert node once the failure message is cleared", () => {
    const html = render();
    expect(html).not.toContain('role="alert"');
    expect(html).not.toContain("research.downloadError");
  });

  it("names the result live region by the report heading instead of re-announcing the whole report", () => {
    const html = render({ downloadError: "research.downloadError" });
    expect(html).toContain('aria-live="polite"');
    expect(html).toContain('aria-labelledby="research-result-title"');
    expect(html).toContain('<h2 id="research-result-title">Redacted report title</h2>');
    expect(html.match(/aria-live="polite"/g) ?? []).toHaveLength(1);
  });

  it("renders the server citation identifiers verbatim in the report body and the evidence rail", () => {
    const html = render();
    // Inline markers keep the order and spelling of the block's citation ids.
    expect(html.indexOf("[S2]")).toBeLessThan(html.indexOf("[S1]"));
    // One inline marker plus the desktop rail and its collapsed mobile copy each
    // keep the identifier verbatim: nothing is renumbered or re-labelled in the UI.
    expect((html.match(/\[S2\]/g) ?? [])).toHaveLength(3);
    expect((html.match(/\[S1\]/g) ?? [])).toHaveLength(3);
    expect(html).toContain("Second cited page");
    expect(html).toContain("First cited page");
  });

  it("keeps the failure message free of provider or stored content", () => {
    const html = render({ downloadError: "research.downloadError" });
    expect(html).not.toMatch(/api[_-]?key|Bearer|s3:|provider/i);
  });
});
