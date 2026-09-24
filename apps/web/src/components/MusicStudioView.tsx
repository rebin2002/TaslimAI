"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { useCallback, useEffect, useMemo, useState } from "react";
import { CheckCircle2, Download, Headphones, LoaderCircle, Music2, RefreshCw, Sparkles, XCircle } from "lucide-react";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { api, type GenerationJob, type MusicGenerationInput, type Project } from "@/lib/api";
import { canCancelMusicJob, parseMusicJobResult } from "@/lib/musicStudioState";

const genres = ["auto", "ambient", "cinematic", "classical", "electronic", "folk", "hip_hop", "jazz", "lofi", "pop", "rock", "world", "other"] as const;
const moods = ["calm", "energetic", "uplifting", "melancholic", "inspiring", "dramatic", "playful", "romantic", "focused", "other"] as const;
const durations = [15, 30, 60, 120, 180, 300, 600] as const;
const vocalPreferences = ["instrumental", "vocal", "either"] as const;
const languages = ["auto", "en", "ar", "ku"] as const;

export function MusicStudioView() {
  const { workspace } = useAuth();
  const { t } = useLocale();
  const searchParams = useSearchParams();
  const [projects, setProjects] = useState<Project[]>([]);
  const [description, setDescription] = useState("");
  const [purpose, setPurpose] = useState("");
  const [genre, setGenre] = useState("auto");
  const [mood, setMood] = useState("calm");
  const [durationSeconds, setDurationSeconds] = useState(60);
  const [vocalPreference, setVocalPreference] = useState("instrumental");
  const [language, setLanguage] = useState("auto");
  const [title, setTitle] = useState("");
  const [additionalInstructions, setAdditionalInstructions] = useState("");
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
        if (active) setError(caught instanceof Error ? caught.message : t("music.pollError"));
      }
    }, 650);
    return () => { active = false; window.clearTimeout(timer); };
  }, [current, t]);

  async function generate(event: React.FormEvent) {
    event.preventDefault();
    if (!workspace || description.trim().length < 3 || purpose.trim().length < 3) {
      setError(t("music.required"));
      return;
    }
    setWorking(true);
    setError("");
    const input: MusicGenerationInput = {
      workspaceId: workspace.id,
      projectId: projectId || null,
      description: description.trim(),
      purpose: purpose.trim(),
      genre,
      mood,
      durationSeconds,
      vocalPreference,
      language,
      title: title.trim() || null,
      additionalInstructions: additionalInstructions.trim() || null,
    };
    try {
      setCurrent(await api.createMusicGenerationJob(input));
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : t("music.createError"));
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
      setError(caught instanceof Error ? caught.message : t("music.cancelError"));
    } finally {
      setWorking(false);
    }
  }

  function createAnother() {
    setCurrent(null);
    setError("");
  }

  const result = useMemo(() => parseMusicJobResult(current), [current]);
  const isSuccess = current?.status === "Succeeded" && !!result?.assetId;
  const isFailure = current?.status === "Failed" || current?.status === "Cancelled";

  return <div className="music-studio-page">
    <div className="music-studio-header">
      <div><p className="section-eyebrow">{t("music.eyebrow")}</p><h1>{t("music.title")}</h1><p>{t("music.subtitle")}</p></div>
      <span className="music-studio-header-icon"><Music2 size={25} /></span>
    </div>

    {!current || isFailure ? <form className="music-studio-layout" onSubmit={(event) => void generate(event)}>
      <section className="account-card music-studio-form-card">
        <div className="card-title"><span className="card-title-icon teal"><Sparkles size={17} /></span><div><h2>{t("music.createTitle")}</h2><p>{t("music.createSubtitle")}</p></div></div>
        <label className="music-primary-field"><span>{t("music.descriptionLabel")}</span><textarea value={description} onChange={(event) => setDescription(event.target.value)} maxLength={4000} placeholder={t("music.descriptionPlaceholder")} aria-label={t("music.descriptionLabel")} required /><small>{description.length}/4000</small></label>
        <label className="music-primary-field"><span>{t("music.purpose")}</span><textarea value={purpose} onChange={(event) => setPurpose(event.target.value)} maxLength={1000} placeholder={t("music.purposePlaceholder")} aria-label={t("music.purpose")} required /><small>{purpose.length}/1000</small></label>
        <div className="music-control-grid">
          <label className="field"><span>{t("music.genre")}</span><select value={genre} onChange={(event) => setGenre(event.target.value)}>{genres.map((value) => <option key={value} value={value}>{t(`music.genre.${value}`)}</option>)}</select></label>
          <label className="field"><span>{t("music.mood")}</span><select value={mood} onChange={(event) => setMood(event.target.value)}>{moods.map((value) => <option key={value} value={value}>{t(`music.mood.${value}`)}</option>)}</select></label>
          <label className="field"><span>{t("music.duration")}</span><select value={durationSeconds} onChange={(event) => setDurationSeconds(Number(event.target.value))}>{durations.map((value) => <option key={value} value={value}>{t("music.durationValue", { seconds: String(value) })}</option>)}</select></label>
          <label className="field"><span>{t("music.vocalPreference")}</span><select value={vocalPreference} onChange={(event) => setVocalPreference(event.target.value)}>{vocalPreferences.map((value) => <option key={value} value={value}>{t(`music.vocal.${value}`)}</option>)}</select></label>
          <label className="field"><span>{t("music.language")}</span><select value={language} onChange={(event) => setLanguage(event.target.value)}>{languages.map((value) => <option key={value} value={value}>{t(`music.language.${value}`)}</option>)}</select></label>
          <label className="field"><span>{t("music.project")}</span><select value={projectId} onChange={(event) => setProjectId(event.target.value)} disabled={loadingProjects}><option value="">{t("music.noProject")}</option>{projects.map((project) => <option key={project.id} value={project.id}>{project.name}</option>)}</select></label>
        </div>
        <details className="music-optional-controls"><summary>{t("music.moreOptions")}</summary><div className="music-optional-grid"><label className="field"><span>{t("music.titleLabel")}</span><input value={title} onChange={(event) => setTitle(event.target.value)} maxLength={160} placeholder={t("music.titlePlaceholder")} /></label><label className="field music-field-wide"><span>{t("music.additionalInstructions")}</span><textarea value={additionalInstructions} onChange={(event) => setAdditionalInstructions(event.target.value)} maxLength={2000} placeholder={t("music.additionalInstructionsPlaceholder")} /></label></div></details>
        {error && <div className="form-error"><XCircle size={15} /> {error}</div>}
        {isFailure && current?.errorMessage && <div className="form-error"><XCircle size={15} /> {current.errorMessage}</div>}
        <button className="primary-button music-generate-button" type="submit" disabled={working || description.trim().length < 3 || purpose.trim().length < 3}><Sparkles size={16} /> {working ? t("music.working") : t("music.generate")}</button>
      </section>
      <aside className="account-card music-studio-guidance"><Headphones size={26} /><h2>{t("music.guidanceTitle")}</h2><p>{t("music.guidanceText")}</p><ul><li>{t("music.guidanceOne")}</li><li>{t("music.guidanceTwo")}</li><li>{t("music.guidanceThree")}</li></ul></aside>
    </form> : <section className="account-card music-generation-state" aria-live="polite">
      {isSuccess && result ? <><div className="music-result-heading"><div><p className="section-eyebrow">{t("music.resultEyebrow")}</p><h2>{t("music.resultTitle")}</h2></div><span className="form-success"><CheckCircle2 size={16} /> {t("music.savedToAssets")}</span></div><div className="music-player"><Music2 size={32} /><div><strong>{result.title ?? t("music.generatedTitle")}</strong><small>{result.durationSeconds ? t("music.durationValue", { seconds: String(result.durationSeconds) }) : ""}</small></div><audio controls preload="metadata" crossOrigin="use-credentials" src={api.assetFileUrl(result.assetId!, true)} aria-label={result.title ?? t("music.generatedTitle")} /></div><div className="music-result-actions"><a className="secondary-button" href={api.assetFileUrl(result.assetId!)}><Download size={15} /> {t("music.download")}</a><Link className="secondary-button" href={`/assets?search=${encodeURIComponent(result.title ?? "Generated music")}`}>{t("music.openAssets")}</Link><button className="primary-button" onClick={createAnother}><RefreshCw size={15} /> {t("music.createAnother")}</button></div></> : <><div className="music-progress-icon"><LoaderCircle size={26} /></div><p className="section-eyebrow">{t("music.progressEyebrow")}</p><h2>{t(`jobs.status${current?.status ?? "Queued"}`)}</h2><p className="music-progress-copy">{t("music.progressText")}</p><div className="generation-progress-label"><span>{t("jobs.progress")}</span><strong>{current?.progressPercent ?? 0}%</strong></div><div className="generation-progress-track"><span style={{ width: `${current?.progressPercent ?? 0}%` }} /></div>{canCancelMusicJob(current) && <button className="secondary-button generation-cancel-button" onClick={() => void cancel()} disabled={working}><XCircle size={15} /> {t("music.cancel")}</button>}</>}
    </section>}
    <p className="music-studio-footnote">{t("music.safetyNote")}</p>
  </div>;
}
