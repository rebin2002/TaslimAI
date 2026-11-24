"use client";

/**
 * Issue #1267 — the connection between the merged template library (#1265) and
 * completed, saved research reports.
 *
 * `AcademicReportExportPanel` is the client container: it lists the workspace's
 * completed research reports, loads the honest summary for the selected one,
 * embeds the #1265 gallery (whose previews are synthetic by design), and exports
 * the existing saved payload through `academicExportApi`. It never sends text to
 * be regenerated: the export endpoints hold no generation dependency at all, and
 * nothing here creates a job.
 *
 * `AcademicReportExportView` is the presentational half — every string comes from
 * `academicExportCopy`, and the pure helpers below are unit-tested with it.
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import {
  AcademicExportApiError,
  exportAcademicReport,
  getAcademicReportSummary,
  listSavedResearchReports,
  type AcademicExportFormat,
  type AcademicReportSummary,
  type SavedReportOption,
} from "@/lib/academicExportApi";
import { AcademicTemplateGalleryContainer } from "./AcademicTemplateGalleryContainer";
import { bundledCatalog } from "./academicTemplateCatalog";
import { academicExportCopy, type AcademicExportCopyKey, type AcademicExportCopyLocale } from "./academicExportCopy";
import type { AcademicTemplateConfiguration } from "./academicTemplateTypes";

/** The last successful export of one report, kept per browser for honest status copy. */
export type ExportMemory = {
  configurationId: string;
  snapshotJson: string;
};

export type ExportStatusKind = "busy" | "first" | "same" | "reformat" | "error";

export type ExportStatus = { kind: ExportStatusKind; messageKey: AcademicExportCopyKey };

const memoryKey = (jobId: string) => `taslim.academic-export.memory.${jobId}`;

type Store = { getItem(key: string): string | null; setItem(key: string, value: string): void };

function localStore(): Store | null {
  if (typeof window === "undefined") return null;
  try {
    return window.localStorage;
  } catch {
    return null;
  }
}

/** Read the remembered export state for a report; corrupt entries are ignored. */
export function readExportMemory(jobId: string, store: Store | null = localStore()): ExportMemory | null {
  if (!jobId || !store) return null;
  try {
    const raw = store.getItem(memoryKey(jobId));
    if (!raw) return null;
    const parsed = JSON.parse(raw) as Partial<ExportMemory>;
    if (typeof parsed.configurationId === "string" && typeof parsed.snapshotJson === "string") {
      return { configurationId: parsed.configurationId, snapshotJson: parsed.snapshotJson };
    }
    return null;
  } catch {
    return null;
  }
}

export function writeExportMemory(jobId: string, memory: ExportMemory, store: Store | null = localStore()): void {
  if (!jobId || !store) return;
  try {
    store.setItem(memoryKey(jobId), JSON.stringify(memory));
  } catch {
    // Storage can be full or blocked; the export itself already succeeded.
  }
}

/**
 * Which honest status the finished download deserves. The server header is the
 * truth when the browser can read it; otherwise the remembered configuration for
 * this exact report decides: same design → re-export, different → reformat, none → first.
 */
export function resolveExportStatusKind(
  serverMode: string | null,
  previous: ExportMemory | null,
  chosen: string,
): ExportStatusKind {
  if (serverMode === "re-export-same-settings") return "same";
  if (serverMode === "template-reformat") return previous ? "reformat" : "first";
  if (!previous) return "first";
  return previous.configurationId === chosen ? "same" : "reformat";
}

/** Map the API's machine code to the copy key that explains it without drama. */
export function exportErrorCopyKey(code: string): AcademicExportCopyKey {
  switch (code) {
    case "ACADEMIC_BIBLIOGRAPHY_INTEGRITY":
      return "errorIntegrity";
    case "ACADEMIC_EXPORT_DIRECTION_MISMATCH":
      return "errorDirection";
    case "ACADEMIC_EXPORT_NOT_FOUND":
      return "errorNotFound";
    case "RATE_LIMITED":
      // The render surface is bounded by the established expensive-operation policy, so a burst of
      // downloads is refused before anything is produced. Say so plainly instead of implying failure.
      return "errorRateLimited";
    default:
      return "errorGeneric";
  }
}

