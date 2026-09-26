"use client";

import { useEffect, useState, type FormEvent } from "react";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Check, Clapperboard, Download, Film, Sparkles, WandSparkles, XCircle } from "lucide-react";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { api, type GenerationJob, type MovieProject, type MovieProviderReadiness, type Project } from "@/lib/api";
import { assetFileUrl } from "@/lib/apiBase";

const aspects = ["16:9", "9:16", "1:1", "4:5", "4:3"];
const styles = ["cinematic", "documentary", "animation", "commercial", "experimental"];
type MovieMode = "Quick" | "Full";

type QuickStatus = "empty" | "preparing" | "queued" | "generating" | "completed" | "failed";

function quickStatus(job: GenerationJob | null): QuickStatus {
  if (!job) return "empty";
  if (job.status === "Pending") return "preparing";
  if (job.status === "Queued") return "queued";
  if (job.status === "Running") return "generating";
  if (job.status === "Succeeded") return "completed";
  return "failed";
}

function readyAsset(project: MovieProject | null) {
  if (!project) return null;
  return project.clips.find((clip) => Boolean(clip.assetId) && ["Completed", "Succeeded", "Ready"].includes(clip.status))?.assetId ?? null;
}

export function MovieStudioView() {
  const { t } = useLocale();
  const { workspace } = useAuth();
  const router = useRouter();
  const searchParams = useSearchParams();
  const [mode, setMode] = useState<MovieMode>("Quick");
  const [projects, setProjects] = useState<Project[]>([]);
  const [provider, setProvider] = useState<MovieProviderReadiness | null>(null);
  const [saved, setSaved] = useState<MovieProject | null>(null);
  const [job, setJob] = useState<GenerationJob | null>(null);
  const [error, setError] = useState("");
  const [working, setWorking] = useState(false);
  const [form, setForm] = useState({ title: "", description: "", durationSeconds: 30, aspectRatio: "16:9", style: "cinematic", language: "en", projectId: searchParams.get("projectId") ?? "", additionalInstructions: "", visualLanguage: "", cameraLanguage: "", colorAndLighting: "", soundAndNarration: "", continuityRules: "" });

  useEffect(() => {
    if (!workspace) return;
    void Promise.all([api.listProjects(workspace.id, "Active"), api.getMovieProvider()]).then(([items, readiness]) => {
      setProjects(items);
      setProvider(readiness.provider);
    }).catch(() => undefined);
  }, [workspace]);

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
      });
      if (mode === "Full") {
        router.push(`/create/movie/${result.project.id}/overview`);
        return;
      }
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
    setForm((current) => ({ ...current, title: "", description: "", additionalInstructions: "" }));
  }

  useEffect(() => {
    if (!job || ["Succeeded", "Failed", "Cancelled"].includes(job.status) || !saved) return;
    const timer = window.setInterval(() => {
      void api.getGenerationJob(job.id).then((nextJob) => {
        setJob(nextJob);
        if (["Succeeded", "Failed", "Cancelled"].includes(nextJob.status)) void api.getMovieProject(saved.id).then(setSaved).catch(() => undefined);
      }).catch(() => undefined);
    }, 3000);
    return () => window.clearInterval(timer);
  }, [job, saved]);

  if (saved) return <QuickMovieResult project={saved} job={job} provider={provider} onReset={reset} t={t} />;

  return <div className="movie-studio-page movie-create-page">
    <header className="movie-studio-header"><div className="movie-studio-header-copy"><div className="movie-breadcrumb"><span>01</span><span className="movie-breadcrumb-line" />{t("movie.eyebrow")}</div><h1>{t("movie.title")}</h1><p>{t("movie.subtitle")}</p></div><div className="movie-header-emblem" aria-hidden="true"><Film size={23} /><span>STORY / MOTION</span></div></header>
    <form className="movie-creation-layout" onSubmit={createMovie}>
      <section className="movie-brief-card"><div className="movie-card-topline"><span>{t("movie.workspaceEyebrow")}</span><Link href="/projects">Open projects</Link></div><div className="movie-mode-switch" role="tablist" aria-label={t("movie.workflowLabel")}><button type="button" className={mode === "Quick" ? "is-active" : ""} onClick={() => setMode("Quick")}><WandSparkles size={17} /><span><strong>{t("movie.quick")}</strong><small>One brief, one output</small></span><Check size={14} className="movie-mode-check" /></button><button type="button" className={mode === "Full" ? "is-active" : ""} onClick={() => setMode("Full")}><Clapperboard size={17} /><span><strong>{t("movie.full")}</strong><small>Plan the whole production</small></span><Check size={14} className="movie-mode-check" /></button></div>
        <div className="movie-section-heading"><span className="movie-step-index">01</span><div><p className="section-eyebrow">{t("movie.briefEyebrow")}</p><h2>{mode === "Quick" ? "Make a focused movie" : "Start the production plan"}</h2></div></div>
        <label className="movie-field"><span>{t("movie.titleLabel")}</span><input value={form.title} onChange={(event) => setForm({ ...form, title: event.target.value })} placeholder={t("movie.titlePlaceholder")} maxLength={160} required /></label>
        <label className="movie-field movie-field-large"><span>{t("movie.descriptionLabel")}</span><textarea value={form.description} onChange={(event) => setForm({ ...form, description: event.target.value })} placeholder={t("movie.descriptionPlaceholder")} rows={6} maxLength={8000} required /><small>{form.description.length}/8000</small></label>
        <div className="movie-control-grid"><label className="movie-field"><span>{t("movie.duration")}</span><div className="movie-input-with-suffix"><input type="number" min={1} max={3600} value={form.durationSeconds} onChange={(event) => setForm({ ...form, durationSeconds: Number(event.target.value) })} /><em>{t("movie.seconds")}</em></div></label><label className="movie-field"><span>{t("movie.aspect")}</span><select value={form.aspectRatio} onChange={(event) => setForm({ ...form, aspectRatio: event.target.value })}>{aspects.map((item) => <option key={item}>{item}</option>)}</select></label><label className="movie-field"><span>{t("movie.style")}</span><select value={form.style} onChange={(event) => setForm({ ...form, style: event.target.value })}>{styles.map((item) => <option key={item} value={item}>{t(`movie.style.${item}`)}</option>)}</select></label><label className="movie-field"><span>{t("movie.language")}</span><select value={form.language} onChange={(event) => setForm({ ...form, language: event.target.value })}><option value="en">English</option><option value="ar">العربية</option><option value="ku">کوردی</option></select></label></div>
        <label className="movie-field"><span>{t("movie.project")}</span><select value={form.projectId} onChange={(event) => setForm({ ...form, projectId: event.target.value })}><option value="">{t("movie.noProject")}</option>{projects.map((project) => <option key={project.id} value={project.id}>{project.name}</option>)}</select><small>{t("movie.projectHint")}</small></label>
        <label className="movie-field"><span>{t("movie.instructions")}</span><textarea value={form.additionalInstructions} onChange={(event) => setForm({ ...form, additionalInstructions: event.target.value })} placeholder={t("movie.instructionsPlaceholder")} rows={3} maxLength={8000} /></label>
        {mode === "Full" && <div className="movie-continuity-form"><div className="movie-section-heading"><span className="movie-step-index">02</span><div><p className="section-eyebrow">{t("movie.guideEyebrow")}</p><h2>{t("movie.guideTitle")}</h2></div></div><p className="movie-field-note">{t("movie.guideIntro")}</p><div className="movie-control-grid"><label className="movie-field"><span>{t("movie.visualLanguage")}</span><input value={form.visualLanguage} onChange={(event) => setForm({ ...form, visualLanguage: event.target.value })} placeholder={t("movie.visualLanguagePlaceholder")} /></label><label className="movie-field"><span>{t("movie.cameraLanguage")}</span><input value={form.cameraLanguage} onChange={(event) => setForm({ ...form, cameraLanguage: event.target.value })} placeholder={t("movie.cameraLanguagePlaceholder")} /></label><label className="movie-field"><span>{t("movie.colorLighting")}</span><input value={form.colorAndLighting} onChange={(event) => setForm({ ...form, colorAndLighting: event.target.value })} placeholder={t("movie.colorLightingPlaceholder")} /></label><label className="movie-field"><span>{t("movie.soundNarration")}</span><input value={form.soundAndNarration} onChange={(event) => setForm({ ...form, soundAndNarration: event.target.value })} placeholder={t("movie.soundNarrationPlaceholder")} /></label></div><label className="movie-field"><span>{t("movie.continuityRules")}</span><textarea value={form.continuityRules} onChange={(event) => setForm({ ...form, continuityRules: event.target.value })} placeholder={t("movie.continuityRulesPlaceholder")} rows={3} /></label></div>}
        {error && <div className="form-error"><XCircle size={15} /> {error}</div>}<div className="movie-submit-row"><span>{mode === "Quick" ? "Quick Movie stays intentionally small: one brief and one durable generation job." : "This creates a durable project and opens its workspace. No footage is invented."}</span><button className="primary-button movie-submit" type="submit" disabled={working || form.description.trim().length < 3}><Sparkles size={16} /> {working ? t("movie.saving") : mode === "Quick" ? t("movie.createQuick") : t("movie.createFull")}</button></div>
      </section>
      <aside className="movie-empty-stage"><div className="movie-stage-label"><span>{mode === "Quick" ? "Quick preview" : "Production canvas"}</span><span>00 / 00</span></div><div className="movie-empty-frame"><div className="movie-frame-grid" /><div className="movie-empty-mark"><Film size={30} /><span>{mode === "Quick" ? "One focused output" : "A project with room to grow"}</span><small>{mode === "Quick" ? "Keep the brief concise and let the result lead." : "The Full Movie workspace opens after the plan is saved."}</small></div></div><div className="movie-stage-footer"><span><span className="movie-status-dot" /> Ready to create</span><span>{mode === "Quick" ? "Simple path" : "12 production rooms"}</span></div></aside>
    </form>
  </div>;
}

