"use client";

import { useEffect, useMemo, useState, type FormEvent } from "react";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import {
  ArrowLeft,
  ArrowUpRight,
  Camera,
  Check,
  Clapperboard,
  Download,
  Film,
  Layers3,
  LoaderCircle,
  MapPin,
  Plus,
  Play,
  RefreshCw,
  Sparkles,
  SlidersHorizontal,
  Users,
  WandSparkles,
  XCircle,
} from "lucide-react";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { api, type CinematographyIntentSelection, type CinematographyPreset, type GenerationJob, type MovieProject, type MovieProviderReadiness, type MovieScene, type Project } from "@/lib/api";
import { assetFileUrl } from "@/lib/apiBase";

const aspects = ["16:9", "9:16", "1:1", "4:5", "4:3"];
const styles = ["cinematic", "documentary", "animation", "commercial", "experimental"];
const cinematographyIntents = ["intimate", "natural", "epic", "dynamic"] as const;
type MovieMode = "Quick" | "Full";
type MovieStatus = "empty" | "preparing" | "queued" | "generating" | "completed" | "failed";
type ShotDraft = CinematographyIntentSelection & { description: string; cameraAndFraming: string; cameraMotion: string; durationSeconds: number | null; compositionNotes: string };

function jobStatus(job: GenerationJob | null): MovieStatus {
  if (!job) return "empty";
  if (job.status === "Pending") return "preparing";
  if (job.status === "Queued") return "queued";
  if (job.status === "Running") return "generating";
  if (job.status === "Succeeded") return "completed";
  return "failed";
}

function hasCompletedClip(project: MovieProject) {
  return project.clips.some((clip) => Boolean(clip.assetId) && ["Completed", "Succeeded", "Ready"].includes(clip.status));
}

function getOutputAssetId(project: MovieProject) {
  const assembly = project.assemblies.find((item) => Boolean(item.assetId) && ["Completed", "Succeeded", "Ready"].includes(item.status));
  if (assembly?.assetId) return assembly.assetId;
  return project.clips.find((clip) => Boolean(clip.assetId) && ["Completed", "Succeeded", "Ready"].includes(clip.status))?.assetId ?? null;
}

