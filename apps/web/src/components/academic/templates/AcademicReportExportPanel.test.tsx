import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import type { AcademicReportSummary } from "@/lib/academicExportApi";
import {
  AcademicReportExportView,
  exportErrorCopyKey,
  formatSavedDate,
  readExportMemory,
  resolveExportStatusKind,
  writeExportMemory,
} from "./AcademicReportExportPanel";
import { academicExportCopy } from "./academicExportCopy";
import type { AcademicTemplateConfiguration } from "./academicTemplateTypes";

const summary: AcademicReportSummary = {
  jobId: "11111111-2222-3333-4444-555555555555",
  title: "Market access in the Kurdistan Region",
  subtitle: null,
  language: "en",
  savedAtUtc: "2026-10-31T10:00:00Z",
  sectionCount: 5,
  sourceCount: 9,
  accessedSourceCount: 6,
  metadataOnlySourceCount: 3,
  sourceContentHash: "hash-not-shown-to-students",
  sources: [],
};

const configuration = {
  configurationId: "classic-thesis.times.150.classic.center",
  templateSlug: "classic-thesis",
  templateId: "classic-thesis",
  displayName: "Classic Thesis",
  variantSelection: {},
  designHash: "abc123",
  spec: {},
} as unknown as AcademicTemplateConfiguration;

function viewProps(overrides: Partial<Parameters<typeof AcademicReportExportView>[0]> = {}) {
  return {
    locale: "en" as const,
    templateCount: 230,
    reports: [{ jobId: summary.jobId, title: summary.title, savedAt: summary.savedAtUtc }],
    loadingReports: false,
    reportsError: false,
    selectedJobId: summary.jobId,
    summary,
    loadingSummary: false,
    configuration: null,
    disclosureText: "",
    busy: false,
    status: null,
    onSelectReport: () => {},
    onDisclosureChange: () => {},
    onConfigurationSelected: () => {},
    onExport: () => {},
    ...overrides,
  };
}

describe("AcademicReportExportView", () => {
  it("leads with the honest framing: saved report, template design, no new research", () => {
    const html = renderToStaticMarkup(<AcademicReportExportView {...viewProps({ summary: null, configuration: null })} />);
    expect(html).toContain(academicExportCopy("en", "panelTitle"));
    expect(html).toContain("230 layout designs");
    expect(html).toContain("no new research is started");
    expect(html).toContain('id="academic-export-report"');
  });

  it("shows loading, empty and failure states as live status text", () => {
    const loading = renderToStaticMarkup(<AcademicReportExportView {...viewProps({ loadingReports: true, reports: [], summary: null })} />);
    expect(loading).toContain(academicExportCopy("en", "reportsLoading"));
    const empty = renderToStaticMarkup(<AcademicReportExportView {...viewProps({ reports: [], summary: null })} />);
    expect(empty).toContain(academicExportCopy("en", "reportsEmpty"));
    expect(empty).toContain('role="status"');
    const failed = renderToStaticMarkup(<AcademicReportExportView {...viewProps({ reports: [], reportsError: true, summary: null })} />);
    expect(failed).toContain('role="alert"');
    expect(failed).toContain(academicExportCopy("en", "reportsFailed"));
  });

  it("summarises the saved report and warns about catalogue-only sources", () => {
    const html = renderToStaticMarkup(<AcademicReportExportView {...viewProps()} />);
    expect(html).toContain(summary.title);
    expect(html).toContain("9 sources");
    expect(html).toContain("6 sources with full text reviewed here");
    expect(html).toContain("3 catalogue records only");
    expect(html).toContain("were not read in full");
    // The internal content hash is a server-side integrity value, never student copy.
    expect(html).not.toContain("hash-not-shown-to-students");
  });

  it("keeps the disclosure field visible and flags it when incomplete", () => {
    const html = renderToStaticMarkup(<AcademicReportExportView {...viewProps()} />);
    expect(html).toContain('id="academic-export-disclosure"');
    expect(html).toContain(academicExportCopy("en", "disclosureMissing"));
    const done = renderToStaticMarkup(
      <AcademicReportExportView {...viewProps({ disclosureText: "AI helped outline chapters; the writing is mine." })} />,
    );
    expect(done).toContain("the writing is mine");
    expect(done).not.toContain(academicExportCopy("en", "disclosureMissing"));
  });

  it("gates the download buttons on a selected template design", () => {
    const withoutDesign = renderToStaticMarkup(<AcademicReportExportView {...viewProps()} />);
    expect(withoutDesign).toContain(academicExportCopy("en", "templateNone"));
    expect((withoutDesign.match(/disabled=""/g) ?? []).length).toBeGreaterThanOrEqual(2);
    const withDesign = renderToStaticMarkup(<AcademicReportExportView {...viewProps({ configuration })} />);
    expect(withDesign).toContain("Classic Thesis · classic-thesis.times.150.classic.center");
    expect(withDesign).not.toContain(academicExportCopy("en", "templateNone"));
    const busy = renderToStaticMarkup(<AcademicReportExportView {...viewProps({ configuration, busy: true, status: { kind: "busy", messageKey: "exporting" } })} />);
    expect(busy).toContain(academicExportCopy("en", "exporting"));
    expect((busy.match(/disabled=""/g) ?? []).length).toBeGreaterThanOrEqual(2);
  });

  it("tells first exports and re-exports apart, and never leaks billing vocabulary", () => {
    const first = renderToStaticMarkup(
      <AcademicReportExportView {...viewProps({ configuration, status: { kind: "first", messageKey: "statusFirst" } })} />,
    );
    expect(first).toContain("reformatted with the selected design. No new research was started");
    expect(first).toContain(academicExportCopy("en", "downloadCheck"));
    // The embedded #1265 gallery carries its own privacy note that names the words it
    // forbids; the scan here is for actual billing vocabulary, per export copy tests.
    const html = first.toLowerCase();
    for (const word of ["credit", "usd", "billing", "invoice", "price", "openai", "$"]) {
      expect(html).not.toContain(word);
    }
  });

  it("renders right-to-left copy for ckb without touching English-only internals", () => {
    const html = renderToStaticMarkup(<AcademicReportExportView {...viewProps({ locale: "ckb", summary: null, configuration: null })} />);
    expect(html).toContain(academicExportCopy("ckb", "panelTitle"));
    expect(html).not.toContain(academicExportCopy("en", "panelTitle"));
  });
});

