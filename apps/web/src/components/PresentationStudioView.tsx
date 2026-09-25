"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { useCallback, useEffect, useState, type FormEvent } from "react";
import {
  ArrowLeft,
  ArrowRight,
  CheckCircle2,
  ChevronDown,
  Download,
  ExternalLink,
  FileText,
  FolderOpen,
  Layers3,
  LoaderCircle,
  LockKeyhole,
  Palette,
  Presentation as PresentationIcon,
  RefreshCw,
  Sparkles,
  XCircle,
} from "lucide-react";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { api, type Asset, type GenerationJob, type PresentationJobResult, type Project, type StoredFile } from "@/lib/api";
import { assetFileUrl } from "@/lib/apiBase";
import {
  canCancelPresentationJob,
  displayPresentationProgress,
  isPresentationSourceReady,
  nextPresentationPollDelay,
  parsePresentationJobResult,
  presentationStudioState,
  shouldPollPresentationJob,
} from "@/lib/presentationStudioState";

const extensions = [".pdf", ".docx", ".txt", ".md", ".csv", ".xlsx"];
type PresentationType = "auto" | "business" | "company_profile" | "sales" | "investor" | "proposal" | "training" | "project_update" | "report" | "educational" | "general";
type Length = "short" | "standard" | "detailed";
type Tone = "professional" | "formal" | "friendly" | "persuasive" | "neutral";
type Language = "auto" | "en" | "ar" | "ku";
type Slide = NonNullable<PresentationJobResult["previewSlides"]>[number];
type SlideBlock = NonNullable<Slide["blocks"]>[number];

function blockItems(block: SlideBlock) {
  return block.items?.filter(Boolean) ?? [];
}

function SlideArtwork({ slide, compact = false }: { slide: Slide; compact?: boolean }) {
  const metrics = slide.blocks?.flatMap((block) => block.metrics ?? []).slice(0, 3) ?? [];
  const columns = slide.blocks?.flatMap((block) => block.columns ?? []).slice(0, 3) ?? [];
  const items = slide.blocks?.flatMap(blockItems).slice(0, 5) ?? [];
  const text = slide.blocks?.find((block) => block.text)?.text;
  const isTitle = slide.type === "title" || slide.type === "cover";

  return (
    <div className={`slide-artwork ${isTitle ? "is-title-slide" : ""} ${compact ? "is-compact" : ""}`} dir="auto">
      <div className="slide-artwork-glow" />
      <div className="slide-artwork-topline"><span>TASLIM / {String(slide.order).padStart(2, "0")}</span><span>{slide.type.replaceAll("_", " ")}</span></div>
      <div className="slide-artwork-content">
        <p className="slide-artwork-kicker">{isTitle ? "Presentation" : `Chapter ${String(slide.order).padStart(2, "0")}`}</p>
        <h3>{slide.title}</h3>
        {slide.subtitle && <p className="slide-artwork-subtitle">{slide.subtitle}</p>}
        {!compact && text && <p className="slide-artwork-text">{text}</p>}
        {!compact && metrics.length > 0 && <div className="slide-metric-row">{metrics.map((metric) => <div key={`${metric.label}-${metric.value}`}><strong>{metric.value}</strong><span>{metric.label}</span></div>)}</div>}
        {!compact && columns.length > 0 && <div className="slide-column-row">{columns.map((column) => <div key={column.heading}><strong>{column.heading}</strong><span>{column.items[0]}</span></div>)}</div>}
        {!compact && items.length > 0 && <ul className="slide-artwork-list">{items.map((item) => <li key={item}>{item}</li>)}</ul>}
      </div>
      <span className="slide-artwork-footer">Taslim.ai</span>
    </div>
  );
}

function EmptySlideCanvas({ title, description }: { title: string; description: string }) {
  return (
    <div className="slide-empty-canvas" dir="auto">
      <div className="slide-empty-grid" />
      <div className="slide-empty-copy">
        <span className="slide-empty-icon"><PresentationIcon size={22} /></span>
        <p className="section-eyebrow">Presentation workspace</p>
        <h2>{title}</h2>
        <p>{description}</p>
      </div>
      <span className="slide-empty-footer">16:9 canvas · ready when you are</span>
    </div>
  );
}