export function MovieStudioView() {
  const { t } = useLocale();
  const { workspace } = useAuth();
  const searchParams = useSearchParams();
  const [mode, setMode] = useState<MovieMode>("Quick");
  const [projects, setProjects] = useState<Project[]>([]);
  const [provider, setProvider] = useState<MovieProviderReadiness | null>(null);
  const [presets, setPresets] = useState<CinematographyPreset[]>([]);
  const [saved, setSaved] = useState<MovieProject | null>(null);
  const [job, setJob] = useState<GenerationJob | null>(null);
  const [error, setError] = useState("");
  const [working, setWorking] = useState(false);
  const [form, setForm] = useState({ title: "", description: "", durationSeconds: 30, aspectRatio: "16:9", style: "cinematic", language: "en", projectId: searchParams.get("projectId") ?? "", additionalInstructions: "", visualLanguage: "", cameraLanguage: "", colorAndLighting: "", soundAndNarration: "", continuityRules: "", cinematographyIntent: "natural" });
  const [newScene, setNewScene] = useState({ title: "", summary: "" });

  useEffect(() => {
    if (!workspace) return;
    void Promise.all([api.listProjects(workspace.id, "Active"), api.getMovieProvider(), api.getCinematographyPresets()]).then(([items, readiness, catalog]) => {
      setProjects(items);
      setProvider(readiness.provider);
      setPresets(catalog);
    }).catch(() => undefined);
  }, [workspace]);

  function update<K extends keyof typeof form>(key: K, value: (typeof form)[K]) {
    setForm((current) => ({ ...current, [key]: value }));
  }

  async function createMovie(event: FormEvent) {
    event.preventDefault();
    if (!workspace) return;
    setWorking(true);
    setError("");
    try {
      const result = await api.createMovieProject({
        workspaceId: workspace.id,
        projectId: form.projectId || null,
        mode,
        title: form.title,
        description: form.description,
        durationSeconds: Number(form.durationSeconds),
        aspectRatio: form.aspectRatio,
        style: form.style,
        language: form.language,
        additionalInstructions: form.additionalInstructions || null,
        visualLanguage: form.visualLanguage || null,
        cameraLanguage: form.cameraLanguage || null,
        colorAndLighting: form.colorAndLighting || null,
        soundAndNarration: form.soundAndNarration || null,
        continuityRules: form.continuityRules || null,
        cinematography: { intent: form.cinematographyIntent, presetId: presets.find((preset) => preset.intent === form.cinematographyIntent)?.id ?? null },
      });
      setSaved(result.project);
      setJob(result.job);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : t("movie.error"));
    } finally {
      setWorking(false);
    }
  }

  async function addScene() {
    if (!saved || !newScene.title.trim() || !newScene.summary.trim()) return;
    setWorking(true);
    setError("");
    try {
      const scene = await api.addMovieScene(saved.id, { title: newScene.title.trim(), summary: newScene.summary.trim() });
      setSaved((current) => current ? { ...current, scenes: [...current.scenes, scene] } : current);
      setNewScene({ title: "", summary: "" });
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : t("movie.error"));
    } finally {
      setWorking(false);
    }
  }

  async function addShot(sceneId: string, draft: ShotDraft) {
    if (!saved || !draft.description.trim()) return;
    setWorking(true);
    setError("");
    try {
      const shot = await api.addMovieShot(sceneId, {
        description: draft.description.trim(),
        cameraAndFraming: draft.cameraAndFraming.trim() || null,
        cameraMotion: draft.cameraMotion.trim() || null,
        durationSeconds: draft.durationSeconds,
        visualContinuityNotes: draft.compositionNotes.trim() || null,
        cinematography: {
          intent: draft.intent ?? null,
          presetId: draft.presetId ?? null,
          notes: draft.notes ?? null,
          shotSize: draft.shotSize ?? null,
          focalLength: draft.focalLength ?? null,
          lensIntent: draft.lensIntent ?? null,
          apertureDepthOfField: draft.apertureDepthOfField ?? null,
          cameraAngle: draft.cameraAngle ?? null,
          cameraMovement: draft.cameraMovement ?? null,
          frameRateIntent: draft.frameRateIntent ?? null,
          lighting: draft.lighting ?? null,
          paletteLook: draft.paletteLook ?? null,
          compositionNotes: draft.compositionNotes ?? null,
        },
      });
      setSaved((current) => current ? { ...current, scenes: current.scenes.map((scene) => scene.id === sceneId ? { ...scene, shots: [...scene.shots, shot] } : scene) } : current);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : t("movie.error"));
    } finally {
      setWorking(false);
    }
  }

  async function generateScene(sceneId: string) {
    if (!saved) return;
    setWorking(true);
    setError("");
    try {
      const result = await api.generateMovieScene(saved.id, sceneId);
      setSaved(result.project);
      setJob(result.job);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : t("movie.error"));
    } finally {
      setWorking(false);
    }
  }

  function reset() {
    setSaved(null);
    setJob(null);
    setError("");
    setMode("Quick");
    setForm((current) => ({ ...current, title: "", description: "", additionalInstructions: "" }));
  }

  useEffect(() => {
    if (!job || ["Succeeded", "Failed", "Cancelled"].includes(job.status)) return;
    const timer = window.setInterval(() => {
      void api.getGenerationJob(job.id).then((nextJob) => {
        setJob(nextJob);
        if (["Succeeded", "Failed", "Cancelled"].includes(nextJob.status) && saved) {
          void api.getMovieProject(saved.id).then(setSaved).catch(() => undefined);
        }
      }).catch(() => undefined);
    }, 3000);
    return () => window.clearInterval(timer);
  }, [job, saved]);

  const status = jobStatus(job);
  const outputAssetId = saved ? getOutputAssetId(saved) : null;
  const completed = Boolean(outputAssetId) || Boolean(job?.status === "Succeeded" && saved && (saved.mode === "Quick" || hasCompletedClip(saved)));

  return (
    <div className="movie-studio-page">
      <header className="movie-studio-header">
        <div className="movie-studio-header-copy">
          <div className="movie-breadcrumb"><span>01</span><span className="movie-breadcrumb-line" />{t("movie.eyebrow")}</div>
          <h1>{t("movie.title")}</h1>
          <p>{t("movie.subtitle")}</p>
        </div>
        <div className="movie-header-emblem" aria-hidden="true"><Film size={23} /><span>STORY / MOTION</span></div>
      </header>

      {!saved ? (
        <form className="movie-creation-layout" onSubmit={createMovie}>
          <section className="movie-brief-card">
            <div className="movie-card-topline"><span>{t("movie.workspaceEyebrow")}</span><span>{t("movie.emptyTitle")}</span></div>
            <div className="movie-mode-switch" role="tablist" aria-label={t("movie.workflowLabel")}>
              <button type="button" className={mode === "Quick" ? "is-active" : ""} onClick={() => setMode("Quick")}><WandSparkles size={17} /><span><strong>{t("movie.quick")}</strong><small>{t("movie.quickHint")}</small></span><Check size={14} className="movie-mode-check" /></button>
              <button type="button" className={mode === "Full" ? "is-active" : ""} onClick={() => setMode("Full")}><Clapperboard size={17} /><span><strong>{t("movie.full")}</strong><small>{t("movie.fullHint")}</small></span><Check size={14} className="movie-mode-check" /></button>
            </div>

            <div className="movie-section-heading"><span className="movie-step-index">01</span><div><p className="section-eyebrow">{t("movie.briefEyebrow")}</p><h2>{t("movie.briefTitle")}</h2></div></div>
            <label className="movie-field"><span>{t("movie.titleLabel")}</span><input value={form.title} onChange={(event) => update("title", event.target.value)} placeholder={t("movie.titlePlaceholder")} maxLength={160} required /></label>
            <label className="movie-field movie-field-large"><span>{t("movie.descriptionLabel")}</span><textarea value={form.description} onChange={(event) => update("description", event.target.value)} placeholder={t("movie.descriptionPlaceholder")} rows={6} maxLength={8000} required /><small>{form.description.length}/8000</small></label>

            <div className="movie-control-grid">
              <label className="movie-field"><span>{t("movie.duration")}</span><div className="movie-input-with-suffix"><input type="number" min={1} max={3600} value={form.durationSeconds} onChange={(event) => update("durationSeconds", Number(event.target.value))} /><em>{t("movie.seconds")}</em></div></label>
              <label className="movie-field"><span>{t("movie.aspect")}</span><select value={form.aspectRatio} onChange={(event) => update("aspectRatio", event.target.value)}>{aspects.map((item) => <option key={item}>{item}</option>)}</select></label>
              <label className="movie-field"><span>{t("movie.style")}</span><select value={form.style} onChange={(event) => update("style", event.target.value)}>{styles.map((item) => <option key={item} value={item}>{t(`movie.style.${item}`)}</option>)}</select></label>
              <label className="movie-field"><span>{t("movie.language")}</span><select value={form.language} onChange={(event) => update("language", event.target.value)}><option value="en">English</option><option value="ar">العربية</option><option value="ku">کوردی</option></select></label>
            </div>

            <label className="movie-field"><span>{t("movie.project")}</span><select value={form.projectId} onChange={(event) => update("projectId", event.target.value)}><option value="">{t("movie.noProject")}</option>{projects.map((project) => <option key={project.id} value={project.id}>{project.name}</option>)}</select><small>{t("movie.projectHint")}</small></label>
            <label className="movie-field"><span>{t("movie.instructions")}</span><textarea value={form.additionalInstructions} onChange={(event) => update("additionalInstructions", event.target.value)} placeholder={t("movie.instructionsPlaceholder")} rows={3} maxLength={8000} /></label>
            <div className="movie-intent-picker" aria-label={t("movie.cinematographyIntent")}>
              <div className="movie-intent-heading"><span><Camera size={14} /> {t("movie.cinematographyIntent")}</span><small>{t("movie.cinematographyIntentHint")}</small></div>
              <div className="movie-intent-options">{cinematographyIntents.map((intent) => <button key={intent} type="button" className={form.cinematographyIntent === intent ? "is-active" : ""} onClick={() => update("cinematographyIntent", intent)}>{t(`movie.intent.${intent}`)}</button>)}</div>
            </div>

            {mode === "Full" && <div className="movie-continuity-form"><div className="movie-section-heading"><span className="movie-step-index">02</span><div><p className="section-eyebrow">{t("movie.guideEyebrow")}</p><h2>{t("movie.guideTitle")}</h2></div></div><p className="movie-field-note">{t("movie.guideIntro")}</p><div className="movie-control-grid"><label className="movie-field"><span>{t("movie.visualLanguage")}</span><input value={form.visualLanguage} onChange={(event) => update("visualLanguage", event.target.value)} placeholder={t("movie.visualLanguagePlaceholder")} /></label><label className="movie-field"><span>{t("movie.cameraLanguage")}</span><input value={form.cameraLanguage} onChange={(event) => update("cameraLanguage", event.target.value)} placeholder={t("movie.cameraLanguagePlaceholder")} /></label><label className="movie-field"><span>{t("movie.colorLighting")}</span><input value={form.colorAndLighting} onChange={(event) => update("colorAndLighting", event.target.value)} placeholder={t("movie.colorLightingPlaceholder")} /></label><label className="movie-field"><span>{t("movie.soundNarration")}</span><input value={form.soundAndNarration} onChange={(event) => update("soundAndNarration", event.target.value)} placeholder={t("movie.soundNarrationPlaceholder")} /></label></div><label className="movie-field"><span>{t("movie.continuityRules")}</span><textarea value={form.continuityRules} onChange={(event) => update("continuityRules", event.target.value)} placeholder={t("movie.continuityRulesPlaceholder")} rows={3} /></label></div>}
            {error && <div className="form-error"><XCircle size={15} /> {error}</div>}
            <div className="movie-submit-row"><span>{t("movie.createHint")}</span><button className="primary-button movie-submit" type="submit" disabled={working || form.description.trim().length < 3}><Sparkles size={16} /> {working ? t("movie.saving") : mode === "Quick" ? t("movie.createQuick") : t("movie.createFull")}</button></div>
          </section>

          <aside className="movie-empty-stage">
            <div className="movie-stage-label"><span>{t("movie.storyboard")}</span><span>00 / 00</span></div>
            <div className="movie-empty-frame"><div className="movie-frame-grid" /><div className="movie-empty-mark"><Film size={30} /><span>{t("movie.emptyTitle")}</span><small>{t("movie.emptyText")}</small></div></div>
            <div className="movie-stage-footer"><span><span className="movie-status-dot" /> {t("movie.ready")}</span><span>{t("movie.sceneCount", { count: "0" })}</span></div>
          </aside>
        </form>
      ) : (
        <MovieProductionWorkspace project={saved} job={job} status={status} completed={completed} outputAssetId={outputAssetId} provider={provider} presets={presets} t={t} error={error} working={working} newScene={newScene} setNewScene={setNewScene} addScene={addScene} addShot={addShot} generateScene={generateScene} reset={reset} />
      )}
    </div>
  );
}