function QuickMovieResult({ project, job, provider, onReset, t }: { project: MovieProject; job: GenerationJob | null; provider: MovieProviderReadiness | null; onReset: () => void; t: (key: string, variables?: Record<string, string>) => string }) {
  const status = quickStatus(job);
  const outputAssetId = readyAsset(project);
  const label = outputAssetId ? "Ready to review" : status === "empty" ? "Plan saved" : t(`movie.state.${status}`);
  return <div className="movie-studio-page movie-quick-result"><div className="movie-quick-toolbar"><Link href="/create" className="movie-back-link">← {t("movie.backToStudios")}</Link><div><span>Quick Movie</span><strong>{project.title}</strong></div><button type="button" className="movie-refine-button" onClick={onReset}>Create another</button></div><div className="movie-quick-layout"><main className="movie-quick-main"><span className="movie-workspace-kicker">{label}</span><h1>{project.title}</h1><p>{project.description}</p>{outputAssetId ? <div className="movie-quick-video"><video src={assetFileUrl(outputAssetId, true)} controls preload="metadata" aria-label={project.title} /><a href={assetFileUrl(outputAssetId)} className="movie-workspace-button is-primary"><Download size={14} /> Download output</a></div> : <div className="movie-quick-empty"><Film size={24} /><strong>{status === "failed" ? "No output was published" : "Your movie output will appear here"}</strong><p>{status === "failed" ? "The brief remains saved. Review it and try again when generation is available." : "The plan is saved, but no generated video is being simulated."}</p></div>}</main><aside className="movie-quick-inspector"><span className="movie-workspace-kicker">Quick brief</span><div><span>Format</span><strong>{project.aspectRatio}</strong></div><div><span>Duration</span><strong>{project.durationSeconds}s</strong></div><div><span>Style</span><strong>{project.style}</strong></div><div className="movie-quick-note"><span className="movie-live-dot" />{provider?.ready ? "Generation is available for this workspace." : "Generation is currently unavailable; the plan is still safe."}</div></aside></div></div>;
}
