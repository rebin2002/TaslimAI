"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { useCallback, useEffect, useState } from "react";
import { ArrowUpRight, BookOpen, CheckCircle2, Download, ExternalLink, FileText, FolderOpen, LoaderCircle, RefreshCw, Search, Sparkles, XCircle } from "lucide-react";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { api, type Asset, type GenerationJob, type Project, type ResearchJobResult, type ResearchSource, type StoredFile } from "@/lib/api";
import { canCancelResearchJob, displayResearchProgress, isResearchSourceReady, isSafeExternalUrl, mergeResearchSources, nextResearchPollDelay, parseResearchJobResult, researchStudioState, shouldPollResearchJob } from "@/lib/researchStudioState";

const extensions = [".pdf", ".docx", ".txt", ".md", ".csv", ".xlsx"];
type Depth = "quick" | "standard" | "deep";
type ReportType = "research_report" | "market_research" | "competitor_research" | "company_research" | "product_research" | "industry_research" | "general_research";
type Language = "auto" | "en" | "ar" | "ku";

type Translate = (key: string, values?: Record<string, string>) => string;

export function ResearchStudioView() {
  const { workspace } = useAuth();
  const { t, locale } = useLocale();
  const searchParams = useSearchParams();
  const [projects, setProjects] = useState<Project[]>([]);
  const [files, setFiles] = useState<StoredFile[]>([]);
  const [recentResearch, setRecentResearch] = useState<Asset[]>([]);
  const [projectId, setProjectId] = useState(() => searchParams.get("projectId") ?? "");
  const [selected, setSelected] = useState<string[]>([]);
  const [title, setTitle] = useState("");
  const [question, setQuestion] = useState("");
  const [objective, setObjective] = useState("");
  const [depth, setDepth] = useState<Depth>("standard");
  const [reportType, setReportType] = useState<ReportType>("research_report");
  const [language, setLanguage] = useState<Language>("auto");
  const [audience, setAudience] = useState("");
  const [geographicFocus, setGeographicFocus] = useState("");
  const [timePeriod, setTimePeriod] = useState("");
  const [preferredDomains, setPreferredDomains] = useState("");
  const [excludedDomains, setExcludedDomains] = useState("");
  const [additionalInstructions, setAdditionalInstructions] = useState("");
  const [useWebSources, setUseWebSources] = useState(true);
  const [current, setCurrent] = useState<GenerationJob | null>(null);
  const [sourceDetails, setSourceDetails] = useState<ResearchSource[]>([]);
  const [loadingSources, setLoadingSources] = useState(true);
  const [working, setWorking] = useState(false);
  const [pollRetry, setPollRetry] = useState(0);
  const [retryingCompleted, setRetryingCompleted] = useState(false);
  const [error, setError] = useState("");
  const [downloadError, setDownloadError] = useState("");

  const loadInputs = useCallback(async () => {
    if (!workspace) return;
    setLoadingSources(true);
    try {
      const [active, archived, available, researchAssets] = await Promise.all([
        api.listProjects(workspace.id, "Active"),
        api.listProjects(workspace.id, "Archived"),
        api.listFiles(workspace.id),
        api.listAssets(workspace.id, { assetType: "research", pageSize: 4, sort: "recent" }),
      ]);
      setProjects([...active, ...archived]);
      setFiles(available.filter((file) => extensions.includes(file.extension.toLowerCase())));
      setRecentResearch(researchAssets.items);
    } catch {
      setProjects([]);
      setFiles([]);
      setRecentResearch([]);
    } finally {
      setLoadingSources(false);
    }
  }, [workspace]);

  // This effect synchronizes authenticated workspace inputs into the local form.
  // eslint-disable-next-line react-hooks/set-state-in-effect
  useEffect(() => { void loadInputs(); }, [loadInputs]);
  useEffect(() => {
    if (!current || !shouldPollResearchJob(current)) return;
    const jobId = current.id;
    let active = true;
    const timer = window.setTimeout(async () => {
      try {
        const next = await api.getGenerationJob(jobId);
        if (!active) return;
        setCurrent(next);
        setPollRetry(0);
        setError("");
      } catch {
        if (!active) return;
        setPollRetry((attempt) => attempt + 1);
        setError(t("research.pollError"));
      }
    }, nextResearchPollDelay(current, pollRetry) ?? 700);
    return () => { active = false; window.clearTimeout(timer); };
  }, [current, pollRetry, t]);
  useEffect(() => {
    if (!current || current.status !== "Succeeded") return;
    const parsed = parseResearchJobResult(current);
    if (!parsed?.assetId) return;
    let active = true;
    void api.getResearchSources(current.id).then((response) => {
      if (active) setSourceDetails(response.sources);
    }).catch(() => {
      if (active) setSourceDetails([]);
    });
    return () => { active = false; };
  }, [current]);

  function toggleFile(file: StoredFile) {
    if (!isResearchSourceReady(file)) return;
    setSelected((value) => value.includes(file.id) ? value.filter((id) => id !== file.id) : value.length >= 5 ? value : [...value, file.id]);
  }

  async function create(event: React.FormEvent) {
    event.preventDefault();
    if (!workspace || question.trim().length < 3 || (!useWebSources && selected.length === 0)) {
      setError(t("research.required"));
      return;
    }
    const instructions = [
      objective.trim() ? `${t("research.objective")}: ${objective.trim()}` : "",
      additionalInstructions.trim(),
    ].filter(Boolean).join("\n\n");
    setWorking(true);
    setError("");
    setDownloadError("");
    setSourceDetails([]);
    try {
      const job = await api.createResearchGenerationJob({
        workspaceId: workspace.id,
        projectId: projectId || null,
        question: question.trim(),
        title: title.trim() || null,
        depth,
        reportType,
        language,
        audience: audience.trim() || null,
        geographicFocus: geographicFocus.trim() || null,
        timePeriod: timePeriod.trim() || null,
        additionalInstructions: instructions || null,
        preferredDomains: preferredDomains.trim() || null,
        excludedDomains: excludedDomains.trim() || null,
        useWebSources,
        attachmentIds: selected,
      });
      setCurrent(job);
      setPollRetry(0);
    } catch {
      setError(t("research.createError"));
    } finally {
      setWorking(false);
    }
  }

  async function cancel() {
    if (!current || !canCancelResearchJob(current)) return;
    setWorking(true);
    setError("");
    try {
      await api.cancelGenerationJob(current.id);
      setCurrent(await api.getGenerationJob(current.id));
    } catch {
      setError(t("research.cancelError"));
    } finally {
      setWorking(false);
    }
  }

  async function retryCompleted() {
    if (!current) return;
    setRetryingCompleted(true);
    setError("");
    try {
      setCurrent(await api.getGenerationJob(current.id));
    } catch {
      setError(t("research.completedLoadError"));
    } finally {
      setRetryingCompleted(false);
    }
  }

  async function downloadRepresentation(representationId: string, fileName: string) {
    if (!result?.assetId) return;
    setWorking(true);
    setDownloadError("");
    try {
      const blob = await api.downloadAssetRepresentation(result.assetId, representationId);
      const url = URL.createObjectURL(blob);
      const anchor = window.document.createElement("a");
      anchor.href = url;
      anchor.download = fileName;
      anchor.click();
      window.setTimeout(() => URL.revokeObjectURL(url), 0);
    } catch {
      setDownloadError(t("research.downloadError"));
    } finally {
      setWorking(false);
    }
  }

  function createAnother() {
    setCurrent(null);
    setSourceDetails([]);
    setError("");
    setDownloadError("");
    setPollRetry(0);
  }

  const parsed = parseResearchJobResult(current);
  const result = mergeResearchSources(parsed, sourceDetails);
  const state = researchStudioState(current, result);
  const readyFiles = files.filter((file) => isResearchSourceReady(file));
  const progress = displayResearchProgress(current);
  const statusKey = current?.status ?? "Queued";
  return (
    <div className="research-studio-page">
      <header className="research-studio-header">
        <div>
          <p className="section-eyebrow">{t("research.eyebrow")}</p>
          <h1>{t("research.title")}</h1>
          <p>{t("research.subtitle")}</p>
        </div>
        <span className="research-studio-header-icon" aria-hidden="true"><BookOpen size={26} /></span>
      </header>

      {!current ? (
        <form className="research-studio-layout" onSubmit={(event) => void create(event)}>
          <section className="account-card research-studio-form-card">
            <div className="card-title">
              <span className="card-title-icon teal"><Search size={17} /></span>
              <div><h2>{t("research.createTitle")}</h2><p>{t("research.createSubtitle")}</p></div>
            </div>
            <label className="image-primary-field">
              <span>{t("research.questionLabel")}</span>
              <textarea value={question} onChange={(event) => setQuestion(event.target.value)} maxLength={8000} placeholder={t("research.questionPlaceholder")} required />
              <small>{question.length}/8000</small>
            </label>
            <label className="image-primary-field research-objective-field">
              <span>{t("research.objective")}</span>
              <textarea value={objective} onChange={(event) => setObjective(event.target.value)} maxLength={1200} placeholder={t("research.objectivePlaceholder")} />
              <small>{objective.length}/1200</small>
            </label>
            <div className="research-control-grid">
              <label className="field"><span>{t("research.depth")}</span><select value={depth} onChange={(event) => setDepth(event.target.value as Depth)}><option value="quick">{t("research.depth.quick")}</option><option value="standard">{t("research.depth.standard")}</option><option value="deep">{t("research.depth.deep")}</option></select></label>
              <label className="field"><span>{t("research.reportType")}</span><select value={reportType} onChange={(event) => setReportType(event.target.value as ReportType)}>{(["research_report", "market_research", "competitor_research", "company_research", "product_research", "industry_research", "general_research"] as ReportType[]).map((value) => <option key={value} value={value}>{t(`research.reportType.${value}`)}</option>)}</select></label>
              <label className="field"><span>{t("research.language")}</span><select value={language} onChange={(event) => setLanguage(event.target.value as Language)}><option value="auto">{t("research.language.auto")}</option><option value="en">{t("research.language.en")}</option><option value="ar">{t("research.language.ar")}</option><option value="ku">{t("research.language.ku")}</option></select></label>
              <label className="field"><span>{t("research.project")}</span><select value={projectId} onChange={(event) => setProjectId(event.target.value)} disabled={loadingSources}><option value="">{t("research.noProject")}</option>{projects.map((project) => <option key={project.id} value={project.id}>{project.name}</option>)}</select></label>
            </div>
            <details className="research-advanced">
              <summary>{t("research.advanced")}</summary>
              <div className="research-advanced-grid">
                <label className="field"><span>{t("research.audience")}</span><input value={audience} onChange={(event) => setAudience(event.target.value)} maxLength={400} placeholder={t("research.audiencePlaceholder")} /></label>
                <label className="field"><span>{t("research.titleLabel")}</span><input value={title} onChange={(event) => setTitle(event.target.value)} maxLength={160} placeholder={t("research.titlePlaceholder")} /></label>
                <label className="field"><span>{t("research.geographicFocus")}</span><input value={geographicFocus} onChange={(event) => setGeographicFocus(event.target.value)} maxLength={240} placeholder={t("research.geographicFocusPlaceholder")} /></label>
                <label className="field"><span>{t("research.timePeriod")}</span><input value={timePeriod} onChange={(event) => setTimePeriod(event.target.value)} maxLength={120} placeholder={t("research.timePeriodPlaceholder")} /></label>
                <label className="field"><span>{t("research.preferredDomains")}</span><input value={preferredDomains} onChange={(event) => setPreferredDomains(event.target.value)} maxLength={2000} placeholder={t("research.preferredDomainsPlaceholder")} /></label>
                <label className="field"><span>{t("research.excludedDomains")}</span><input value={excludedDomains} onChange={(event) => setExcludedDomains(event.target.value)} maxLength={2000} placeholder={t("research.excludedDomainsPlaceholder")} /></label>
                <label className="field research-advanced-wide"><span>{t("research.additionalInstructions")}</span><textarea value={additionalInstructions} onChange={(event) => setAdditionalInstructions(event.target.value)} maxLength={3000} placeholder={t("research.additionalInstructionsPlaceholder")} /></label>
              </div>
            </details>
            <label className="research-checkbox"><input type="checkbox" checked={useWebSources} onChange={(event) => setUseWebSources(event.target.checked)} /> <span>{t("research.useWebSources")}</span></label>
            <div className="research-source-heading"><div><h3>{t("research.contextTitle")}</h3><p>{t("research.sourcesHint")}</p></div><strong>{selected.length}/5</strong></div>
            <div className="research-source-list">
              {loadingSources ? <p className="usage-empty">{t("research.loadingSources")}</p> : readyFiles.length === 0 ? <p className="usage-empty">{t("research.noSources")}</p> : readyFiles.map((file) => <label className={`research-source-option ${selected.includes(file.id) ? "is-selected" : ""}`} key={file.id}><input type="checkbox" checked={selected.includes(file.id)} onChange={() => toggleFile(file)} /><FileText size={16} /><span><strong>{file.originalFileName}</strong><small>{file.extension.toUpperCase()} · {Math.ceil(file.sizeBytes / 1024)} KB</small></span></label>)}
            </div>
            {error && <div className="form-error"><XCircle size={15} /> {error}</div>}
            <button className="primary-button research-submit-button" type="submit" disabled={working || question.trim().length < 3 || (!useWebSources && selected.length === 0)}><Sparkles size={16} /> {working ? t("research.working") : t("research.generate")}</button>
          </section>
          <aside className="research-compose-rail">
            <section className="account-card research-studio-guidance"><BookOpen size={27} /><p className="section-eyebrow">{t("research.guidanceEyebrow")}</p><h2>{t("research.guidanceTitle")}</h2><p>{t("research.guidanceText")}</p><ul><li>{t("research.guidanceOne")}</li><li>{t("research.guidanceTwo")}</li><li>{t("research.guidanceThree")}</li></ul><Link className="secondary-button" href="/assets">{t("research.openAssets")}</Link></section>
            <RecentResearch assets={recentResearch} loading={loadingSources} locale={locale} t={t} />
          </aside>
        </form>
      ) : state === "failed" || state === "cancelled" ? (
        <section className="account-card research-generation-state research-terminal-state" aria-live="polite"><XCircle size={28} /><p className="section-eyebrow">{t(`jobs.status${statusKey}`)}</p><h2>{current.errorMessage || t("research.failedSafe")}</h2><div className="research-result-actions"><button className="primary-button" onClick={createAnother}><RefreshCw size={15} /> {t("research.createAnother")}</button><Link className="secondary-button" href="/assets">{t("research.openAssets")}</Link></div></section>
      ) : state === "succeeded" && result?.assetId ? (
        <ResearchResultView result={result} working={working} downloadError={downloadError} onDownload={(id, name) => void downloadRepresentation(id, name)} onCreateAnother={createAnother} t={t} />
      ) : state === "completed-unavailable" ? (
        <section className="account-card research-generation-state research-terminal-state"><RefreshCw size={28} /><p className="section-eyebrow">{t("jobs.statusSucceeded")}</p><h2>{t("research.completedLoadError")}</h2><p>{t("research.completedLoadHint")}</p><div className="research-result-actions"><button className="primary-button" onClick={() => void retryCompleted()} disabled={retryingCompleted}>{retryingCompleted ? t("research.working") : t("research.retry")}</button><Link className="secondary-button" href="/assets">{t("research.openAssets")}</Link></div></section>
      ) : (
        <ResearchProgressView current={current} statusKey={statusKey} progress={progress} error={error} working={working} onCancel={() => void cancel()} t={t} />
      )}
      <p className="research-studio-footnote">{t("research.safetyNote")}</p>
    </div>
  );
}

