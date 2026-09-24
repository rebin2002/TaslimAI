"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Clapperboard, Film, Layers3, MapPin, Plus, Sparkles, Users, WandSparkles } from "lucide-react";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { api, type GenerationJob, type MovieProject, type MovieProviderReadiness, type Project } from "@/lib/api";

const aspects = ["16:9", "9:16", "1:1", "4:5", "4:3"];
const styles = ["cinematic", "documentary", "animation", "commercial", "experimental"];

export function MovieStudioView() {
  const { t } = useLocale();
  const { workspace } = useAuth();
  const searchParams = useSearchParams();
  const [mode, setMode] = useState<"Quick" | "Full">("Quick");
  const [projects, setProjects] = useState<Project[]>([]);
  const [provider, setProvider] = useState<MovieProviderReadiness | null>(null);
  const [saved, setSaved] = useState<MovieProject | null>(null);
  const [job, setJob] = useState<GenerationJob | null>(null);
  const [error, setError] = useState("");
  const [working, setWorking] = useState(false);
  const [form, setForm] = useState({ title: "", description: "", durationSeconds: 30, aspectRatio: "16:9", style: "cinematic", language: "en", projectId: searchParams.get("projectId") ?? "", additionalInstructions: "", visualLanguage: "", cameraLanguage: "", colorAndLighting: "", soundAndNarration: "", continuityRules: "" });
  const [newScene, setNewScene] = useState({ title: "", summary: "" });
  const [newCharacter, setNewCharacter] = useState({ name: "", description: "" });
  const [newLocation, setNewLocation] = useState({ name: "", description: "" });

  useEffect(() => {
    if (!workspace) return;
    void Promise.all([api.listProjects(workspace.id, "Active"), api.getMovieProvider()]).then(([items, readiness]) => { setProjects(items); setProvider(readiness.provider); }).catch(() => undefined);
  }, [workspace]);

  function update<K extends keyof typeof form>(key: K, value: (typeof form)[K]) { setForm((current) => ({ ...current, [key]: value })); }

  async function createMovie(event: React.FormEvent) {
    event.preventDefault();
    if (!workspace) return;
    setWorking(true); setError("");
    try {
      const result = await api.createMovieProject({ workspaceId: workspace.id, projectId: form.projectId || null, mode, title: form.title, description: form.description, durationSeconds: Number(form.durationSeconds), aspectRatio: form.aspectRatio, style: form.style, language: form.language, additionalInstructions: form.additionalInstructions || null, visualLanguage: form.visualLanguage || null, cameraLanguage: form.cameraLanguage || null, colorAndLighting: form.colorAndLighting || null, soundAndNarration: form.soundAndNarration || null, continuityRules: form.continuityRules || null });
      setSaved(result.project); setJob(result.job);
    } catch (cause) { setError(cause instanceof Error ? cause.message : t("movie.error")); }
    finally { setWorking(false); }
  }

  async function add(kind: "scene" | "character" | "location") {
    if (!saved) return;
    setWorking(true); setError("");
    try {
      const updated = kind === "scene"
        ? await api.addMovieScene(saved.id, { title: newScene.title, summary: newScene.summary })
        : kind === "character"
          ? await api.addMovieCharacter(saved.id, { name: newCharacter.name, description: newCharacter.description })
          : await api.addMovieLocation(saved.id, { name: newLocation.name, description: newLocation.description });
      setSaved((current) => current ? { ...current, scenes: kind === "scene" ? [...current.scenes, updated as never] : current.scenes, characters: kind === "character" ? [...current.characters, updated as never] : current.characters, locations: kind === "location" ? [...current.locations, updated as never] : current.locations } : current);
      if (kind === "scene") setNewScene({ title: "", summary: "" });
      if (kind === "character") setNewCharacter({ name: "", description: "" });
      if (kind === "location") setNewLocation({ name: "", description: "" });
    } catch (cause) { setError(cause instanceof Error ? cause.message : t("movie.error")); }
    finally { setWorking(false); }
  }

  function reset() { setSaved(null); setJob(null); setError(""); setForm((current) => ({ ...current, title: "", description: "", additionalInstructions: "" })); }

  useEffect(() => {
    if (!job || job.status === "Succeeded" || job.status === "Failed" || job.status === "Cancelled") return;
    const timer = window.setInterval(() => { void api.getGenerationJob(job.id).then(setJob).catch(() => undefined); }, 3000);
    return () => window.clearInterval(timer);
  }, [job]);

  return <div className="movie-studio-page">
    <section className="movie-hero">
      <div><p className="section-eyebrow"><span className="pulse-dot" /> {t("movie.eyebrow")}</p><h1>{t("movie.title")}</h1><p>{t("movie.subtitle")}</p></div>
      <div className="movie-hero-mark" aria-hidden="true"><Film size={30} /><span>01</span></div>
    </section>
    <div className="movie-workspace-header"><div><p className="section-eyebrow">{t("movie.workspaceEyebrow")}</p><h2>{saved ? saved.title : t("movie.workspaceTitle")}</h2></div><Link className="secondary-button" href="/projects"><Layers3 size={15} /> {t("movie.openProjects")}</Link></div>
    {!saved ? <form className="movie-studio-grid" onSubmit={createMovie}>
      <section className="account-card movie-builder-card">
        <div className="movie-mode-switch" role="tablist" aria-label={t("movie.workflowLabel")}>
          <button type="button" className={mode === "Quick" ? "is-active" : ""} onClick={() => setMode("Quick")}><WandSparkles size={16} /><span><strong>{t("movie.quick")}</strong><small>{t("movie.quickHint")}</small></span></button>
          <button type="button" className={mode === "Full" ? "is-active" : ""} onClick={() => setMode("Full")}><Clapperboard size={16} /><span><strong>{t("movie.full")}</strong><small>{t("movie.fullHint")}</small></span></button>
        </div>
        <div className="movie-form-heading"><span className="movie-number">01</span><div><p className="section-eyebrow">{t("movie.briefEyebrow")}</p><h2>{t("movie.briefTitle")}</h2></div></div>
        <label className="field"><span>{t("movie.titleLabel")}</span><input value={form.title} onChange={(event) => update("title", event.target.value)} placeholder={t("movie.titlePlaceholder")} maxLength={160} required /></label>
        <label className="field"><span>{t("movie.descriptionLabel")}</span><textarea value={form.description} onChange={(event) => update("description", event.target.value)} placeholder={t("movie.descriptionPlaceholder")} rows={5} maxLength={8000} required /><small>{form.description.length}/8000</small></label>
        <div className="movie-control-grid"><label className="field"><span>{t("movie.duration")}</span><div className="movie-duration-input"><input type="number" min={1} max={3600} value={form.durationSeconds} onChange={(event) => update("durationSeconds", Number(event.target.value))} /><span>{t("movie.seconds")}</span></div></label><label className="field"><span>{t("movie.aspect")}</span><select value={form.aspectRatio} onChange={(event) => update("aspectRatio", event.target.value)}>{aspects.map((item) => <option key={item}>{item}</option>)}</select></label><label className="field"><span>{t("movie.style")}</span><select value={form.style} onChange={(event) => update("style", event.target.value)}>{styles.map((item) => <option key={item} value={item}>{t(`movie.style.${item}`)}</option>)}</select></label><label className="field"><span>{t("movie.language")}</span><select value={form.language} onChange={(event) => update("language", event.target.value)}><option value="en">English</option><option value="ar">العربية</option><option value="ku">کوردی</option></select></label></div>
        <label className="field"><span>{t("movie.project")}</span><select value={form.projectId} onChange={(event) => update("projectId", event.target.value)}><option value="">{t("movie.noProject")}</option>{projects.map((project) => <option key={project.id} value={project.id}>{project.name}</option>)}</select><small>{t("movie.projectHint")}</small></label>
        <label className="field"><span>{t("movie.instructions")}</span><textarea value={form.additionalInstructions} onChange={(event) => update("additionalInstructions", event.target.value)} placeholder={t("movie.instructionsPlaceholder")} rows={3} maxLength={8000} /></label>
        {mode === "Full" && <div className="movie-guide-form"><div className="movie-form-heading"><span className="movie-number">02</span><div><p className="section-eyebrow">{t("movie.guideEyebrow")}</p><h2>{t("movie.guideTitle")}</h2></div></div><p className="movie-muted">{t("movie.guideIntro")}</p><div className="movie-guide-grid"><label className="field"><span>{t("movie.visualLanguage")}</span><input value={form.visualLanguage} onChange={(event) => update("visualLanguage", event.target.value)} placeholder={t("movie.visualLanguagePlaceholder")} /></label><label className="field"><span>{t("movie.cameraLanguage")}</span><input value={form.cameraLanguage} onChange={(event) => update("cameraLanguage", event.target.value)} placeholder={t("movie.cameraLanguagePlaceholder")} /></label><label className="field"><span>{t("movie.colorLighting")}</span><input value={form.colorAndLighting} onChange={(event) => update("colorAndLighting", event.target.value)} placeholder={t("movie.colorLightingPlaceholder")} /></label><label className="field"><span>{t("movie.soundNarration")}</span><input value={form.soundAndNarration} onChange={(event) => update("soundAndNarration", event.target.value)} placeholder={t("movie.soundNarrationPlaceholder")} /></label></div><label className="field"><span>{t("movie.continuityRules")}</span><textarea value={form.continuityRules} onChange={(event) => update("continuityRules", event.target.value)} placeholder={t("movie.continuityRulesPlaceholder")} rows={3} /></label></div>}
        {error && <div className="form-error">{error}</div>}<button className="primary-button movie-submit" type="submit" disabled={working || form.description.trim().length < 3}><Sparkles size={16} /> {working ? t("movie.saving") : mode === "Quick" ? t("movie.createQuick") : t("movie.createFull")}</button>
      </section>
      <aside className="movie-side-column"><section className="account-card movie-guide-card"><span className="movie-side-icon"><Clapperboard size={19} /></span><p className="section-eyebrow">{t("movie.guideCardEyebrow")}</p><h2>{t("movie.guideCardTitle")}</h2><p>{t("movie.guideCardText")}</p><div className="movie-guide-points"><span><Film size={14} /> {t("movie.guidePointOne")}</span><span><Users size={14} /> {t("movie.guidePointTwo")}</span><span><MapPin size={14} /> {t("movie.guidePointThree")}</span></div></section><section className="account-card movie-provider-card"><div className="movie-provider-status"><span className={`status-dot ${provider?.ready ? "ready" : ""}`} /><span>{provider?.ready ? t("movie.providerReady") : t("movie.providerUnavailable")}</span></div><p>{t("movie.providerText")}</p></section></aside>
    </form> : <MoviePlan project={saved} job={job} provider={provider} t={t} error={error} working={working} newScene={newScene} newCharacter={newCharacter} newLocation={newLocation} setNewScene={setNewScene} setNewCharacter={setNewCharacter} setNewLocation={setNewLocation} add={add} reset={reset} />}
  </div>;
}

