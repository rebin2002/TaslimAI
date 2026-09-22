"use client";

import Link from "next/link";
import { useCallback, useEffect, useState } from "react";
import { CheckCircle2, Download, FileText, LoaderCircle, Presentation as PresentationIcon, RefreshCw, Sparkles, XCircle } from "lucide-react";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { api, type GenerationJob, type Project, type StoredFile } from "@/lib/api";
import { canCancelPresentationJob, displayPresentationProgress, isPresentationSourceReady, nextPresentationPollDelay, parsePresentationJobResult, presentationStudioState, shouldPollPresentationJob } from "@/lib/presentationStudioState";

const extensions = [".pdf", ".docx", ".txt", ".md", ".csv", ".xlsx"];
type PresentationType = "auto" | "business" | "company_profile" | "sales" | "investor" | "proposal" | "training" | "project_update" | "report" | "educational" | "general";
type Length = "short" | "standard" | "detailed";
type Tone = "professional" | "formal" | "friendly" | "persuasive" | "neutral";
type Language = "auto" | "en" | "ar" | "ku";

export function PresentationStudioView() {
  const { workspace } = useAuth();
  const { t } = useLocale();
  const [projects, setProjects] = useState<Project[]>([]);
  const [files, setFiles] = useState<StoredFile[]>([]);
  const [projectId, setProjectId] = useState("");
  const [selected, setSelected] = useState<string[]>([]);
  const [title, setTitle] = useState("");
  const [description, setDescription] = useState("");
  const [presentationType, setPresentationType] = useState<PresentationType>("auto");
  const [length, setLength] = useState<Length>("standard");
  const [tone, setTone] = useState<Tone>("professional");
  const [language, setLanguage] = useState<Language>("auto");
  const [audience, setAudience] = useState("");
  const [brandCompany, setBrandCompany] = useState("");
  const [additionalInstructions, setAdditionalInstructions] = useState("");
  const [includeAgenda, setIncludeAgenda] = useState(true);
  const [includeClosingNextSteps, setIncludeClosingNextSteps] = useState(true);
  const [current, setCurrent] = useState<GenerationJob | null>(null);
  const [loadingSources, setLoadingSources] = useState(true);
  const [working, setWorking] = useState(false);
  const [pollRetry, setPollRetry] = useState(0);
  const [retryingCompleted, setRetryingCompleted] = useState(false);
  const [error, setError] = useState("");
  const [downloadError, setDownloadError] = useState("");

  const loadSources = useCallback(async () => {
    if (!workspace) return;
    setLoadingSources(true);
    try {
      const [active, archived, sourceFiles] = await Promise.all([api.listProjects(workspace.id, "Active"), api.listProjects(workspace.id, "Archived"), api.listFiles(workspace.id)]);
      setProjects([...active, ...archived]);
      setFiles(sourceFiles.filter((file) => extensions.includes(file.extension.toLowerCase())));
    } catch { setProjects([]); setFiles([]); } finally { setLoadingSources(false); }
  }, [workspace]);

  // Source loading synchronizes authenticated workspace data into local UI state.
  // eslint-disable-next-line react-hooks/set-state-in-effect
  useEffect(() => { void loadSources(); }, [loadSources]);
  useEffect(() => {
    if (!current || !shouldPollPresentationJob(current)) return;
    const jobId = current.id;
    let active = true;
    const timer = window.setTimeout(async () => {
      try { const next = await api.getGenerationJob(jobId); if (!active) return; setCurrent(next); setPollRetry(0); setError(""); }
      catch { if (!active) return; setPollRetry((attempt) => attempt + 1); setError(t("presentation.pollError")); }
    }, nextPresentationPollDelay(current, pollRetry) ?? 700);
    return () => { active = false; window.clearTimeout(timer); };
  }, [current, pollRetry, t]);

  function toggleFile(file: StoredFile) {
    if (!isPresentationSourceReady(file)) return;
    setSelected((value) => value.includes(file.id) ? value.filter((id) => id !== file.id) : value.length >= 5 ? value : [...value, file.id]);
  }
  async function create(event: React.FormEvent) {
    event.preventDefault();
    if (!workspace || description.trim().length < 3) { setError(t("presentation.required")); return; }
    setWorking(true); setError(""); setDownloadError("");
    try {
      const job = await api.createPresentationGenerationJob({ workspaceId: workspace.id, projectId: projectId || null, title: title.trim() || null, description: description.trim(), presentationType, length, tone, language, audience: audience.trim() || null, brandCompany: brandCompany.trim() || null, additionalInstructions: additionalInstructions.trim() || null, includeAgenda, includeClosingNextSteps, attachmentIds: selected });
      setCurrent(job); setPollRetry(0);
    } catch { setError(t("presentation.createError")); } finally { setWorking(false); }
  }
  async function cancel() {
    if (!current || !canCancelPresentationJob(current)) return;
    setWorking(true); setError("");
    try { await api.cancelGenerationJob(current.id); setCurrent(await api.getGenerationJob(current.id)); } catch { setError(t("presentation.cancelError")); } finally { setWorking(false); }
  }
  async function retryCompleted() {
    if (!current) return;
    setRetryingCompleted(true); setError("");
    try { setCurrent(await api.getGenerationJob(current.id)); } catch { setError(t("presentation.completedLoadError")); } finally { setRetryingCompleted(false); }
  }
  async function downloadRepresentation(representationId: string, fileName: string) {
    if (!result?.assetId) return;
    setWorking(true); setDownloadError("");
    try { const blob = await api.downloadAssetRepresentation(result.assetId, representationId); const url = URL.createObjectURL(blob); const anchor = window.document.createElement("a"); anchor.href = url; anchor.download = fileName; anchor.click(); window.setTimeout(() => URL.revokeObjectURL(url), 0); }
    catch { setDownloadError(t("presentation.downloadError")); } finally { setWorking(false); }
  }
  function createAnother() { setCurrent(null); setError(""); setDownloadError(""); setPollRetry(0); }

  const result = parsePresentationJobResult(current);
  const state = presentationStudioState(current, result);
  const readyFiles = files.filter((file) => isPresentationSourceReady(file));
  const progress = displayPresentationProgress(current);
  const statusKey = current?.status ?? "Queued";
  return <div className="presentation-studio-page">
    <div className="presentation-studio-header"><div><p className="section-eyebrow">{t("presentation.eyebrow")}</p><h1>{t("presentation.title")}</h1><p>{t("presentation.subtitle")}</p></div><span className="presentation-studio-header-icon"><PresentationIcon size={26} /></span></div>
    {!current ? <form className="presentation-studio-layout" onSubmit={(event) => void create(event)}>
      <section className="account-card presentation-studio-form-card">
        <div className="card-title"><span className="card-title-icon teal"><Sparkles size={17} /></span><div><h2>{t("presentation.createTitle")}</h2><p>{t("presentation.createSubtitle")}</p></div></div>
        <label className="image-primary-field"><span>{t("presentation.descriptionLabel")}</span><textarea value={description} onChange={(event) => setDescription(event.target.value)} maxLength={8000} placeholder={t("presentation.descriptionPlaceholder")} required /><small>{description.length}/8000</small></label>
        <div className="presentation-control-grid"><label className="field"><span>{t("presentation.type")}</span><select value={presentationType} onChange={(event) => setPresentationType(event.target.value as PresentationType)}>{(["auto", "business", "company_profile", "sales", "investor", "proposal", "training", "project_update", "report", "educational", "general"] as PresentationType[]).map((value) => <option key={value} value={value}>{t(`presentation.type.${value}`)}</option>)}</select></label><label className="field"><span>{t("presentation.length")}</span><select value={length} onChange={(event) => setLength(event.target.value as Length)}><option value="short">{t("presentation.length.short")}</option><option value="standard">{t("presentation.length.standard")}</option><option value="detailed">{t("presentation.length.detailed")}</option></select></label><label className="field"><span>{t("presentation.tone")}</span><select value={tone} onChange={(event) => setTone(event.target.value as Tone)}>{(["professional", "formal", "friendly", "persuasive", "neutral"] as Tone[]).map((value) => <option key={value} value={value}>{t(`presentation.tone.${value}`)}</option>)}</select></label><label className="field"><span>{t("presentation.language")}</span><select value={language} onChange={(event) => setLanguage(event.target.value as Language)}><option value="auto">{t("presentation.language.auto")}</option><option value="en">{t("presentation.language.en")}</option><option value="ar">{t("presentation.language.ar")}</option><option value="ku">{t("presentation.language.ku")}</option></select></label></div>
        <label className="field"><span>{t("presentation.project")}</span><select value={projectId} onChange={(event) => setProjectId(event.target.value)} disabled={loadingSources}><option value="">{t("presentation.noProject")}</option>{projects.map((project) => <option key={project.id} value={project.id}>{project.name}</option>)}</select></label>
        <details className="presentation-advanced"><summary>{t("presentation.advanced")}</summary><div className="presentation-advanced-grid"><label className="field"><span>{t("presentation.titleLabel")}</span><input value={title} onChange={(event) => setTitle(event.target.value)} maxLength={160} placeholder={t("presentation.titlePlaceholder")} /></label><label className="field"><span>{t("presentation.audience")}</span><input value={audience} onChange={(event) => setAudience(event.target.value)} maxLength={400} placeholder={t("presentation.audiencePlaceholder")} /></label><label className="field"><span>{t("presentation.brandCompany")}</span><input value={brandCompany} onChange={(event) => setBrandCompany(event.target.value)} maxLength={160} placeholder={t("presentation.brandCompanyPlaceholder")} /></label><label className="field presentation-advanced-wide"><span>{t("presentation.additionalInstructions")}</span><textarea value={additionalInstructions} onChange={(event) => setAdditionalInstructions(event.target.value)} maxLength={3000} placeholder={t("presentation.additionalInstructionsPlaceholder")} /></label></div></details>
        <div className="presentation-checkboxes"><label className="presentation-checkbox"><input type="checkbox" checked={includeAgenda} onChange={(event) => setIncludeAgenda(event.target.checked)} /> <span>{t("presentation.includeAgenda")}</span></label><label className="presentation-checkbox"><input type="checkbox" checked={includeClosingNextSteps} onChange={(event) => setIncludeClosingNextSteps(event.target.checked)} /> <span>{t("presentation.includeClosing")}</span></label></div>
        <div className="presentation-source-heading"><div><h3>{t("presentation.sources")}</h3><p>{t("presentation.sourcesHint")}</p></div><strong>{selected.length}/5</strong></div>
        <div className="presentation-source-list">{loadingSources ? <p className="usage-empty">{t("presentation.loadingSources")}</p> : readyFiles.length === 0 ? <p className="usage-empty">{t("presentation.noSources")}</p> : readyFiles.map((file) => <label className={`presentation-source-option ${selected.includes(file.id) ? "is-selected" : ""}`} key={file.id}><input type="checkbox" checked={selected.includes(file.id)} onChange={() => toggleFile(file)} /><FileText size={16} /><span><strong>{file.originalFileName}</strong><small>{file.extension.toUpperCase()} · {Math.ceil(file.sizeBytes / 1024)} KB</small></span></label>)}</div>
        {error && <div className="form-error"><XCircle size={15} /> {error}</div>}<button className="primary-button" type="submit" disabled={working || description.trim().length < 3}><Sparkles size={16} /> {working ? t("presentation.working") : t("presentation.generate")}</button>
      </section>
      <aside className="account-card presentation-studio-guidance"><PresentationIcon size={27} /><h2>{t("presentation.guidanceTitle")}</h2><p>{t("presentation.guidanceText")}</p><ul><li>{t("presentation.guidanceOne")}</li><li>{t("presentation.guidanceTwo")}</li><li>{t("presentation.guidanceThree")}</li></ul><Link className="secondary-button" href="/assets">{t("presentation.openAssets")}</Link></aside>
    </form> : state === "failed" || state === "cancelled" ? <section className="account-card presentation-generation-state presentation-terminal-state" aria-live="polite"><XCircle size={28} /><p className="section-eyebrow">{t(`jobs.status${statusKey}`)}</p><h2>{current.errorMessage || t("presentation.failedSafe")}</h2><div className="presentation-result-actions"><button className="primary-button" onClick={createAnother}><RefreshCw size={15} /> {t("presentation.createAnother")}</button><Link className="secondary-button" href="/assets">{t("presentation.openAssets")}</Link></div></section> : state === "succeeded" && result?.assetId ? <section className="account-card presentation-generation-state" aria-live="polite"><div className="presentation-result-heading"><div><p className="section-eyebrow">{t("presentation.resultEyebrow")}</p><h2>{result.title || t("presentation.resultTitle")}</h2></div><span className="form-success"><CheckCircle2 size={16} /> {t("presentation.savedToAssets")}</span></div><p className="presentation-result-meta">{result.slideCount ? t("presentation.slideCount", { count: String(result.slideCount) }) : ""}</p>{result.previewSlides?.length ? <div className="presentation-preview-grid">{result.previewSlides.map((slide) => <article className="presentation-preview-card" key={slide.order}><span>{String(slide.order).padStart(2, "0")} · {slide.type.replaceAll("_", " ")}</span><h3>{slide.title}</h3>{slide.subtitle && <p>{slide.subtitle}</p>}{slide.blocks?.slice(0, 1).map((block, index) => <div key={index}>{block.text && <p>{block.text}</p>}{block.items?.length ? <ul>{block.items.slice(0, 4).map((item) => <li key={item}>{item}</li>)}</ul> : null}</div>)}</article>)}</div> : null}{downloadError && <div className="form-error"><XCircle size={15} /> {downloadError}</div>}<div className="presentation-result-actions">{result.representations?.filter((representation) => representation.type === "pptx").map((representation) => <button className="secondary-button" key={representation.id} type="button" onClick={() => void downloadRepresentation(representation.id, representation.fileName)} disabled={working}><Download size={15} /> PPTX</button>)}{!result.representations?.some((representation) => representation.type === "pptx") && <span className="presentation-no-downloads">{t("presentation.noDownloads")}</span>}<Link className="secondary-button" href="/assets">{t("presentation.openAssets")}</Link><button className="primary-button" onClick={createAnother}><RefreshCw size={15} /> {t("presentation.createAnother")}</button></div></section> : state === "completed-unavailable" ? <section className="account-card presentation-generation-state presentation-terminal-state"><RefreshCw size={28} /><p className="section-eyebrow">{t("jobs.statusSucceeded")}</p><h2>{t("presentation.completedLoadError")}</h2><p>{t("presentation.completedLoadHint")}</p><div className="presentation-result-actions"><button className="primary-button" onClick={() => void retryCompleted()} disabled={retryingCompleted}>{retryingCompleted ? t("presentation.working") : t("presentation.retry")}</button><Link className="secondary-button" href="/assets">{t("presentation.openAssets")}</Link></div></section> : <section className="account-card presentation-generation-state" aria-live="polite"><div className="image-progress-icon"><LoaderCircle size={26} /></div><p className="section-eyebrow">{t("presentation.progressEyebrow")}</p><h2>{t(`jobs.status${statusKey}`)}</h2><p className="image-progress-copy">{t("presentation.progressText")}</p><div className="generation-progress-label"><span>{t("jobs.progress")}</span><strong>{progress}%</strong></div><div className="generation-progress-track"><span style={{ width: `${progress}%` }} /></div>{error && <div className="form-error"><XCircle size={15} /> {error}</div>}{canCancelPresentationJob(current) && <button className="secondary-button generation-cancel-button" onClick={() => void cancel()} disabled={working}><XCircle size={15} /> {t("presentation.cancel")}</button>}</section>}
    <p className="presentation-studio-footnote">{t("presentation.safetyNote")}</p>
  </div>;
}
