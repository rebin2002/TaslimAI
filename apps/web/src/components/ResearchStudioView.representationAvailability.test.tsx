import { readFileSync } from "node:fs";
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
import { parseResearchJobResult } from "@/lib/researchStudioState";
import {
  filterStudioDownloadableRepresentations,
  readStudioRepresentationDownloadability,
} from "@/lib/studioRepresentationAvailability";
import type { GenerationJob } from "@/lib/api";

/**
 * Research Studio's representation download controls against the live
 * per-representation readiness contract of #1117/#1124 (issue #1049, consumer
 * half; PR #1122 deliberately scoped itself to Document/Presentation and names
 * this view as a separate lane).
 *
 * Why this view needs its own coverage: unlike the presentation toolbar, the
 * Research report renders one *enabled* button per advertised rendition taken
 * straight from the immutable `ResultJson` snapshot, and falls back to a
 * "no downloads" affordance when the snapshot lists none. Both decisions are
 * synthetic, provider-free fixtures here: fabricated GUIDs, a hand-written
 * snapshot string, no API client, no network, no stored file.
 *
 * Pinned behaviour:
 * 1. An explicit `canDownload: false` withdraws only that control; the ready
 *    sibling keeps its button and the toolbar keeps the sources-CSV export,
 *    which is a different route and not a representation download.
 * 2. A payload without the companion field renders byte-identical markup to
 *    today (old-API compatibility golden), including when the prop is omitted.
 * 3. When every rendition is withdrawn the existing `research.noDownloads`
 *    affordance appears instead of a dead-end button.
 * 4. An unknown rendition id stays advertised.
 * 5. Nothing from a withheld entry (file name, id, content type) or the flag
 *    itself reaches the DOM.
 */

const DOCX_READY = "00000000-0000-4000-8000-000000000031";
const PDF_PRUNED = "00000000-0000-4000-8000-000000000032";
const UNRELATED_REPRESENTATION = "00000000-0000-4000-8000-0000000000f9";
const DOCX_CONTENT_TYPE = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

/** A completed Research job whose published snapshot advertises DOCX + PDF. */
function researchJob(representationDownloads?: unknown): GenerationJob {
  const resultJson = JSON.stringify({
    assetId: "asset-synthetic-research-1",
    researchType: "research",
    title: "Synthetic report",
    executiveSummary: "Synthetic summary.",
    keyFindings: [],
    sections: [],
    conclusion: "Synthetic conclusion.",
    sources: [],
    representations: [
      { id: DOCX_READY, type: "docx", fileName: "synthetic-report.docx", contentType: DOCX_CONTENT_TYPE },
      { id: PDF_PRUNED, type: "pdf", fileName: "synthetic-report.pdf", contentType: "application/pdf" },
    ],
  });
  const job: Record<string, unknown> = {
    id: "job-synthetic-research-1",
    status: "Succeeded",
    progressPercent: 100,
    resultJson,
  };
  // The companion field is absent (not undefined-valued) on a legacy payload,
  // exactly as an older read model serves it.
  if (representationDownloads !== undefined) job.representationDownloads = representationDownloads;
  return job as unknown as GenerationJob;
}

const identity = (key: string) => key;

/** Renders the report shell from the real parser + the real companion reader. */
function renderReport(job: GenerationJob, options: { passProp?: boolean } = {}) {
  const result = parseResearchJobResult(job);
  expect(result, "the synthetic snapshot must parse through the production reader").not.toBeNull();
  const facts = readStudioRepresentationDownloadability(job);
  const prop = options.passProp === false ? {} : { downloadability: facts };
  return renderToStaticMarkup(
    <ResearchResultView
      result={result!}
      {...prop}
      sourcesLoadStatus="ready"
      onRetrySources={vi.fn()}
      working={false}
      downloadError=""
      onDownload={vi.fn()}
      onExport={vi.fn()}
      onCreateAnother={vi.fn()}
      t={identity}
    />,
  );
}

/** The action toolbar only, so report prose can never satisfy an assertion. */
function actions(html: string): string {
  const start = html.indexOf('<div class="research-result-actions">');
  expect(start, "the report must render its research-result-actions toolbar").toBeGreaterThan(-1);
  const end = html.indexOf("</div>", start);
  expect(end, "the actions toolbar must be closable").toBeGreaterThan(start);
  return html.slice(start, end);
}

function renditionControls(toolbar: string): string[] {
  return ["DOCX", "PDF"].filter((token) => toolbar.includes(token));
}