type MoviePlanProps = { project: MovieProject; job: GenerationJob | null; provider: MovieProviderReadiness | null; t: (key: string) => string; error: string; working: boolean; newScene: { title: string; summary: string }; newCharacter: { name: string; description: string }; newLocation: { name: string; description: string }; setNewScene: (value: { title: string; summary: string }) => void; setNewCharacter: (value: { name: string; description: string }) => void; setNewLocation: (value: { name: string; description: string }) => void; add: (kind: "scene" | "character" | "location") => Promise<void>; reset: () => void };
function MoviePlan({ project, job, provider, t, error, working, newScene, newCharacter, newLocation, setNewScene, setNewCharacter, setNewLocation, add, reset }: MoviePlanProps) {
  return <div className="movie-plan-layout"><section className="account-card movie-plan-summary"><div><p className="section-eyebrow">{project.mode === "Quick" ? t("movie.quickPlan") : t("movie.fullPlan")}</p><h2>{project.title}</h2><p>{project.description}</p></div><div className="movie-summary-chips"><span>{project.durationSeconds}s</span><span>{project.aspectRatio}</span><span>{project.style}</span><span>{project.language.toUpperCase()}</span></div></section><section className="account-card movie-provider-banner"><div className="movie-provider-status"><span className={`status-dot ${provider?.ready ? "ready" : ""}`} /><strong>{provider?.ready ? t("movie.providerReady") : t("movie.providerUnavailable")}</strong></div><p>{provider?.ready ? t("movie.providerReadyText") : t("movie.providerUnavailableText")}</p>{project.mode === "Quick" && <span className="movie-job-badge">{job ? `${t("movie.jobQueued")} · ${job.progressPercent}%` : t("movie.jobQueued")}</span>}{job?.status === "Succeeded" && <Link className="secondary-button" href="/assets">{t("movie.openAssets")}</Link>}{job?.status === "Failed" && <div className="form-error">{job.errorMessage || t("movie.error")}</div>}</section>{project.mode === "Full" && <><section className="account-card movie-guide-detail"><div className="movie-detail-heading"><div><p className="section-eyebrow">{t("movie.guideEyebrow")}</p><h2>{t("movie.guideTitle")}</h2></div><span className="movie-guide-stamp">{t("movie.continuityStamp")}</span></div><div className="movie-guide-detail-grid"><div><span>{t("movie.visualLanguage")}</span><p>{project.guide.visualLanguage || t("movie.notSet")}</p></div><div><span>{t("movie.cameraLanguage")}</span><p>{project.guide.cameraLanguage || t("movie.notSet")}</p></div><div><span>{t("movie.colorLighting")}</span><p>{project.guide.colorAndLighting || t("movie.notSet")}</p></div><div><span>{t("movie.soundNarration")}</span><p>{project.guide.soundAndNarration || t("movie.notSet")}</p></div><div className="wide"><span>{t("movie.continuityRules")}</span><p>{project.guide.continuityRules || t("movie.notSet")}</p></div></div></section><div className="movie-board"><PlanningColumn title={t("movie.scenes")} icon={<Film size={16} />} count={project.scenes.length} items={project.scenes.map((item) => `${item.sequence}. ${item.title}`)} fields={<><input value={newScene.title} onChange={(event) => setNewScene({ ...newScene, title: event.target.value })} placeholder={t("movie.sceneTitle")} /><input value={newScene.summary} onChange={(event) => setNewScene({ ...newScene, summary: event.target.value })} placeholder={t("movie.sceneSummary")} /><button type="button" className="secondary-button" onClick={() => void add("scene")} disabled={working || !newScene.title || !newScene.summary}><Plus size={14} /> {t("movie.addScene")}</button></>} /><PlanningColumn title={t("movie.characters")} icon={<Users size={16} />} count={project.characters.length} items={project.characters.map((item) => item.name)} fields={<><input value={newCharacter.name} onChange={(event) => setNewCharacter({ ...newCharacter, name: event.target.value })} placeholder={t("movie.characterName")} /><input value={newCharacter.description} onChange={(event) => setNewCharacter({ ...newCharacter, description: event.target.value })} placeholder={t("movie.characterDescription")} /><button type="button" className="secondary-button" onClick={() => void add("character")} disabled={working || !newCharacter.name || !newCharacter.description}><Plus size={14} /> {t("movie.addCharacter")}</button></>} /><PlanningColumn title={t("movie.locations")} icon={<MapPin size={16} />} count={project.locations.length} items={project.locations.map((item) => item.name)} fields={<><input value={newLocation.name} onChange={(event) => setNewLocation({ ...newLocation, name: event.target.value })} placeholder={t("movie.locationName")} /><input value={newLocation.description} onChange={(event) => setNewLocation({ ...newLocation, description: event.target.value })} placeholder={t("movie.locationDescription")} /><button type="button" className="secondary-button" onClick={() => void add("location")} disabled={working || !newLocation.name || !newLocation.description}><Plus size={14} /> {t("movie.addLocation")}</button></>} /></div></>}{error && <div className="form-error">{error}</div>}<div className="movie-plan-actions"><button className="primary-button" type="button" onClick={reset}>{t("movie.createAnother")}</button><Link className="secondary-button" href="/assets">{t("movie.openAssets")}</Link></div></div>;
}
function PlanningColumn({ title, icon, count, items, fields }: { title: string; icon: React.ReactNode; count: number; items: string[]; fields: React.ReactNode }) { return <section className="account-card movie-planning-column"><div className="movie-column-heading"><div>{icon}<h3>{title}</h3></div><span>{count}</span></div><div className="movie-planned-items">{items.length ? items.map((item) => <div key={item}>{item}</div>) : <p>—</p>}</div><div className="movie-add-form">{fields}</div></section>; }