describe("export panel helpers", () => {
  function fakeStore(seed: Record<string, string> = {}) {
    const data = new Map(Object.entries(seed));
    return { getItem: (key: string) => data.get(key) ?? null, setItem: (key: string, value: string) => void data.set(key, value) };
  }

  it("round-trips export memory and ignores corrupt or missing entries", () => {
    const store = fakeStore();
    expect(readExportMemory("job-1", store)).toBeNull();
    writeExportMemory("job-1", { configurationId: "cfg-a", snapshotJson: "{json}" }, store);
    expect(readExportMemory("job-1", store)).toEqual({ configurationId: "cfg-a", snapshotJson: "{json}" });
    expect(readExportMemory("job-2", store)).toBeNull();
    const corrupt = fakeStore({ "taslim.academic-export.memory.job-3": "not json {{{" });
    expect(readExportMemory("job-3", corrupt)).toBeNull();
    expect(readExportMemory("", store)).toBeNull();
    expect(readExportMemory("job-1", null)).toBeNull();
  });

  it("prefers the server-declared mode and derives honestly when it is hidden", () => {
    const previous = { configurationId: "cfg-a", snapshotJson: "s" };
    expect(resolveExportStatusKind("re-export-same-settings", null, "cfg-b")).toBe("same");
    expect(resolveExportStatusKind("template-reformat", previous, "cfg-b")).toBe("reformat");
    expect(resolveExportStatusKind("template-reformat", null, "cfg-b")).toBe("first");
    expect(resolveExportStatusKind(null, null, "cfg-a")).toBe("first");
    expect(resolveExportStatusKind(null, previous, "cfg-a")).toBe("same");
    expect(resolveExportStatusKind(null, previous, "cfg-b")).toBe("reformat");
  });

  it("maps API failure codes to honest copy keys", () => {
    expect(exportErrorCopyKey("ACADEMIC_BIBLIOGRAPHY_INTEGRITY")).toBe("errorIntegrity");
    expect(exportErrorCopyKey("ACADEMIC_EXPORT_DIRECTION_MISMATCH")).toBe("errorDirection");
    expect(exportErrorCopyKey("ACADEMIC_EXPORT_NOT_FOUND")).toBe("errorNotFound");
    // The export POST is rate bounded, so the 429 code must not be reported as a generic failure.
    expect(exportErrorCopyKey("RATE_LIMITED")).toBe("errorRateLimited");
    expect(exportErrorCopyKey("ANYTHING_ELSE")).toBe("errorGeneric");
  });

  it("formats saved dates safely", () => {
    expect(formatSavedDate(null, "en")).toBeNull();
    expect(formatSavedDate("garbage", "en")).toBeNull();
    expect(formatSavedDate("2026-10-31T10:00:00Z", "en")).toMatch(/2026/);
  });
});