const localeTag: Record<AcademicExportCopyLocale, string> = { en: "en", ckb: "ckb", ar: "ar" };

export function formatSavedDate(iso: string | null, locale: AcademicExportCopyLocale): string | null {
  if (!iso) return null;
  const stamp = Date.parse(iso);
  if (Number.isNaN(stamp)) return null;
  try {
    return new Intl.DateTimeFormat(localeTag[locale], { dateStyle: "medium" }).format(new Date(stamp));
  } catch {
    return new Intl.DateTimeFormat("en", { dateStyle: "medium" }).format(new Date(stamp));
  }
}

export type AcademicReportExportViewProps = {
  locale: AcademicExportCopyLocale;
  templateCount: number;
  reports: SavedReportOption[];
  loadingReports: boolean;
  reportsError: boolean;
  selectedJobId: string | null;
  summary: AcademicReportSummary | null;
  loadingSummary: boolean;
  configuration: AcademicTemplateConfiguration | null;
  disclosureText: string;
  busy: boolean;
  status: ExportStatus | null;
  onSelectReport: (jobId: string) => void;
  onDisclosureChange: (value: string) => void;
  onConfigurationSelected: (configuration: AcademicTemplateConfiguration) => void;
  onExport: (format: AcademicExportFormat) => void;
};

/** Presentational half: renders exactly the state it is given. */
export function AcademicReportExportView({
  locale,
  templateCount,
  reports,
  loadingReports,
  reportsError,
  selectedJobId,
  summary,
  loadingSummary,
  configuration,
  disclosureText,
  busy,
  status,
  onSelectReport,
  onDisclosureChange,
  onConfigurationSelected,
  onExport,
}: AcademicReportExportViewProps) {
  const copy = (key: AcademicExportCopyKey, vars?: Record<string, string | number>) => academicExportCopy(locale, key, vars);
  const canExport = Boolean(summary && configuration && !busy);
  return (
    <section className="academic-export-panel" aria-labelledby="academic-export-title">
      <h2 id="academic-export-title">{copy("panelTitle")}</h2>
      <p className="academic-export-intro">{copy("panelIntro", { count: templateCount })}</p>

      <div className="academic-export-field">
        <label htmlFor="academic-export-report">{copy("reportLabel")}</label>
        <select
          id="academic-export-report"
          value={selectedJobId ?? ""}
          disabled={loadingReports || reports.length === 0}
          onChange={(event) => event.target.value && onSelectReport(event.target.value)}
        >
          <option value="">{loadingReports ? copy("reportsLoading") : copy("reportPlaceholder")}</option>
          {reports.map((report) => (
            <option key={report.jobId} value={report.jobId}>
              {report.title}
            </option>
          ))}
        </select>
        {loadingReports ? <p role="status">{copy("reportsLoading")}</p> : null}
        {!loadingReports && reportsError ? <p role="alert">{copy("reportsFailed")}</p> : null}
        {!loadingReports && !reportsError && reports.length === 0 ? <p role="status">{copy("reportsEmpty")}</p> : null}
      </div>

      {summary ? (
        <div className="academic-export-summary">
          <h3>{copy("summaryTitle")}</h3>
          <strong>{summary.title}</strong>
          <ul>
            <li>{copy("savedAt", { date: formatSavedDate(summary.savedAtUtc, locale) ?? summary.savedAtUtc })}</li>
            <li>{copy("sectionsCount", { count: summary.sectionCount })}</li>
            <li>{copy("sourcesCount", { count: summary.sourceCount })}</li>
            {/* Split by what the workspace actually holds. A single "full text reviewed here" line
                counted rows backed by nothing but a search-result snippet, which is a claim about the
                student's reading that no stored row proves. */}
            <li>{copy("fullTextCount", { count: summary.fullTextSourceCount })}</li>
            <li>{copy("extractedTextCount", { count: summary.extractedTextSourceCount })}</li>
            <li>{copy("searchResultCount", { count: summary.searchResultOnlySourceCount })}</li>
            <li>{copy("metadataOnlyCount", { count: summary.metadataOnlySourceCount })}</li>
            <li>{copy("accessBreakdownNote")}</li>
          </ul>
          {summary.metadataOnlySourceCount > 0 ? <p className="academic-export-warning">{copy("metadataWarning")}</p> : null}

          <div className="academic-export-disclosure">
            <h3>{copy("disclosureHeading")}</h3>
            <p>{copy("disclosureBody")}</p>
            <label htmlFor="academic-export-disclosure">{copy("disclosureLabel")}</label>
            <textarea
              id="academic-export-disclosure"
              rows={4}
              maxLength={6000}
              value={disclosureText}
              placeholder={copy("disclosurePlaceholder")}
              onChange={(event) => onDisclosureChange(event.target.value)}
            />
            {disclosureText.trim().length === 0 ? <p className="academic-export-warning">{copy("disclosureMissing")}</p> : null}
          </div>
        </div>
      ) : (
        loadingSummary && selectedJobId ? <p role="status">{copy("summaryLoading")}</p> : null
      )}

      <div className="academic-export-templates">
        <h3>{copy("templateHeading")}</h3>
        <AcademicTemplateGalleryContainer locale={locale} onConfigurationSelected={onConfigurationSelected} />
        <p className="academic-export-selected" role="status" aria-live="polite">
          {configuration
            ? `${copy("templateSelected")}: ${configuration.displayName} · ${configuration.configurationId}`
            : copy("templateNone")}
        </p>
      </div>

      <div className="academic-export-actions">
        <h3>{copy("formatHeading")}</h3>
        <button type="button" disabled={!canExport} onClick={() => onExport("docx")}>
          {copy("exportDocx")}
        </button>
        <button type="button" disabled={!canExport} onClick={() => onExport("pdf")}>
          {copy("exportPdf")}
        </button>
        <p role="status" aria-live="polite">
          {busy ? copy("exporting") : status ? copy(status.messageKey) : null}
        </p>
        {status && status.kind !== "busy" && status.kind !== "error" ? (
          <p className="academic-export-check">{copy("downloadCheck")}</p>
        ) : null}
      </div>
    </section>
  );
}

