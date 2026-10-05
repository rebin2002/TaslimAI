"use client";
import { localeTag } from "@/lib/i18n";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { useCallback, useEffect, useRef, useState } from "react";
import {
  ArrowLeft,
  Check,
  CheckCircle2,
  Clipboard,
  Download,
  FileText,
  FolderOpen,
  LoaderCircle,
  RefreshCw,
  Sparkles,
  WandSparkles,
  XCircle,
} from "lucide-react";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { ApiError, api, type Asset, type GenerationJob, type Project, type StoredFile } from "@/lib/api";
import {
  canCancelDocumentJob,
  clearDocumentActiveJobId,
  displayDocumentProgress,
  documentPresentationState,
  isDocumentSourceReady,
  isRestorableDocumentJob,
  nextDocumentPollDelay,
  parseDocumentJobResult,
  persistDocumentActiveJobId,
  retainDocumentWorkspaceProjectId,
  retainDocumentWorkspaceFileIds,
  readDocumentActiveJobId,
  shouldResetDocumentWorkspaceState,
  shouldPollDocumentJob,
} from "@/lib/documentStudioState";

const extensions = [".pdf", ".docx", ".txt", ".md", ".csv", ".xlsx"];
type DocumentType = "auto" | "report" | "proposal" | "business_letter" | "company_profile" | "meeting_minutes" | "article" | "general";
type DocumentLength = "short" | "standard" | "detailed";
type DocumentLanguage = "auto" | "en" | "ar" | "ku";
type DocumentOutputFormat = "docx" | "pdf" | "both";
type DocumentTone = "professional" | "formal" | "friendly" | "persuasive" | "neutral";

function formatAssetDate(value: string, locale: string) {
  return new Intl.DateTimeFormat(localeTag(locale), { month: "short", day: "numeric", year: "numeric" }).format(new Date(value));
}