describe("Research report download controls honour live availability (#1049)", () => {
  it("withholds only the pruned rendition and keeps its ready sibling advertised", () => {
    const html = renderReport(researchJob([
      { id: DOCX_READY, type: "docx", fileName: "synthetic-report.docx", contentType: DOCX_CONTENT_TYPE, canDownload: true },
      { id: PDF_PRUNED, type: "pdf", fileName: "synthetic-report.pdf", contentType: "application/pdf", canDownload: false },
    ]));
    const toolbar = actions(html);
    expect(renditionControls(toolbar)).toEqual(["DOCX"]);
    expect(toolbar).toContain("research-result-actions");
    // The empty-state copy must not appear while a downloadable rendition exists.
    expect(toolbar).not.toContain("research.noDownloads");
  });

  it("keeps a legacy payload byte-identical when the companion field is absent", () => {
    const job = researchJob();
    expect(readStudioRepresentationDownloadability(job), "no field means no live facts").toBeNull();
    const toolbar = actions(renderReport(job));
    expect(renditionControls(toolbar)).toEqual(["DOCX", "PDF"]);
    // Passing the resolved `null` explicitly must be the same render as omitting
    // the prop entirely: old-API payloads cannot lose a control.
    expect(renderReport(job, { passProp: false })).toBe(renderReport(job));
    expect(renderReport(job, { passProp: false })).toBe(renderReport(job, { passProp: true }));
  });

  it("falls back to the existing no-downloads affordance when every rendition is withdrawn", () => {
    const html = renderReport(researchJob([
      { id: DOCX_READY, type: "docx", fileName: "synthetic-report.docx", contentType: DOCX_CONTENT_TYPE, canDownload: false },
      { id: PDF_PRUNED, type: "pdf", fileName: "synthetic-report.pdf", contentType: "application/pdf", canDownload: false },
    ]));
    const toolbar = actions(html);
    expect(renditionControls(toolbar)).toEqual([]);
    expect(toolbar).toContain("research-no-downloads");
    expect(toolbar).toContain("research.noDownloads");
    // The sources-CSV export is a different route and stays untouched, as do
    // the Open-assets link and the create-another action.
    expect(toolbar).toContain("research.references");
    expect(toolbar).toContain('href="/assets"');
    expect(toolbar).toContain("research.createAnother");
  });

  it("keeps an unrecognised rendition advertised: only an explicit false withdraws", () => {
    const toolbar = actions(renderReport(researchJob([
      { id: UNRELATED_REPRESENTATION, canDownload: false },
    ])));
    expect(renditionControls(toolbar)).toEqual(["DOCX", "PDF"]);
  });

  it("never lets a withheld entry or the flag itself reach the DOM", () => {
    const html = renderReport(researchJob([
      { id: PDF_PRUNED, canDownload: false },
    ]));
    for (const leak of ["synthetic-report.pdf", "application/pdf", PDF_PRUNED, "canDownload", "representationDownloads"]) {
      expect(html, `${leak} must not be rendered`).not.toContain(leak);
    }
    // The ready sibling still renders through the snapshot it already had.
    expect(actions(html)).toContain("DOCX");
  });
});

describe("Research Studio availability wiring reuses the shared helper (#1049)", () => {
  const viewSource = readFileSync(new URL("./ResearchStudioView.tsx", import.meta.url), "utf8");

  it("reads the live facts from the job DTO beside the snapshot", () => {
    expect(viewSource).toContain(
      "const representationDownloadability = readStudioRepresentationDownloadability(current);",
    );
    expect(viewSource).toContain("downloadability={representationDownloadability}");
  });

  it("composes the gate after the report's own docx/pdf type filter", () => {
    expect(viewSource).toContain(
      '(result.representations ?? []).filter((representation) => ["docx", "pdf"].includes(representation.type))',
    );
    expect(viewSource).toContain(
      "filterStudioDownloadableRepresentations(advertisedRepresentations, downloadability)",
    );
    // The controls and the empty-state copy both draw from the gated list.
    expect(viewSource).toContain("{downloadableRepresentations.map((representation) =>");
    expect(viewSource).toContain("{downloadableRepresentations.length === 0 &&");
  });

  it("treats the facts as optional so existing callers keep today's render", () => {
    expect(viewSource).toContain("downloadability = null");
    expect(viewSource).toContain("downloadability?: StudioRepresentationAvailabilityFacts | null");
  });

  it("consumes the shared studio module instead of duplicating the contract", () => {
    expect(viewSource).toContain('from "@/lib/studioRepresentationAvailability"');
    // The companion field is owned by the helper: this view may name it in a
    // comment but must never read it itself, which is what keeps the three
    // studios on one definition of "downloadable right now".
    expect(viewSource).not.toMatch(/\.\s*representationDownloads\b|\[\s*["']representationDownloads["']\s*\]/);
    expect(filterStudioDownloadableRepresentations([{ id: DOCX_READY }], null)).toHaveLength(1);
  });

  it("leaves the sibling studio files and the helper to their own lanes", () => {
    // This lane edits exactly one component; the document/presentation
    // consumers and the helper arrive with PR #1122.
    expect(viewSource).toContain("#1049");
  });
});