function ResearchProgressView({ current, statusKey, progress, error, working, onCancel, t }: { current: GenerationJob; statusKey: string; progress: number; error: string; working: boolean; onCancel: () => void; t: Translate }) {
  return <section className="account-card research-generation-state research-progress-state" aria-live="polite"><div className="research-progress-topline"><div className="research-progress-icon"><LoaderCircle size={24} /></div><div><p className="section-eyebrow">{t("research.progressEyebrow")}</p><h2>{t(`jobs.status${statusKey}`)}</h2></div></div><p className="research-progress-copy">{t("research.progressText")}</p><div className="research-job-status-line"><span className={`research-job-dot research-job-dot-${current.status.toLowerCase()}`} /><strong>{t(`jobs.status${statusKey}`)}</strong><span>{t("research.jobProgress")}</span><b>{progress}%</b></div><div className="generation-progress-track"><span style={{ width: `${progress}%` }} /></div>{error && <div className="form-error"><XCircle size={15} /> {error}</div>}{canCancelResearchJob(current) && <button className="secondary-button generation-cancel-button" onClick={onCancel} disabled={working}><XCircle size={15} /> {t("research.cancel")}</button>}</section>;
}

function ResearchResultView({ result, working, downloadError, onDownload, onCreateAnother, t }: { result: ResearchJobResult; working: boolean; downloadError: string; onDownload: (id: string, name: string) => void; onCreateAnother: () => void; t: Translate }) {
  const sources = result.sources ?? [];
  return <section className="research-report-shell" aria-live="polite">
    <div className="research-result-heading"><div><p className="section-eyebrow">{t("research.resultEyebrow")}</p><h2>{result.title || t("research.resultTitle")}</h2>{result.subtitle && <p className="research-report-subtitle">{result.subtitle}</p>}</div><span className="form-success"><CheckCircle2 size={16} /> {t("research.savedToAssets")}</span></div>
    <div className="research-result-meta"><span>{t("research.sourceCount", { count: String(result.sourceCount ?? sources.length) })}</span><span>·</span><span>{t("research.references")}</span></div>
    <div className="research-report-layout">
      <article className="research-report-content">
        {result.executiveSummary && <section className="research-summary"><p className="research-section-kicker">{t("research.executiveSummary")}</p><p>{result.executiveSummary}</p></section>}
        {result.keyFindings?.length ? <section className="research-findings"><div className="research-section-heading"><p className="research-section-kicker">{t("research.keyFindings")}</p><span>{result.keyFindings.length}</span></div>{result.keyFindings.slice(0, 8).map((block, index) => <ResearchBlock key={index} block={block} />)}</section> : null}
        {result.sections?.slice(0, 8).map((section) => <section className="research-report-section" key={section.heading}><h3>{section.heading}</h3>{section.blocks.slice(0, 8).map((block, index) => <ResearchBlock key={index} block={block} />)}</section>)}
        {result.conclusion && <section className="research-conclusion"><p className="research-section-kicker">{t("research.conclusion")}</p><p>{result.conclusion}</p></section>}
      </article>
      <aside className="research-evidence-rail"><EvidenceRail sources={sources} t={t} /></aside>
    </div>
    <details className="research-mobile-sources"><summary><span>{t("research.sourcesTitle")}</span><strong>{sources.length}</strong></summary><EvidenceRail sources={sources} t={t} /></details>
    {downloadError && <div className="form-error"><XCircle size={15} /> {downloadError}</div>}
    <div className="research-result-actions">{result.representations?.filter((representation) => ["docx", "pdf"].includes(representation.type)).map((representation) => <button className="secondary-button" key={representation.id} type="button" onClick={() => onDownload(representation.id, representation.fileName)} disabled={working}><Download size={15} /> {representation.type.toUpperCase()}</button>)}{!result.representations?.some((representation) => ["docx", "pdf"].includes(representation.type)) && <span className="research-no-downloads">{t("research.noDownloads")}</span>}<Link className="secondary-button" href="/assets">{t("research.openAssets")}</Link><button className="primary-button" onClick={onCreateAnother}><RefreshCw size={15} /> {t("research.createAnother")}</button></div>
  </section>;
}

