"use client";

/* The private download endpoint requires the browser's authenticated session cookie. */
/* eslint-disable @next/next/no-img-element */

import Link from "next/link";
import { useCallback, useEffect, useMemo, useState } from "react";
import {
  ArrowUpRight,
  Check,
  CheckCircle2,
  ChevronDown,
  Download,
  Image as ImageIcon,
  LoaderCircle,
  Paperclip,
  Palette,
  RefreshCw,
  Sparkles,
  XCircle,
} from "lucide-react";
import { useAuth } from "@/components/AuthProvider";
import { AssetDetail, type AssetDetailLabels } from "@/components/AssetDetail";
import { useLocale } from "@/components/LocaleProvider";
import { useSearchParams } from "next/navigation";
import { api, type Asset, type GenerationJob, type ImageGenerationInput, type Project } from "@/lib/api";
import { canCancelImageJob, parseImageJobResult } from "@/lib/imageStudioState";

const styles = ["auto", "photorealistic", "product", "illustration", "3d", "minimal", "poster", "social_media"] as const;
const aspects = ["square", "portrait", "landscape"] as const;
const qualities = ["standard", "high"] as const;
const purposes = ["product", "marketing", "social", "portrait", "illustration", "background", "other"] as const;

type Purpose = (typeof purposes)[number];

const purposeStyles: Record<Purpose, (typeof styles)[number]> = {
  product: "product",
  marketing: "poster",
  social: "social_media",
  portrait: "photorealistic",
  illustration: "illustration",
  background: "minimal",
  other: "auto",
};