type ProductionProps = { project: MovieProject; job: GenerationJob | null; status: MovieStatus; completed: boolean; outputAssetId: string | null; provider: MovieProviderReadiness | null; presets: CinematographyPreset[]; t: (key: string, variables?: Record<string, string>) => string; error: string; working: boolean; newScene: { title: string; summary: string }; setNewScene: (value: { title: string; summary: string }) => void; addScene: () => Promise<void>; addShot: (sceneId: string, draft: ShotDraft) => Promise<void>; generateScene: (sceneId: string) => Promise<void>; reset: () => void };

function MovieProductionWorkspace({ project, job, status, completed, outputAssetId, provider, presets, t, error, working, newScene, setNewScene, addScene, addShot, generateScene, reset }: ProductionProps) {
  const outputLabel = completed ? t("movie.state.completed") : t(`movie.state.${status}`);
  return (
    <div className="movie-production-workspace">
      <div className="movie-production-toolbar"><Link href="/create" className="movie-back-link"><ArrowLeft size={15} /> {t("movie.backToStudios")}</Link><div className="movie-toolbar-project"><span>{project.mode === "Quick" ? t("movie.quick") : t("movie.full")}</span><strong>{project.title}</strong></div><button className="movie-refine-button" type="button" onClick={reset}><RefreshCw size={14} /> {t("movie.createAnother")}</button></div>
      <div className="movie-production-grid">
        <aside className="movie-project-rail">
          <div className="movie-rail-heading"><span className="section-eyebrow">{t("movie.projectStatus")}</span><span className={`movie-status-pill ${status}`}>{outputLabel}</span></div>
          <h2>{project.title}</h2><p>{project.description}</p>
          <div className="movie-project-meta"><span>{project.aspectRatio}</span><span>{project.durationSeconds}s</span><span>{t(`movie.style.${project.style}`)}</span></div>
          <div className="movie-rail-rule" />
          <div className="movie-rail-list"><span><Film size={14} /> {t("movie.sceneCount", { count: String(project.scenes.length) })}</span><span><Users size={14} /> {project.characters.length} {t("movie.characters")}</span><span><MapPin size={14} /> {project.locations.length} {t("movie.locations")}</span></div>
          <GenerationState status={status} job={job} t={t} provider={provider} />
        </aside>

        <main className="movie-storyboard-panel">
          <div className="movie-panel-heading"><div><span className="section-eyebrow">{t("movie.storyboard")}</span><h2>{project.mode === "Quick" ? t("movie.quickPlan") : t("movie.fullPlan")}</h2></div><span className="movie-panel-count">{project.scenes.length.toString().padStart(2, "0")} {t("movie.scenes")}</span></div>
          {project.scenes.length ? <div className="movie-scene-list">{project.scenes.map((scene, index) => <MovieSceneCard key={scene.id} scene={scene} index={index} t={t} working={working} presets={presets} onAddShot={addShot} onGenerate={generateScene} />)}</div> : <div className="movie-storyboard-empty"><div className="movie-empty-thumb"><Film size={23} /></div><h3>{t("movie.storyboardEmpty")}</h3><p>{t("movie.storyboardEmptyText")}</p></div>}
          {project.mode === "Full" && <form className="movie-add-scene" onSubmit={(event) => { event.preventDefault(); void addScene(); }}><div className="movie-add-scene-title"><Plus size={16} /><strong>{t("movie.addScene")}</strong></div><input value={newScene.title} onChange={(event) => setNewScene({ ...newScene, title: event.target.value })} placeholder={t("movie.sceneTitle")} /><input value={newScene.summary} onChange={(event) => setNewScene({ ...newScene, summary: event.target.value })} placeholder={t("movie.sceneSummary")} /><button className="secondary-button" type="submit" disabled={working || !newScene.title.trim() || !newScene.summary.trim()}><Plus size={14} /> {t("movie.addScene")}</button></form>}
          {error && <div className="form-error"><XCircle size={15} /> {error}</div>}
        </main>

        <aside className="movie-output-column"><MovieOutput project={project} completed={completed} outputAssetId={outputAssetId} t={t} /><ContinuitySummary project={project} t={t} /></aside>
      </div>
    </div>
  );
}