function EvidenceRail({ sources, t }: { sources: ResearchSource[]; t: Translate }) {
  return <div className="research-evidence-panel"><div className="research-sources-heading"><div><p className="research-section-kicker">{t("research.evidenceRail")}</p><h3>{t("research.sourcesTitle")}</h3></div><span>{sources.length}</span></div>{sources.length === 0 ? <p className="research-no-evidence">{t("research.noSources")}</p> : sources.map((source) => <article className="research-source-card" key={source.citationId}><div className="research-source-card-heading"><span className="research-citation">[{source.citationId}]</span><div><h4>{source.title}</h4><small>{source.publisher || source.domain} · {source.sourceType}</small></div>{isSafeExternalUrl(source.url) ? <a href={source.url ?? undefined} target="_blank" rel="noreferrer" aria-label={`${t("research.openSource")}: ${source.title}`}><ExternalLink size={15} /></a> : null}</div>{source.snippet && <p>{source.snippet}</p>}{source.evidence?.slice(0, 3).map((evidence, index) => <div className="research-evidence" key={`${source.citationId}-${index}`}><strong>{evidence.topic || t("research.evidence")}</strong><span>{evidence.excerpt}</span></div>)}</article>)}</div>;
}

function RecentResearch({ assets, loading, locale, t }: { assets: Asset[]; loading: boolean; locale: string; t: Translate }) {
  return <section className="account-card research-recent-card"><div className="research-recent-heading"><div><p className="research-section-kicker">{t("research.recentEyebrow")}</p><h2>{t("research.recentTitle")}</h2></div><FolderOpen size={18} /></div><p className="research-recent-description">{t("research.recentDescription")}</p>{loading ? <p className="research-recent-empty">{t("research.loadingSources")}</p> : assets.length === 0 ? <p className="research-recent-empty">{t("research.recentEmpty")}</p> : <div className="research-recent-list">{assets.map((asset) => <Link className="research-recent-item" href={`/assets?search=${encodeURIComponent(asset.name)}`} key={asset.id}><span className="research-recent-icon"><FileText size={15} /></span><span><strong>{asset.name}</strong><small>{asset.projectName || t("research.workspaceReport")} · {formatAssetDate(asset.createdAt, locale)}</small></span><ArrowUpRight size={15} /></Link>)}</div>}</section>;
}

function formatAssetDate(value: string, locale: string) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : new Intl.DateTimeFormat(locale, { month: "short", day: "numeric", year: "numeric" }).format(date);
}

function ResearchBlock({ block }: { block: { type: string; text?: string | null; items?: string[] | null; rows?: { cells: string[] }[] | null; citationIds?: string[] } }) {
  return <div className={`research-report-block research-report-block-${block.type}`}>
    {block.text && <p>{block.text}</p>}
    {block.items?.length ? <ul>{block.items.slice(0, 8).map((item) => <li key={item}>{item}</li>)}</ul> : null}
    {block.rows?.length ? <div className="research-report-table">{block.rows.slice(0, 8).map((row, index) => <div key={index}>{row.cells.map((cell) => <span key={cell}>{cell}</span>)}</div>)}</div> : null}
    {block.citationIds?.length ? <div className="research-citation-list">{block.citationIds.map((citation) => <span key={citation}>[{citation}]</span>)}</div> : null}
  </div>;
}
