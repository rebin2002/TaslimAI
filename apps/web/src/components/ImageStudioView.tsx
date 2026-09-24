"use client";

/* The private download endpoint requires the browser's authenticated session cookie. */
/* eslint-disable @next/next/no-img-element */

import Link from "next/link";
import { useCallback, useEffect, useMemo, useState } from "react";
import { CheckCircle2, Download, Image as ImageIcon, LoaderCircle, Palette, RefreshCw, Sparkles, XCircle } from "lucide-react";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { useSearchParams } from "next/navigation";
import { api, type GenerationJob, type ImageGenerationInput, type Project } from "@/lib/api";
import { canCancelImageJob, parseImageJobResult } from "@/lib/imageStudioState";

const styles = ["auto", "photorealistic", "product", "illustration", "3d", "minimal", "poster", "social_media"] as const;
const aspects = ["square", "portrait", "landscape"] as const;
const qualities = ["standard", "high"] as const;

export function ImageStudioView() {
  const { workspace } = useAuth();
  const { t } = useLocale();
  const searchParams = useSearchParams();
  const [projects, setProjects] = useState<Project[]>([]);
  const [description, setDescription] = useState("");
  const [style, setStyle] = useState<string>("auto");
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

  // Loading remote projects after the workspace changes is an external synchronization.
  // eslint-disable-next-line react-hooks/set-state-in-effect
  useEffect(() => { void loadProjects(); }, [loadProjects]);

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

  async function generate(event: React.FormEvent) {
    event.preventDefault();
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

  function createAnother() {
    setCurrent(null);
    setError("");
  }

  const result = useMemo(() => parseImageJobResult(current), [current]);
  const isSuccess = current?.status === "Succeeded" && !!result?.assetId;
  const isFailure = current?.status === "Failed" || current?.status === "Cancelled";

  return <div className="image-studio-page">
    <div className="image-studio-header">
      <div><p className="section-eyebrow">{t("image.eyebrow")}</p><h1>{t("image.title")}</h1><p>{t("image.subtitle")}</p></div>
      <span className="image-studio-header-icon"><Palette size={25} /></span>
    </div>

    {!current || isFailure ? <form className="image-studio-layout" onSubmit={(event) => void generate(event)}>
      <section className="account-card image-studio-form-card">
        <div className="card-title"><span className="card-title-icon teal"><Sparkles size={17} /></span><div><h2>{t("image.createTitle")}</h2><p>{t("image.createSubtitle")}</p></div></div>
        <label className="image-primary-field"><span>{t("image.descriptionLabel")}</span><textarea value={description} onChange={(event) => setDescription(event.target.value)} maxLength={4000} placeholder={t("image.descriptionPlaceholder")} aria-label={t("image.descriptionLabel")} required /><small>{description.length}/4000</small></label>
        <div className="image-control-grid">
          <label className="field"><span>{t("image.style")}</span><select value={style} onChange={(event) => setStyle(event.target.value)}>{styles.map((value) => <option key={value} value={value}>{t(`image.style.${value}`)}</option>)}</select></label>
          <label className="field"><span>{t("image.aspectRatio")}</span><select value={aspectRatio} onChange={(event) => setAspectRatio(event.target.value)}>{aspects.map((value) => <option key={value} value={value}>{t(`image.aspect.${value}`)}</option>)}</select></label>
          <label className="field"><span>{t("image.quality")}</span><select value={quality} onChange={(event) => setQuality(event.target.value)}>{qualities.map((value) => <option key={value} value={value}>{t(`image.quality.${value}`)}</option>)}</select></label>
          <label className="field"><span>{t("image.project")}</span><select value={projectId} onChange={(event) => setProjectId(event.target.value)} disabled={loadingProjects}><option value="">{t("image.noProject")}</option>{projects.map((project) => <option key={project.id} value={project.id}>{project.name}</option>)}</select></label>
        </div>
        <details className="image-optional-controls"><summary>{t("image.moreOptions")}</summary><div className="image-optional-grid"><label className="field"><span>{t("image.titleLabel")}</span><input value={title} onChange={(event) => setTitle(event.target.value)} maxLength={120} placeholder={t("image.titlePlaceholder")} /></label><label className="field"><span>{t("image.mood")}</span><input value={mood} onChange={(event) => setMood(event.target.value)} maxLength={120} placeholder={t("image.moodPlaceholder")} /></label><label className="field"><span>{t("image.background")}</span><input value={background} onChange={(event) => setBackground(event.target.value)} maxLength={240} placeholder={t("image.backgroundPlaceholder")} /></label><label className="field image-field-wide"><span>{t("image.textInImage")}</span><input value={textInImage} onChange={(event) => setTextInImage(event.target.value)} maxLength={500} placeholder={t("image.textInImagePlaceholder")} /></label></div></details>
        {error && <div className="form-error"><XCircle size={15} /> {error}</div>}
        {isFailure && current?.errorMessage && <div className="form-error"><XCircle size={15} /> {current.errorMessage}</div>}
        <button className="primary-button image-generate-button" type="submit" disabled={working || description.trim().length < 3}><Sparkles size={16} /> {working ? t("image.working") : t("image.generate")}</button>
      </section>
      <aside className="account-card image-studio-guidance"><ImageIcon size={26} /><h2>{t("image.guidanceTitle")}</h2><p>{t("image.guidanceText")}</p><ul><li>{t("image.guidanceOne")}</li><li>{t("image.guidanceTwo")}</li><li>{t("image.guidanceThree")}</li></ul></aside>
    </form> : <section className="account-card image-generation-state" aria-live="polite">
      {isSuccess && result ? <><div className="image-result-heading"><div><p className="section-eyebrow">{t("image.resultEyebrow")}</p><h2>{t("image.resultTitle")}</h2></div><span className="form-success"><CheckCircle2 size={16} /> {t("image.savedToAssets")}</span></div><div className={`image-result-frame is-${result.aspectRatio ?? "square"}`}>{/* Private authenticated storage endpoint; Next Image cannot attach the session cookie. */}<img crossOrigin="use-credentials" src={api.assetFileUrl(result.assetId!, true)} alt={description} /></div><div className="image-result-actions"><a className="secondary-button" href={api.assetFileUrl(result.assetId!)}><Download size={15} /> {t("image.download")}</a><Link className="secondary-button" href={`/assets?search=${encodeURIComponent(title || "Generated image")}`}>{t("image.openAssets")}</Link><button className="primary-button" onClick={createAnother}><RefreshCw size={15} /> {t("image.createAnother")}</button></div></> : <><div className="image-progress-icon"><LoaderCircle size={26} /></div><p className="section-eyebrow">{t("image.progressEyebrow")}</p><h2>{t(`jobs.status${current?.status ?? "Queued"}`)}</h2><p className="image-progress-copy">{t("image.progressText")}</p><div className="generation-progress-label"><span>{t("jobs.progress")}</span><strong>{current?.progressPercent ?? 0}%</strong></div><div className="generation-progress-track"><span style={{ width: `${current?.progressPercent ?? 0}%` }} /></div>{canCancelImageJob(current) && <button className="secondary-button generation-cancel-button" onClick={() => void cancel()} disabled={working}><XCircle size={15} /> {t("image.cancel")}</button>}</>}
    </section>}
    <p className="image-studio-footnote">{t("image.safetyNote")}</p>
  </div>;
}