function assetSnippet(asset: Asset) {
  return asset.description || asset.sourceJobTitle || (asset.projectName ? asset.projectName : "Workspace document");
}
type DocumentProgressMeterProps = Readonly<{
  progress: number;
  label: string;
  announcement: string;
}>;
export function DocumentProgressMeter({ progress, label, announcement }: DocumentProgressMeterProps) {
  const boundedProgress = Math.max(0, Math.min(100, progress));
  return <>
    <p className="sr-only" role="status" aria-live="polite" aria-atomic="true">{announcement}</p>
    <div className="generation-progress-label"><span>{label}</span><strong>{boundedProgress}%</strong></div>
    <div className="generation-progress-track" role="progressbar" aria-label={label} aria-valuemin={0} aria-valuemax={100} aria-valuenow={boundedProgress} aria-valuetext={`${boundedProgress}%`}>
      <span style={{ width: `${boundedProgress}%` }} />
    </div>
  </>;
}
type DocumentPreviewTableProps = Readonly<{
  rows: ReadonlyArray<{ cells: ReadonlyArray<string> }>;
  label: string;
}>;
export function DocumentPreviewTable({ rows, label }: DocumentPreviewTableProps) {
  return <div className="document-preview-table" role="table" aria-label={label}>{rows.map((row, rowIndex) => <div className="document-preview-row" key={rowIndex} role="row">{row.cells.map((cell, cellIndex) => <span key={`${rowIndex}-${cellIndex}`} role="cell">{cell}</span>)}</div>)}</div>;
}
export function DocumentStudioView() {
  const { workspace } = useAuth();
  const { locale, t } = useLocale();
  const searchParams = useSearchParams();
  const [projects, setProjects] = useState<Project[]>([]);
  const [files, setFiles] = useState<StoredFile[]>([]);
  const [recentAssets, setRecentAssets] = useState<Asset[]>([]);
  const [projectId, setProjectId] = useState(() => searchParams.get("projectId") ?? "");
  const [selected, setSelected] = useState<string[]>([]);
  const [title, setTitle] = useState("");
  const [prompt, setPrompt] = useState("");
  const [documentType, setDocumentType] = useState<DocumentType>("auto");
  const [length, setLength] = useState<DocumentLength>("standard");
  const [audience, setAudience] = useState("");
  const [additionalInstructions, setAdditionalInstructions] = useState("");
  const [language, setLanguage] = useState<DocumentLanguage>("auto");
  const [outputFormat, setOutputFormat] = useState<DocumentOutputFormat>("both");
  const [tone, setTone] = useState<DocumentTone>("professional");
  const [includeTableOfContents, setIncludeTableOfContents] = useState(true);
  const [current, setCurrent] = useState<GenerationJob | null>(null);
  const [loadingSources, setLoadingSources] = useState(true);
  const [loadingRecent, setLoadingRecent] = useState(true);
  const [working, setWorking] = useState(false);
  const [retryingCompleted, setRetryingCompleted] = useState(false);
  const [pollRetry, setPollRetry] = useState(0);
  const [error, setError] = useState("");
  const [downloadError, setDownloadError] = useState("");
  const [copyState, setCopyState] = useState<"idle" | "copied" | "error">("idle");
  const restoreJobId = useRef<string | null>(null);
  const activeJobId = useRef<string | null>(null);
  const activeAssetId = useRef<string | null>(null);
  const observedWorkspaceId = useRef<string | null>(workspace?.id ?? null);
  const workspaceGeneration = useRef(0);

  const loadSources = useCallback(async () => {
    if (!workspace) return;
    const workspaceId = workspace.id;
    const workspaceVersion = workspaceGeneration.current;
    setLoadingSources(true);
    setLoadingRecent(true);
    const [projectsResult, archivedResult, filesResult, assetsResult] = await Promise.allSettled([
      api.listProjects(workspaceId, "Active"),
      api.listProjects(workspaceId, "Archived"),
      api.listFiles(workspaceId),
      api.listAssets(workspaceId, { assetType: "document", sort: "recent", pageSize: 4 }),
    ]);
    if (observedWorkspaceId.current !== workspaceId || workspaceGeneration.current !== workspaceVersion) return;
    if (projectsResult.status === "fulfilled" && archivedResult.status === "fulfilled") {
      const loadedProjects = [...projectsResult.value, ...archivedResult.value];
      setProjects(loadedProjects);
      setProjectId((currentProjectId) => retainDocumentWorkspaceProjectId(currentProjectId, loadedProjects));
    } else {
      setProjects([]);
    }
    if (filesResult.status === "fulfilled") {
      const loadedFiles = filesResult.value.filter((file) => extensions.includes(file.extension.toLowerCase()));
      setFiles(loadedFiles);
      setSelected((selectedIds) => retainDocumentWorkspaceFileIds(selectedIds, loadedFiles));
    } else {
      setFiles([]);
      setSelected([]);
    }
    setRecentAssets(assetsResult.status === "fulfilled" ? assetsResult.value.items : []);
    setLoadingSources(false);
    setLoadingRecent(false);
  }, [workspace]);

  // Workspace-scoped jobs, briefs, selections, and source lists must not survive an ownership boundary.
  // The generation counter fences responses and actions that started in the previous workspace.
  useEffect(() => {
    const nextWorkspaceId = workspace?.id ?? null;
    if (observedWorkspaceId.current === nextWorkspaceId) return;
    const previousWorkspaceId = observedWorkspaceId.current;
    observedWorkspaceId.current = nextWorkspaceId;
    workspaceGeneration.current += 1;
    restoreJobId.current = null;
    activeJobId.current = null;
    activeAssetId.current = null;
    if (previousWorkspaceId === null && nextWorkspaceId !== null && !shouldResetDocumentWorkspaceState(current, nextWorkspaceId)) return;
    setCurrent(null);
    setSelected([]);
    setProjects([]);
    setFiles([]);
    setRecentAssets([]);
    setProjectId("");
    setTitle("");
    setPrompt("");
    setDocumentType("auto");
    setLength("standard");
    setAudience("");
    setAdditionalInstructions("");
    setLanguage("auto");
    setOutputFormat("both");
    setTone("professional");
    setIncludeTableOfContents(true);
    setPollRetry(0);
    setRetryingCompleted(false);
    setWorking(false);
    setLoadingSources(true);
    setLoadingRecent(true);
    setError("");
    setDownloadError("");
    setCopyState("idle");
  }, [current, workspace?.id]);

  // Synchronize source documents, projects, and recent document assets when the authenticated workspace changes.
  // eslint-disable-next-line react-hooks/set-state-in-effect
  useEffect(() => { void loadSources(); }, [loadSources]);

  useEffect(() => {
    if (!workspace || current) return;
    const workspaceId = workspace.id;
    const workspaceVersion = workspaceGeneration.current;
    const storedJobId = readDocumentActiveJobId(workspaceId);
    if (!storedJobId) return;
    restoreJobId.current = storedJobId;
    let active = true;
    void api.getGenerationJob(storedJobId).then((job) => {
      if (!active || workspaceGeneration.current !== workspaceVersion || restoreJobId.current !== storedJobId) return;
      if (!isRestorableDocumentJob(job, workspaceId, storedJobId)) {
        clearDocumentActiveJobId(workspaceId);
        return;
      }
      activeJobId.current = job.id;
      activeAssetId.current = parseDocumentJobResult(job)?.assetId ?? null;
      setCurrent(job);
      setPollRetry(0);
    }).catch((cause) => {
      if (!active || workspaceGeneration.current !== workspaceVersion || restoreJobId.current !== storedJobId) return;
      if (cause instanceof ApiError && (cause.status === 403 || cause.status === 404)) {
        clearDocumentActiveJobId(workspaceId);
      }
    });
    return () => { active = false; };
  }, [workspace, current]);

  useEffect(() => {
    if (!current || !shouldPollDocumentJob(current)) return;
    const jobId = current.id;
    const workspaceVersion = workspaceGeneration.current;
    let active = true;
    const timer = window.setTimeout(async () => {
      try {
        const next = await api.getGenerationJob(jobId);
        if (!active || workspaceGeneration.current !== workspaceVersion || current.id !== jobId || !isRestorableDocumentJob(next, current.workspaceId, jobId)) return;
        activeJobId.current = next.id;
        activeAssetId.current = parseDocumentJobResult(next)?.assetId ?? null;
        setCurrent(next);
        setError("");
        setPollRetry(0);
      } catch {
        if (!active || workspaceGeneration.current !== workspaceVersion || current.id !== jobId) return;
        setError(t("document.pollError"));
        // Keep polling after a transient request failure; the current job remains the source of truth.
        setPollRetry((attempt) => attempt + 1);
      }
    }, nextDocumentPollDelay(current, pollRetry) ?? 700);
    return () => { active = false; window.clearTimeout(timer); };
  }, [current, pollRetry, t]);

  function toggleFile(file: StoredFile) {
    if (!isDocumentSourceReady(file)) return;
    setSelected((currentSelection) => currentSelection.includes(file.id)
      ? currentSelection.filter((id) => id !== file.id)
      : currentSelection.length >= 5 ? currentSelection : [...currentSelection, file.id]);
  }

  async function create(event: React.FormEvent) {
    event.preventDefault();
    if (!workspace || prompt.trim().length < 3) {
      setError(t("document.required"));
      return;
    }
    const workspaceId = workspace.id;
    const workspaceVersion = workspaceGeneration.current;
    setWorking(true);
    setError("");
    setDownloadError("");
    setCopyState("idle");
    restoreJobId.current = null;
    clearDocumentActiveJobId(workspaceId);
    try {
      const job = await api.createDocumentGenerationJob({ workspaceId, projectId: projectId || null, title: title.trim() || null, description: prompt.trim(), documentType, length, audience: audience.trim() || null, additionalInstructions: additionalInstructions.trim() || null, attachmentIds: selected, language, outputFormat, tone, includeTableOfContents });
      if (workspaceGeneration.current !== workspaceVersion || observedWorkspaceId.current !== workspaceId || !isRestorableDocumentJob(job, workspaceId)) return;
      activeJobId.current = job.id;
      activeAssetId.current = parseDocumentJobResult(job)?.assetId ?? null;
      persistDocumentActiveJobId(workspaceId, job.id);
      setCurrent(job);
      setPollRetry(0);
    } catch {
      if (workspaceGeneration.current === workspaceVersion && observedWorkspaceId.current === workspaceId) setError(t("document.createError"));
    } finally {
      if (workspaceGeneration.current === workspaceVersion && observedWorkspaceId.current === workspaceId) setWorking(false);
    }
  }

  async function cancel() {
    if (!current || !canCancelDocumentJob(current)) return;
    const workspaceId = current.workspaceId;
    const workspaceVersion = workspaceGeneration.current;
    const jobId = current.id;
    setWorking(true);
    setError("");
    try {
      await api.cancelGenerationJob(jobId);
      const next = await api.getGenerationJob(jobId);
      if (workspaceGeneration.current !== workspaceVersion || activeJobId.current !== jobId || !isRestorableDocumentJob(next, workspaceId, jobId)) return;
      activeAssetId.current = parseDocumentJobResult(next)?.assetId ?? null;
      setCurrent(next);
    } catch {
      if (workspaceGeneration.current === workspaceVersion && activeJobId.current === jobId) setError(t("document.cancelError"));
    } finally {
      if (workspaceGeneration.current === workspaceVersion && activeJobId.current === jobId) setWorking(false);
    }
  }

  async function retryCompleted() {
    if (!current) return;
    const workspaceId = current.workspaceId;
    const workspaceVersion = workspaceGeneration.current;
    const jobId = current.id;
    setRetryingCompleted(true);
    setError("");
    try {
      const next = await api.getGenerationJob(jobId);
      if (workspaceGeneration.current === workspaceVersion && activeJobId.current === jobId && isRestorableDocumentJob(next, workspaceId, jobId)) {
        activeAssetId.current = parseDocumentJobResult(next)?.assetId ?? null;
        setCurrent(next);
      }
    } catch {
      if (workspaceGeneration.current === workspaceVersion && activeJobId.current === jobId) setError(t("document.completedLoadError"));
    } finally {
      if (workspaceGeneration.current === workspaceVersion && activeJobId.current === jobId) setRetryingCompleted(false);
    }
  }

  async function downloadRepresentation(representationId: string, fileName: string) {
    if (!result?.assetId) return;
    const assetId = result.assetId;
    const jobId = current?.id;
    const workspaceVersion = workspaceGeneration.current;
    setWorking(true);
    setDownloadError("");
    try {
      const blob = await api.downloadAssetRepresentation(assetId, representationId);
      if (workspaceGeneration.current !== workspaceVersion || activeJobId.current !== jobId || activeAssetId.current !== assetId) return;
      const url = URL.createObjectURL(blob);
      const anchor = window.document.createElement("a");
      anchor.href = url;
      anchor.download = fileName;
      anchor.click();
      window.setTimeout(() => URL.revokeObjectURL(url), 0);
    } catch {
      if (workspaceGeneration.current === workspaceVersion && activeJobId.current === jobId && activeAssetId.current === assetId) setDownloadError(t("document.downloadError"));
    } finally {
      if (workspaceGeneration.current === workspaceVersion && activeJobId.current === jobId && activeAssetId.current === assetId) setWorking(false);
    }
  }

  function createAnother() {
    restoreJobId.current = null;
    activeJobId.current = null;
    activeAssetId.current = null;
    if (workspace) clearDocumentActiveJobId(workspace.id);
    setCurrent(null);
    setError("");
    setDownloadError("");
    setCopyState("idle");
    setPollRetry(0);
  }

  function refineDocument() {
    createAnother();
    window.setTimeout(() => document.getElementById("document-brief")?.focus(), 0);
  }

  async function copyDocument() {
    if (!result) return;
    const text = [result.title, result.summary, ...(result.sections ?? []).flatMap((section) => [section.heading, ...section.blocks.flatMap((block) => [block.text ?? "", ...(block.items ?? []), ...(block.rows ?? []).map((row) => row.cells.join(" | "))])])].filter(Boolean).join("\n\n");
    try {
      await navigator.clipboard.writeText(text);
      setCopyState("copied");
      window.setTimeout(() => setCopyState("idle"), 1800);
    } catch {
      setCopyState("error");
    }
  }

  const result = parseDocumentJobResult(current);
  const presentation = documentPresentationState(current, result);
  const readyFiles = files.filter((file) => isDocumentSourceReady(file));
  const displayProgress = displayDocumentProgress(current);
  const statusKey = current?.status ?? "Queued";
  const statusLabel = t(`jobs.status${statusKey}`);
  const resultTitle = result?.title || t("document.resultTitle");

  return <div className="document-studio-page">
    <header className="document-studio-header">
      <div className="document-studio-heading">
        <p className="section-eyebrow">{t("document.eyebrow")}</p>
        <h1>{t("document.title")}</h1>
        <p>{t("document.subtitle")}</p>
      </div>
      <div className="document-studio-header-mark" aria-hidden="true"><FileText size={25} /></div>
    </header>

    {!current ? <>
      <div className="document-compose-layout">
        <form className="document-brief-card" aria-labelledby="document-create-title" onSubmit={(event) => void create(event)}>
          <div className="document-card-heading">
            <div className="document-card-title"><span className="document-card-icon"><Sparkles size={16} /></span><div><p className="document-card-kicker">{t("document.createTitle")}</p><h2 id="document-create-title">{t("document.createSubtitle")}</h2></div></div>
            <span className="document-step-count">01</span>
          </div>
          <label className="document-field document-field-primary"><span>{t("document.descriptionLabel")}</span><textarea id="document-brief" aria-describedby="document-brief-count" value={prompt} onChange={(event) => setPrompt(event.target.value)} maxLength={8000} placeholder={t("document.descriptionPlaceholder")} required /><small id="document-brief-count">{prompt.length}/8000</small></label>
          <div className="document-field-row"><label className="document-field"><span>{t("document.type")}</span><select value={documentType} onChange={(event) => setDocumentType(event.target.value as DocumentType)}><option value="auto">{t("document.typeAuto")}</option><option value="report">{t("document.typeReport")}</option><option value="proposal">{t("document.typeProposal")}</option><option value="business_letter">{t("document.typeLetter")}</option><option value="company_profile">{t("document.typeProfile")}</option><option value="meeting_minutes">{t("document.typeMinutes")}</option><option value="article">{t("document.typeArticle")}</option><option value="general">{t("document.typeGeneral")}</option></select></label><label className="document-field"><span>{t("document.length")}</span><select value={length} onChange={(event) => setLength(event.target.value as DocumentLength)}><option value="short">{t("document.lengthShort")}</option><option value="standard">{t("document.lengthStandard")}</option><option value="detailed">{t("document.lengthDetailed")}</option></select></label></div>
          <div className="document-field-row"><label className="document-field"><span>{t("document.tone")}</span><select value={tone} onChange={(event) => setTone(event.target.value as DocumentTone)}><option value="professional">{t("document.toneProfessional")}</option><option value="formal">{t("document.toneFormal")}</option><option value="friendly">{t("document.toneFriendly")}</option><option value="persuasive">{t("document.tonePersuasive")}</option><option value="neutral">{t("document.toneNeutral")}</option></select></label><label className="document-field"><span>{t("document.language")}</span><select value={language} onChange={(event) => setLanguage(event.target.value as DocumentLanguage)}><option value="auto">{t("document.languageAuto")}</option><option value="en">{t("document.languageEnglish")}</option><option value="ar">{t("document.languageArabic")}</option><option value="ku">{t("document.languageKurdish")}</option></select></label></div>
          <div className="document-field-row"><label className="document-field"><span>{t("document.outputFormat")}</span><select id="document-output-format" value={outputFormat} onChange={(event) => setOutputFormat(event.target.value as DocumentOutputFormat)}><option value="both">{t("document.outputBoth")}</option><option value="docx">{t("document.outputDocx")}</option><option value="pdf">{t("document.outputPdf")}</option></select></label></div>
          <label className="document-field"><span>{t("document.project")}</span><select id="document-project" value={projectId} onChange={(event) => setProjectId(event.target.value)} disabled={loadingSources}><option value="">{t("document.noProject")}</option>{projects.map((project) => <option key={project.id} value={project.id}>{project.name}</option>)}</select></label>
          <details className="document-advanced"><summary>{t("document.advanced")}</summary><div className="document-advanced-grid"><label className="document-field"><span>{t("document.titleLabel")}</span><input value={title} onChange={(event) => setTitle(event.target.value)} maxLength={160} placeholder={t("document.titlePlaceholder")} /></label><label className="document-field"><span>{t("document.audience")}</span><input value={audience} onChange={(event) => setAudience(event.target.value)} maxLength={400} placeholder={t("document.audiencePlaceholder")} /></label><label className="document-field document-advanced-wide"><span>{t("document.additionalInstructions")}</span><textarea value={additionalInstructions} onChange={(event) => setAdditionalInstructions(event.target.value)} maxLength={3000} placeholder={t("document.additionalInstructionsPlaceholder")} /></label></div></details>
          <div className="document-brief-footer"><label className="document-checkbox"><input type="checkbox" checked={includeTableOfContents} onChange={(event) => setIncludeTableOfContents(event.target.checked)} /> <span>{t("document.includeContents")}</span></label><button className="primary-button document-generate-button" type="submit" disabled={working || prompt.trim().length < 3} aria-busy={working}><Sparkles size={15} /> {working ? t("document.working") : t("document.generate")}</button></div>
          {error && <div className="form-error" role="alert"><XCircle size={15} /> {error}</div>}
        </form>

        <aside className="document-compose-rail">
          <section className="document-rail-card document-context-card" aria-labelledby="document-sources-title"><div className="document-rail-icon"><FolderOpen size={17} /></div><p className="document-card-kicker">{t("document.sources")}</p><h2 id="document-sources-title">{t("document.sourcesHint")}</h2><div className="document-source-list">{loadingSources ? <p className="document-empty-copy">{t("document.loadingSources")}</p> : readyFiles.length === 0 ? <p className="document-empty-copy">{t("document.noSources")}</p> : readyFiles.map((file) => <label className={`document-source-option ${selected.includes(file.id) ? "is-selected" : ""}`} key={file.id}><input type="checkbox" checked={selected.includes(file.id)} disabled={!selected.includes(file.id) && selected.length >= 5} onChange={() => toggleFile(file)} /><FileText size={15} /><span><strong>{file.originalFileName}</strong><small>{file.extension.toUpperCase()} · {Math.ceil(file.sizeBytes / 1024)} KB</small></span>{selected.includes(file.id) && <Check size={14} />}</label>)}</div><div className="document-source-count" aria-live="polite">{selected.length}/5 {t("document.sourceLabel")}</div></section>
          <section className="document-rail-card document-recent-card" aria-labelledby="document-recent-title"><div className="document-recent-heading"><div><p className="document-card-kicker">{t("document.recentTitle")}</p><h2 id="document-recent-title">{t("document.recentSubtitle")}</h2></div><Link href="/assets" className="document-rail-link" aria-label={t("document.openAssets")}><ArrowLeft size={15} /></Link></div>{loadingRecent ? <p className="document-empty-copy">{t("assets.loading")}</p> : recentAssets.length === 0 ? <p className="document-empty-copy">{t("document.recentEmpty")}</p> : <div className="document-recent-list">{recentAssets.map((asset) => <Link className="document-recent-item" key={asset.id} href={`/assets?search=${encodeURIComponent(asset.name)}`}><span className="document-recent-icon"><FileText size={16} /></span><span className="document-recent-copy"><strong>{asset.name}</strong><small>{assetSnippet(asset)}</small><em>{formatAssetDate(asset.createdAt, locale)}</em></span><ArrowLeft size={14} /></Link>)}</div>}</section>
        </aside>
      </div>
      <p className="document-studio-footnote">{t("document.safetyNote")}</p>
    </> : presentation === "failed" || presentation === "cancelled" ? <section className="document-state-card document-terminal-state" aria-live="polite"><div className="document-state-icon is-error"><XCircle size={26} /></div><p className="section-eyebrow">{t(`jobs.status${statusKey}`)}</p><h2>{current.errorMessage || t("document.failedSafe")}</h2><p>{t("document.completedLoadHint")}</p><div className="document-result-actions"><button className="primary-button" onClick={createAnother}><RefreshCw size={15} /> {t("document.newDocument")}</button><Link className="secondary-button" href="/assets"><FolderOpen size={15} /> {t("document.openAssets")}</Link></div></section> : presentation === "succeeded" && result?.assetId ? <section className="document-workspace" aria-live="polite" aria-labelledby="document-reader-title">
      <article className="document-reader">
        <div className="document-reader-topline"><span className="document-status-pill"><CheckCircle2 size={14} /> {t("document.documentReady")}</span><span className="document-reader-type">{result.language || t("document.languageAuto")}</span></div>
        <header className="document-reader-heading"><p className="section-eyebrow">{t("document.resultEyebrow")}</p><h2 id="document-reader-title">{resultTitle}</h2><p>{t("document.previewHint")}</p></header>
        {result.summary && <p className="document-reader-summary">{result.summary}</p>}
        <div className="document-reader-rule" />
        {result.sections?.length ? <div className="document-reader-sections">{result.sections.map((section, sectionIndex) => <section className="document-reader-section" key={`${section.heading}-${sectionIndex}`}><h3><span>{String(sectionIndex + 1).padStart(2, "0")}</span>{section.heading}</h3>{section.blocks.map((block, index) => <div className="document-reader-block" key={`${section.heading}-${index}`}>{block.text && <p>{block.text}</p>}{block.items?.length ? <ul>{block.items.map((item) => <li key={item}>{item}</li>)}</ul> : null}{block.rows?.length ? <DocumentPreviewTable rows={block.rows} label={t("document.preview")} /> : null}</div>)}</section>)}</div> : <p className="document-empty-copy">{t("document.resultSummaryUnavailable")}</p>}
      </article>
      <aside className="document-inspector" aria-labelledby="document-inspector-title">
        <div className="document-inspector-header"><div className="document-inspector-icon"><FileText size={17} /></div><div><p className="document-card-kicker">{t("document.readingView")}</p><h2 id="document-inspector-title">{t("document.documentLabel")}</h2></div></div>
        <div className="document-inspector-meta"><div><span>{t("document.contextLabel")}</span><strong>{projects.find((project) => project.id === current.projectId)?.name || t("document.noProject")}</strong></div><div><span>{t("document.type")}</span><strong>{result.documentType || t("document.typeGeneral")}</strong></div><div><span>{t("document.sources")}</span><strong>{selected.length ? `${selected.length}/5` : t("document.noSources")}</strong></div></div>
        {downloadError && <div className="form-error" role="alert"><XCircle size={15} /> {downloadError}</div>}
        <div className="document-action-stack"><button className="document-action-button is-primary" type="button" onClick={() => void copyDocument()}><Clipboard size={16} /> {copyState === "copied" ? t("document.copied") : t("document.copy")}</button>{result.representations?.map((representation) => <button className="document-action-button" key={representation.id} type="button" onClick={() => void downloadRepresentation(representation.id, representation.fileName)} disabled={working} aria-busy={working} aria-label={`${t("document.downloadLabel")} ${representation.type.toUpperCase()}`}><Download size={16} /> {representation.type.toUpperCase()}</button>)}<Link className="document-action-button" href={`/assets?search=${encodeURIComponent(resultTitle)}`}><FolderOpen size={16} /> {t("document.openAsset")}</Link><button className="document-action-button" type="button" onClick={refineDocument}><WandSparkles size={16} /> {t("document.refine")}</button><button className="document-action-button" type="button" onClick={createAnother}><RefreshCw size={16} /> {t("document.regenerate")}</button></div>
        <p className="sr-only" role="status" aria-live="polite" aria-atomic="true">{copyState === "copied" ? t("document.copied") : copyState === "error" ? t("document.copyError") : ""}</p>
        <div className="document-inspector-footer"><button type="button" className="document-back-button" onClick={refineDocument}><ArrowLeft size={14} /> {t("document.backToBrief")}</button><span>{t("document.savedToAssets")}</span></div>
      </aside>
    </section> : presentation === "completed-unavailable" ? <section className="document-state-card document-terminal-state" aria-live="polite"><div className="document-state-icon"><RefreshCw size={26} /></div><p className="section-eyebrow">{t("jobs.statusSucceeded")}</p><h2>{t("document.completedLoadError")}</h2><p>{t("document.completedLoadHint")}</p><div className="document-result-actions"><button className="primary-button" onClick={() => void retryCompleted()} disabled={retryingCompleted} aria-busy={retryingCompleted}>{retryingCompleted ? t("document.working") : t("document.retry")}</button><Link className="secondary-button" href="/assets"><FolderOpen size={15} /> {t("document.openAssets")}</Link></div></section> : <section className="document-state-card" aria-live="polite" aria-busy={shouldPollDocumentJob(current)}><div className="document-state-icon is-loading"><LoaderCircle size={25} /></div><p className="section-eyebrow">{t("document.progressEyebrow")}</p><h2>{statusLabel}</h2><p>{t("document.progressText")}</p><DocumentProgressMeter progress={displayProgress} label={t("jobs.progress")} announcement={t("jobs.progressAnnouncement", { title: current.title || resultTitle, status: statusLabel, progress: String(displayProgress) })} />{error && <div className="form-error" role="alert"><XCircle size={15} /> {error}</div>}{canCancelDocumentJob(current) && <button className="secondary-button generation-cancel-button" onClick={() => void cancel()} disabled={working} aria-busy={working}><XCircle size={15} /> {t("document.cancel")}</button>}</section>}
  </div>;
}
