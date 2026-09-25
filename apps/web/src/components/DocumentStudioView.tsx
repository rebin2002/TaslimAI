"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { useCallback, useEffect, useState } from "react";
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
import { api, type Asset, type GenerationJob, type Project, type StoredFile } from "@/lib/api";
import {
  canCancelDocumentJob,
  displayDocumentProgress,
  documentPresentationState,
  isDocumentSourceReady,
  nextDocumentPollDelay,
  parseDocumentJobResult,
  shouldPollDocumentJob,
} from "@/lib/documentStudioState";

const extensions = [".pdf", ".docx", ".txt", ".md", ".csv", ".xlsx"];
type DocumentType = "auto" | "report" | "proposal" | "business_letter" | "company_profile" | "meeting_minutes" | "article" | "general";
type DocumentLength = "short" | "standard" | "detailed";
type DocumentLanguage = "auto" | "en" | "ar" | "ku";
type DocumentTone = "professional" | "formal" | "friendly" | "persuasive" | "neutral";

function formatAssetDate(value: string, locale: string) {
  return new Intl.DateTimeFormat(locale, { month: "short", day: "numeric", year: "numeric" }).format(new Date(value));
}

function assetSnippet(asset: Asset) {
  return asset.description || asset.sourceJobTitle || (asset.projectName ? asset.projectName : "Workspace document");
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

  const loadSources = useCallback(async () => {
    if (!workspace) return;
    setLoadingSources(true);
    setLoadingRecent(true);
    const [projectsResult, archivedResult, filesResult, assetsResult] = await Promise.allSettled([
      api.listProjects(workspace.id, "Active"),
      api.listProjects(workspace.id, "Archived"),
      api.listFiles(workspace.id),
      api.listAssets(workspace.id, { assetType: "document", sort: "recent", pageSize: 4 }),
    ]);
    if (projectsResult.status === "fulfilled" && archivedResult.status === "fulfilled") {
      setProjects([...projectsResult.value, ...archivedResult.value]);
    } else {
      setProjects([]);
    }
    setFiles(filesResult.status === "fulfilled" ? filesResult.value.filter((file) => extensions.includes(file.extension.toLowerCase())) : []);
    setRecentAssets(assetsResult.status === "fulfilled" ? assetsResult.value.items : []);
    setLoadingSources(false);
    setLoadingRecent(false);
  }, [workspace]);

  // Synchronize source documents, projects, and recent document assets when the authenticated workspace changes.
  // eslint-disable-next-line react-hooks/set-state-in-effect
  useEffect(() => { void loadSources(); }, [loadSources]);

  useEffect(() => {
    if (!current || !shouldPollDocumentJob(current)) return;
    const jobId = current.id;
    let active = true;
    const timer = window.setTimeout(async () => {
      try {
        const next = await api.getGenerationJob(jobId);
        if (!active) return;
        setCurrent(next);
        setError("");
        setPollRetry(0);
      } catch {
        if (!active) return;
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
    setWorking(true);
    setError("");
    setDownloadError("");
    setCopyState("idle");
    try {
      setCurrent(await api.createDocumentGenerationJob({ workspaceId: workspace.id, projectId: projectId || null, title: title.trim() || null, description: prompt.trim(), documentType, length, audience: audience.trim() || null, additionalInstructions: additionalInstructions.trim() || null, attachmentIds: selected, language, tone, includeTableOfContents }));
      setPollRetry(0);
    } catch {
      setError(t("document.createError"));
    } finally {
      setWorking(false);
    }
  }

  async function cancel() {
    if (!current || !canCancelDocumentJob(current)) return;
    setWorking(true);
    setError("");
    try {
      await api.cancelGenerationJob(current.id);
      setCurrent(await api.getGenerationJob(current.id));
    } catch {
      setError(t("document.cancelError"));
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
      setError(t("document.completedLoadError"));
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
      setDownloadError(t("document.downloadError"));
    } finally {
      setWorking(false);
    }
  }

  function createAnother() {
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
        <form className="document-brief-card" onSubmit={(event) => void create(event)}>
          <div className="document-card-heading">
            <div className="document-card-title"><span className="document-card-icon"><Sparkles size={16} /></span><div><p className="document-card-kicker">{t("document.createTitle")}</p><h2>{t("document.createSubtitle")}</h2></div></div>
            <span className="document-step-count">01</span>
          </div>
          <label className="document-field document-field-primary"><span>{t("document.descriptionLabel")}</span><textarea id="document-brief" value={prompt} onChange={(event) => setPrompt(event.target.value)} maxLength={8000} placeholder={t("document.descriptionPlaceholder")} required /><small>{prompt.length}/8000</small></label>
          <div className="document-field-row"><label className="document-field"><span>{t("document.type")}</span><select value={documentType} onChange={(event) => setDocumentType(event.target.value as DocumentType)}><option value="auto">{t("document.typeAuto")}</option><option value="report">{t("document.typeReport")}</option><option value="proposal">{t("document.typeProposal")}</option><option value="business_letter">{t("document.typeLetter")}</option><option value="company_profile">{t("document.typeProfile")}</option><option value="meeting_minutes">{t("document.typeMinutes")}</option><option value="article">{t("document.typeArticle")}</option><option value="general">{t("document.typeGeneral")}</option></select></label><label className="document-field"><span>{t("document.length")}</span><select value={length} onChange={(event) => setLength(event.target.value as DocumentLength)}><option value="short">{t("document.lengthShort")}</option><option value="standard">{t("document.lengthStandard")}</option><option value="detailed">{t("document.lengthDetailed")}</option></select></label></div>
          <div className="document-field-row"><label className="document-field"><span>{t("document.tone")}</span><select value={tone} onChange={(event) => setTone(event.target.value as DocumentTone)}><option value="professional">{t("document.toneProfessional")}</option><option value="formal">{t("document.toneFormal")}</option><option value="friendly">{t("document.toneFriendly")}</option><option value="persuasive">{t("document.tonePersuasive")}</option><option value="neutral">{t("document.toneNeutral")}</option></select></label><label className="document-field"><span>{t("document.language")}</span><select value={language} onChange={(event) => setLanguage(event.target.value as DocumentLanguage)}><option value="auto">{t("document.languageAuto")}</option><option value="en">{t("document.languageEnglish")}</option><option value="ar">{t("document.languageArabic")}</option><option value="ku">{t("document.languageKurdish")}</option></select></label></div>
          <div className="document-field"><span>{t("document.project")}</span><select value={projectId} onChange={(event) => setProjectId(event.target.value)} disabled={loadingSources}><option value="">{t("document.noProject")}</option>{projects.map((project) => <option key={project.id} value={project.id}>{project.name}</option>)}</select></div>
          <details className="document-advanced"><summary>{t("document.advanced")}</summary><div className="document-advanced-grid"><label className="document-field"><span>{t("document.titleLabel")}</span><input value={title} onChange={(event) => setTitle(event.target.value)} maxLength={160} placeholder={t("document.titlePlaceholder")} /></label><label className="document-field"><span>{t("document.audience")}</span><input value={audience} onChange={(event) => setAudience(event.target.value)} maxLength={400} placeholder={t("document.audiencePlaceholder")} /></label><label className="document-field document-advanced-wide"><span>{t("document.additionalInstructions")}</span><textarea value={additionalInstructions} onChange={(event) => setAdditionalInstructions(event.target.value)} maxLength={3000} placeholder={t("document.additionalInstructionsPlaceholder")} /></label></div></details>
          <div className="document-brief-footer"><label className="document-checkbox"><input type="checkbox" checked={includeTableOfContents} onChange={(event) => setIncludeTableOfContents(event.target.checked)} /> <span>{t("document.includeContents")}</span></label><button className="primary-button document-generate-button" type="submit" disabled={working || prompt.trim().length < 3}><Sparkles size={15} /> {working ? t("document.working") : t("document.generate")}</button></div>
          {error && <div className="form-error" role="alert"><XCircle size={15} /> {error}</div>}
        </form>

        <aside className="document-compose-rail">
          <section className="document-rail-card document-context-card"><div className="document-rail-icon"><FolderOpen size={17} /></div><p className="document-card-kicker">{t("document.sources")}</p><h2>{t("document.sourcesHint")}</h2><div className="document-source-list">{loadingSources ? <p className="document-empty-copy">{t("document.loadingSources")}</p> : readyFiles.length === 0 ? <p className="document-empty-copy">{t("document.noSources")}</p> : readyFiles.map((file) => <label className={`document-source-option ${selected.includes(file.id) ? "is-selected" : ""}`} key={file.id}><input type="checkbox" checked={selected.includes(file.id)} onChange={() => toggleFile(file)} /><FileText size={15} /><span><strong>{file.originalFileName}</strong><small>{file.extension.toUpperCase()} · {Math.ceil(file.sizeBytes / 1024)} KB</small></span>{selected.includes(file.id) && <Check size={14} />}</label>)}</div><div className="document-source-count">{selected.length}/5 {t("document.sourceLabel")}</div></section>
          <section className="document-rail-card document-recent-card"><div className="document-recent-heading"><div><p className="document-card-kicker">{t("document.recentTitle")}</p><h2>{t("document.recentSubtitle")}</h2></div><Link href="/assets" className="document-rail-link" aria-label={t("document.openAssets")}><ArrowLeft size={15} /></Link></div>{loadingRecent ? <p className="document-empty-copy">{t("assets.loading")}</p> : recentAssets.length === 0 ? <p className="document-empty-copy">{t("document.recentEmpty")}</p> : <div className="document-recent-list">{recentAssets.map((asset) => <Link className="document-recent-item" key={asset.id} href={`/assets?search=${encodeURIComponent(asset.name)}`}><span className="document-recent-icon"><FileText size={16} /></span><span className="document-recent-copy"><strong>{asset.name}</strong><small>{assetSnippet(asset)}</small><em>{formatAssetDate(asset.createdAt, locale)}</em></span><ArrowLeft size={14} /></Link>)}</div>}</section>
        </aside>
      </div>
      <p className="document-studio-footnote">{t("document.safetyNote")}</p>
    </> : presentation === "failed" || presentation === "cancelled" ? <section className="document-state-card document-terminal-state" aria-live="polite"><div className="document-state-icon is-error"><XCircle size={26} /></div><p className="section-eyebrow">{t(`jobs.status${statusKey}`)}</p><h2>{current.errorMessage || t("document.failedSafe")}</h2><p>{t("document.completedLoadHint")}</p><div className="document-result-actions"><button className="primary-button" onClick={createAnother}><RefreshCw size={15} /> {t("document.newDocument")}</button><Link className="secondary-button" href="/assets"><FolderOpen size={15} /> {t("document.openAssets")}</Link></div></section> : presentation === "succeeded" && result?.assetId ? <section className="document-workspace" aria-live="polite">
      <article className="document-reader">
        <div className="document-reader-topline"><span className="document-status-pill"><CheckCircle2 size={14} /> {t("document.documentReady")}</span><span className="document-reader-type">{result.language || t("document.languageAuto")}</span></div>
        <header className="document-reader-heading"><p className="section-eyebrow">{t("document.resultEyebrow")}</p><h2>{resultTitle}</h2><p>{t("document.previewHint")}</p></header>
        {result.summary && <p className="document-reader-summary">{result.summary}</p>}
        <div className="document-reader-rule" />
        {result.sections?.length ? <div className="document-reader-sections">{result.sections.map((section, sectionIndex) => <section className="document-reader-section" key={`${section.heading}-${sectionIndex}`}><h3><span>{String(sectionIndex + 1).padStart(2, "0")}</span>{section.heading}</h3>{section.blocks.map((block, index) => <div className="document-reader-block" key={`${section.heading}-${index}`}>{block.text && <p>{block.text}</p>}{block.items?.length ? <ul>{block.items.map((item) => <li key={item}>{item}</li>)}</ul> : null}{block.rows?.length ? <div className="document-preview-table">{block.rows.map((row, rowIndex) => <div className="document-preview-row" key={rowIndex}>{row.cells.map((cell, cellIndex) => <span key={`${rowIndex}-${cellIndex}`}>{cell}</span>)}</div>)}</div> : null}</div>)}</section>)}</div> : <p className="document-empty-copy">{t("document.resultSummaryUnavailable")}</p>}
      </article>
      <aside className="document-inspector">
        <div className="document-inspector-header"><div className="document-inspector-icon"><FileText size={17} /></div><div><p className="document-card-kicker">{t("document.readingView")}</p><h2>{t("document.documentLabel")}</h2></div></div>
        <div className="document-inspector-meta"><div><span>{t("document.contextLabel")}</span><strong>{projects.find((project) => project.id === current.projectId)?.name || t("document.noProject")}</strong></div><div><span>{t("document.type")}</span><strong>{result.documentType || t("document.typeGeneral")}</strong></div><div><span>{t("document.sources")}</span><strong>{selected.length ? `${selected.length}/5` : t("document.noSources")}</strong></div></div>
        {downloadError && <div className="form-error" role="alert"><XCircle size={15} /> {downloadError}</div>}
        <div className="document-action-stack"><button className="document-action-button is-primary" type="button" onClick={() => void copyDocument()}><Clipboard size={16} /> {copyState === "copied" ? t("document.copied") : t("document.copy")}</button>{result.representations?.map((representation) => <button className="document-action-button" key={representation.id} type="button" onClick={() => void downloadRepresentation(representation.id, representation.fileName)} disabled={working}><Download size={16} /> {representation.type.toUpperCase()}</button>)}<Link className="document-action-button" href={`/assets?search=${encodeURIComponent(resultTitle)}`}><FolderOpen size={16} /> {t("document.openAsset")}</Link><button className="document-action-button" type="button" onClick={refineDocument}><WandSparkles size={16} /> {t("document.refine")}</button><button className="document-action-button" type="button" onClick={createAnother}><RefreshCw size={16} /> {t("document.regenerate")}</button></div>
        <div className="document-inspector-footer"><button type="button" className="document-back-button" onClick={refineDocument}><ArrowLeft size={14} /> {t("document.backToBrief")}</button><span>{t("document.savedToAssets")}</span></div>
      </aside>
    </section> : presentation === "completed-unavailable" ? <section className="document-state-card document-terminal-state" aria-live="polite"><div className="document-state-icon"><RefreshCw size={26} /></div><p className="section-eyebrow">{t("jobs.statusSucceeded")}</p><h2>{t("document.completedLoadError")}</h2><p>{t("document.completedLoadHint")}</p><div className="document-result-actions"><button className="primary-button" onClick={() => void retryCompleted()} disabled={retryingCompleted}>{retryingCompleted ? t("document.working") : t("document.retry")}</button><Link className="secondary-button" href="/assets"><FolderOpen size={15} /> {t("document.openAssets")}</Link></div></section> : <section className="document-state-card" aria-live="polite"><div className="document-state-icon is-loading"><LoaderCircle size={25} /></div><p className="section-eyebrow">{t("document.progressEyebrow")}</p><h2>{t(`jobs.status${statusKey}`)}</h2><p>{t("document.progressText")}</p><div className="generation-progress-label"><span>{t("jobs.progress")}</span><strong>{displayProgress}%</strong></div><div className="generation-progress-track"><span style={{ width: `${displayProgress}%` }} /></div>{error && <div className="form-error" role="alert"><XCircle size={15} /> {error}</div>}{canCancelDocumentJob(current) && <button className="secondary-button generation-cancel-button" onClick={() => void cancel()} disabled={working}><XCircle size={15} /> {t("document.cancel")}</button>}</section>}
  </div>;
}