function GenerationState({ status, job, provider, t }: { status: MovieStatus; job: GenerationJob | null; provider: MovieProviderReadiness | null; t: (key: string, variables?: Record<string, string>) => string }) {
  const percent = job?.progressPercent ?? 0;
  return <section className="movie-generation-state"><div className="movie-state-heading"><span className={`movie-state-icon ${status}`}>{status === "completed" ? <Check size={15} /> : status === "failed" ? <XCircle size={15} /> : status === "empty" ? <Film size={15} /> : <LoaderCircle size={15} />}</span><div><strong>{t(`movie.state.${status}`)}</strong><small>{t(`movie.state.${status}Text`)}</small></div></div>{["preparing", "queued", "generating"].includes(status) && <><div className="movie-progress"><span style={{ width: `${Math.max(5, percent)}%` }} /></div><div className="movie-progress-meta"><span>{t("movie.progress")}</span><strong>{percent}%</strong></div></>}{status === "failed" && <p className="movie-safe-failure">{t("movie.failedSafe")}</p>}{status === "empty" && <p className="movie-safe-failure">{provider?.ready ? t("movie.availableText") : t("movie.unavailableText")}</p>}</section>;
}

function MovieSceneCard({ scene, index, t, working, presets, onAddShot, onGenerate }: { scene: MovieScene; index: number; t: (key: string, variables?: Record<string, string>) => string; working: boolean; presets: CinematographyPreset[]; onAddShot: (sceneId: string, draft: ShotDraft) => Promise<void>; onGenerate: (sceneId: string) => Promise<void> }) {
  const completedClip = scene.clips.find((clip) => Boolean(clip.assetId) && ["Completed", "Succeeded", "Ready"].includes(clip.status));
  const isGenerating = scene.clips.some((clip) => ["Pending", "Queued", "Generating", "Running"].includes(clip.status));
  const clipStatus = completedClip ? "completed" : isGenerating ? "generating" : scene.clips.some((clip) => ["Failed", "Error"].includes(clip.status)) ? "failed" : "empty";
  return <article className="movie-scene-card"><div className="movie-scene-thumb">{completedClip?.assetId ? <video src={assetFileUrl(completedClip.assetId, true)} controls preload="metadata" aria-label={scene.title} /> : <><div className="movie-thumb-lines" /><span className="movie-scene-number">{String(index + 1).padStart(2, "0")}</span><span className="movie-thumb-caption">{t("movie.scenePlaceholder")}</span></>}</div><div className="movie-scene-copy"><div className="movie-scene-topline"><span>{t("movie.scene")} {String(scene.sequence).padStart(2, "0")}</span><span className={`movie-scene-status ${clipStatus}`}>{t(`movie.sceneStatus.${clipStatus}`)}</span></div><h3>{scene.title}</h3><p>{scene.summary}</p><div className="movie-scene-footer"><span>{scene.durationSeconds ? `${scene.durationSeconds}s` : t("movie.durationUnset")}</span>{!completedClip && <button className="movie-scene-action" type="button" disabled={working || isGenerating} onClick={() => void onGenerate(scene.id)}>{isGenerating ? <LoaderCircle size={13} /> : <Sparkles size={13} />} {isGenerating ? t("movie.generating") : t("movie.generateScene")}</button>}{completedClip?.assetId && <a className="movie-scene-action" href={assetFileUrl(completedClip.assetId)}><Download size={13} /> {t("movie.download")}</a>}</div><ShotDesigner scene={scene} presets={presets} t={t} working={working} onAddShot={onAddShot} /></div></article>;
}