export function ImageStudioView() {
  const { workspace } = useAuth();
  const { t, locale } = useLocale();
  const searchParams = useSearchParams();
  const [projects, setProjects] = useState<Project[]>([]);
  const [recentAssets, setRecentAssets] = useState<Asset[]>([]);
  const [selectedAsset, setSelectedAsset] = useState<Asset | null>(null);
  const [description, setDescription] = useState("");
  const [purpose, setPurpose] = useState<Purpose>("product");
  const [style, setStyle] = useState<string>(purposeStyles.product);
  const [aspectRatio, setAspectRatio] = useState<string>("square");
  const [quality, setQuality] = useState<string>("standard");
  const [title, setTitle] = useState("");
  const [mood, setMood] = useState("");
  const [background, setBackground] = useState("");
  const [textInImage, setTextInImage] = useState("");
  const [projectId, setProjectId] = useState(() => searchParams.get("projectId") ?? "");
  const [current, setCurrent] = useState<GenerationJob | null>(null);
  const [working, setWorking] = useState(false);
  const [loadingProjects, setLoadingProjects] = useState(true);
  const [loadingRecent, setLoadingRecent] = useState(true);
  const [error, setError] = useState("");

  const loadProjects = useCallback(async () => {
    if (!workspace) return;
    setLoadingProjects(true);
    try {
      const [active, archived] = await Promise.all([api.listProjects(workspace.id, "Active"), api.listProjects(workspace.id, "Archived")]);
      setProjects([...active, ...archived]);
    } catch {
      setProjects([]);
    } finally {
      setLoadingProjects(false);
    }
  }, [workspace]);

  const loadRecent = useCallback(async () => {
    if (!workspace) return;
    setLoadingRecent(true);
    try {
      const result = await api.listAssets(workspace.id, { assetType: "image", status: "Active", sort: "recent", page: 1, pageSize: 6 });
      setRecentAssets(result.items);
    } catch {
      setRecentAssets([]);
    } finally {
      setLoadingRecent(false);
    }
  }, [workspace]);

  // Loading remote projects and assets after the workspace changes is an external synchronization.
  // eslint-disable-next-line react-hooks/set-state-in-effect
  useEffect(() => { void loadProjects(); }, [loadProjects]);
  // eslint-disable-next-line react-hooks/set-state-in-effect
  useEffect(() => { void loadRecent(); }, [loadRecent]);

  useEffect(() => {
    if (!current || current.status === "Succeeded" || current.status === "Failed" || current.status === "Cancelled") return;
    let active = true;
    const timer = window.setTimeout(async () => {
      try {
        const next = await api.getGenerationJob(current.id);
        if (active) setCurrent(next);
      } catch (caught) {
        if (active) setError(caught instanceof Error ? caught.message : t("image.pollError"));
      }
    }, 650);
    return () => { active = false; window.clearTimeout(timer); };
  }, [current, t]);

  async function startGeneration() {
    if (!workspace || description.trim().length < 3) {
      setError(t("image.descriptionRequired"));
      return;
    }
    setWorking(true);
    setError("");
    const input: ImageGenerationInput = {
      workspaceId: workspace.id,
      projectId: projectId || null,
      description: description.trim(),
      style,
      aspectRatio,
      quality,
      title: title.trim() || null,
      mood: mood.trim() || null,
      background: background.trim() || null,
      textInImage: textInImage.trim() || null,
    };
    try {
      setCurrent(await api.createImageGenerationJob(input));
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : t("image.createError"));
    } finally {
      setWorking(false);
    }
  }

  async function cancel() {
    if (!current) return;
    setWorking(true);
    setError("");
    try {
      await api.cancelGenerationJob(current.id);
      setCurrent(await api.getGenerationJob(current.id));
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : t("image.cancelError"));
    } finally {
      setWorking(false);
    }
  }

  function choosePurpose(nextPurpose: Purpose) {
    setPurpose(nextPurpose);
    setStyle(purposeStyles[nextPurpose]);
  }

  function createAnother() {
    setCurrent(null);
    setError("");
  }

  const result = useMemo(() => parseImageJobResult(current), [current]);
  const isSuccess = current?.status === "Succeeded" && !!result?.assetId;
  const isFailure = current?.status === "Failed" || current?.status === "Cancelled" || (current?.status === "Succeeded" && !result);
  const failureMessage = current?.errorMessage || (current?.status === "Succeeded" ? t("image.resultUnavailable") : t("image.failedText"));
  const detailLabels = useMemo<AssetDetailLabels>(() => ({
    detailEyebrow: t("assets.detailEyebrow"),
    close: t("common.close"),
    structuredTitle: t("assets.structuredTitle"),
    structuredDescription: t("assets.structuredDescription"),
    previewLabel: t("assets.structuredTitle"),
    summaryLabel: t("assets.description"),
    download: t("assets.download"),
    open: t("assets.open"),
    edit: t("assets.rename"),
    type: t("assets.typeLabel"),
    typeValue: selectedAsset ? t(`assets.type.${selectedAsset.assetType}`) : "",
    created: t("assets.created"),
    project: t("assets.project"),
    noProject: t("assets.noProject"),
    file: t("assets.file"),
    notAvailable: t("assets.notAvailable"),
    source: t("assets.source"),
    manual: t("assets.manualSource"),
    description: t("assets.description"),
    representations: t("assets.representations"),
    studios: {
      image: t("navigation.imageStudio"),
      document: t("navigation.documentStudio"),
      presentation: t("navigation.presentationStudio"),
      research: t("navigation.researchStudio"),
      social: t("navigation.socialStudio"),
      voice: t("navigation.voiceStudio"),
      music: t("navigation.musicStudio"),
      movie: t("navigation.movieStudio"),
      system: t("assets.systemSource"),
    },
  }), [selectedAsset, t]);

  return <div className="image-studio-page">
    <header className="image-studio-header">
      <div className="image-studio-header-copy">
        <div className="image-studio-breadcrumb"><span className="image-studio-mark"><Palette size={15} /></span><span>{t("image.workspaceLabel")}</span><span className="image-studio-slash">/</span><strong>{t("image.title")}</strong></div>
        <p className="section-eyebrow">{t("image.eyebrow")}</p>
        <h1 aria-label={t("image.title")}>{t("image.workspaceTitle")}</h1>
        <p>{t("image.workspaceSubtitle")}</p>
      </div>
      <Link className="image-studio-library-link" href="/assets?assetType=image"><ImageIcon size={15} /> {t("image.openAssets")} <ArrowUpRight size={14} /></Link>
    </header>

    <div className="image-studio-workspace">
      <form className="image-creation-panel" onSubmit={(event) => { event.preventDefault(); void startGeneration(); }}>
        <div className="image-panel-heading"><div><p className="image-panel-kicker">{t("image.createTitle")}</p><h2>{t("image.promptLabel")}</h2></div><span className="image-panel-step">01</span></div>
        <label className="image-prompt-field"><span className="sr-only">{t("image.promptLabel")}</span><textarea value={description} onChange={(event) => setDescription(event.target.value)} maxLength={4000} placeholder={t("image.promptPlaceholder")} aria-label={t("image.promptLabel")} required /><small>{description.length}/4000</small></label>
        <div className="image-panel-tools">
          <button className="image-attachment-button" type="button" disabled title={t("image.referenceUnavailable")}><Paperclip size={15} /> {t("image.attachReference")} <span>{t("image.unavailableTag")}</span></button>
          <label className="image-project-select"><span>{t("image.project")}</span><select value={projectId} onChange={(event) => setProjectId(event.target.value)} disabled={loadingProjects}><option value="">{t("image.noProject")}</option>{projects.map((project) => <option key={project.id} value={project.id}>{project.name}</option>)}</select><ChevronDown size={13} /></label>
        </div>

        <div className="image-option-group"><div className="image-option-label"><span>{t("image.purpose")}</span><small>{t("image.autoHint")}</small></div><div className="image-purpose-grid">{purposes.map((value) => <button key={value} type="button" className={purpose === value ? "is-selected" : ""} aria-pressed={purpose === value} onClick={() => choosePurpose(value)}><span className={`image-purpose-dot is-${value}`} />{t(`image.purpose.${value}`)}</button>)}</div></div>
        <div className="image-option-group"><div className="image-option-label"><span>{t("image.aspectRatio")}</span></div><div className="image-segmented-control">{aspects.map((value) => <button key={value} type="button" className={aspectRatio === value ? "is-selected" : ""} aria-pressed={aspectRatio === value} onClick={() => setAspectRatio(value)}><span className={`image-format-icon is-${value}`} />{t(`image.aspect.${value}`)}</button>)}</div></div>

        <details className="image-advanced-options"><summary>{t("image.moreOptions")} <ChevronDown size={15} /></summary><div className="image-advanced-grid"><label className="image-field"><span>{t("image.style")}</span><select value={style} onChange={(event) => setStyle(event.target.value)}>{styles.map((value) => <option key={value} value={value}>{t(`image.style.${value}`)}</option>)}</select></label><label className="image-field"><span>{t("image.quality")}</span><select value={quality} onChange={(event) => setQuality(event.target.value)}>{qualities.map((value) => <option key={value} value={value}>{t(`image.quality.${value}`)}</option>)}</select></label><label className="image-field"><span>{t("image.titleLabel")}</span><input value={title} onChange={(event) => setTitle(event.target.value)} maxLength={120} placeholder={t("image.titlePlaceholder")} /></label><label className="image-field"><span>{t("image.mood")}</span><input value={mood} onChange={(event) => setMood(event.target.value)} maxLength={120} placeholder={t("image.moodPlaceholder")} /></label><label className="image-field image-field-wide"><span>{t("image.background")}</span><input value={background} onChange={(event) => setBackground(event.target.value)} maxLength={240} placeholder={t("image.backgroundPlaceholder")} /></label><label className="image-field image-field-wide"><span>{t("image.textInImage")}</span><input value={textInImage} onChange={(event) => setTextInImage(event.target.value)} maxLength={500} placeholder={t("image.textInImagePlaceholder")} /></label></div></details>
        {error && <div className="image-panel-error" role="alert"><XCircle size={15} /> {error}</div>}
        <button className="image-generate-button" type="submit" disabled={working || description.trim().length < 3}><span>{working ? <LoaderCircle className="image-button-spinner" size={16} /> : <Sparkles size={16} />}</span>{working ? t("image.working") : t("image.generate")}<span className="image-button-arrow">↗</span></button>
        <p className="image-panel-note"><CheckCircle2 size={13} /> {t("image.privateNote")}</p>
      </form>

      <main className="image-result-canvas" aria-live="polite">
        {!current && <div className="image-empty-canvas"><div className="image-canvas-orbit image-canvas-orbit-one" /><div className="image-canvas-orbit image-canvas-orbit-two" /><div className="image-canvas-core"><Sparkles size={25} /></div><p className="image-canvas-eyebrow">{t("image.canvasEmptyEyebrow")}</p><h2>{t("image.canvasEmptyTitle")}</h2><p>{t("image.canvasEmptyText")}</p><span className="image-canvas-hint">{t("image.canvasHint")}</span></div>}
        {current && !isSuccess && !isFailure && <div className="image-generating-canvas"><div className="image-generating-spinner"><LoaderCircle size={26} /></div><p className="image-canvas-eyebrow">{t("image.progressEyebrow")}</p><h2>{t(`jobs.status${current.status}`)}</h2><p>{t("image.progressText")}</p><div className="image-progress-meta"><span>{t("jobs.progress")}</span><strong>{current.progressPercent ?? 0}%</strong></div><div className="image-progress-track"><span style={{ width: `${current.progressPercent ?? 0}%` }} /></div>{canCancelImageJob(current) && <button className="image-cancel-button" type="button" onClick={() => void cancel()} disabled={working}><XCircle size={15} /> {t("image.cancel")}</button>}</div>}
        {isFailure && <div className="image-failure-canvas"><div className="image-failure-icon"><XCircle size={24} /></div><p className="image-canvas-eyebrow">{t("image.failedEyebrow")}</p><h2>{current?.status === "Cancelled" ? t("jobs.statusCancelled") : t("image.failedTitle")}</h2><p>{failureMessage}</p><button className="image-retry-button" type="button" onClick={() => void startGeneration()} disabled={working}><RefreshCw size={15} /> {t("image.retry")}</button></div>}
        {isSuccess && result && <div className="image-success-canvas"><div className="image-result-heading"><div><p className="image-canvas-eyebrow">{t("image.resultEyebrow")}</p><h2>{t("image.resultTitle")}</h2></div><span className="image-saved-badge"><Check size={14} /> {t("image.savedToAssets")}</span></div><div className={`image-result-frame is-${result.aspectRatio ?? "square"}`}><img crossOrigin="use-credentials" src={api.assetFileUrl(result.assetId!, true)} alt={description} /></div><div className="image-result-actions"><a className="image-result-action" href={api.assetFileUrl(result.assetId!)}><Download size={15} /> {t("image.download")}</a><Link className="image-result-action" href={`/assets?search=${encodeURIComponent(title || "Generated image")}`}><ImageIcon size={15} /> {t("image.openAssets")}</Link><button className="image-result-action is-primary" type="button" onClick={createAnother}><RefreshCw size={15} /> {t("image.createAnother")}</button></div></div>}
      </main>

      <aside className="image-recent-panel">
        <div className="image-recent-heading"><div><p className="image-panel-kicker">{t("image.recentEyebrow")}</p><h2>{t("image.recentTitle")}</h2></div><Link href="/assets?assetType=image" aria-label={t("image.openAssets")}><ArrowUpRight size={16} /></Link></div>
        {loadingRecent ? <div className="image-recent-loading"><span className="image-mini-spinner" /></div> : recentAssets.length === 0 ? <div className="image-recent-empty"><ImageIcon size={21} /><p>{t("image.recentEmpty")}</p><small>{t("image.recentEmptyText")}</small></div> : <div className="image-recent-list">{recentAssets.map((asset) => <button key={asset.id} className="image-recent-item" type="button" onClick={() => setSelectedAsset(asset)}><span className="image-recent-thumb">{asset.hasFile && asset.canPreview ? <img src={api.assetFileUrl(asset.id, true)} alt="" loading="lazy" /> : <ImageIcon size={18} />}</span><span className="image-recent-copy"><strong>{asset.name}</strong><small>{asset.projectName || t("assets.workspaceLevel")}</small><time dateTime={asset.createdAt}>{new Intl.DateTimeFormat(locale, { month: "short", day: "numeric" }).format(new Date(asset.createdAt))}</time></span><ArrowUpRight className="image-recent-arrow" size={14} /></button>)}</div>}
        <div className="image-recent-footer"><CheckCircle2 size={14} /><span>{t("image.libraryNote")}</span></div>
      </aside>
    </div>

    <p className="image-studio-footnote">{t("image.safetyNote")}</p>
    {selectedAsset && <AssetDetail asset={selectedAsset} locale={locale} labels={detailLabels} onClose={() => setSelectedAsset(null)} onEdit={() => setSelectedAsset(null)} />}
  </div>;
}