function RecentPresentationCard({ asset }: { asset: Asset }) {
  const imagePreview = asset.canPreview && asset.mimeType?.startsWith("image/");
  return (
    <a className="recent-presentation-card" href={assetFileUrl(asset.id, true)} target="_blank" rel="noreferrer">
      <div className="recent-presentation-thumb">
        {imagePreview ? <div className="recent-presentation-image" style={{ backgroundImage: `url("${assetFileUrl(asset.id, true)}")` }} aria-label="Presentation preview" role="img" /> : <><Layers3 size={20} /><span>{asset.mimeType?.split("/").pop()?.toUpperCase() || "DECK"}</span></>}
      </div>
      <div className="recent-presentation-copy"><strong>{asset.name}</strong><span>{asset.projectName || "Workspace asset"}</span></div>
      <ExternalLink size={14} />
    </a>
  );
}

export function PresentationStudioView() {
  const { workspace } = useAuth();
  const { locale, t } = useLocale();
  const searchParams = useSearchParams();
  const [projects, setProjects] = useState<Project[]>([]);
  const [files, setFiles] = useState<StoredFile[]>([]);
  const [recentPresentations, setRecentPresentations] = useState<Asset[]>([]);
  const [projectId, setProjectId] = useState(() => searchParams.get("projectId") ?? "");
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
  const [activeSlide, setActiveSlide] = useState(0);
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
      const [active, archived, sourceFiles, assetResult] = await Promise.all([
        api.listProjects(workspace.id, "Active"),
        api.listProjects(workspace.id, "Archived"),
        api.listFiles(workspace.id),
        api.listAssets(workspace.id, { assetType: "presentation", sort: "recent", pageSize: 6 }),
      ]);
      setProjects([...active, ...archived]);
      setFiles(sourceFiles.filter((file) => extensions.includes(file.extension.toLowerCase())));
      setRecentPresentations(assetResult.items);
    } catch {
      setProjects([]);
      setFiles([]);
      setRecentPresentations([]);
    } finally {
      setLoadingSources(false);
    }
  }, [workspace]);

  // Source loading synchronizes authenticated workspace data into local UI state.
  // eslint-disable-next-line react-hooks/set-state-in-effect
  useEffect(() => { void loadSources(); }, [loadSources]);
  useEffect(() => {
    if (!current || !shouldPollPresentationJob(current)) return;
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
        setError(t("presentation.pollError"));
      }
    }, nextPresentationPollDelay(current, pollRetry) ?? 700);
    return () => { active = false; window.clearTimeout(timer); };
  }, [current, pollRetry, t]);

  const result = parsePresentationJobResult(current);
  const state = presentationStudioState(current, result);
  const readyFiles = files.filter((file) => isPresentationSourceReady(file));
  const progress = displayPresentationProgress(current);
  const statusKey = current?.status ?? "Queued";
  const slides = result?.previewSlides ?? [];
  const selectedSlide = slides[activeSlide] ?? slides[0];
  const selectedSlideNumber = selectedSlide ? selectedSlide.order : activeSlide + 1;
  const progressSteps = ["Brief", "Structure", "Slides"];

  function toggleFile(file: StoredFile) {
    if (!isPresentationSourceReady(file)) return;
    setSelected((value) => value.includes(file.id) ? value.filter((id) => id !== file.id) : value.length >= 5 ? value : [...value, file.id]);
  }

  async function create(event: FormEvent) {
    event.preventDefault();
    if (!workspace || title.trim().length < 3 || description.trim().length < 3) {
      setError(t("presentation.required"));
      return;
    }
    setWorking(true);
    setError("");
    setDownloadError("");
    try {
      const job = await api.createPresentationGenerationJob({
        workspaceId: workspace.id,
        projectId: projectId || null,
        title: title.trim(),
        description: description.trim(),
        presentationType,
        length,
        tone,
        language,
        audience: audience.trim() || null,
        brandCompany: brandCompany.trim() || null,
        additionalInstructions: additionalInstructions.trim() || null,
        includeAgenda,
        includeClosingNextSteps,
        attachmentIds: selected,
      });
      setCurrent(job);
      setActiveSlide(0);
      setPollRetry(0);
    } catch {
      setError(t("presentation.createError"));
    } finally {
      setWorking(false);
    }
  }

  async function cancel() {
    if (!current || !canCancelPresentationJob(current)) return;
    setWorking(true);
    setError("");
    try {
      await api.cancelGenerationJob(current.id);
      setCurrent(await api.getGenerationJob(current.id));
    } catch {
      setError(t("presentation.cancelError"));
    } finally {
      setWorking(false);
    }
  }

  async function retryCompleted() {
    if (!current) return;
    setRetryingCompleted(true);
    setError("");
    try { setCurrent(await api.getGenerationJob(current.id)); } catch { setError(t("presentation.completedLoadError")); } finally { setRetryingCompleted(false); }
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
      setDownloadError(t("presentation.downloadError"));
    } finally {
      setWorking(false);
    }
  }

  function createAnother() {
    setCurrent(null);
    setActiveSlide(0);
    setError("");
    setDownloadError("");
    setPollRetry(0);
  }

  const dateLabel = result?.title && current?.completedAt ? new Date(current.completedAt).toLocaleDateString(locale, { month: "short", day: "numeric", year: "numeric" }) : "";
  const typeOptions = ["auto", "business", "company_profile", "sales", "investor", "proposal", "training", "project_update", "report", "educational", "general"] as PresentationType[];

  return <div className="presentation-studio-page">
    <div className="presentation-studio-header">
      <div>
        <p className="section-eyebrow">{t("presentation.eyebrow")}</p>
        <h1>{t("presentation.title")}</h1>
        <p>{t("presentation.subtitle")}</p>
      </div>
      <span className="presentation-studio-header-icon"><PresentationIcon size={26} /></span>
    </div>

    {!current ? <>
      <form className="presentation-compose-workspace" onSubmit={(event) => void create(event)}>
        <section className="presentation-compose-stage">
          <div className="presentation-stage-toolbar"><div><span className="stage-label">01 / STORYBOARD</span><strong>Build the story before the slides</strong></div><span className="stage-status"><LockKeyhole size={13} /> Private workspace</span></div>
          <EmptySlideCanvas title={title.trim() || "Your presentation starts here"} description={description.trim() || "Add a topic and purpose to shape your first slide."} />
          <div className="presentation-stage-caption"><span><Sparkles size={14} /> Slide-first creation</span><span>16:9</span></div>
        </section>
        <section className="account-card presentation-brief-card">
          <div className="card-title"><span className="card-title-icon teal"><Sparkles size={17} /></span><div><h2>{t("presentation.createTitle")}</h2><p>{t("presentation.createSubtitle")}</p></div></div>
          <label className="presentation-brief-field"><span>{t("presentation.titleLabel")}</span><input value={title} onChange={(event) => setTitle(event.target.value)} maxLength={160} placeholder={t("presentation.titlePlaceholder")} required /></label>
          <label className="presentation-brief-field presentation-brief-field-large"><span>{t("presentation.descriptionLabel")}</span><textarea value={description} onChange={(event) => setDescription(event.target.value)} maxLength={8000} placeholder={t("presentation.descriptionPlaceholder")} required /><small>{description.length}/8000</small></label>
          <label className="presentation-brief-field"><span>{t("presentation.audience")}</span><input value={audience} onChange={(event) => setAudience(event.target.value)} maxLength={400} placeholder={t("presentation.audiencePlaceholder")} /></label>
          <div className="presentation-compact-grid">
            <label className="field"><span>{t("presentation.type")}</span><select value={presentationType} onChange={(event) => setPresentationType(event.target.value as PresentationType)}>{typeOptions.map((value) => <option key={value} value={value}>{t(`presentation.type.${value}`)}</option>)}</select></label>
            <label className="field"><span>{t("presentation.language")}</span><select value={language} onChange={(event) => setLanguage(event.target.value as Language)}><option value="auto">{t("presentation.language.auto")}</option><option value="en">{t("presentation.language.en")}</option><option value="ar">{t("presentation.language.ar")}</option><option value="ku">{t("presentation.language.ku")}</option></select></label>
            <label className="field"><span>{t("presentation.length")}</span><select value={length} onChange={(event) => setLength(event.target.value as Length)}><option value="short">{t("presentation.length.short")}</option><option value="standard">{t("presentation.length.standard")}</option><option value="detailed">{t("presentation.length.detailed")}</option></select></label>
            <label className="field"><span>{t("presentation.tone")}</span><select value={tone} onChange={(event) => setTone(event.target.value as Tone)}>{(["professional", "formal", "friendly", "persuasive", "neutral"] as Tone[]).map((value) => <option key={value} value={value}>{t(`presentation.tone.${value}`)}</option>)}</select></label>
          </div>
          <details className="presentation-advanced"><summary><Palette size={14} /> {t("presentation.advanced")} <ChevronDown size={14} /></summary><div className="presentation-advanced-grid"><label className="field"><span>{t("presentation.project")}</span><select value={projectId} onChange={(event) => setProjectId(event.target.value)} disabled={loadingSources}><option value="">{t("presentation.noProject")}</option>{projects.map((project) => <option key={project.id} value={project.id}>{project.name}</option>)}</select></label><label className="field"><span>{t("presentation.brandCompany")}</span><input value={brandCompany} onChange={(event) => setBrandCompany(event.target.value)} maxLength={160} placeholder={t("presentation.brandCompanyPlaceholder")} /></label><label className="field presentation-advanced-wide"><span>{t("presentation.additionalInstructions")}</span><textarea value={additionalInstructions} onChange={(event) => setAdditionalInstructions(event.target.value)} maxLength={3000} placeholder={t("presentation.additionalInstructionsPlaceholder")} /></label></div></details>
          <div className="presentation-checkboxes"><label className="presentation-checkbox"><input type="checkbox" checked={includeAgenda} onChange={(event) => setIncludeAgenda(event.target.checked)} /> <span>{t("presentation.includeAgenda")}</span></label><label className="presentation-checkbox"><input type="checkbox" checked={includeClosingNextSteps} onChange={(event) => setIncludeClosingNextSteps(event.target.checked)} /> <span>{t("presentation.includeClosing")}</span></label></div>
          <div className="presentation-source-heading"><div><h3><FolderOpen size={14} /> {t("presentation.sources")}</h3><p>{t("presentation.sourcesHint")}</p></div><strong>{selected.length}/5</strong></div>
          <div className="presentation-source-list">{loadingSources ? <p className="usage-empty">{t("presentation.loadingSources")}</p> : readyFiles.length === 0 ? <p className="usage-empty">{t("presentation.noSources")}</p> : readyFiles.map((file) => <label className={`presentation-source-option ${selected.includes(file.id) ? "is-selected" : ""}`} key={file.id}><input type="checkbox" checked={selected.includes(file.id)} onChange={() => toggleFile(file)} /><FileText size={16} /><span><strong>{file.originalFileName}</strong><small>{file.extension.toUpperCase()} · {Math.ceil(file.sizeBytes / 1024)} KB</small></span></label>)}</div>
          {error && <div className="form-error"><XCircle size={15} /> {error}</div>}
          <button className="primary-button presentation-create-button" type="submit" disabled={working || title.trim().length < 3 || description.trim().length < 3}><Sparkles size={16} /> {working ? t("presentation.working") : t("presentation.generate")} <ArrowRight size={15} /></button>
        </section>
      </form>
      {recentPresentations.length > 0 && <section className="recent-presentations-section"><div className="recent-presentations-heading"><div><p className="section-eyebrow">Your library</p><h2>Recent presentations</h2></div><Link className="text-link" href="/assets?assetType=presentation">View all <ArrowRight size={14} /></Link></div><div className="recent-presentations-grid">{recentPresentations.map((asset) => <RecentPresentationCard asset={asset} key={asset.id} />)}</div></section>}
    </> : state === "failed" || state === "cancelled" ? <section className="account-card presentation-generation-state presentation-terminal-state" aria-live="polite"><XCircle size={28} /><p className="section-eyebrow">{t(`jobs.status${statusKey}`)}</p><h2>{current.errorMessage || t("presentation.failedSafe")}</h2><div className="presentation-result-actions"><button className="primary-button" onClick={createAnother}><RefreshCw size={15} /> {t("presentation.createAnother")}</button><Link className="secondary-button" href="/assets">{t("presentation.openAssets")}</Link></div></section> : state === "succeeded" && result?.assetId ? <section className="presentation-output-workspace" aria-live="polite">
      <aside className="presentation-slide-navigator"><div className="slide-navigator-heading"><div><p className="section-eyebrow">Storyboard</p><strong>{slides.length || result.slideCount || 0} slides</strong></div><span>{selectedSlideNumber}/{slides.length || result.slideCount || 0}</span></div><div className="slide-thumbnail-list">{slides.map((slide, index) => <button className={`slide-thumbnail ${index === activeSlide ? "is-active" : ""}`} key={`${slide.order}-${slide.title}`} onClick={() => setActiveSlide(index)} type="button" aria-label={`Open slide ${slide.order}: ${slide.title}`}><SlideArtwork slide={slide} compact /><span className="slide-thumbnail-label"><b>{String(slide.order).padStart(2, "0")}</b><span>{slide.title}</span></span></button>)}</div></aside>
      <section className="presentation-output-main"><div className="presentation-output-toolbar"><div><p className="section-eyebrow">{t("presentation.resultEyebrow")}</p><h2>{result.title || t("presentation.resultTitle")}</h2><span>{result.slideCount ? t("presentation.slideCount", { count: String(result.slideCount) }) : ""}{dateLabel ? ` · ${dateLabel}` : ""}</span></div><div className="presentation-output-actions">{result.representations?.filter((representation) => representation.type === "pptx").map((representation) => <button className="secondary-button" key={representation.id} type="button" onClick={() => void downloadRepresentation(representation.id, representation.fileName)} disabled={working}><Download size={15} /> PPTX</button>)}<Link className="secondary-button" href="/assets"><ExternalLink size={15} /> {t("presentation.openAssets")}</Link></div></div><div className="presentation-active-slide">{selectedSlide ? <SlideArtwork slide={selectedSlide} /> : <EmptySlideCanvas title={t("presentation.resultTitle")} description={t("presentation.completedLoadHint")} />}<button className="slide-nav-button slide-nav-prev" onClick={() => setActiveSlide((index) => Math.max(0, index - 1))} disabled={activeSlide === 0} aria-label="Previous slide"><ArrowLeft size={17} /></button><button className="slide-nav-button slide-nav-next" onClick={() => setActiveSlide((index) => Math.min(Math.max(0, slides.length - 1), index + 1))} disabled={activeSlide >= slides.length - 1} aria-label="Next slide"><ArrowRight size={17} /></button></div><div className="presentation-output-footer"><span><CheckCircle2 size={15} /> {t("presentation.savedToAssets")}</span><span>{t("presentation.safetyNote")}</span></div>{downloadError && <div className="form-error"><XCircle size={15} /> {downloadError}</div>}</section>
      <aside className="presentation-output-rail"><div className="output-rail-card"><span className="output-rail-icon"><PresentationIcon size={17} /></span><p className="section-eyebrow">Selected slide</p><h3>{selectedSlide?.title || t("presentation.resultTitle")}</h3><p>{selectedSlide?.subtitle || "Review the generated slide content before sharing."}</p><div className="output-rail-meta"><span>{selectedSlide ? `Slide ${selectedSlide.order}` : "—"}</span><span>{selectedSlide?.type.replaceAll("_", " ") || "—"}</span></div></div><div className="output-rail-card output-rail-note"><LockKeyhole size={16} /><strong>Private by default</strong><p>Your presentation is saved to this workspace asset library. Nothing is published automatically.</p></div><button className="primary-button output-new-button" onClick={createAnother}><RefreshCw size={15} /> {t("presentation.createAnother")}</button></aside>
    </section> : state === "completed-unavailable" ? <section className="account-card presentation-generation-state presentation-terminal-state"><RefreshCw size={28} /><p className="section-eyebrow">{t("jobs.statusSucceeded")}</p><h2>{t("presentation.completedLoadError")}</h2><p>{t("presentation.completedLoadHint")}</p><div className="presentation-result-actions"><button className="primary-button" onClick={() => void retryCompleted()} disabled={retryingCompleted}>{retryingCompleted ? t("presentation.working") : t("presentation.retry")}</button><Link className="secondary-button" href="/assets">{t("presentation.openAssets")}</Link></div></section> : <section className="presentation-progress-workspace" aria-live="polite"><div className="presentation-progress-canvas"><div className="presentation-progress-orb"><LoaderCircle size={30} /></div><p className="section-eyebrow">{t("presentation.progressEyebrow")}</p><h2>{t(`jobs.status${statusKey}`)}</h2><p>{t("presentation.progressText")}</p><div className="generation-progress-label"><span>{t("jobs.progress")}</span><strong>{progress}%</strong></div><div className="generation-progress-track"><span style={{ width: `${progress}%` }} /></div></div><div className="presentation-progress-steps">{progressSteps.map((step, index) => <div className={index <= (progress > 66 ? 2 : progress > 33 ? 1 : 0) ? "is-done" : ""} key={step}><span>{index + 1}</span><strong>{step}</strong></div>)}</div>{error && <div className="form-error"><XCircle size={15} /> {error}</div>}{canCancelPresentationJob(current) && <button className="secondary-button generation-cancel-button" onClick={() => void cancel()} disabled={working}><XCircle size={15} /> {t("presentation.cancel")}</button>}</section>}
    <p className="presentation-studio-footnote">{t("presentation.safetyNote")}</p>
  </div>;
}