export type AcademicReportExportPanelProps = {
  /** Deep-link (?job=...) straight to one saved report. */
  initialJobId?: string | null;
  /** Optional host override for tests and embedding. */
  localeOverride?: AcademicExportCopyLocale;
  /** Injectable storage for tests; defaults to window.localStorage. */
  store?: Store | null;
};

export function AcademicReportExportPanel({
  initialJobId = null,
  localeOverride,
  store,
}: AcademicReportExportPanelProps = {}) {
  const { workspace } = useAuth();
  const { locale } = useLocale();
  // The shared web locale tag for Sorani Kurdish is "ku"; the template surface
  // (and the API) name it "ckb". One mapping, kept explicit.
  const copyLocale: AcademicExportCopyLocale =
    localeOverride ?? (locale === "ar" ? "ar" : locale === "ku" || (locale as string) === "ckb" ? "ckb" : "en");
  const storage = store === undefined ? localStore() : store;

  const workspaceId = workspace?.id ?? null;
  const [reports, setReports] = useState<SavedReportOption[]>([]);
  const [loadingReports, setLoadingReports] = useState(true);
  const [reportsError, setReportsError] = useState(false);
  const [selectedJobId, setSelectedJobId] = useState<string | null>(initialJobId);
  const [summary, setSummary] = useState<AcademicReportSummary | null>(null);
  const [loadingSummary, setLoadingSummary] = useState(false);
  const [configuration, setConfiguration] = useState<AcademicTemplateConfiguration | null>(null);
  const [disclosureText, setDisclosureText] = useState("");
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState<ExportStatus | null>(null);
  // The workspace whose data is currently painted; a change wipes it before the next
  // fetch resolves, so one student's list or summary is never shown under another.
  const paintedWorkspaceRef = useRef<string | null | undefined>(undefined);

  useEffect(() => {
    let cancelled = false;
    // State updates happen inside callbacks, never synchronously in the effect body
    // (react-hooks/set-state-in-effect), and a workspace change immediately invalidates
    // the previous principal's selection so its summary can never render in between.
    void Promise.resolve().then(() => {
      if (cancelled) return;
      if (!workspaceId) {
        setLoadingReports(false);
        return;
      }
      setLoadingReports(true);
      setReportsError(false);
      if (paintedWorkspaceRef.current !== workspaceId) {
        paintedWorkspaceRef.current = workspaceId;
        setReports([]);
        setSelectedJobId(null);
        setSummary(null);
      }
      listSavedResearchReports(workspaceId)
        .then((items) => {
          if (cancelled) return;
          setReports(items);
          // A deep-linked job is honored only when it is actually one of this
          // workspace's saved reports; anything else cannot reach another tenant.
          setSelectedJobId((current) =>
            current ?? items.find((item) => item.jobId === initialJobId)?.jobId ?? items[0]?.jobId ?? null,
          );
        })
        .catch(() => {
          if (!cancelled) {
            setReports([]);
            setReportsError(true);
            setSelectedJobId(null);
          }
        })
        .finally(() => {
          if (!cancelled) setLoadingReports(false);
        });
    });
    return () => {
      cancelled = true;
    };
  }, [workspaceId, initialJobId]);

  useEffect(() => {
    let cancelled = false;
    void Promise.resolve().then(() => {
      if (cancelled) return;
      if (!selectedJobId) {
        setSummary(null);
        return;
      }
      setLoadingSummary(true);
      setSummary(null);
      setStatus(null);
      getAcademicReportSummary(selectedJobId)
        .then((value) => {
          if (!cancelled) setSummary(value);
        })
        .catch((error: unknown) => {
          if (!cancelled && error instanceof AcademicExportApiError) {
            setStatus({ kind: "error", messageKey: exportErrorCopyKey(error.code) });
          }
        })
        .finally(() => {
          if (!cancelled) setLoadingSummary(false);
        });
    });
    return () => {
      cancelled = true;
    };
  }, [selectedJobId]);

  const handleExport = useCallback(
    async (format: AcademicExportFormat) => {
      if (!selectedJobId || !configuration) return;
      setBusy(true);
      setStatus({ kind: "busy", messageKey: "exporting" });
      const previous = readExportMemory(selectedJobId, storage);
      try {
        const result = await exportAcademicReport(selectedJobId, {
          configurationId: configuration.configurationId,
          format,
          previousSnapshotJson: previous?.snapshotJson ?? null,
          aiDisclosureText: disclosureText.trim() ? disclosureText.trim() : null,
        });
        writeExportMemory(
          selectedJobId,
          { configurationId: configuration.configurationId, snapshotJson: result.snapshot ?? previous?.snapshotJson ?? "" },
          storage,
        );
        const kind = resolveExportStatusKind(result.mode, previous, configuration.configurationId);
        setStatus({ kind, messageKey: kind === "same" ? "statusSame" : kind === "reformat" ? "statusReformat" : "statusFirst" });
        triggerDownload(result.blob, result.fileName);
      } catch (error) {
        const code = error instanceof AcademicExportApiError ? error.code : "ACADEMIC_EXPORT_NETWORK";
        setStatus({ kind: "error", messageKey: exportErrorCopyKey(code) });
      } finally {
        setBusy(false);
      }
    },
    [selectedJobId, configuration, disclosureText, storage],
  );

  return (
    <AcademicReportExportView
      locale={copyLocale}
      templateCount={bundledCatalog.distinctConfigurationCount}
      reports={reports}
      loadingReports={loadingReports}
      reportsError={reportsError}
      selectedJobId={selectedJobId}
      summary={summary}
      loadingSummary={loadingSummary}
      configuration={configuration}
      disclosureText={disclosureText}
      busy={busy}
      status={status}
      onSelectReport={(jobId) => {
        setSelectedJobId(jobId);
        setConfiguration(null);
      }}
      onDisclosureChange={setDisclosureText}
      onConfigurationSelected={setConfiguration}
      onExport={(format) => void handleExport(format)}
    />
  );
}

function triggerDownload(blob: Blob, fileName: string): void {
  if (typeof document === "undefined") return;
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = fileName;
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  setTimeout(() => URL.revokeObjectURL(url), 30_000);
}
