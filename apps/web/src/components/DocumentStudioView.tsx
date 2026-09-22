"use client";

import Link from "next/link";
import { useCallback, useEffect, useState } from "react";
import { BookOpen, CheckCircle2, Download, FileText, LoaderCircle, RefreshCw, Sparkles, XCircle } from "lucide-react";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { api, type GenerationJob, type Project, type StoredFile } from "@/lib/api";
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

export function DocumentStudioView() {
  const { workspace } = useAuth();
  const { t } = useLocale();
  const [projects, setProjects] = useState<Project[]>([]);
  const [files, setFiles] = useState<StoredFile[]>([]);
  const [projectId, setProjectId] = useState("");
  const [selected, setSelected] = useState<string[]>([]);
  const [title, setTitle] = useState("");
  const [prompt, setPrompt] = useState("");
  const [documentType, setDocumentType] = useState<"auto" | "report" | "proposal" | "business_letter" | "company_profile" | "meeting_minutes" | "article" | "general">("auto");
  const [length, setLength] = useState<"short" | "standard" | "detailed">("standard");
  const [audience, setAudience] = useState("");
  const [additionalInstructions, setAdditionalInstructions] = useState("");
  const [language, setLanguage] = useState<"auto" | "en" | "ar" | "ku">("auto");
  const [tone, setTone] = useState<"professional" | "concise" | "academic" | "friendly">("professional");
  const [includeTableOfContents, setIncludeTableOfContents] = useState(true);
  const [current, setCurrent] = useState<GenerationJob | null>(null);
  const [loadingSources, setLoadingSources] = useState(true);
  const [working, setWorking] = useState(false);
  const [retryingCompleted, setRetryingCompleted] = useState(false);
  const [pollRetry, setPollRetry] = useState(0);
  const [error, setError] = useState("");
  const [downloadError, setDownloadError] = useState("");

  const loadSources = useCallback(async () => {
    if (!workspace) return;
    setLoadingSources(true);
    try {
      const [active, archived, sourceFiles] = await Promise.all([
        api.listProjects(workspace.id, "Active"),
        api.listProjects(workspace.id, "Archived"),
        api.listFiles(workspace.id),
      ]);
      setProjects([...active, ...archived]);
      setFiles(sourceFiles.filter((file) => extensions.includes(file.extension.toLowerCase())));
    } catch {
      setProjects([]);
      setFiles([]);
    } finally {
      setLoadingSources(false);
    }
  }, [workspace]);

  // Synchronize source documents and project options when the authenticated workspace changes.
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
    setPollRetry(0);
  }

  const result = parseDocumentJobResult(current);
  const presentation = documentPresentationState(current, result);
  const readyFiles = files.filter((file) => isDocumentSourceReady(file));
  const displayProgress = displayDocumentProgress(current);
  const statusKey = current?.status ?? "Queued";

  return <div className="document-studio-page">
    <div className="document-studio-header">
      <div><p className="section-eyebrow">{t("document.eyebrow")}</p><h1>{t("document.title")}</h1><p>{t("document.subtitle")}</p></div>
      <span className="document-studio-header-icon"><BookOpen size={25} /></span>
    </div>
    {!current ? <form className="document-studio-layout" onSubmit={(event) => void create(event)}>
      <section className="account-card document-studio-form-card">
        <div className="card-title"><span className="card-title-icon teal"><Sparkles size={17} /></span><div><h2>{t("document.createTitle")}</h2><p>{t("document.createSubtitle")}</p></div></div>
        <label className="image-primary-field"><span>{t("document.descriptionLabel")}</span><textarea value={prompt} onChange={(event) => setPrompt(event.target.value)} maxLength={8000} placeholder={t("document.descriptionPlaceholder")} required /><small>{prompt.length}/8000</small></label>
        <label className="field"><span>{t("document.project")}</span><select value={projectId} onChange={(event) => setProjectId(event.target.value)} disabled={loadingSources}><option value="">{t("document.noProject")}</option>{projects.map((project) => <option key={project.id} value={project.id}>{project.name}</option>)}</select></label>
        <div className="document-control-grid"><label className="field"><span>{t("document.type")}</span><select value={documentType} onChange={(event) => setDocumentType(event.target.value as typeof documentType)}><option value="auto">{t("document.typeAuto")}</option><option value="report">{t("document.typeReport")}</option><option value="proposal">{t("document.typeProposal")}</option><option value="business_letter">{t("document.typeLetter")}</option><option value="company_profile">{t("document.typeProfile")}</option><option value="meeting_minutes">{t("document.typeMinutes")}</option><option value="article">{t("document.typeArticle")}</option><option value="general">{t("document.typeGeneral")}</option></select></label><label className="field"><span>{t("document.length")}</span><select value={length} onChange={(event) => setLength(event.target.value as typeof length)}><option value="short">{t("document.lengthShort")}</option><option value="standard">{t("document.lengthStandard")}</option><option value="detailed">{t("document.lengthDetailed")}</option></select></label><label className="field"><span>{t("document.tone")}</span><select value={tone} onChange={(event) => setTone(event.target.value as typeof tone)}><option value="professional">{t("document.toneProfessional")}</option><option value="formal">{t("document.toneFormal")}</option><option value="friendly">{t("document.toneFriendly")}</option><option value="persuasive">{t("document.tonePersuasive")}</option><option value="neutral">{t("document.toneNeutral")}</option></select></label><label className="field"><span>{t("document.language")}</span><select value={language} onChange={(event) => setLanguage(event.target.value as typeof language)}><option value="auto">{t("document.languageAuto")}</option><option value="en">{t("document.languageEnglish")}</option><option value="ar">{t("document.languageArabic")}</option><option value="ku">{t("document.languageKurdish")}</option></select></label></div>
        <details className="document-advanced"><summary>{t("document.advanced")}</summary><div className="document-advanced-grid"><label className="field"><span>{t("document.titleLabel")}</span><input value={title} onChange={(event) => setTitle(event.target.value)} maxLength={160} placeholder={t("document.titlePlaceholder")} /></label><label className="field"><span>{t("document.audience")}</span><input value={audience} onChange={(event) => setAudience(event.target.value)} maxLength={400} placeholder={t("document.audiencePlaceholder")} /></label><label className="field document-advanced-wide"><span>{t("document.additionalInstructions")}</span><textarea value={additionalInstructions} onChange={(event) => setAdditionalInstructions(event.target.value)} maxLength={3000} placeholder={t("document.additionalInstructionsPlaceholder")} /></label></div></details>
        <label className="document-checkbox"><input type="checkbox" checked={includeTableOfContents} onChange={(event) => setIncludeTableOfContents(event.target.checked)} /> <span>{t("document.includeContents")}</span></label>
        <div className="document-source-heading"><div><h3>{t("document.sources")}</h3><p>{t("document.sourcesHint")}</p></div><strong>{selected.length}/5</strong></div>
        <div className="document-source-list">{loadingSources ? <p className="usage-empty">{t("document.loadingSources")}</p> : readyFiles.length === 0 ? <p className="usage-empty">{t("document.noSources")}</p> : readyFiles.map((file) => <label className={`document-source-option ${selected.includes(file.id) ? "is-selected" : ""}`} key={file.id}><input type="checkbox" checked={selected.includes(file.id)} onChange={() => toggleFile(file)} /><FileText size={16} /><span><strong>{file.originalFileName}</strong><small>{file.extension.toUpperCase()} · {Math.ceil(file.sizeBytes / 1024)} KB</small></span></label>)}</div>
        {error && <div className="form-error"><XCircle size={15} /> {error}</div>}
        <button className="primary-button" type="submit" disabled={working || prompt.trim().length < 3}><Sparkles size={16} /> {working ? t("document.working") : t("document.generate")}</button>
      </section>
      <aside className="account-card document-studio-guidance"><BookOpen size={26} /><h2>{t("document.guidanceTitle")}</h2><p>{t("document.guidanceText")}</p><ul><li>{t("document.guidanceOne")}</li><li>{t("document.guidanceTwo")}</li><li>{t("document.guidanceThree")}</li></ul><Link className="secondary-button" href="/assets">{t("document.openAssets")}</Link></aside>
    </form> : presentation === "failed" || presentation === "cancelled" ? <section className="account-card document-generation-state document-terminal-state" aria-live="polite"><XCircle size={28} /><p className="section-eyebrow">{t(`jobs.status${statusKey}`)}</p><h2>{current.errorMessage || t("document.failedSafe")}</h2><div className="document-result-actions"><button className="primary-button" onClick={createAnother}><RefreshCw size={15} /> {t("document.createAnother")}</button><Link className="secondary-button" href="/assets">{t("document.openAssets")}</Link></div></section> : presentation === "succeeded" && result?.assetId ? <section className="account-card document-generation-state" aria-live="polite"><div className="document-result-heading"><div><p className="section-eyebrow">{t("document.resultEyebrow")}</p><h2>{result.title || t("document.resultTitle")}</h2></div><span className="form-success"><CheckCircle2 size={16} /> {t("document.savedToAssets")}</span></div><p className="document-result-summary">{result.summary || t("document.resultSummaryUnavailable")}</p>{result.sections?.length ? <div className="document-preview"><p className="section-eyebrow">{t("document.preview")}</p>{result.sections.map((section) => <section className="document-preview-section" key={section.heading}><h3>{section.heading}</h3>{section.blocks.map((block, index) => <div className="document-preview-block" key={`${section.heading}-${index}`}>{block.text && <p>{block.text}</p>}{block.items?.length ? <ul>{block.items.map((item) => <li key={item}>{item}</li>)}</ul> : null}{block.rows?.length ? <div className="document-preview-table">{block.rows.map((row, rowIndex) => <div className="document-preview-row" key={rowIndex}>{row.cells.map((cell, cellIndex) => <span key={`${rowIndex}-${cellIndex}`}>{cell}</span>)}</div>)}</div> : null}</div>)}</section>)}</div> : null}{downloadError && <div className="form-error"><XCircle size={15} /> {downloadError}</div>}<div className="document-result-actions">{result.representations?.map((representation) => <button className="secondary-button" key={representation.id} type="button" onClick={() => void downloadRepresentation(representation.id, representation.fileName)} disabled={working}><Download size={15} /> {representation.type.toUpperCase()}</button>)}{!result.representations?.length && <span className="document-no-downloads">{t("document.noDownloads")}</span>}<Link className="secondary-button" href={`/assets?search=${encodeURIComponent(result.title || "Generated document")}`}>{t("document.openAssets")}</Link><button className="primary-button" onClick={createAnother}><RefreshCw size={15} /> {t("document.createAnother")}</button></div></section> : presentation === "completed-unavailable" ? <section className="account-card document-generation-state document-terminal-state" aria-live="polite"><RefreshCw size={28} /><p className="section-eyebrow">{t("jobs.statusSucceeded")}</p><h2>{t("document.completedLoadError")}</h2><p>{t("document.completedLoadHint")}</p><div className="document-result-actions"><button className="primary-button" onClick={() => void retryCompleted()} disabled={retryingCompleted}>{retryingCompleted ? t("document.working") : t("document.retry")} </button><Link className="secondary-button" href="/assets">{t("document.openAssets")}</Link></div></section> : <section className="account-card document-generation-state" aria-live="polite"><div className="image-progress-icon"><LoaderCircle size={26} /></div><p className="section-eyebrow">{t("document.progressEyebrow")}</p><h2>{t(`jobs.status${statusKey}`)}</h2><p className="image-progress-copy">{t("document.progressText")}</p><div className="generation-progress-label"><span>{t("jobs.progress")}</span><strong>{displayProgress}%</strong></div><div className="generation-progress-track"><span style={{ width: `${displayProgress}%` }} /></div>{error && <div className="form-error"><XCircle size={15} /> {error}</div>}{canCancelDocumentJob(current) && <button className="secondary-button generation-cancel-button" onClick={() => void cancel()} disabled={working}><XCircle size={15} /> {t("document.cancel")}</button>}</section>}
    <p className="document-studio-footnote">{t("document.safetyNote")}</p>
  </div>;
}