function emptyShotDraft(preset?: CinematographyPreset): ShotDraft {
  return {
    description: "", cameraAndFraming: "", cameraMotion: "", durationSeconds: null,
    intent: preset?.intent ?? "natural", presetId: preset?.id ?? null, notes: null,
    shotSize: preset?.shotSize ?? "", focalLength: preset?.focalLength ?? "", lensIntent: preset?.lensIntent ?? "",
    apertureDepthOfField: preset?.apertureDepthOfField ?? "", cameraAngle: preset?.cameraAngle ?? "",
    cameraMovement: preset?.cameraMovement ?? "", frameRateIntent: preset?.frameRateIntent ?? "",
    lighting: preset?.lighting ?? "", paletteLook: preset?.paletteLook ?? "",
    compositionNotes: preset?.compositionNotes ?? "",
  };
}

function ShotDesigner({ scene, presets, t, working, onAddShot }: { scene: MovieScene; presets: CinematographyPreset[]; t: (key: string, variables?: Record<string, string>) => string; working: boolean; onAddShot: (sceneId: string, draft: ShotDraft) => Promise<void> }) {
  const [draft, setDraft] = useState<ShotDraft>(() => emptyShotDraft(presets.find((preset) => preset.intent === "natural")));
  useEffect(() => {
    if (!draft.presetId && presets.length) setDraft(emptyShotDraft(presets.find((preset) => preset.intent === "natural")));
  }, [draft.presetId, presets]);
  const update = <K extends keyof ShotDraft>(key: K, value: ShotDraft[K]) => setDraft((current) => ({ ...current, [key]: value }));
  const chooseIntent = (intent: string) => {
    const preset = presets.find((item) => item.intent === intent);
    setDraft((current) => ({ ...emptyShotDraft(preset), description: current.description, durationSeconds: current.durationSeconds }));
  };
  const choosePreset = (id: string) => {
    const preset = presets.find((item) => item.id === id);
    if (preset) setDraft((current) => ({ ...emptyShotDraft(preset), description: current.description, durationSeconds: current.durationSeconds }));
  };
  return <section className="movie-shot-designer" aria-labelledby={`shot-designer-${scene.id}`}>
    <div className="movie-shot-designer-heading"><span><SlidersHorizontal size={14} /> <strong id={`shot-designer-${scene.id}`}>{t("movie.shotDesigner")}</strong></span><small>{t("movie.shotDesignerHint")}</small></div>
    <div className="movie-shot-intents">{cinematographyIntents.map((intent) => <button key={intent} type="button" className={draft.intent === intent ? "is-active" : ""} onClick={() => chooseIntent(intent)}>{t(`movie.intent.${intent}`)}</button>)}</div>
    <label className="movie-shot-field"><span>{t("movie.shotDescription")}</span><textarea value={draft.description} onChange={(event) => update("description", event.target.value)} placeholder={t("movie.shotDescriptionPlaceholder")} rows={2} maxLength={8000} /></label>
    <details className="movie-shot-advanced"><summary><span>{t("movie.advancedControls")}</span><small>{t("movie.productionIntentNote")}</small></summary>
      <div className="movie-shot-control-grid">
        <label className="movie-shot-field"><span>{t("movie.shotPreset")}</span><select value={draft.presetId ?? ""} onChange={(event) => choosePreset(event.target.value)}><option value="">{t("movie.customIntent")}</option>{presets.map((preset) => <option key={preset.id} value={preset.id}>{preset.name}</option>)}</select></label>
        {(["shotSize", "focalLength", "lensIntent", "apertureDepthOfField", "cameraAngle", "cameraMovement", "frameRateIntent", "lighting", "paletteLook"] as const).map((key) => <label className="movie-shot-field" key={key}><span>{t(`movie.${key}`)}</span><input value={draft[key] ?? ""} onChange={(event) => update(key, event.target.value)} /></label>)}
        <label className="movie-shot-field"><span>{t("movie.duration")}</span><input type="number" min={1} max={3600} value={draft.durationSeconds ?? ""} onChange={(event) => update("durationSeconds", event.target.value ? Number(event.target.value) : null)} /></label>
        <label className="movie-shot-field movie-shot-wide"><span>{t("movie.compositionNotes")}</span><textarea value={draft.compositionNotes ?? ""} onChange={(event) => update("compositionNotes", event.target.value)} rows={2} /></label>
      </div>
    </details>
    <div className="movie-shot-designer-footer"><small>{t("movie.capabilityClassificationNote")}</small><button type="button" className="movie-scene-action" disabled={working || !draft.description.trim()} onClick={() => void onAddShot(scene.id, draft)}><Plus size={13} /> {t("movie.addShot")}</button></div>
    {scene.shots.length > 0 && <div className="movie-shot-list">{scene.shots.map((shot) => <div key={shot.id}><span>{t("movie.shot")} {String(shot.sequence).padStart(2, "0")}</span><strong>{shot.description}</strong></div>)}</div>}
  </section>;
}

function MovieOutput({ project, completed, outputAssetId, t }: { project: MovieProject; completed: boolean; outputAssetId: string | null; t: (key: string, variables?: Record<string, string>) => string }) {
  return <section className={`movie-output-card ${outputAssetId ? "has-output" : ""}`}><div className="movie-output-heading"><div><span className="section-eyebrow">{t("movie.outputEyebrow")}</span><h2>{completed ? t("movie.outputTitle") : t("movie.outputPending")}</h2></div>{outputAssetId && <span className="movie-output-live"><span /> {t("movie.completed")}</span>}</div>{outputAssetId ? <div className="movie-video-hero"><video src={assetFileUrl(outputAssetId, true)} controls preload="metadata" aria-label={project.title} /><div className="movie-video-overlay"><Play size={18} /> <span>{project.title}</span></div></div> : <div className="movie-output-placeholder"><div className="movie-output-signal"><Film size={22} /></div><strong>{t("movie.noOutput")}</strong><p>{t("movie.outputText")}</p></div>}{outputAssetId && <div className="movie-output-actions"><a className="primary-button" href={assetFileUrl(outputAssetId)}><Download size={14} /> {t("movie.download")}</a><a className="secondary-button" href={assetFileUrl(outputAssetId, true)} target="_blank" rel="noreferrer"><ArrowUpRight size={14} /> {t("movie.openOutput")}</a></div>}</section>;
}

function ContinuitySummary({ project, t }: { project: MovieProject; t: (key: string, variables?: Record<string, string>) => string }) {
  const guide = project.guide;
  const fields = useMemo(() => [[t("movie.visualLanguage"), guide.visualLanguage], [t("movie.cameraLanguage"), guide.cameraLanguage], [t("movie.colorLighting"), guide.colorAndLighting], [t("movie.soundNarration"), guide.soundAndNarration]], [guide, t]);
  return <section className="movie-continuity-card"><div className="movie-continuity-heading"><div><span className="section-eyebrow">{t("movie.guideEyebrow")}</span><h2>{t("movie.continuity")}</h2></div><Layers3 size={17} /></div><div className="movie-continuity-list">{fields.map(([label, value]) => <div key={label}><span>{label}</span><p>{value || t("movie.notSet")}</p></div>)}</div>{guide.cinematographyBible && <div className="movie-continuity-rule"><span>{t("movie.cinematographyBible")}</span><p>{guide.cinematographyBible.intent ? t(`movie.intent.${guide.cinematographyBible.intent}`) : t("movie.notSet")}{guide.cinematographyBible.presetId ? ` · ${guide.cinematographyBible.presetId}` : ""}</p></div>}{guide.continuityRules && <div className="movie-continuity-rule"><span>{t("movie.continuityRules")}</span><p>{guide.continuityRules}</p></div>}</section>;
}
