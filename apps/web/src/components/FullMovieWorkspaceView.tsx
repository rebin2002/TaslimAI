"use client";
/* eslint-disable @next/next/no-img-element -- storyboard previews use authenticated Asset URLs. */
/* eslint-disable react-hooks/set-state-in-effect -- module loaders synchronize persisted read models. */
/* eslint-disable react-hooks/exhaustive-deps -- loader callbacks intentionally follow module/project identity. */

import Link from "next/link";
import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent, type ReactNode } from "react";
import {
  AlertCircle,
  AlertTriangle,
  ArrowDown,
  ArrowLeft,
  ArrowUp,
  ArrowUpRight,
  Archive,
  AudioLines,
  BookOpen,
  Check,
  CheckCircle2,
  ChevronRight,
  CircleAlert,
  Clapperboard,
  Clock3,
  Film,
  Gauge,
  ImageOff,
  Layers3,
  ListChecks,
  LockKeyhole,
  Map,
  PencilRuler,
  Plus,
  Play,
  RefreshCw,
  Save,
  ShieldCheck,
  SlidersHorizontal,
  Sparkles,
  Target,
  Image as ImageIcon,
  Trash2,
  Users,
  Workflow,
  X,
} from "lucide-react";
import { api, type Asset, type CinematographyPreset, type DirectorProposal, type DirectorStoryAction, type MovieCast, type MovieCharacter, type MovieCharacterState, type MovieOverview, type MovieProductionReviewInput, type MovieProductionVersion, type MovieProject, type MovieTake, type MovieProjectShell, type MovieScene, type MovieSceneShotPlan, type MovieSceneWorkspace, type MovieScenesWorkspace, type MovieScreenplayElementType, type MovieShot, type MovieShotPlanningInput, type MovieStoryboardCandidate, type MovieStoryboardProject, type MovieStoryboardScene, type MovieStoryboardShot, type MovieStory, type MovieStoryRevision, type MovieStoryRevisionInput } from "@/lib/api";
import { assetFileUrl } from "@/lib/apiBase";
import { MovieWorldWorkspace } from "@/components/MovieWorldWorkspace";
import { ShotDesigner, type ShotDesignerDraft } from "@/components/ShotDesigner";
import { MovieDirectorPanel } from "@/components/MovieDirectorPanel";
import { MovieProductionResolutionPanel } from "@/components/MovieProductionResolutionPanel";
import { displayProductionStage, displayProductionStatus, type MovieResolutionTier } from "@/lib/movieProductionResolution";

export const fullMovieModules = [
  { slug: "overview", label: "Overview", icon: Gauge },
  { slug: "story", label: "Story", icon: BookOpen },
  { slug: "cast", label: "Cast", icon: Users },
  { slug: "world", label: "World", icon: Map },
  { slug: "scenes", label: "Scenes", icon: Clapperboard },
  { slug: "storyboard", label: "Storyboard", icon: Layers3 },
  { slug: "production", label: "Production", icon: Workflow },
  { slug: "edit", label: "Edit", icon: PencilRuler },
  { slug: "audio", label: "Audio", icon: AudioLines },
  { slug: "qc", label: "QC", icon: ShieldCheck },
  { slug: "exports", label: "Exports", icon: Play },
  { slug: "team", label: "Team", icon: Users },
] as const;

const futureModules = new Set<ModuleSlug>(["audio", "qc", "exports", "team"]);

type ModuleSlug = (typeof fullMovieModules)[number]["slug"];

type ModuleCopy = {
  eyebrow: string;
  title: string;
  description: string;
};

const moduleCopy: Record<ModuleSlug, ModuleCopy> = {
  overview: { eyebrow: "Project command", title: "A clear view of the film", description: "Keep the creative intent, story spine, and generated work in one calm production surface." },
  story: { eyebrow: "Story room", title: "Shape the story before the shots", description: "The brief is the source of truth for every scene, character, and future generation." },
  cast: { eyebrow: "Continuity desk", title: "Cast and performance", description: "Keep character identity and performance notes ready for the scenes that depend on them." },
  world: { eyebrow: "World building", title: "Locations with memory", description: "Capture the visual rules that make every location feel like the same world." },
  scenes: { eyebrow: "Scene plan", title: "Build the film in scenes", description: "Order the story, keep the intent visible, and make the next production step obvious." },
  storyboard: { eyebrow: "Visual plan", title: "See the cut before the cut", description: "A filmstrip for the scenes you have planned and the footage you have actually generated." },
  production: { eyebrow: "Production desk", title: "Move from plan to footage", description: "Track what is planned, what is in progress, and what is ready for review." },
  edit: { eyebrow: "Edit room", title: "A timeline waiting for footage", description: "The edit surface is reserved for real clips and real editorial decisions." },
  audio: { eyebrow: "Sound stage", title: "Audio belongs to the picture", description: "Keep narration, ambience, and music direction close to the cut they support." },
  qc: { eyebrow: "Review gate", title: "Quality control, when there is a cut", description: "A deliberate review surface for continuity, pacing, and delivery readiness." },
  exports: { eyebrow: "Delivery desk", title: "Exports without surprises", description: "Prepare final delivery formats only when the project has a real, reviewable output." },
  team: { eyebrow: "Collaboration", title: "A shared production room", description: "The project is ready for roles and review permissions when the collaboration layer arrives." },
};

function moduleFromSlug(slug: string | undefined): ModuleSlug {
  return fullMovieModules.some((item) => item.slug === slug) ? slug as ModuleSlug : "overview";
}

function hasReadyAsset(status: string, assetId: string | null) {
  return Boolean(assetId) && ["Completed", "Succeeded", "Ready"].includes(status);
}

function readyClipForScene(scene: MovieScene) {
  return scene.clips.find((clip) => hasReadyAsset(clip.status, clip.assetId));
}

function latestProductionVersion(shot: MovieShot) {
  return [...shot.productionVersions].sort((left, right) => right.versionNumber - left.versionNumber)[0] ?? null;
}

function formatDuration(seconds: number | null | undefined) {
  if (!seconds) return "Duration unset";
  const minutes = Math.floor(seconds / 60);
  const remainder = seconds % 60;
  return minutes ? `${minutes}m ${remainder}s` : `${remainder}s`;
}

export function FullMovieWorkspaceView({ projectId, module }: { projectId: string; module: string }) {
  if (moduleFromSlug(module) === "world") return <WorldOnlyWorkspace projectId={projectId} />;
  return <FullMovieProjectWorkspace projectId={projectId} module={module} />;
}

function WorldOnlyWorkspace({ projectId }: { projectId: string }) {
  return <div className="movie-studio-page movie-full-workspace"><header className="movie-workspace-header"><Link href={`/create/movie/${projectId}/overview`} className="movie-workspace-back"><ArrowLeft size={14} /> Movie Studio</Link><div className="movie-workspace-heading"><div><span className="movie-workspace-kicker">Focused production read model</span><h2>World room</h2><p>Locations, sets, props, references, usage, and continuity — without loading the complete project graph.</p></div><div className="movie-workspace-meta"><span>World V2</span><span>Asset-backed</span></div></div></header><nav className="movie-workspace-nav" aria-label="Full Movie Project navigation"><div className="movie-workspace-nav-label">Project map</div><div className="movie-workspace-nav-links">{fullMovieModules.map((item) => <Link key={item.slug} href={`/create/movie/${projectId}/${item.slug}`} className={item.slug === "world" ? "is-active" : ""}>{item.label}{futureModules.has(item.slug) && <span aria-hidden="true">Soon</span>}</Link>)}</div></nav><main className="movie-world-only-main movie-workspace-main"><MovieWorldWorkspace projectId={projectId} /></main></div>;
}

function FullMovieProjectWorkspace({ projectId, module }: { projectId: string; module: string }) {
  const activeModule = moduleFromSlug(module);
  const [project, setProject] = useState<MovieProject | null>(null);
  const [overview, setOverview] = useState<MovieOverview | null>(null);
  const [projectShell, setProjectShell] = useState<MovieProjectShell | null>(null);
  const [storyboard, setStoryboard] = useState<MovieStoryboardProject | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [selectedSceneId, setSelectedSceneId] = useState<string | null>(null);
  const [selectedShotId, setSelectedShotId] = useState<string | null>(null);
  const [newScene, setNewScene] = useState({ title: "", summary: "" });
  const [addingScene, setAddingScene] = useState(false);
  const [presets, setPresets] = useState<CinematographyPreset[]>([]);
  const [savingShot, setSavingShot] = useState(false);

  const loadProject = useCallback(() => {
    setLoading(true); setError("");
    let mounted = true;
    void api.getMovieProject(projectId).then((result) => { if (mounted) { setProject(result); setSelectedSceneId(result.scenes[0]?.id ?? null); setSelectedShotId(result.scenes[0]?.shots[0]?.id ?? null); } }).catch((cause) => { if (mounted) setError(cause instanceof Error ? cause.message : "This movie project could not be loaded."); }).finally(() => { if (mounted) setLoading(false); });
    return () => { mounted = false; };
  }, [projectId]);
  useEffect(() => {
    let mounted = true;
    const load = activeModule === "overview" ? api.getMovieOverview(projectId) : activeModule === "story" ? api.getMovieProjectShell(projectId) : activeModule === "storyboard" ? api.getMovieStoryboard(projectId) : api.getMovieProject(projectId);
    void load.then((result) => {
      if (!mounted) return;
      if (activeModule === "overview") setOverview(result as MovieOverview);
      else if (activeModule === "story") setProjectShell(result as MovieProjectShell);
      else if (activeModule === "storyboard") setStoryboard(result as MovieStoryboardProject);
      else {
        const fullProject = result as MovieProject;
        setProject(fullProject);
        setSelectedSceneId(fullProject.scenes[0]?.id ?? null);
        setSelectedShotId(fullProject.scenes[0]?.shots[0]?.id ?? null);
      }
    }).catch((cause) => {
      if (mounted) setError(cause instanceof Error ? cause.message : "This movie project could not be loaded.");
    }).finally(() => {
      if (mounted) setLoading(false);
    });
    void api.getCinematographyPresets().then((catalog) => { if (mounted) setPresets(catalog); }).catch(() => undefined);
    return () => { mounted = false; };
  }, [activeModule, projectId]);

  const selectedScene = useMemo(() => project?.scenes.find((scene) => scene.id === selectedSceneId) ?? project?.scenes[0] ?? null, [project, selectedSceneId]);
  const selectedShot = useMemo(() => selectedScene?.shots.find((shot) => shot.id === selectedShotId) ?? selectedScene?.shots[0] ?? null, [selectedScene, selectedShotId]);
  const readyClips = project?.clips.filter((clip) => hasReadyAsset(clip.status, clip.assetId)) ?? [];
  const outputAssetId = overview?.latestOutputAssetId ?? project?.assemblies.find((assembly) => hasReadyAsset(assembly.status, assembly.assetId))?.assetId ?? readyClips[0]?.assetId ?? null;
  const completionPercent = overview?.progress.production.percent ?? (project ? Math.min(100, Math.round(((project.scenes.length ? readyClips.length : 0) / Math.max(project.scenes.length, 1)) * 100)) : 0);
  const copy = moduleCopy[activeModule];

  async function addScene() {
    if (!project || !newScene.title.trim() || !newScene.summary.trim()) return;
    setAddingScene(true);
    setError("");
    try {
      const scene = await api.addMovieScene(project.id, { title: newScene.title.trim(), summary: newScene.summary.trim() });
      setProject((current) => current ? { ...current, scenes: [...current.scenes, scene] } : current);
      setSelectedSceneId(scene.id);
      setNewScene({ title: "", summary: "" });
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "The scene could not be saved.");
    } finally {
      setAddingScene(false);
    }
  }

  async function addShot(sceneId: string, draft: ShotDesignerDraft): Promise<boolean> {
    if (!project) return false;
    setSavingShot(true); setError("");
    try {
      const shot = await api.addMovieShot(sceneId, { description: draft.description, durationSeconds: draft.durationSeconds, cameraAndFraming: [draft.cinematography.shotSize, draft.cinematography.focalLength, draft.cinematography.cameraAngle].filter(Boolean).join(" · ") || null, cameraMotion: draft.cinematography.cameraMovement ?? null, visualContinuityNotes: draft.cinematography.compositionNotes ?? null, cinematography: draft.cinematography });
      setProject((current) => current ? { ...current, scenes: current.scenes.map((scene) => scene.id === sceneId ? { ...scene, shots: [...scene.shots, shot] } : scene) } : current);
      setSelectedShotId(shot.id);
      return true;
    } catch (cause) { setError(cause instanceof Error ? cause.message : "The shot could not be saved."); return false; } finally { setSavingShot(false); }
  }

  function applyShotPlan(plan: MovieSceneShotPlan) {
    setProject((current) => current ? { ...current, scenes: current.scenes.map((scene) => scene.id === plan.sceneId ? { ...scene, shots: plan.shots } : scene) } : current);
    setSelectedShotId((current) => plan.shots.some((shot) => shot.id === current) ? current : plan.shots[0]?.id ?? null);
  }

  async function reviewProductionVersion(versionId: string, input: MovieProductionReviewInput) {
    setError("");
    try { await api.reviewMovieProductionVersion(versionId, input); setProject(await api.getMovieProject(projectId)); }
    catch (cause) { setError(cause instanceof Error ? cause.message : "The production review could not be saved."); }
  }

  async function createProductionVersion(shotId: string, input: { stage: "ProductionKeyframe"; sourceVersionId: string; compositionJson: string; firstFrameNotes?: string | null; lastFrameNotes?: string | null }) {
    setError("");
    try { await api.createMovieProductionVersion(shotId, input); setProject(await api.getMovieProject(projectId)); }
    catch (cause) { setError(cause instanceof Error ? cause.message : "The keyframe plan could not be saved."); }
  }

  async function refreshProject() { const result = await api.getMovieProject(projectId); setProject(result); }

  async function refreshStoryboard() {
    const next = await api.getMovieStoryboard(projectId);
    setStoryboard(next);
  }

  async function generateScene(sceneId: string) {
    if (!project) return;
    setError("");
    try {
      const result = await api.generateMovieScene(project.id, sceneId);
      setProject(result.project);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "This scene could not be queued.");
    }
  }

  if (loading) return <WorkspaceSkeleton />;
  const workspace = overview?.project ?? project ?? projectShell ?? storyboard;
  const fullProject = project as MovieProject;
  if (!workspace || (activeModule !== "overview" && activeModule !== "story" && activeModule !== "storyboard" && !project)) return <div className="movie-studio-page movie-full-workspace"><div className="movie-workspace-error" role="alert"><XCircleIcon /><h1>Workspace unavailable</h1><p>{error || "This movie project is not available in the current workspace."}</p><div className="movie-workspace-error-actions"><button type="button" className="movie-workspace-button is-primary" onClick={() => void loadProject()}><RefreshCw size={14} /> Try again</button><Link href="/create/movie" className="movie-workspace-button is-secondary"><ArrowLeft size={14} /> Back to Movie Studio</Link></div></div></div>;

  return (
    <div className="movie-studio-page movie-full-workspace">
      <header className="movie-workspace-header">
        <Link href="/create/movie" className="movie-workspace-back"><ArrowLeft size={14} /> Movie Studio</Link>
        <div className="movie-workspace-heading">
          <div>
            <span className="movie-workspace-kicker">Full Movie Project · {workspace.status}</span>
            <h1>{workspace.title}</h1>
            <p>{workspace.description}</p>
          </div>
          <div className="movie-workspace-meta"><span>{workspace.aspectRatio}</span><span>{formatDuration(workspace.durationSeconds)}</span><span>{workspace.style}</span></div>
        </div>
      </header>

      <div className={`movie-workspace-layout ${activeModule === "story" ? "is-story-layout" : ""}`}>
        <nav className="movie-workspace-nav" aria-label="Full Movie Project navigation">
          <div className="movie-workspace-nav-label">Project map</div>
          <div className="movie-workspace-nav-list">
            {fullMovieModules.map((item) => {
              const Icon = item.icon;
              const href = `/create/movie/${workspace.id}/${item.slug}`;
              const isFuture = futureModules.has(item.slug); return <Link key={item.slug} href={href} aria-label={item.label} className={`movie-workspace-nav-item ${activeModule === item.slug ? "is-active" : ""}`} aria-current={activeModule === item.slug ? "page" : undefined} data-module-state={isFuture ? "foundation" : "operational"}><Icon size={15} /><span>{item.label}</span>{isFuture && <span className="movie-nav-state" aria-hidden="true">Soon</span>}{activeModule === item.slug && <ChevronRight size={13} />}</Link>;
            })}
          </div>
          <div className="movie-workspace-nav-foot"><span className="movie-live-dot" /> <span>Plan saved locally to this project</span></div>
        </nav>

        <main className="movie-workspace-main">
          <div className="movie-module-heading"><div><span className="movie-workspace-kicker">{copy.eyebrow}</span><h2>{copy.title}</h2><p>{copy.description}</p></div><span className="movie-module-index">{String(fullMovieModules.findIndex((item) => item.slug === activeModule) + 1).padStart(2, "0")} / 12</span></div>
          {activeModule === "overview" && overview && <OverviewModule overview={overview} outputAssetId={outputAssetId} />}
          {activeModule === "story" && <StoryModule projectId={workspace.id} guideLocked={projectShell?.lockedGuideRevisionNumber != null} />}
          {activeModule === "cast" && <CastModule projectId={workspace.id} />}
          {activeModule === "world" && <WorldModule projectId={fullProject.id} />}
          {activeModule === "scenes" && <ScenesModule projectId={fullProject.id} project={fullProject} presets={presets} savingShot={savingShot} onAddShot={addShot} selectedSceneId={selectedScene?.id ?? null} selectedShotId={selectedShotId} onSelectScene={(sceneId) => { setSelectedSceneId(sceneId); setSelectedShotId(null); }} onSelectShot={setSelectedShotId} onGenerate={generateScene} onPlanChange={applyShotPlan} />}
          {activeModule === "storyboard" && storyboard && <OperationalStoryboardModule storyboard={storyboard} onRefresh={() => void refreshStoryboard()} onError={setError} />}
          {activeModule === "production" && <ProductionModule project={fullProject} completionPercent={completionPercent} onRefresh={refreshProject} />}
          {activeModule === "edit" && <EditModule project={fullProject} />}
          {activeModule === "audio" && <FutureModule icon={<AudioLines size={20} />} title="Audio is not connected yet" text="The sound stage is reserved for real narration, ambience, and music assets. Nothing is simulated here." />}
          {activeModule === "qc" && <FutureModule icon={<ShieldCheck size={20} />} title="QC is a future review gate" text="Continuity and delivery checks will appear once this project has a real cut to inspect." />}
          {activeModule === "exports" && <FutureModule icon={<Play size={20} />} title="Exports are not available yet" text="Final packaging stays unavailable until there is a reviewable project output." />}
          {activeModule === "team" && <FutureModule icon={<Users size={20} />} title="Team controls are not connected yet" text="This route is reserved for shared roles, review notes, and permissions. No access controls are implied by this shell." />}
          {error && <div className="movie-workspace-error-inline" role="alert"><AlertCircle size={15} aria-hidden="true" /><span>{error}</span><button type="button" onClick={() => void loadProject()}><RefreshCw size={12} /> Retry</button></div>}
        </main>

        {activeModule !== "story" && (project ? <MovieDirectorPanel project={project} activeModule={activeModule} selectedScene={selectedScene} selectedShot={selectedShot} onProjectRefresh={refreshProject} /> : <aside className="movie-director-panel"><div className="movie-director-heading"><span className="movie-workspace-kicker">Director / Inspector</span><SlidersHorizontal size={16} /></div><div className="movie-director-section"><span className="movie-inspector-label">Current module</span><strong>{copy.title}</strong><p>Load the full project room to enable grounded Director proposals.</p></div><div className="movie-director-note"><Sparkles size={14} /><p>Director actions remain scoped to persisted movie project records.</p></div></aside>)}
      </div>
    </div>
  );
}

function OverviewModule({ overview, outputAssetId }: { overview: MovieOverview; outputAssetId: string | null }) {
  const project = overview.project;
  const action = overview.nextActions[0];
  const progressItems = [
    ["Storyboard", overview.progress.storyboard],
    ["Keyframe", overview.progress.keyframe],
    ["Production", overview.progress.production],
    ["Final takes", overview.progress.selectedFinalTakes],
  ] as const;
  const approvalItems = [
    ["Production", overview.approvals.production],
    ["Collaborative reviews", overview.approvals.collaborative],
    ["Screenplay", overview.approvals.screenplay],
    ["Director proposals", overview.approvals.directorProposals],
  ] as const;
  return <div className="movie-overview-command">
    <section className="movie-command-hero">
      <div className="movie-command-hero-copy"><span className="movie-workspace-kicker">Production command</span><h3>{project.productionStatus === "Draft" ? "The plan is taking shape." : `The film is in ${formatStatus(project.productionStatus)}.`}</h3><p>{project.description || "No project description has been written yet."}</p><div className="movie-command-tags"><span>{formatStatus(project.productionStatus)}</span><span>{project.qualityLevel} quality</span><span>{project.autoDirectorEnabled ? "Auto Director on" : "Auto Director off"}</span></div></div>
      <div className="movie-command-hero-stat"><span>Production</span><strong>{formatPercent(overview.progress.production.percent)}</strong><small>{overview.progress.production.completed} of {overview.progress.production.total || "no"} shots reviewable</small></div>
    </section>

    <section className="movie-command-next"><div><span className="movie-workspace-kicker">Next action</span><h3>{action?.label ?? "No next action"}</h3><p>{action?.reason ?? "There is no deterministic recommendation from the current persisted state."}</p></div>{action && <Link href={`/create/movie/${project.id}/${action.module}`} className="movie-workspace-button is-primary">Open {action.module} <ArrowUpRight size={14} /></Link>}</section>

    <div className="movie-command-metrics">
      <CommandMetric label="Scenes" value={overview.scenes.total} detail={overview.scenes.total ? `${overview.scenes.approved} approved` : "Not started"} icon={<Clapperboard size={17} />} />
      <CommandMetric label="Shots" value={overview.shots.total} detail={overview.shots.total ? `${overview.shots.approved} approved` : "Not planned"} icon={<Target size={17} />} />
      <CommandMetric label="Final takes" value={overview.takes.finalized} detail={`${overview.takes.selected} selected`} icon={<CheckCircle2 size={17} />} />
      <CommandMetric label="Open approvals" value={approvalItems.reduce((sum, [, bucket]) => sum + bucket.pending, 0)} detail={overview.warnings.length ? `${overview.warnings.length} warning${overview.warnings.length === 1 ? "" : "s"}` : "No warnings"} icon={<CircleAlert size={17} />} />
    </div>

    <div className="movie-command-grid">
      <section className="movie-command-section movie-command-progress"><CommandSectionTitle eyebrow="Production path" title="From plan to picture" detail="Real persisted state only" />{progressItems.map(([label, stage]) => <ProgressLine key={label} label={label} stage={stage} />)}</section>
      <section className="movie-command-section"><CommandSectionTitle eyebrow="Project health" title="What is ready" detail="No inferred completion" /><div className="movie-health-list"><HealthLine label="Story" value={overview.story.status === "NotStarted" ? "Not started" : formatStatus(overview.story.status)} tone={overview.story.status === "Approved" ? "ready" : "attention"} /><HealthLine label="Cast" value={overview.cast.total === 0 ? "Not defined" : `${overview.cast.ready ?? 0} / ${overview.cast.total} defined`} tone={overview.cast.total > 0 && overview.cast.ready === overview.cast.total ? "ready" : "attention"} /><HealthLine label="World" value={overview.world.total === 0 ? "Not defined" : `${overview.world.total} records`} tone={overview.world.total > 0 ? "ready" : "attention"} /><HealthLine label="Quality" value={`${project.qualityLevel} · ${project.autoDirectorEnabled ? "Auto Director" : "Manual direction"}`} tone="neutral" /></div></section>
    </div>

    <div className="movie-command-grid movie-command-grid-bottom">
      <section className="movie-command-section"><CommandSectionTitle eyebrow="Approval desk" title="Decisions waiting" detail="Kept separate by source" />{approvalItems.map(([label, bucket]) => <div className="movie-approval-row" key={label}><span>{label}</span><strong className={bucket.pending ? "has-pending" : ""}>{bucket.pending ? `${bucket.pending} pending` : "Clear"}</strong></div>)}</section>
      <section className="movie-command-section"><CommandSectionTitle eyebrow="Production economics" title={overview.cost.isKnown ? "Recorded estimate" : "No estimate recorded yet"} detail="Planning view" /><div className="movie-cost-readout"><div><span>Recorded estimate</span><strong>{formatCost(overview.cost.recordedProviderCostUsd, overview.cost.currency)}</strong></div><div><span>Estimated remaining</span><strong>{formatCost(overview.cost.estimatedRemainingProviderCostUsd, overview.cost.currency)}</strong></div></div><p className="movie-command-note">{overview.cost.note ?? "Estimates are shown for planning only and do not activate charging."}</p></section>
    </div>

    <div className="movie-command-grid movie-command-grid-bottom"><section className="movie-command-section"><CommandSectionTitle eyebrow="Warnings & blockers" title={overview.warnings.length ? `${overview.warnings.length} signals need attention` : "No active warnings"} detail="Continuity and production" />{overview.warnings.length ? <div className="movie-warning-list">{overview.warnings.map((warning) => <div className={`movie-warning-row is-${warning.severity}`} key={warning.key}><CircleAlert size={14} /><div><strong>{warning.label}</strong><p>{warning.detail}</p></div></div>)}</div> : <HonestEmpty text="No unresolved warnings have been recorded for this project." />}</section><section className="movie-command-section"><CommandSectionTitle eyebrow="Recent activity" title="Meaningful changes" detail="Latest persisted records" />{overview.recentActivity.length ? <div className="movie-activity-list">{overview.recentActivity.map((item) => <div className="movie-activity-row" key={item.key}><Clock3 size={14} /><div><strong>{item.label}</strong><span>{item.detail}</span></div><time dateTime={item.occurredAt}>{formatRelativeDate(item.occurredAt)}</time></div>)}</div> : <HonestEmpty text="Activity will appear after a project record changes." />}</section></div>

    {outputAssetId ? <section className="movie-generated-surface movie-command-output"><div className="movie-surface-heading"><div><span className="movie-workspace-kicker">Latest output</span><h3>Reviewable project output</h3></div><span className="movie-surface-status"><span className="is-ready" /> Ready</span></div><div className="movie-generated-video"><video src={assetFileUrl(outputAssetId, true)} controls preload="metadata" aria-label={project.title} /><div className="movie-generated-video-caption"><Play size={14} /> {project.title}</div></div></section> : <section className="movie-command-empty"><Film size={20} /><div><span className="movie-workspace-kicker">Latest output</span><h3>No output yet</h3><p>No generated footage yet. The plan is visible above; real output will appear here when the persisted production workflow creates one.</p></div></section>}
  </div>;
}

function CommandSectionTitle({ eyebrow, title, detail }: { eyebrow: string; title: string; detail: string }) { return <div className="movie-command-section-title"><div><span className="movie-workspace-kicker">{eyebrow}</span><h3>{title}</h3></div><small>{detail}</small></div>; }
function CommandMetric({ label, value, detail, icon }: { label: string; value: number; detail: string; icon: ReactNode }) { return <div className="movie-command-metric"><span className="movie-command-metric-icon">{icon}</span><div><span>{label}</span><strong>{value}</strong><small>{detail}</small></div></div>; }
function ProgressLine({ label, stage }: { label: string; stage: MovieOverview["progress"]["storyboard"] }) { return <div className="movie-progress-line"><div><span>{label}</span><strong>{stage.percent === null ? "Not started" : `${stage.percent}%`}</strong></div><div className="movie-progress-track"><span style={{ width: `${stage.percent ?? 0}%` }} /></div><small>{stage.total === 0 ? "No persisted shot plan" : `${stage.completed} of ${stage.total} complete`}</small></div>; }
function HealthLine({ label, value, tone }: { label: string; value: string; tone: "ready" | "attention" | "neutral" }) { return <div className="movie-health-line"><span>{label}</span><strong className={`is-${tone}`}>{value}</strong></div>; }
function HonestEmpty({ text }: { text: string }) { return <div className="movie-honest-empty"><span />{text}</div>; }
function formatStatus(value: string) { return value.replace(/([a-z])([A-Z])/g, "$1 $2"); }
function formatPercent(value: number | null) { return value === null ? "—" : `${value}%`; }
function formatCost(value: number | null, currency: string) { return value === null ? "Not available" : `${currency} ${value.toFixed(2)}`; }
function formatRelativeDate(value: string) { const date = new Date(value); return Number.isNaN(date.getTime()) ? "Recently" : date.toLocaleDateString(undefined, { month: "short", day: "numeric" }); }


type StorySection = "Premise" | "Logline" | "Synopsis" | "Treatment" | "Screenplay";
const storySections: StorySection[] = ["Premise", "Logline", "Synopsis", "Treatment", "Screenplay"];
const screenplayElementTypes: MovieScreenplayElementType[] = ["Action", "Dialogue", "Parenthetical", "Transition", "Note"];

type StorySectionPresentation = {
  title: string;
  navDetail: string;
  description: string;
};

const storySectionPresentation: Record<StorySection, StorySectionPresentation> = {
  Premise: { title: "Premise", navDetail: "Dramatic engine", description: "The essential human tension that makes this film worth telling." },
  Logline: { title: "Logline", navDetail: "One clear promise", description: "A concise, decision-ready expression of the protagonist, goal, pressure, and stakes." },
  Synopsis: { title: "Synopsis", navDetail: "Complete arc", description: "The story in grounded prose: setup, turns, escalation, and consequence." },
  Treatment: { title: "Treatment", navDetail: "Prose map", description: "A scene-aware narrative pass that prepares the film for structured pages." },
  Screenplay: { title: "Screenplay", navDetail: "Structured pages", description: "Typed story blocks remain usable by the downstream scene and production rooms." },
};

function storyAuthorshipLabel(authorship: MovieStoryRevisionInput["authorship"]) {
  if (authorship === "AiSuggested") return "AI suggested";
  if (authorship === "HumanEdited") return "Human edited";
  return "Human authored";
}

function storyAuthorshipDescription(authorship: MovieStoryRevisionInput["authorship"]) {
  if (authorship === "AiSuggested") return "AI-generated material is visible as a suggestion until a human reviews and revises it.";
  if (authorship === "HumanEdited") return "A human has materially revised an earlier suggestion or prior pass.";
  return "This revision is written or materially authored by a human.";
}

function storyAuthorshipTone(authorship: MovieStoryRevisionInput["authorship"]) {
  if (authorship === "AiSuggested") return "is-ai";
  if (authorship === "HumanEdited") return "is-human-edited";
  return "is-human";
}

function sectionForDirectorApply(proposal: DirectorProposal, requestedAction: DirectorStoryAction): StorySection {
  const changedFields = proposal.storyReview?.changes.map((change) => `${change.field} ${change.target ?? ""}`.toLowerCase()).join(" ") ?? "";
  const directlyChanged = storySections.find((candidate) => changedFields.includes(candidate.toLowerCase()));
  if (directlyChanged) return directlyChanged;
  if (["screenplay", "scene", "dialogue", "passage", "pacing"].some((term) => changedFields.includes(term))) return "Screenplay";

  const sectionByAction: Record<DirectorStoryAction, StorySection> = {
    develop_premise: "Premise",
    improve_logline: "Logline",
    expand_synopsis: "Synopsis",
    create_refine_treatment: "Treatment",
    propose_screenplay_scene: "Screenplay",
    rewrite_selected_passage: "Screenplay",
    improve_dialogue: "Screenplay",
    tighten_pacing: "Screenplay",
    identify_story_inconsistencies: "Screenplay",
  };
  return sectionByAction[requestedAction];
}

function emptyStoryDraft(): MovieStoryRevisionInput {
  return { premise: "", logline: "", synopsis: "", treatment: "", authorship: "Human", changeSummary: "", scenes: [] };
}

function revisionToDraft(revision: MovieStoryRevision): MovieStoryRevisionInput {
  return { premise: revision.premise, logline: revision.logline, synopsis: revision.synopsis, treatment: revision.treatment, authorship: revision.authorship, parentRevisionId: revision.parentRevisionId, changeSummary: revision.changeSummary ?? "", scenes: revision.scenes.map((scene) => ({ sceneIdentifier: scene.sceneIdentifier, actNumber: scene.actNumber, sequenceNumber: scene.sequenceNumber, movieSceneId: scene.movieSceneId, slugline: scene.slugline, synopsis: scene.synopsis, elements: scene.elements.map((element) => ({ elementType: element.elementType, content: element.content, characterName: element.characterName, parenthetical: element.parenthetical })) })) };
}

function StoryModule({ projectId, guideLocked }: { projectId: string; guideLocked: boolean }) {
  const [story, setStory] = useState<MovieStory | null>(null);
  const [draft, setDraft] = useState<MovieStoryRevisionInput>(emptyStoryDraft);
  const [selectedRevision, setSelectedRevision] = useState<MovieStoryRevision | null>(null);
  const [draftRevisionId, setDraftRevisionId] = useState<string | null>(null);
  const [section, setSection] = useState<StorySection>("Screenplay");
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [dirty, setDirty] = useState(false);
  const [savedAt, setSavedAt] = useState<Date | null>(null);
  const [error, setError] = useState("");
  const [storyProposal, setStoryProposal] = useState<DirectorProposal | null>(null);
  const [storyAction, setStoryAction] = useState<DirectorStoryAction>("develop_premise");
  const [storySceneId, setStorySceneId] = useState<string | null>(null);
  const [storyBusy, setStoryBusy] = useState(false);
  const [focusAfterDirectorApply, setFocusAfterDirectorApply] = useState<StorySection | null>(null);

  useEffect(() => {
    let mounted = true;
    void api.getMovieStory(projectId).then((result) => {
      if (!mounted) return;
      setStory(result);
      const current = result.currentRevision;
      setDraft(current ? revisionToDraft(current) : emptyStoryDraft());
      setSelectedRevision(current);
      setDraftRevisionId(current?.status === "Draft" ? current.id : null);
    }).catch((cause) => {
      if (!mounted) return;
      if (typeof cause === "object" && cause !== null && "status" in cause && (cause as { status?: number }).status === 404) {
        setStory({ id: "local-story", movieProjectId: projectId, workspaceId: "", premise: "", logline: "", synopsis: "", treatment: "", approvalState: "Draft", currentRevisionId: null, approvedRevisionId: null, createdAt: new Date().toISOString(), updatedAt: new Date().toISOString(), currentRevision: null, approvedRevision: null, revisions: [], canEdit: true, canApprove: false });
        setDraft(emptyStoryDraft());
        setSelectedRevision(null);
      } else setError(cause instanceof Error ? cause.message : "The story could not be loaded.");
    }).finally(() => { if (mounted) setLoading(false); });
    return () => { mounted = false; };
  }, [projectId]);

  async function saveDraft() {
    if (!story?.canEdit || saving) return;
    setSaving(true);
    setError("");
    try {
      if (draftRevisionId) {
        const saved = await api.updateMovieStoryDraft(projectId, draftRevisionId, draft);
        setStory(await api.getMovieStory(projectId));
        setSelectedRevision(saved);
      } else {
        const next = await api.createMovieStoryRevision(projectId, { ...draft, parentRevisionId: story.currentRevisionId });
        setStory(next);
        setSelectedRevision(next.currentRevision);
        setDraftRevisionId(next.currentRevisionId);
      }
      setDirty(false);
      setSavedAt(new Date());
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "The draft could not be saved.");
    } finally {
      setSaving(false);
    }
  }

  useEffect(() => {
    if (!dirty || !story?.canEdit || !draft.premise.trim() || !draft.logline.trim() || !draft.synopsis.trim() || !draft.treatment.trim()) return;
    const timer = window.setTimeout(() => { void saveDraft(); }, 1800);
    return () => window.clearTimeout(timer);
    // The draft object is intentionally observed through the dirty flag to debounce editor input.
  }, [dirty, draft]);

  function updateDraft(patch: Partial<MovieStoryRevisionInput>) {
    setDraft((current) => ({ ...current, ...patch }));
    setDirty(true);
  }

  function updateScene(index: number, patch: Partial<MovieStoryRevisionInput["scenes"][number]>) {
    updateDraft({ scenes: draft.scenes.map((scene, sceneIndex) => sceneIndex === index ? { ...scene, ...patch } : scene) });
  }

  function updateElement(sceneIndex: number, elementIndex: number, patch: Partial<MovieStoryRevisionInput["scenes"][number]["elements"][number]>) {
    updateScene(sceneIndex, { elements: draft.scenes[sceneIndex].elements.map((element, index) => index === elementIndex ? { ...element, ...patch } : element) });
  }

  function addScene() {
    updateDraft({ scenes: [...draft.scenes, { sceneIdentifier: `SCENE-${draft.scenes.length + 1}`, actNumber: 1, sequenceNumber: draft.scenes.length + 1, slugline: "INT./EXT. LOCATION - TIME", synopsis: "", elements: [{ elementType: "Action", content: "", characterName: null, parenthetical: null }] }] });
  }

  function newDraftFromCurrent() {
    if (!story?.currentRevision) return;
    setDraft({ ...revisionToDraft(story.currentRevision), parentRevisionId: story.currentRevision.id, changeSummary: "New story pass" });
    setSelectedRevision(null);
    setDraftRevisionId(null);
    setDirty(true);
  }

  async function submit() {
    try {
      if (dirty) await saveDraft();
      const latest = await api.getMovieStory(projectId);
      const revisionId = latest.currentRevisionId ?? draftRevisionId;
      if (!revisionId) return;
      const revision = await api.submitMovieStoryRevision(projectId, revisionId);
      setStory(await api.getMovieStory(projectId));
      setSelectedRevision(revision);
      setDraftRevisionId(revision.id);
      setDirty(false);
    } catch (cause) { setError(cause instanceof Error ? cause.message : "The revision could not be submitted."); }
  }

  async function approve() {
    if (!story?.canApprove) return;
    try {
      if (dirty) await saveDraft();
      const latest = await api.getMovieStory(projectId);
      const revisionId = latest.currentRevisionId ?? draftRevisionId;
      if (!revisionId) return;
      await api.approveMovieStoryRevision(projectId, revisionId);
      const refreshed = await api.getMovieStory(projectId);
      setStory(refreshed);
      setSelectedRevision(refreshed.currentRevision);
      setDraftRevisionId(null);
      setDirty(false);
    } catch (cause) { setError(cause instanceof Error ? cause.message : "The revision could not be approved."); }
  }

  async function inspectRevision(revisionId: string) {
    try {
      const revision = await api.getMovieStoryRevision(projectId, revisionId);
      setSelectedRevision(revision);
      setDraft(revisionToDraft(revision));
      setDraftRevisionId(revision.status === "Draft" ? revision.id : null);
      setDirty(false);
    } catch (cause) { setError(cause instanceof Error ? cause.message : "That revision could not be opened."); }
  }

  async function createStoryProposal() {
    setStoryBusy(true);
    setError("");
    try {
      const result = await api.createMovieDirectorProposal(projectId, { storyAction, targetSceneId: storySceneId });
      setStoryProposal(result.proposal);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "The Director proposal could not be created.");
    } finally { setStoryBusy(false); }
  }

  async function reviewStoryProposal(approve: boolean) {
    if (!storyProposal) return;
    setStoryBusy(true);
    try {
      const result = approve ? await api.approveMovieDirectorProposal(storyProposal.id) : await api.rejectMovieDirectorProposal(storyProposal.id);
      setStoryProposal(result);
    } catch (cause) { setError(cause instanceof Error ? cause.message : "The proposal review could not be saved."); }
    finally { setStoryBusy(false); }
  }

  async function applyStoryProposal() {
    const action = storyProposal?.actions.find((item) => item.actionType === "story_assistance");
    if (!storyProposal || !action || storyProposal.status !== "Approved") return;
    setStoryBusy(true);
    try {
      const result = await api.executeMovieDirectorAction(action.id);
      if (result.action.status !== "Succeeded") throw new Error(result.result.safeMessage);
      const changedSection = sectionForDirectorApply(storyProposal, storyAction);
      const nextStory = await api.getMovieStory(projectId);
      setStory(nextStory);
      setSelectedRevision(nextStory.currentRevision);
      setDraft(nextStory.currentRevision ? revisionToDraft(nextStory.currentRevision) : emptyStoryDraft());
      setDraftRevisionId(nextStory.currentRevision?.status === "Draft" ? nextStory.currentRevision.id : null);
      setDirty(false);
      setSection(changedSection);
      setFocusAfterDirectorApply(changedSection);
      setStoryProposal(null);
    } catch (cause) { setError(cause instanceof Error ? cause.message : "The approved Story proposal could not be applied."); }
    finally { setStoryBusy(false); }
  }

  useEffect(() => {
    if (!focusAfterDirectorApply || focusAfterDirectorApply !== section) return;
    const frame = window.requestAnimationFrame(() => {
      const target = document.getElementById(`story-editor-${focusAfterDirectorApply.toLowerCase()}`);
      if (target instanceof HTMLElement) {
        target.focus({ preventScroll: true });
        target.scrollIntoView({
          block: "start",
          behavior: window.matchMedia("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth",
        });
      }
      setFocusAfterDirectorApply(null);
    });
    return () => window.cancelAnimationFrame(frame);
  }, [focusAfterDirectorApply, section]);

  if (loading) return <div className="movie-story-loading"><span className="loading-spinner" /><p>Opening the story room…</p></div>;
  if (!story) return <div className="movie-module-empty"><BookOpen size={20} /><strong>Story unavailable</strong><p>{error || "This story is not available in the current workspace."}</p></div>;

  const current = story.currentRevision;
  const approved = story.approvedRevision;
  const status = selectedRevision?.status ?? "New draft";
  const canEdit = story.canEdit && (!selectedRevision || (selectedRevision.status === "Draft" && selectedRevision.id === story.currentRevisionId));

  const presentation = storySectionPresentation[section];
  const provenanceTone = storyAuthorshipTone(draft.authorship);

  return <div className="movie-story-workspace">
    <section className="movie-story-statusbar">
      <div><span className="movie-workspace-kicker">Story workspace</span><h3>Write the film, one deliberate pass at a time.</h3><p>Structured screenplay elements keep the writing useful to the production rooms that follow.</p></div>
      <div className="movie-story-actions"><span className={`movie-story-state is-${story.approvalState.toLowerCase()}`}><span />{story.approvalState === "Approved" ? "Approved story" : story.approvalState === "InReview" ? "In review" : "Draft workspace"}</span>{savedAt && <small>Saved {savedAt.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}</small>}{story.canEdit && <button type="button" className="movie-workspace-button is-quiet" onClick={() => void saveDraft()} disabled={saving || !dirty}>{saving ? "Saving…" : <><Save size={13} /> Save draft</>}</button>}{story.canEdit && current?.status === "Approved" && <button type="button" className="movie-workspace-button is-quiet" onClick={newDraftFromCurrent}><Plus size={13} /> New revision</button>}{story.canEdit && draftRevisionId && current?.status === "Draft" && <button type="button" className="movie-workspace-button is-primary" onClick={() => void submit()} disabled={saving}>Send for review</button>}{story.canApprove && draftRevisionId && <button type="button" className="movie-workspace-button is-approve" onClick={() => void approve()} disabled={saving}>Approve revision</button>}</div>
    </section>
    {error && <div className="movie-workspace-error-inline"><XCircleIcon /> {error}</div>}
    {approved && <div className="movie-story-approval-banner"><LockKeyhole size={15} /><div><strong>Approved screenplay · Revision {approved.revisionNumber}</strong><span>Downstream Director and scene breakdown should use this revision. Your current draft remains separate.</span></div></div>}
    {!approved && <div className="movie-story-approval-banner is-muted"><BookOpen size={15} /><div><strong>No approved screenplay yet</strong><span>Finish a human review before production decisions treat this story as authoritative.</span></div></div>}
    <section className="movie-workspace-section movie-director-story-assist"><div className="movie-section-head"><div><span className="movie-workspace-kicker">Director assistance</span><h3>Propose, review, then apply</h3></div><Sparkles size={17} /></div><p className="movie-story-safety-note">The Director uses the locked guide, current or approved Story, selected scene, and bounded Cast / World references. It never silently rewrites an approved revision.</p>{!guideLocked && <div className="movie-workspace-error-inline" role="alert"><LockKeyhole size={15} /><span>Lock the Movie Guide before creating a Story Director proposal.</span><Link href={`/create/movie/${projectId}/cast`}>Open Cast to lock it</Link></div>}<div className="movie-story-assist-controls"><label><span>Action</span><select value={storyAction} onChange={(event) => setStoryAction(event.target.value as DirectorStoryAction)}><option value="develop_premise">Develop my story</option><option value="improve_logline">Improve logline</option><option value="expand_synopsis">Expand synopsis</option><option value="create_refine_treatment">Create / refine treatment</option><option value="propose_screenplay_scene">Propose screenplay scene</option><option value="rewrite_selected_passage">Rewrite selected passage</option><option value="improve_dialogue">Improve dialogue</option><option value="tighten_pacing">Tighten pacing</option><option value="identify_story_inconsistencies">Identify story inconsistencies</option></select></label>{(current?.scenes.length ?? approved?.scenes.length ?? 0) > 0 && <label><span>Target scene</span><select value={storySceneId ?? ""} onChange={(event) => setStorySceneId(event.target.value || null)}><option value="">Choose a scene</option>{(current?.scenes ?? approved?.scenes ?? []).map((scene) => <option key={scene.id} value={scene.id}>{scene.sceneIdentifier} · {scene.slugline}</option>)}</select></label>}<button className="movie-workspace-button is-primary" type="button" onClick={() => void createStoryProposal()} disabled={storyBusy || !guideLocked}>{storyBusy ? "Working…" : "Create proposal"}</button></div></section>
    {storyProposal?.storyReview && <StoryProposalReview proposal={storyProposal} busy={storyBusy} onReview={(approveProposal) => void reviewStoryProposal(approveProposal)} onApply={() => void applyStoryProposal()} />}
    <div className="movie-story-editor-layout">
      <nav className="movie-story-sections" aria-label="Story sections">
        <div className="movie-story-section-context"><span className="movie-inspector-label">Current section</span><strong>{section}</strong><small>{presentation.navDetail}</small></div>
        {storySections.map((item) => <button type="button" key={item} className={section === item ? "is-active" : ""} onClick={() => setSection(item)} aria-current={section === item ? "step" : undefined} aria-pressed={section === item} aria-controls={`story-editor-${item.toLowerCase()}`}><span className="movie-story-section-copy"><strong>{item}</strong><small>{storySectionPresentation[item].navDetail}</small></span><ChevronRight size={13} aria-hidden="true" /></button>)}
        <div className="movie-story-provenance"><span className="movie-inspector-label">Revision provenance</span><span className={`movie-story-provenance-badge ${provenanceTone}`}>{draft.authorship === "AiSuggested" ? <Sparkles size={12} /> : <PencilRuler size={12} />}{storyAuthorshipLabel(draft.authorship)}</span><p>{storyAuthorshipDescription(draft.authorship)}</p></div>
      </nav>
      <article className={`movie-story-manuscript is-${section.toLowerCase()}`} aria-labelledby="story-current-section">
        <header className="movie-story-manuscript-head"><div><span className="movie-workspace-kicker">Current manuscript section</span><h4 id="story-current-section">{presentation.title}</h4><p>{presentation.description}</p></div><div className="movie-story-manuscript-meta"><span className={`movie-story-provenance-badge ${provenanceTone}`}>{draft.authorship === "AiSuggested" ? <Sparkles size={12} /> : <PencilRuler size={12} />}{storyAuthorshipLabel(draft.authorship)}</span><span className="movie-story-revision-chip">{status} · Revision {selectedRevision?.revisionNumber ?? "new"}</span></div></header>
        {section === "Screenplay" ? <ScreenplayEditor draft={draft} editable={canEdit} onUpdateDraft={updateDraft} onUpdateScene={updateScene} onUpdateElement={updateElement} onAddScene={addScene} /> : <StoryTextEditor section={section} draft={draft} editable={canEdit} onUpdate={(value) => updateDraft({ [section.toLowerCase()]: value } as Partial<MovieStoryRevisionInput>)} />}
      </article>
      <aside className="movie-story-history" aria-label="Revision history"><div className="movie-section-head"><div><span className="movie-workspace-kicker">Version control</span><h4>Revision history</h4></div><span className="movie-section-count">{story.revisions.length} passes</span></div>{story.revisions.length ? <div className="movie-story-history-list">{story.revisions.map((revision) => <button type="button" className={`movie-story-history-item ${revision.id === story.currentRevisionId ? "is-current" : ""} ${revision.id === story.approvedRevisionId ? "is-approved" : ""}`} key={revision.id} onClick={() => void inspectRevision(revision.id)} aria-current={revision.id === selectedRevision?.id ? "true" : undefined}><span>Revision {revision.revisionNumber}</span><strong>{revision.status}</strong><small>{storyAuthorshipLabel(revision.authorship)} · {revision.changeSummary || "No change note"}</small></button>)}</div> : <p className="movie-story-history-empty">Your first saved pass will appear here.</p>}<div className="movie-story-history-note"><ShieldCheck size={14} /><span>Approved and rejected revisions are immutable.</span></div></aside>
    </div>
  </div>;
}

function StoryProposalReview({ proposal, busy, onReview, onApply }: { proposal: DirectorProposal; busy: boolean; onReview: (approve: boolean) => void; onApply: () => void }) {
  const review = proposal.storyReview!;
  return <section className="movie-workspace-section movie-story-proposal-review"><div className="movie-section-head"><div><span className="movie-workspace-kicker">Proposal review · {proposal.status}</span><h3>{proposal.title}</h3></div><span className="movie-section-count">{review.appliesToStory ? "Revision candidate" : "Review only"}</span></div><p>{proposal.summary}</p>{review.changes.map((change) => <div className="movie-story-diff" key={`${change.field}-${change.target}`}><span className="movie-inspector-label">{change.field}</span><div><article><small>Existing content</small><p>{change.existingContent}</p></article><article className="is-proposed"><small>Proposed content</small><p>{change.proposedContent}</p></article></div></div>)}{review.findings.length > 0 && <div className="movie-story-findings"><strong>Grounded Director findings</strong><ul>{review.findings.map((finding, index) => <li key={`${finding.category}-${finding.affectedTarget.targetId ?? "project"}-${index}`}><div><b>{finding.findingType.replaceAll("_", " ")}</b><span> · {finding.severity} · {finding.category.replaceAll("_", " ")} · {Math.round(finding.confidence * 100)}% confidence</span></div><p>{finding.explanation}</p><small>Suggested correction: {finding.suggestedCorrection}</small>{finding.uncertainty && <small>Uncertainty: {finding.uncertainty}</small>}<details><summary>Evidence</summary><ul>{finding.evidence.map((evidence, evidenceIndex) => <li key={`${evidence.source}-${evidenceIndex}`}><b>{evidence.source}</b>{evidence.revision && ` · ${evidence.revision}`}<br />{evidence.excerpt}</li>)}</ul></details></li>)}</ul></div>}{review.findings.length === 0 && !review.appliesToStory && <div className="movie-story-findings"><strong>No grounded inconsistencies found</strong><p>The bounded Story, locked Guide, and available continuity records did not produce a typed finding. This is not a claim that unstated story meaning is consistent.</p></div>}<div className="movie-story-review-actions">{proposal.status === "PendingApproval" && <><button className="movie-workspace-button is-primary" type="button" onClick={() => onReview(true)} disabled={busy}>Accept proposal</button><button className="movie-workspace-button" type="button" onClick={() => onReview(false)} disabled={busy}>Reject</button></>}{proposal.status === "Approved" && review.appliesToStory && <button className="movie-workspace-button is-primary" type="button" onClick={onApply} disabled={busy}>Apply to editable Story revision</button>}{proposal.status === "Approved" && !review.appliesToStory && <span className="movie-story-approved-note">Findings accepted. No Story text was changed.</span>}</div></section>;
}


function StoryTextEditor({ section, draft, editable, onUpdate }: { section: Exclude<StorySection, "Screenplay">; draft: MovieStoryRevisionInput; editable: boolean; onUpdate: (value: string) => void }) {
  const key = section.toLowerCase() as "premise" | "logline" | "synopsis" | "treatment";
  const textareaRef = useRef<HTMLTextAreaElement>(null);
  const hints: Record<typeof key, string> = { premise: "What is the human truth or dramatic engine?", logline: "Who wants what, what stands in the way, and why now?", synopsis: "The complete story arc in clear, grounded prose.", treatment: "A scene-aware prose map of the story before pages." };

  function resizeTextarea(target: HTMLTextAreaElement) {
    target.style.height = "0px";
    target.style.height = `${target.scrollHeight}px`;
  }

  useEffect(() => {
    if (textareaRef.current) resizeTextarea(textareaRef.current);
  }, [key, draft[key]]);

  return <div className={`movie-story-text-editor is-${key}`}><p className="movie-story-writing-prompt" id={`story-editor-${key}-hint`}>{hints[key]}</p><textarea ref={textareaRef} id={`story-editor-${key}`} aria-label={section} aria-describedby={`story-editor-${key}-hint`} value={draft[key]} onInput={(event) => resizeTextarea(event.currentTarget)} onChange={(event) => onUpdate(event.target.value)} disabled={!editable} placeholder={`Write the ${section.toLowerCase()}…`} /><span className="movie-story-character-count">{draft[key].length.toLocaleString()} characters</span></div>;
}

function ScreenplayEditor({ draft, editable, onUpdateDraft, onUpdateScene, onUpdateElement, onAddScene }: { draft: MovieStoryRevisionInput; editable: boolean; onUpdateDraft: (patch: Partial<MovieStoryRevisionInput>) => void; onUpdateScene: (index: number, patch: Partial<MovieStoryRevisionInput["scenes"][number]>) => void; onUpdateElement: (sceneIndex: number, elementIndex: number, patch: Partial<MovieStoryRevisionInput["scenes"][number]["elements"][number]>) => void; onAddScene: () => void }) {
  return <div className="movie-screenplay-editor" id="story-editor-screenplay" tabIndex={-1} aria-label="Screenplay editor"><div className="movie-screenplay-toolbar"><span>{draft.scenes.length} scenes</span><span>Structured pages</span><label>Authorship<select aria-label="Revision authorship" value={draft.authorship} onChange={(event) => onUpdateDraft({ authorship: event.target.value as MovieStoryRevisionInput["authorship"] })} disabled={!editable}><option value="Human">Human</option><option value="HumanEdited">Human edited</option><option value="AiSuggested">AI suggested</option></select></label></div>{draft.scenes.length ? draft.scenes.map((scene, sceneIndex) => <article className="movie-screenplay-scene" key={`${scene.sceneIdentifier}-${sceneIndex}`}><header><span className="movie-screenplay-scene-number">{String(sceneIndex + 1).padStart(2, "0")}</span><div><input aria-label={`Scene ${sceneIndex + 1} identifier`} value={scene.sceneIdentifier} onChange={(event) => onUpdateScene(sceneIndex, { sceneIdentifier: event.target.value })} disabled={!editable} /><input className="movie-screenplay-slugline" aria-label={`Scene ${sceneIndex + 1} heading`} value={scene.slugline} onChange={(event) => onUpdateScene(sceneIndex, { slugline: event.target.value })} disabled={!editable} /></div><span className="movie-screenplay-scene-meta">Act {scene.actNumber ?? "—"} · Seq {scene.sequenceNumber ?? "—"}</span></header><textarea className="movie-screenplay-scene-note" aria-label={`Scene ${sceneIndex + 1} synopsis`} value={scene.synopsis ?? ""} onChange={(event) => onUpdateScene(sceneIndex, { synopsis: event.target.value })} disabled={!editable} placeholder="Scene intention / beat" />{scene.elements.map((element, elementIndex) => <div className={`movie-screenplay-element is-${element.elementType.toLowerCase()}`} key={`${scene.sceneIdentifier}-${elementIndex}`}><select aria-label={`Scene ${sceneIndex + 1} element ${elementIndex + 1} type`} value={element.elementType} onChange={(event) => onUpdateElement(sceneIndex, elementIndex, { elementType: event.target.value as MovieScreenplayElementType })} disabled={!editable}>{screenplayElementTypes.map((type) => <option key={type} value={type}>{type}</option>)}</select>{element.elementType === "Dialogue" && <input aria-label={`Scene ${sceneIndex + 1} element ${elementIndex + 1} character`} value={element.characterName ?? ""} onChange={(event) => onUpdateElement(sceneIndex, elementIndex, { characterName: event.target.value })} disabled={!editable} placeholder="CHARACTER" />}{element.elementType === "Dialogue" && <input aria-label={`Scene ${sceneIndex + 1} element ${elementIndex + 1} parenthetical`} value={element.parenthetical ?? ""} onChange={(event) => onUpdateElement(sceneIndex, elementIndex, { parenthetical: event.target.value })} disabled={!editable} placeholder="(parenthetical)" />}{element.elementType === "Transition" ? <input aria-label={`Scene ${sceneIndex + 1} transition`} value={element.content} onChange={(event) => onUpdateElement(sceneIndex, elementIndex, { content: event.target.value })} disabled={!editable} placeholder="CUT TO:" /> : <textarea aria-label={`Scene ${sceneIndex + 1} element ${elementIndex + 1} content`} value={element.content} onChange={(event) => onUpdateElement(sceneIndex, elementIndex, { content: event.target.value })} disabled={!editable} placeholder={element.elementType === "Action" ? "Describe what we see and hear…" : "Write the page…"} />}{editable && <button type="button" aria-label="Remove screenplay element" onClick={() => onUpdateScene(sceneIndex, { elements: scene.elements.filter((_, index) => index !== elementIndex) })}><Trash2 size={13} /></button>}</div>)}<div className="movie-screenplay-scene-actions">{editable && <button type="button" className="movie-text-action" onClick={() => onUpdateScene(sceneIndex, { elements: [...scene.elements, { elementType: "Action", content: "", characterName: null, parenthetical: null }] })}><Plus size={12} /> Add element</button>}</div></article>) : <div className="movie-screenplay-empty"><BookOpen size={20} /><strong>Your first scene starts the pages.</strong><p>Use typed screenplay blocks instead of flattening the script into a generic document.</p></div>}{editable && <button type="button" className="movie-add-screenplay-scene" onClick={onAddScene}><Plus size={14} /> Add scene</button>}</div>;
}

type CharacterDraft = Omit<MovieCharacter, "id" | "states" | "relationships" | "continuityLocks">;
type StateDraft = Omit<MovieCharacterState, "id" | "createdAt" | "updatedAt"> & { key: string };
const emptyCharacterDraft: CharacterDraft = { name: "", role: "", description: "", appearance: "", physicalDescription: "", wardrobe: "", voiceReference: "", personalityAndStoryNotes: "", voiceAndPerformance: "", continuityNotes: "", referenceAssetId: null, referenceAssetIds: [] };
const emptyStateDraft: StateDraft = { key: "", label: "", wardrobe: "", ageOrTimeState: "", appearance: "", injuryOrCondition: "", locationOrStoryState: "", continuityNotes: "" };

function CastModule({ projectId }: { projectId: string }) {
  const [cast, setCast] = useState<MovieCast | null>(null);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [detail, setDetail] = useState<MovieCharacter | null>(null);
  const [assets, setAssets] = useState<Asset[]>([]);
  const [draft, setDraft] = useState<CharacterDraft>(emptyCharacterDraft);
  const [stateDraft, setStateDraft] = useState<StateDraft>(emptyStateDraft);
  const [editingStateId, setEditingStateId] = useState<string | null>(null);
  const [relationship, setRelationship] = useState({ relatedCharacterId: "", relationshipType: "", notes: "" });
  const [lock, setLock] = useState({ fieldKey: "appearance", lockedValue: "", characterStateId: "" });
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");

  async function loadCast(nextSelectedId?: string | null) {
    const result = await api.getMovieCast(projectId);
    setCast(result);
    const nextId = nextSelectedId === undefined ? (selectedId ?? result.characters[0]?.id ?? null) : nextSelectedId;
    setSelectedId(nextId);
    if (nextId) {
      const loaded = await api.getMovieCharacterDetail(nextId);
      setDetail(loaded.character);
      setDraft(draftFromCharacter(loaded.character));
    } else {
      setDetail(null);
      setDraft(emptyCharacterDraft);
    }
  }

  useEffect(() => {
    let mounted = true;
    void api.getMovieCast(projectId).then((result) => {
      if (!mounted) return;
      setCast(result);
      setSelectedId(result.characters[0]?.id ?? null);
      if (result.characters[0]) return api.getMovieCharacterDetail(result.characters[0].id).then((loaded) => { if (mounted) { setDetail(loaded.character); setDraft(draftFromCharacter(loaded.character)); } });
      return undefined;
    }).catch((cause) => { if (mounted) setError(cause instanceof Error ? cause.message : "The cast room could not be loaded."); }).finally(() => { if (mounted) setLoading(false); });
    return () => { mounted = false; };
  }, [projectId]);

  useEffect(() => {
    if (!cast) return;
    void api.listAssets(cast.project.workspaceId, { assetType: "image", status: "Active", pageSize: 24 }).then((result) => setAssets(result.items)).catch(() => setAssets([]));
  }, [cast]);

  async function selectCharacter(id: string) {
    setError("");
    try { const loaded = await api.getMovieCharacterDetail(id); setSelectedId(id); setDetail(loaded.character); setDraft(draftFromCharacter(loaded.character)); setStateDraft(emptyStateDraft); setEditingStateId(null); } catch (cause) { setError(cause instanceof Error ? cause.message : "The character could not be loaded."); }
  }
  async function saveCharacter() {
    if (!draft.name.trim() || !draft.description.trim()) return;
    setSaving(true); setError("");
    try {
      const payload = { ...draft, name: draft.name.trim(), description: draft.description.trim(), referenceAssetIds: draft.referenceAssetIds };
      const saved = selectedId ? await api.updateMovieCharacter(selectedId, payload) : await api.addMovieCharacter(projectId, payload);
      await loadCast(saved.id);
    } catch (cause) { setError(cause instanceof Error ? cause.message : "The character card could not be saved."); } finally { setSaving(false); }
  }
  async function saveState() {
    if (!detail || !stateDraft.key.trim()) return;
    setSaving(true); setError("");
    try {
      const saved = editingStateId ? await api.updateMovieCharacterState(editingStateId, stateDraft) : await api.addMovieCharacterState(detail.id, stateDraft);
      const next = { ...detail, states: editingStateId ? detail.states.map((item) => item.id === saved.id ? saved : item) : [...detail.states, saved] };
      setDetail(next); setStateDraft(emptyStateDraft); setEditingStateId(null); await loadCast(detail.id);
    } catch (cause) { setError(cause instanceof Error ? cause.message : "The character state could not be saved."); } finally { setSaving(false); }
  }
  async function saveRelationship() {
    if (!detail || !relationship.relatedCharacterId || !relationship.relationshipType.trim()) return;
    setSaving(true); setError("");
    try { await api.addMovieCharacterRelationship(detail.id, relationship); setRelationship({ relatedCharacterId: "", relationshipType: "", notes: "" }); const loaded = await api.getMovieCharacterDetail(detail.id); setDetail(loaded.character); await loadCast(detail.id); } catch (cause) { setError(cause instanceof Error ? cause.message : "The relationship could not be saved."); } finally { setSaving(false); }
  }
  async function saveLock() {
    if (!detail || !lock.lockedValue.trim()) return;
    setSaving(true); setError("");
    try { await api.lockMovieCharacterFact(detail.id, { fieldKey: lock.fieldKey, lockedValue: lock.lockedValue, characterStateId: lock.characterStateId || null }); setLock({ ...lock, lockedValue: "" }); const loaded = await api.getMovieCharacterDetail(detail.id); setDetail(loaded.character); await loadCast(detail.id); } catch (cause) { setError(cause instanceof Error ? cause.message : "The continuity fact could not be locked."); } finally { setSaving(false); }
  }
  function toggleAsset(assetId: string) { setDraft((current) => { const ids = current.referenceAssetIds.includes(assetId) ? current.referenceAssetIds.filter((id) => id !== assetId) : [...current.referenceAssetIds, assetId]; return { ...current, referenceAssetIds: ids, referenceAssetId: ids[0] ?? null }; }); }
  if (loading) return <div className="movie-workspace-section movie-cast-loading"><span className="loading-spinner" /><span>Loading cast records…</span></div>;
  if (!cast) return <EmptyModule title="Cast room unavailable" text={error || "The cast read model is not available in this workspace."} />;
  const lockedCardFields = new Set((detail?.continuityLocks ?? []).filter((item) => !item.characterStateId).map((item) => item.fieldKey));
  const lockedStateFields = new Set((detail?.continuityLocks ?? []).filter((item) => item.characterStateId === (editingStateId ?? lock.characterStateId)).map((item) => item.fieldKey));
  return <div className="movie-module-stack movie-cast-room">
    <section className="movie-cast-hero"><div><span className="movie-workspace-kicker">Character cards · {cast.characters.length} records</span><h3>Durable identities for the film</h3><p>Reference assets stay in the shared Asset library. Empty imagery stays empty until a real asset is attached.</p></div><div className="movie-cast-hero-stat"><strong>{cast.characters.filter((item) => item.referenceAssetCount > 0).length}</strong><span>with references</span></div></section>
    <div className="movie-cast-layout"><section className="movie-cast-index movie-workspace-section"><div className="movie-section-head"><div><span className="movie-workspace-kicker">Cast index</span><h3>{cast.characters.length ? "Choose a character" : "No cast records yet"}</h3></div><span className="movie-section-count">{cast.characters.length} total</span></div>{cast.characters.length ? <div className="movie-cast-card-grid">{cast.characters.map((item) => <button key={item.id} type="button" className={`movie-cast-card ${selectedId === item.id ? "is-selected" : ""}`} onClick={() => void selectCharacter(item.id)}><CharacterAssetPreview assetId={item.referenceAssetId} name={item.name} /><span className="movie-record-index">{item.role || "Role not set"}</span><strong>{item.name}</strong><small>{item.description}</small><div className="movie-cast-card-meta"><span>{item.stateCount} state{item.stateCount === 1 ? "" : "s"}</span><span>{item.lockedFactCount ? <><LockKeyhole size={11} /> {item.lockedFactCount} locked</> : "No locks"}</span></div><div className="movie-cast-current-state"><span>Current state</span><strong>{item.latestState?.label || item.latestState?.key || "No state set"}</strong></div>{item.relationshipTypes.length > 0 && <em>{item.relationshipTypes.join(" · ")}</em>}</button>)}</div> : <EmptyModule title="No cast records yet" text="Create the first character card when the story has a person worth keeping consistent. No face or reference is fabricated here." />}</section>
      <section className="movie-cast-editor movie-workspace-section"><div className="movie-section-head"><div><span className="movie-workspace-kicker">Character card</span><h3>{detail?.name || "Create a character"}</h3></div>{detail && <span className="movie-section-count">{detail.continuityLocks.length} locked facts</span>}</div><CharacterEditor draft={draft} lockedFields={lockedCardFields} onChange={setDraft} onSave={() => void saveCharacter()} saving={saving} assets={assets} onToggleAsset={toggleAsset} /><StateEditor detail={detail} draft={stateDraft} lockedFields={lockedStateFields} editingStateId={editingStateId} onChange={setStateDraft} onEdit={(state) => { setEditingStateId(state.id); setStateDraft(state); }} onSave={() => void saveState()} saving={saving} /><CastSecondaryEditors detail={detail} cast={cast} relationship={relationship} onRelationshipChange={setRelationship} onSaveRelationship={() => void saveRelationship()} lock={lock} onLockChange={setLock} onSaveLock={() => void saveLock()} saving={saving} /></section></div>
    {error && <div className="movie-workspace-error-inline"><XCircleIcon /> {error}</div>}
  </div>;
}

function draftFromCharacter(character: MovieCharacter): CharacterDraft { return { name: character.name, role: character.role, description: character.description, appearance: character.appearance, physicalDescription: character.physicalDescription, wardrobe: character.wardrobe, voiceReference: character.voiceReference, personalityAndStoryNotes: character.personalityAndStoryNotes, voiceAndPerformance: character.voiceAndPerformance, continuityNotes: character.continuityNotes, referenceAssetId: character.referenceAssetId, referenceAssetIds: character.referenceAssetIds }; }
function CharacterAssetPreview({ assetId, name }: { assetId: string | null; name: string }) { return <div className="movie-cast-card-art">{assetId ? <img src={assetFileUrl(assetId, true)} alt={`${name} reference`} loading="lazy" /> : <><ImageIcon size={24} /><span>No reference asset</span></>}</div>; }
function CharacterEditor({ draft, lockedFields, onChange, onSave, saving, assets, onToggleAsset }: { draft: CharacterDraft; lockedFields: Set<string>; onChange: (draft: CharacterDraft) => void; onSave: () => void; saving: boolean; assets: Asset[]; onToggleAsset: (id: string) => void }) {
  const field = (key: keyof CharacterDraft, label: string, multiline = true) => <label className={`movie-cast-field ${multiline ? "is-wide" : ""}`}><span>{label}{lockedFields.has(key) && <LockKeyhole size={11} />}</span>{multiline ? <textarea value={String(draft[key] ?? "")} disabled={lockedFields.has(key)} onChange={(event) => onChange({ ...draft, [key]: event.target.value })} /> : <input value={String(draft[key] ?? "")} disabled={lockedFields.has(key)} onChange={(event) => onChange({ ...draft, [key]: event.target.value })} />}</label>;
  return <div className="movie-cast-editor-body"><div className="movie-cast-form-grid">{field("name", "Name", false)}{field("role", "Role / importance", false)}{field("description", "Story function / description")}{field("appearance", "Appearance")}{field("physicalDescription", "Physical description")}{field("wardrobe", "Wardrobe / reference direction")}{field("personalityAndStoryNotes", "Personality / story notes")}{field("voiceReference", "Voice reference")}{field("voiceAndPerformance", "Performance notes")}{field("continuityNotes", "Continuity notes")}</div><div className="movie-cast-asset-picker"><div className="movie-section-head"><div><span className="movie-workspace-kicker">Reference assets</span><h3>Shared Asset library</h3></div><span className="movie-section-count">{draft.referenceAssetIds.length} selected</span></div>{assets.length ? <div className="movie-cast-asset-grid">{assets.map((asset) => <button type="button" key={asset.id} className={`movie-cast-asset ${draft.referenceAssetIds.includes(asset.id) ? "is-selected" : ""}`} onClick={() => onToggleAsset(asset.id)}><img src={assetFileUrl(asset.id, true)} alt={asset.name} loading="lazy" /><span>{asset.name}</span></button>)}</div> : <p className="movie-cast-muted">No active image assets are available in this workspace. Add one in Assets, then return here.</p>}</div><div className="movie-cast-save-row"><p>Locked fields stay read-only here; an approved value is never silently overwritten.</p><button type="button" className="movie-workspace-button is-primary" onClick={onSave} disabled={saving || !draft.name.trim() || !draft.description.trim()}><Save size={14} /> {saving ? "Saving…" : "Save character card"}</button></div></div>;
}

function StateEditor({ detail, draft, lockedFields, editingStateId, onChange, onEdit, onSave, saving }: { detail: MovieCharacter | null; draft: StateDraft; lockedFields: Set<string>; editingStateId: string | null; onChange: (draft: StateDraft) => void; onEdit: (state: MovieCharacterState) => void; onSave: () => void; saving: boolean }) {
  const field = (key: keyof StateDraft, label: string) => <label className="movie-cast-field"><span>{label}{lockedFields.has(key) && <LockKeyhole size={11} />}</span><textarea value={String(draft[key] ?? "")} disabled={lockedFields.has(key)} onChange={(event) => onChange({ ...draft, [key]: event.target.value })} /></label>;
  return <section className="movie-cast-subsection"><div className="movie-section-head"><div><span className="movie-workspace-kicker">Story / production context</span><h3>Character states</h3></div><span className="movie-section-count">{detail?.states.length ?? 0} saved</span></div>{detail?.states.length ? <div className="movie-cast-state-list">{detail.states.map((state) => <button type="button" key={state.id} className={`movie-cast-state ${editingStateId === state.id ? "is-selected" : ""}`} onClick={() => onEdit(state)}><span>{state.label || state.key}</span><small>{state.wardrobe || state.injuryOrCondition || state.ageOrTimeState || "No state detail set"}</small><em>{detail.continuityLocks.filter((item) => item.characterStateId === state.id).length ? <><LockKeyhole size={11} /> Locked facts</> : "Editable context"}</em></button>)}</div> : <p className="movie-cast-muted">No wardrobe, injury, age/time, or performance variation has been persisted yet.</p>}<div className="movie-cast-form-grid movie-cast-state-form">{field("key", "State key")}{field("label", "Label")}{field("wardrobe", "Wardrobe")}{field("ageOrTimeState", "Age / time state")}{field("appearance", "Appearance variation")}{field("injuryOrCondition", "Injury / condition")}{field("locationOrStoryState", "Location / story state")}{field("continuityNotes", "State continuity notes")}</div><div className="movie-cast-save-row"><p>{editingStateId ? "Editing a persisted state." : "Add only a state supported by the production context."}</p><button type="button" className="movie-workspace-button" onClick={onSave} disabled={saving || !draft.key.trim()}><Plus size={14} /> {saving ? "Saving…" : editingStateId ? "Update state" : "Add state"}</button></div></section>;
}

function CastSecondaryEditors({ detail, cast, relationship, onRelationshipChange, onSaveRelationship, lock, onLockChange, onSaveLock, saving }: { detail: MovieCharacter | null; cast: MovieCast; relationship: { relatedCharacterId: string; relationshipType: string; notes: string }; onRelationshipChange: (value: { relatedCharacterId: string; relationshipType: string; notes: string }) => void; onSaveRelationship: () => void; lock: { fieldKey: string; lockedValue: string; characterStateId: string }; onLockChange: (value: { fieldKey: string; lockedValue: string; characterStateId: string }) => void; onSaveLock: () => void; saving: boolean }) {
  return <section className="movie-cast-subsection movie-cast-secondary"><div className="movie-section-head"><div><span className="movie-workspace-kicker">Connections & approvals</span><h3>Relationships and locks</h3></div><LockKeyhole size={16} /></div><div className="movie-cast-secondary-grid"><div><span className="movie-inspector-label">Relationships</span>{detail?.relationships.length ? <div className="movie-cast-chip-list">{detail.relationships.map((item) => <span key={item.id}>{item.relationshipType} · {item.relatedCharacterName}</span>)}</div> : <p className="movie-cast-muted">No relationships persisted.</p>}<select value={relationship.relatedCharacterId} onChange={(event) => onRelationshipChange({ ...relationship, relatedCharacterId: event.target.value })}><option value="">Related character</option>{cast.characters.filter((item) => item.id !== detail?.id).map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}</select><input value={relationship.relationshipType} onChange={(event) => onRelationshipChange({ ...relationship, relationshipType: event.target.value })} placeholder="Relationship type" /><textarea value={relationship.notes} onChange={(event) => onRelationshipChange({ ...relationship, notes: event.target.value })} placeholder="Optional relationship note" /><button type="button" className="movie-workspace-button" onClick={onSaveRelationship} disabled={saving || !relationship.relatedCharacterId || !relationship.relationshipType.trim()}><Plus size={14} /> Add relationship</button></div><div><span className="movie-inspector-label">Continuity locks</span>{detail?.continuityLocks.length ? <div className="movie-cast-lock-list">{detail.continuityLocks.map((item) => <div key={item.id}><LockKeyhole size={12} /><strong>{item.fieldKey}</strong><span>{item.lockedValue}</span><small>{item.characterStateId ? "State fact" : "Card fact"} · approved and protected</small></div>)}</div> : <p className="movie-cast-muted">No facts are locked. Locks explain why a field is read-only and prevent silent mutation.</p>}<select value={lock.characterStateId} onChange={(event) => onLockChange({ ...lock, characterStateId: event.target.value })}><option value="">Lock a card fact</option>{detail?.states.map((item) => <option key={item.id} value={item.id}>Lock a state fact · {item.label || item.key}</option>)}</select><select value={lock.fieldKey} onChange={(event) => onLockChange({ ...lock, fieldKey: event.target.value })}>{(lock.characterStateId ? ["label", "wardrobe", "ageOrTimeState", "appearance", "injuryOrCondition", "locationOrStoryState", "continuityNotes"] : ["name", "role", "description", "appearance", "physicalDescription", "wardrobe", "voiceReference", "personalityAndStoryNotes", "voiceAndPerformance", "continuityNotes"]).map((field) => <option key={field} value={field}>{field}</option>)}</select><input value={lock.lockedValue} onChange={(event) => onLockChange({ ...lock, lockedValue: event.target.value })} placeholder="Exact current value to approve" /><button type="button" className="movie-workspace-button" onClick={onSaveLock} disabled={saving || !lock.lockedValue.trim()}><LockKeyhole size={14} /> Approve lock</button></div></div></section>;
}


function WorldModule({ projectId }: { projectId: string }) {
  return <div className="movie-module-stack"><MovieWorldWorkspace projectId={projectId} /></div>;
}

type SceneFilter = "all" | "proposed" | "approved" | "applied" | "manual";

type SceneEditInput = {
  title: string;
  summary: string;
  durationSeconds?: number | null;
  continuityNotes?: string | null;
  narration?: string | null;
  dialogue?: string | null;
};

function sceneProvenance(scene: MovieSceneWorkspace) {
  const normalizedStatus = scene.productionStatus.toLowerCase();
  const normalizedApproval = scene.approvalState.toLowerCase();
  const provenanceText = [scene.screenplaySource, scene.screenplaySynopsis].filter(Boolean).join(" ");
  const isApplied = Boolean(scene.screenplaySceneId);
  const isApproved = normalizedStatus.includes("approved") || normalizedApproval.includes("approved");
  const isProposed = !isApproved && (normalizedStatus.includes("planned") || normalizedStatus.includes("draft") || normalizedStatus.includes("proposed"));
  const isAiSuggested = /\b(?:ai(?:[-\s]suggested)?|director)\b/i.test(provenanceText);
  return { isApplied, isApproved, isProposed, isAiSuggested, isManual: !isApplied };
}

function sceneMatchesFilter(scene: MovieSceneWorkspace, filter: SceneFilter) {
  if (filter === "all") return true;
  const provenance = sceneProvenance(scene);
  return filter === "approved" ? provenance.isApproved : filter === "proposed" ? provenance.isProposed : filter === "applied" ? provenance.isApplied : provenance.isManual;
}

function SceneProvenanceBadges({ scene, compact = false }: { scene: MovieSceneWorkspace; compact?: boolean }) {
  const provenance = sceneProvenance(scene);
  const badges = [
    provenance.isProposed ? { label: "Proposed", tone: "proposed" } : null,
    provenance.isApproved ? { label: "Approved", tone: "approved" } : null,
    provenance.isApplied ? { label: "Applied / current", tone: "applied" } : null,
    provenance.isManual ? { label: "Manually edited", tone: "manual" } : null,
    provenance.isAiSuggested ? { label: "AI-suggested", tone: "ai" } : null,
  ].filter(Boolean) as Array<{ label: string; tone: string }>;
  return <span className={`movie-scene-provenance-badges ${compact ? "is-compact" : ""}`}>{badges.length ? badges.map((badge) => <span key={badge.label} className={`movie-scene-provenance is-${badge.tone}`}>{badge.label}</span>) : <span className="movie-scene-provenance is-neutral">Current plan</span>}</span>;
}

function ScenesModule({ projectId, project, presets, savingShot, onAddShot, selectedSceneId, selectedShotId, onSelectScene, onSelectShot, onGenerate, onPlanChange }: { projectId: string; project: MovieProject; presets: CinematographyPreset[]; savingShot: boolean; onAddShot: (sceneId: string, draft: ShotDesignerDraft) => Promise<boolean>; selectedSceneId: string | null; selectedShotId: string | null; onSelectScene: (sceneId: string) => void; onSelectShot: (shotId: string | null) => void; onGenerate: (sceneId: string) => Promise<void>; onPlanChange: (plan: MovieSceneShotPlan) => void }) {
  const [workspace, setWorkspace] = useState<MovieScenesWorkspace | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [breakdownBusy, setBreakdownBusy] = useState(false);
  const [newScene, setNewScene] = useState({ title: "", summary: "", sequenceId: "" });
  const [addingScene, setAddingScene] = useState(false);
  const [manualComposerOpen, setManualComposerOpen] = useState(false);
  const [filter, setFilter] = useState<SceneFilter>("all");
  const [editingSceneId, setEditingSceneId] = useState<string | null>(null);
  const [savingScene, setSavingScene] = useState(false);
  const manualComposerRef = useRef<HTMLFormElement>(null);
  const selectedScene = useMemo(() => workspace?.acts.flatMap((act) => act.sequences.flatMap((sequence) => sequence.scenes)).find((scene) => scene.id === selectedSceneId) ?? workspace?.acts[0]?.sequences[0]?.scenes[0] ?? null, [workspace, selectedSceneId]);
  const sequences = workspace?.acts.flatMap((act) => act.sequences) ?? [];
  const allScenes = workspace?.acts.flatMap((act) => act.sequences.flatMap((sequence) => sequence.scenes)) ?? [];
  const visibleScenes = useMemo(() => allScenes.filter((scene) => sceneMatchesFilter(scene, filter)), [allScenes, filter]);
  const hasScenes = allScenes.length > 0;
  const sceneShotCount = selectedScene?.shotCount ?? 0;

  const refresh = async () => {
    setLoading(true);
    try { setWorkspace(await api.getMovieScenesWorkspace(projectId)); } catch (cause) { setError(cause instanceof Error ? cause.message : "The Scenes room could not be loaded."); } finally { setLoading(false); }
  };
  useEffect(() => { void refresh(); }, [projectId]);
  useEffect(() => { if (!selectedSceneId && selectedScene) onSelectScene(selectedScene.id); }, [selectedScene, selectedSceneId, onSelectScene]);
  useEffect(() => { if (manualComposerOpen) window.setTimeout(() => manualComposerRef.current?.querySelector<HTMLInputElement>('input[name="scene-title"]')?.focus(), 0); }, [manualComposerOpen]);

  function openManualComposer() {
    setManualComposerOpen(true);
    window.setTimeout(() => manualComposerRef.current?.scrollIntoView({ behavior: "smooth", block: "center" }), 0);
  }

  async function breakDown() {
    if (!workspace || workspace.screenplayApprovalState !== "Approved") {
      setError("Plan my scenes uses the approved screenplay. Approve a screenplay revision first, or create a scene manually.");
      return;
    }
    setBreakdownBusy(true); setError("");
    try { const result = await api.breakDownMovieScreenplay(projectId); setWorkspace(result.workspace); const first = result.workspace.acts[0]?.sequences[0]?.scenes[0]; if (first) onSelectScene(first.id); }
    catch (cause) { setError(cause instanceof Error ? cause.message : "The approved screenplay could not be broken down."); }
    finally { setBreakdownBusy(false); }
  }

  async function reorderScene(scene: MovieSceneWorkspace, delta: number) {
    setError("");
    try { await api.reorderMovieEntity("scenes", scene.id, scene.sequence + delta); await refresh(); onSelectScene(scene.id); }
    catch (cause) { setError(cause instanceof Error ? cause.message : "The scene order could not be saved."); }
  }

  async function addScene() {
    if (!newScene.title.trim() || !newScene.summary.trim()) return;
    setAddingScene(true); setError("");
    try {
      let sequenceId = newScene.sequenceId || sequences[0]?.id;
      if (!sequenceId) {
        const act = await api.addMovieV2Act(projectId, { title: "Act 1" });
        const sequence = await api.addMovieV2Sequence(act.id, { title: "Sequence 1" });
        sequenceId = sequence.id;
      }
      const scene = await api.addMovieV2Scene(sequenceId, { title: newScene.title.trim(), summary: newScene.summary.trim() });
      await refresh(); onSelectScene(scene.id); setNewScene({ title: "", summary: "", sequenceId }); setManualComposerOpen(false); setFilter("all");
    } catch (cause) { setError(cause instanceof Error ? cause.message : "The production scene could not be saved."); }
    finally { setAddingScene(false); }
  }

  async function updateScene(sceneId: string, input: SceneEditInput) {
    setSavingScene(true); setError("");
    try { await api.updateMovieScene(sceneId, input); await refresh(); setEditingSceneId(null); }
    catch (cause) { setError(cause instanceof Error ? cause.message : "Scene editing is not available in the current Scenes API. Your draft was not saved."); }
    finally { setSavingScene(false); }
  }

  if (loading) return <section className="movie-workspace-section"><div className="movie-workspace-loading"><span className="loading-spinner" /><p>Loading the scene map…</p></div></section>;
  if (!workspace) return <EmptyModule title="Scenes workspace unavailable" text={error || "The production hierarchy could not be loaded."} />;
  return <div className="movie-module-stack movie-scenes-room">
    <section className={`movie-scene-command-bar ${hasScenes ? "has-scenes" : "is-empty"}`}><div><span className="movie-workspace-kicker">Scene planning desk</span><h3>{hasScenes ? "Shape the next production decision." : "Plan the film in scenes."}</h3><p>{hasScenes ? "Review the current plan, inspect its source, then move one scene forward at a time." : "Screenplay → production scenes → shots. Start from the approved screenplay, or make the first scene by hand. Existing scenes and shots are never silently replaced."}</p></div>{hasScenes ? <div className="movie-scene-command-actions"><button type="button" className="movie-workspace-button is-secondary" onClick={openManualComposer}><Plus size={14} /> Create manually</button><button type="button" className="movie-workspace-button is-primary" onClick={() => void breakDown()} disabled={breakdownBusy}>{breakdownBusy ? "Planning…" : "Plan another pass"}</button></div> : <div className="movie-scene-empty-actions"><button type="button" className="movie-workspace-button is-primary" onClick={() => void breakDown()} disabled={breakdownBusy}>{breakdownBusy ? "Planning…" : <><Sparkles size={14} /> Plan my scenes</>}</button><button type="button" className="movie-workspace-button is-secondary" onClick={openManualComposer}><PencilRuler size={14} /> Create manually</button></div>}</section>
    {!hasScenes && <section className="movie-scenes-empty-state" aria-labelledby="scenes-empty-title"><div className="movie-empty-orbit"><Clapperboard size={24} /></div><div><span className="movie-workspace-kicker">A calm first step</span><h3 id="scenes-empty-title">Your scene plan is still open.</h3><p>{workspace.screenplayApprovalState === "Approved" ? "Plan my scenes will map the approved screenplay into editable production records. You can review the result before planning shots." : "An approved screenplay is not available yet. Create a manual scene now, or return to Story to approve the source first."}</p></div><div className="movie-scenes-empty-source"><BookOpen size={14} /><span>{workspace.screenplayApprovalState === "Approved" ? `Approved screenplay${workspace.approvedRevisionNumber ? ` · revision ${workspace.approvedRevisionNumber}` : ""}` : "No approved screenplay linked"}</span></div></section>}
    <div className="movie-scenes-summary"><Metric label="Acts" value={workspace.acts.length} /><Metric label="Sequences" value={sequences.length} /><Metric label="Scenes" value={workspace.sceneCount} /><Metric label="Shots planned" value={workspace.shotCount} /></div>
    {workspace.screenplayApprovalState !== "Approved" && <div className="movie-scenes-callout"><BookOpen size={15} /><span>{workspace.screenplayApprovalState === "NotAvailable" ? "No approved screenplay is linked yet. Manual production scene creation remains available." : `Screenplay status: ${workspace.screenplayApprovalState}. Approve a revision before mapping it.`}</span></div>}
    {hasScenes && <section className="movie-scene-legend" aria-label="Scene status legend"><div><span className="movie-workspace-kicker">Read the plan</span><strong>Provenance stays visible</strong></div><p>Badges are derived from persisted screenplay links and scene status. AI-suggested appears only when the API records that provenance.</p><div className="movie-scene-legend-items"><span className="movie-scene-provenance is-proposed">Proposed</span><span className="movie-scene-provenance is-approved">Approved</span><span className="movie-scene-provenance is-applied">Applied / current</span><span className="movie-scene-provenance is-manual">Manually edited</span></div></section>}
    {hasScenes && <div className="movie-scene-filter-bar" role="toolbar" aria-label="Filter scenes"><span className="movie-inspector-label">Show</span>{([['all', 'All scenes'], ['proposed', 'Proposed'], ['approved', 'Approved'], ['applied', 'Applied / current'], ['manual', 'Manual']] as Array<[SceneFilter, string]>).map(([value, label]) => <button type="button" key={value} className={filter === value ? "is-active" : ""} aria-pressed={filter === value} onClick={() => setFilter(value)}>{label}<span>{value === "all" ? allScenes.length : allScenes.filter((scene) => sceneMatchesFilter(scene, value)).length}</span></button>)}</div>}
    <div className="movie-scene-workspace movie-scenes-hierarchy-layout" data-scene-shot-count={sceneShotCount}><section className="movie-workspace-section movie-scene-list-panel"><div className="movie-section-head"><div><span className="movie-workspace-kicker">Canonical hierarchy</span><h3>{filter === "all" ? "Acts, sequences, scenes" : `${filter[0].toUpperCase()}${filter.slice(1)} scenes`}</h3></div><span className="movie-section-count">{visibleScenes.length} shown</span></div>{workspace.acts.length && visibleScenes.length ? workspace.acts.map((act) => <SceneAct key={act.id} act={act} selectedSceneId={selectedScene?.id ?? null} visibleSceneIds={new Set(visibleScenes.map((scene) => scene.id))} onSelect={onSelectScene} onGenerate={onGenerate} onReorder={reorderScene} />) : <EmptyModule title={hasScenes ? "No scenes match this view" : "No production hierarchy yet"} text={hasScenes ? "Try another provenance filter." : "Plan from an approved screenplay or create a scene below."} />}</section><section className="movie-workspace-section movie-scene-inspector"><span className="movie-workspace-kicker">Scene inspector</span>{selectedScene ? <><ScenesInspector scene={selectedScene} editing={editingSceneId === selectedScene.id} saving={savingScene} onEdit={() => setEditingSceneId(selectedScene.id)} onCancel={() => setEditingSceneId(null)} onSave={(input) => updateScene(selectedScene.id, input)} /><ShotPlanBoard scene={selectedScene} selectedShotId={selectedShotId} onSelectShot={onSelectShot} onPlanChange={onPlanChange} />{project.scenes.find((item) => item.id === selectedScene.id) && <SceneShotDesigner scene={project.scenes.find((item) => item.id === selectedScene.id)!} guide={project.guide} presets={presets} saving={savingShot} onAddShot={onAddShot} />}</> : <EmptyModule title="Select a scene" text="The inspector will show screenplay source, continuity, world records, and shot readiness." />}</section></div>
    {(!hasScenes || manualComposerOpen) && <form ref={manualComposerRef} className="movie-add-scene-modern movie-scenes-add-form" onSubmit={(event) => { event.preventDefault(); void addScene(); }}><div><span className="movie-workspace-kicker">Manual production scene</span><h3>{hasScenes ? "Add a scene to the current plan" : "Create your first scene"}</h3><p>Manual scenes stay clearly marked and can be edited later when the scene workflow API is connected.</p></div><label><span className="sr-only">Scene title</span><input name="scene-title" value={newScene.title} onChange={(event) => setNewScene({ ...newScene, title: event.target.value })} placeholder="Scene title" /></label><label><span className="sr-only">Scene purpose</span><input value={newScene.summary} onChange={(event) => setNewScene({ ...newScene, summary: event.target.value })} placeholder="One-line scene intent" /></label><label><span className="sr-only">Sequence</span><select value={newScene.sequenceId} onChange={(event) => setNewScene({ ...newScene, sequenceId: event.target.value })}><option value="">First sequence</option>{sequences.map((sequence) => <option key={sequence.id} value={sequence.id}>{sequence.sequence}. {sequence.title}</option>)}</select></label><div className="movie-scenes-form-actions"><button className="movie-workspace-button is-primary" type="submit" disabled={addingScene || !newScene.title.trim() || !newScene.summary.trim()}>{addingScene ? "Saving…" : <><Save size={13} /> Save scene</>}</button>{hasScenes && <button type="button" className="movie-workspace-button is-quiet" onClick={() => setManualComposerOpen(false)}>Close</button>}</div></form>}
    {error && <div className="movie-workspace-error-inline" role="alert"><XCircleIcon /> <span>{error}</span><button type="button" onClick={() => setError("")}><X size={12} /> Dismiss</button></div>}
  </div>;
}
function SceneAct({ act, selectedSceneId, visibleSceneIds, onSelect, onGenerate, onReorder }: { act: import("@/lib/api").MovieScenesAct; selectedSceneId: string | null; visibleSceneIds: Set<string>; onSelect: (id: string) => void; onGenerate: (id: string) => Promise<void>; onReorder: (scene: MovieSceneWorkspace, delta: number) => Promise<void> }) {
  const sequences = act.sequences.map((sequence) => ({ ...sequence, scenes: sequence.scenes.filter((scene) => visibleSceneIds.has(scene.id)) })).filter((sequence) => sequence.scenes.length);
  if (!sequences.length) return null;
  return <div className="movie-hierarchy-act"><div className="movie-hierarchy-label"><span>ACT {String(act.sequence).padStart(2, "0")}</span><strong>{act.title}</strong><small className={`movie-hierarchy-status is-${act.status.toLowerCase()}`}>{act.status}</small></div>{sequences.map((sequence) => <div className="movie-hierarchy-sequence" key={sequence.id}><div className="movie-hierarchy-sequence-head"><span>SEQ {String(sequence.sequence).padStart(2, "0")}</span><strong>{sequence.title}</strong><small>{sequence.scenes.length} scenes</small></div>{sequence.scenes.map((scene, index) => <SceneWorkspaceItem key={scene.id} scene={scene} isSelected={scene.id === selectedSceneId} canMoveUp={index > 0} canMoveDown={index < sequence.scenes.length - 1} onSelect={() => onSelect(scene.id)} onGenerate={() => void onGenerate(scene.id)} onReorder={(delta) => void onReorder(scene, delta)} />)}</div>)}</div>;
}
function SceneWorkspaceItem({ scene, isSelected, canMoveUp, canMoveDown, onSelect, onGenerate, onReorder }: { scene: MovieSceneWorkspace; isSelected: boolean; canMoveUp: boolean; canMoveDown: boolean; onSelect: () => void; onGenerate: () => void; onReorder: (delta: number) => void }) {
  return <article className={`movie-scene-list-item ${isSelected ? "is-selected" : ""}`}><button type="button" className="movie-scene-list-main" onClick={onSelect} aria-pressed={isSelected}><span className="movie-scene-sequence">{String(scene.sequence).padStart(2, "0")}</span><span><strong>{scene.title}</strong><small>{scene.slug}</small><SceneProvenanceBadges scene={scene} compact /></span><ChevronRight size={14} /></button><div className="movie-scene-list-actions"><span className="movie-scene-shot-count">{scene.shotCount} shots</span><span className="movie-scene-order-actions"><button type="button" aria-label={`Move ${scene.title} up`} onClick={() => onReorder(-1)} disabled={!canMoveUp}><ArrowUp size={11} /></button><button type="button" aria-label={`Move ${scene.title} down`} onClick={() => onReorder(1)} disabled={!canMoveDown}><ArrowDown size={11} /></button></span><button type="button" className="movie-text-action" onClick={onGenerate}><Sparkles size={12} /> Generate / regenerate</button></div></article>;
}
function ScenesInspector({ scene, editing, saving, onEdit, onCancel, onSave }: { scene: MovieSceneWorkspace; editing: boolean; saving: boolean; onEdit: () => void; onCancel: () => void; onSave: (input: SceneEditInput) => Promise<void> }) {
  const [draft, setDraft] = useState<SceneEditInput>({ title: scene.title, summary: scene.description, durationSeconds: scene.durationSeconds, continuityNotes: scene.continuityWarnings.join("\n") });
  useEffect(() => { setDraft({ title: scene.title, summary: scene.description, durationSeconds: scene.durationSeconds, continuityNotes: scene.continuityWarnings.join("\n") }); }, [scene.id, scene.title, scene.description, scene.durationSeconds, scene.continuityWarnings]);
  const provenance = sceneProvenance(scene);
  return <div className="movie-inspector-content">{editing ? <form className="movie-scene-edit-form" onSubmit={(event) => { event.preventDefault(); void onSave({ ...draft, title: draft.title.trim(), summary: draft.summary.trim() }); }}><label><span>Scene title</span><input value={draft.title} onChange={(event) => setDraft({ ...draft, title: event.target.value })} /></label><label><span>Scene intent / summary</span><textarea value={draft.summary} onChange={(event) => setDraft({ ...draft, summary: event.target.value })} /></label><label><span>Continuity notes</span><textarea value={draft.continuityNotes ?? ""} onChange={(event) => setDraft({ ...draft, continuityNotes: event.target.value })} /></label><div className="movie-scene-edit-actions"><button type="submit" className="movie-workspace-button is-primary" disabled={saving || !draft.title.trim() || !draft.summary.trim()}>{saving ? "Saving…" : <><Save size={13} /> Save edit</>}</button><button type="button" className="movie-workspace-button is-quiet" onClick={onCancel}>Cancel</button></div><p className="movie-scene-edit-note">This saves through the scene update contract when available. No local-only scene record is created.</p></form> : <><div className="movie-scenes-inspector-topline"><SceneProvenanceBadges scene={scene} /><span className="movie-scene-state">{scene.productionStatus}</span></div><div className="movie-scenes-inspector-heading"><div><h3>{scene.title}</h3><p>{scene.description}</p></div><button type="button" className="movie-text-action" onClick={onEdit}><PencilRuler size={12} /> Edit</button></div><div className="movie-inspector-facts"><span><strong>{scene.shotCount ?? 0}</strong> shots</span><span><strong>{scene.storyPosition}</strong> position</span></div><div className="movie-scenes-detail-grid"><RecordLine label="Slug / source" value={scene.slug} /><RecordLine label="Purpose" value={scene.purpose} /><RecordLine label="Characters" value={scene.characters.join(", ") || null} /><RecordLine label="Location" value={scene.locations.map((item) => item.name).join(", ") || null} /><RecordLine label="Set" value={scene.sets.map((item) => item.name).join(", ") || null} /><RecordLine label="Screenplay" value={scene.screenplaySource ? `${scene.screenplaySource}${scene.screenplayRevisionNumber ? ` · revision ${scene.screenplayRevisionNumber}` : ""}` : null} /></div>{scene.screenplaySynopsis && <div className="movie-scenes-source-note"><BookOpen size={14} /><p>{scene.screenplaySynopsis}</p></div>}{scene.continuityWarnings.length ? <div className="movie-scenes-warning-list"><span className="movie-inspector-label">Continuity warnings</span>{scene.continuityWarnings.map((warning) => <p key={warning}>{warning}</p>)}</div> : <div className="movie-scenes-clear-signal"><ShieldCheck size={14} /> No persisted continuity warnings</div>}<div className="movie-scenes-review-boundary"><Sparkles size={14} /><div><strong>{provenance.isApplied ? "Applied to the current production map" : "Scene proposal boundary"}</strong><p>Use the Director panel for a grounded proposal. Review it, approve or reject it, then execute explicitly; this room never silently applies AI changes.</p></div></div></>}</div>;
}
type ShotDraft = MovieShotPlanningInput & { status?: string | null };
type ShotEditorDraft = ShotDraft & {
  qualityRequirements: string;
  productionComplexity: string;
  audioDependency: string;
  vfxRequirements: string;
  productionNotes: string;
};

const blankShot: ShotEditorDraft = { description: "", purpose: "", subjects: "", locationSet: "", durationSeconds: null, productionRequirements: "", continuityReferences: "", cameraAndFraming: "", cameraMotion: "", narration: "", dialogue: "", visualContinuityNotes: "", subjectCharacterIds: [], qualityRequirements: "", productionComplexity: "", audioDependency: "", vfxRequirements: "", productionNotes: "" };

const advancedShotLabels = [
  ["Production complexity", "productionComplexity"],
  ["Audio / dialogue dependency", "audioDependency"],
  ["VFX requirements", "vfxRequirements"],
  ["Production notes", "productionNotes"],
] as const;

function readShotEditorNotes(value: string | null): Pick<ShotEditorDraft, "qualityRequirements" | "productionComplexity" | "audioDependency" | "vfxRequirements" | "productionNotes"> {
  const parsed = { qualityRequirements: "", productionComplexity: "", audioDependency: "", vfxRequirements: "", productionNotes: "" };
  if (!value?.trim()) return parsed;
  const entries: Array<[keyof typeof parsed, string]> = [["qualityRequirements", "Quality requirements"], ["productionComplexity", "Production complexity"], ["audioDependency", "Audio / dialogue dependency"], ["vfxRequirements", "VFX requirements"], ["productionNotes", "Production notes"]];
  const found = entries.some(([, label]) => value.includes(`${label}:`));
  if (!found) return { ...parsed, qualityRequirements: value };
  for (const [key, label] of entries) {
    const match = value.match(new RegExp(`${label}:\\s*([^\\n]*)`, "i"));
    parsed[key] = match?.[1]?.trim() ?? "";
  }
  return parsed;
}

function writeShotEditorNotes(draft: ShotEditorDraft) {
  const values: Array<[string, string]> = [["Quality requirements", draft.qualityRequirements], ...advancedShotLabels.map(([label, key]) => [label, draft[key]] as [string, string])];
  const filled = values.filter(([, value]) => value.trim());
  return filled.length ? filled.map(([label, value]) => `${label}: ${value.trim()}`).join("\n") : null;
}

function draftFromShot(shot: MovieShot): ShotEditorDraft {
  return { ...blankShot, description: shot.description, purpose: shot.purpose, subjects: shot.subjects, locationSet: shot.locationSet, durationSeconds: shot.durationSeconds, productionRequirements: shot.productionRequirements, continuityReferences: shot.continuityReferences, cameraAndFraming: shot.cameraAndFraming, cameraMotion: shot.cameraMotion, narration: shot.narration, dialogue: shot.dialogue, visualContinuityNotes: shot.visualContinuityNotes, subjectCharacterIds: shot.subjectCharacterIds, status: shot.status, ...readShotEditorNotes(shot.productionRequirements) };
}

function ShotPlanBoard({ scene, selectedShotId, onSelectShot, onPlanChange }: { scene: { id: string; durationSeconds: number | null }; selectedShotId: string | null; onSelectShot: (shotId: string | null) => void; onPlanChange: (plan: MovieSceneShotPlan) => void }) {
  const [plan, setPlan] = useState<MovieSceneShotPlan | null>(null);
  const [draft, setDraft] = useState<ShotEditorDraft>(blankShot);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");

  useEffect(() => {
    let active = true;
    setPlan(null);
    setEditingId(null);
    void api.getMovieSceneShotPlan(scene.id).then((result) => { if (active) setPlan(result); }).catch((cause) => { if (active) setError(cause instanceof Error ? cause.message : "Shot plan could not be loaded."); });
    return () => { active = false; };
  }, [scene.id]);

  async function saveShot(event: FormEvent) {
    event.preventDefault();
    if (!draft.description?.trim()) return;
    setSaving(true); setError("");
    try {
      const input = { description: draft.description.trim(), purpose: draft.purpose, subjects: draft.subjects, subjectCharacterIds: draft.subjectCharacterIds, locationSet: draft.locationSet, durationSeconds: draft.durationSeconds ? Number(draft.durationSeconds) : null, productionRequirements: writeShotEditorNotes(draft), continuityReferences: draft.continuityReferences, cameraAndFraming: draft.cameraAndFraming, cameraMotion: draft.cameraMotion, narration: draft.narration, dialogue: draft.dialogue, visualContinuityNotes: draft.visualContinuityNotes, status: draft.status };
      if (editingId) await api.updateMovieShot(editingId, input);
      else await api.addMovieShot(scene.id, input);
      const nextPlan = await api.getMovieSceneShotPlan(scene.id);
      setPlan(nextPlan); onPlanChange(nextPlan); setDraft(blankShot); setEditingId(null); onSelectShot(editingId ?? nextPlan.shots.at(-1)?.id ?? null);
    } catch (cause) { setError(cause instanceof Error ? cause.message : "The shot could not be saved."); }
    finally { setSaving(false); }
  }

  async function reorder(shotId: string, sequence: number) {
    setError("");
    try { const result = await api.reorderMovieShot(shotId, sequence); setPlan(result); onPlanChange(result); }
    catch (cause) { setError(cause instanceof Error ? cause.message : "The shot order could not be changed."); }
  }

  async function archive(shotId: string) {
    setError("");
    try { const result = await api.archiveMovieShot(shotId); setPlan(result); onPlanChange(result); }
    catch (cause) { setError(cause instanceof Error ? cause.message : "The shot could not be archived."); }
  }

  if (!plan) return <div className="movie-shot-plan-loading"><span className="loading-spinner" /> Loading shot plan…</div>;
  return <div className="movie-shot-plan"><div className="movie-shot-plan-summary"><div><span className="movie-inspector-label">Shot Plan state</span><strong>{plan.readyShotCount}/{plan.activeShotCount} ready for Storyboard</strong></div><div className="movie-shot-coverage"><span>{plan.coveragePercent}% duration coverage</span><div className="movie-inspector-meter"><span style={{ width: `${plan.coveragePercent}%` }} /></div></div><small>{plan.totalDurationSeconds}s of {scene.durationSeconds ?? "unset"}s planned · creating a shot never starts generation</small></div>{error && <div className="movie-workspace-error-inline"><XCircleIcon /> {error}</div>}<div className="movie-shot-plan-list">{plan.shots.map((shot, index) => <ShotPlanCard key={shot.id} shot={shot} index={index} count={plan.shots.length} selected={shot.id === selectedShotId} onSelect={() => onSelectShot(shot.id)} onEdit={() => { onSelectShot(shot.id); setEditingId(shot.id); setDraft(draftFromShot(shot)); }} onMove={(sequence) => void reorder(shot.id, sequence)} onArchive={() => void archive(shot.id)} />)}</div><form className="movie-shot-editor" onSubmit={(event) => void saveShot(event)}><div className="movie-shot-editor-heading"><div><span className="movie-workspace-kicker">{editingId ? "Manual edit" : "Shot planning"}</span><h4>{editingId ? "Edit this shot" : "Add a shot to the scene"}</h4><p>Start with the story action, then add detail only when the shot needs it.</p></div><span className="movie-shot-editor-status">{editingId ? "Editing saved plan" : "New plan"}</span></div><div className="movie-shot-core-grid"><label><span>What must this shot communicate?</span><input aria-label="Shot purpose" placeholder="Purpose or story beat" value={draft.purpose ?? ""} onChange={(event) => setDraft({ ...draft, purpose: event.target.value })} /></label><label><span>Duration</span><div className="movie-shot-input-with-suffix"><input aria-label="Expected duration" type="number" min="1" max="3600" placeholder="—" value={draft.durationSeconds ?? ""} onChange={(event) => setDraft({ ...draft, durationSeconds: event.target.value ? Number(event.target.value) : null })} /><em>sec</em></div></label><label><span>Who / what appears</span><input aria-label="Subjects / characters" placeholder="Characters, props, or subject" value={draft.subjects ?? ""} onChange={(event) => setDraft({ ...draft, subjects: event.target.value })} /></label><label><span>Location / set</span><input aria-label="Location / set" placeholder="Where the shot takes place" value={draft.locationSet ?? ""} onChange={(event) => setDraft({ ...draft, locationSet: event.target.value })} /></label><label className="movie-shot-core-wide"><span>What happens</span><textarea aria-label="Shot description" placeholder="Describe the visible action in plain language." rows={3} value={draft.description} onChange={(event) => setDraft({ ...draft, description: event.target.value })} /></label><label><span>Camera intention</span><input aria-label="Camera / framing" placeholder="For example: medium-wide, eye level" value={draft.cameraAndFraming ?? ""} onChange={(event) => setDraft({ ...draft, cameraAndFraming: event.target.value })} /></label><label><span>Camera movement</span><input aria-label="Camera movement" placeholder="For example: locked, slow push" value={draft.cameraMotion ?? ""} onChange={(event) => setDraft({ ...draft, cameraMotion: event.target.value })} /></label></div><div className="movie-shot-advanced-sections"><details><summary><span><strong>Cinematography</strong><small>Framing and movement beyond the basic intention</small></span><ChevronRight size={15} /></summary><div className="movie-shot-form-grid"><label><span>Camera / framing</span><input value={draft.cameraAndFraming ?? ""} onChange={(event) => setDraft({ ...draft, cameraAndFraming: event.target.value })} /></label><label><span>Movement</span><input value={draft.cameraMotion ?? ""} onChange={(event) => setDraft({ ...draft, cameraMotion: event.target.value })} /></label></div><p className="movie-shot-editor-hint">Use production language, not a prompt. This remains shot intent and does not promise a physical camera control.</p></details><details><summary><span><strong>Continuity</strong><small>References that keep this shot aligned with the scene</small></span><ChevronRight size={15} /></summary><div className="movie-shot-form-grid"><label><span>Continuity references</span><textarea aria-label="Continuity references" rows={2} value={draft.continuityReferences ?? ""} onChange={(event) => setDraft({ ...draft, continuityReferences: event.target.value })} /></label><label><span>Visual continuity notes</span><textarea rows={2} value={draft.visualContinuityNotes ?? ""} onChange={(event) => setDraft({ ...draft, visualContinuityNotes: event.target.value })} /></label></div></details><details><summary><span><strong>Quality & production</strong><small>Requirements, complexity, VFX, and handoff notes</small></span><ChevronRight size={15} /></summary><div className="movie-shot-form-grid"><label><span>Quality requirements</span><textarea rows={2} value={draft.qualityRequirements} onChange={(event) => setDraft({ ...draft, qualityRequirements: event.target.value })} /></label>{advancedShotLabels.filter(([label]) => label !== "Audio / dialogue dependency").map(([label, key]) => <label key={key}><span>{label}</span><textarea rows={2} value={draft[key]} onChange={(event) => setDraft({ ...draft, [key]: event.target.value })} /></label>)}</div><p className="movie-shot-editor-hint">These notes are saved with the shot’s production requirements. Director quality and future resolution recommendations have a reserved place here; resolution selection is not available yet.</p></details><details><summary><span><strong>Audio & dialogue</strong><small>What the picture depends on hearing or saying</small></span><ChevronRight size={15} /></summary><div className="movie-shot-form-grid"><label><span>Audio / dialogue dependency</span><textarea rows={2} value={draft.audioDependency} onChange={(event) => setDraft({ ...draft, audioDependency: event.target.value })} /></label><label><span>Narration</span><textarea rows={2} value={draft.narration ?? ""} onChange={(event) => setDraft({ ...draft, narration: event.target.value })} /></label><label className="movie-shot-wide"><span>Dialogue</span><textarea rows={2} value={draft.dialogue ?? ""} onChange={(event) => setDraft({ ...draft, dialogue: event.target.value })} /></label></div></details></div><div className="movie-shot-form-actions"><button className="movie-workspace-button is-primary" type="submit" disabled={saving || !draft.description.trim()}>{saving ? "Saving…" : <><Save size={13} /> {editingId ? "Save shot changes" : "Add planned shot"}</>}</button>{editingId && <button className="movie-workspace-button" type="button" onClick={() => { setEditingId(null); setDraft(blankShot); }}>Cancel</button>}</div></form></div>;
}

function ShotPlanCard({ shot, index, count, selected, onSelect, onEdit, onMove, onArchive }: { shot: MovieShot; index: number; count: number; selected: boolean; onSelect: () => void; onEdit: () => void; onMove: (sequence: number) => void; onArchive: () => void }) {
  const archived = shot.status === "Archived";
  return <article className={`movie-shot-plan-card ${archived ? "is-archived" : ""} ${selected ? "is-selected" : ""}`}><button type="button" className="movie-shot-plan-card-select" onClick={onSelect} aria-pressed={selected}><span className="movie-scene-sequence">{String(shot.sequence).padStart(2, "0")}</span><span className="movie-shot-plan-card-copy"><strong>{shot.description}</strong><small>{shot.planState} · {shot.durationSeconds ? `${shot.durationSeconds}s` : "duration unset"}</small></span><span className={`movie-shot-readiness ${shot.readiness.ready ? "is-ready" : ""}`}>{shot.readiness.ready ? <><Check size={12} /> Ready</> : "Needs detail"}</span></button><div className="movie-shot-plan-facts"><span><b>Purpose</b>{shot.purpose || "Not set"}</span><span><b>Who / what</b>{shot.subjects || "Not set"}</span><span><b>Camera</b>{[shot.cameraAndFraming, shot.cameraMotion].filter(Boolean).join(" · ") || "Not set"}</span><span><b>Set</b>{shot.locationSet || "Not set"}</span></div>{!shot.readiness.ready && <p className="movie-shot-missing">{shot.readiness.summary}</p>}<div className="movie-shot-card-actions"><button type="button" className="movie-text-action" onClick={onEdit} disabled={archived}><PencilRuler size={12} /> Edit</button><button type="button" className="movie-text-action" onClick={() => onMove(shot.sequence - 1)} disabled={index === 0 || archived}><ArrowUp size={12} /> Up</button><button type="button" className="movie-text-action" onClick={() => onMove(shot.sequence + 1)} disabled={index === count - 1 || archived}><ArrowDown size={12} /> Down</button><button type="button" className="movie-text-action is-danger" onClick={onArchive} disabled={archived}><Archive size={12} /> {archived ? "Archived" : "Archive"}</button></div></article>;
}

function SceneShotDesigner({ scene, guide, presets, saving, onAddShot }: { scene: MovieScene; guide: MovieProject["guide"]; presets: CinematographyPreset[]; saving: boolean; onAddShot: (sceneId: string, draft: ShotDesignerDraft) => Promise<boolean> }) {
  return <ShotDesigner scene={scene} guide={guide} presets={presets} saving={saving} onAddShot={(draft) => onAddShot(scene.id, draft)} />;
}

function StoryboardFoundationModule({ project }: { project: MovieProject }) {
  return <div className="movie-module-stack"><section className="movie-workspace-section"><div className="movie-section-head"><div><span className="movie-workspace-kicker">Filmstrip</span><h3>Scenes, not placeholders</h3></div><span className="movie-section-count">{project.scenes.length} frames planned</span></div>{project.scenes.length ? <div className="movie-filmstrip">{project.scenes.map((scene) => { const clip = readyClipForScene(scene); return <article className="movie-filmstrip-card" key={scene.id}>{clip?.assetId ? <video src={assetFileUrl(clip.assetId, true)} preload="metadata" muted aria-label={scene.title} /> : <div className="movie-filmstrip-placeholder"><Film size={19} /><span>No footage</span></div>}<div><span>{String(scene.sequence).padStart(2, "0")}</span><strong>{scene.title}</strong><small>{formatDuration(scene.durationSeconds)}</small></div></article>; })}</div> : <EmptyGeneratedStage title="Your storyboard is empty" text="Planned scenes will become the first visual pass here." />}</section><ModuleIntro icon={<Layers3 size={18} />} title="Storyboard foundation" text="This surface is ready for real frames. It will not manufacture thumbnails for scenes that have no generated footage." /></div>;
}

function OperationalStoryboardModule({ storyboard, onRefresh, onError }: { storyboard: MovieStoryboardProject; onRefresh: () => void; onError: (message: string) => void }) {
  const shotCount = storyboard.scenes.reduce((count, scene) => count + scene.shots.length, 0);
  return <div className="movie-module-stack movie-storyboard-room">
    <section className="movie-storyboard-summary">
      <div><span className="movie-workspace-kicker">Operational funnel</span><h3>Shot Plan → Storyboard Candidate → Approved Storyboard</h3><p>Review candidates by scene and shot. Approval is explicit; rejected versions remain in the record for revision.</p></div>
      <div className="movie-storyboard-summary-stats"><span><strong>{storyboard.scenes.length}</strong> scenes</span><span><strong>{shotCount}</strong> shots</span><span><strong>{storyboard.scenes.flatMap((scene) => scene.shots).filter((shot) => shot.approvalStatus === "Approved").length}</strong> approved</span></div>
    </section>
    {!storyboard.scenes.length ? <section className="movie-workspace-section movie-storyboard-empty-state"><div className="movie-empty-orbit"><ListChecks size={23} /></div><h3>Start with the Shot Plan</h3><p>No scenes or shots have been planned yet. The storyboard room will show real candidates once a shot exists.</p><Link href={`/create/movie/${storyboard.id}/scenes`} className="movie-workspace-button is-primary"><Clapperboard size={14} /> Open Scenes</Link></section> : !shotCount ? <section className="movie-workspace-section movie-storyboard-empty-state"><div className="movie-empty-orbit"><ListChecks size={23} /></div><h3>Shot Plans are the next step</h3><p>{storyboard.scenes.length} scene{storyboard.scenes.length === 1 ? " is" : "s are"} saved, but no shots have been added. Add a shot plan before creating storyboard candidates.</p><Link href={`/create/movie/${storyboard.id}/scenes`} className="movie-workspace-button is-primary"><Clapperboard size={14} /> Add Shot Plan</Link></section> : storyboard.scenes.map((scene) => <StoryboardSceneCard key={scene.id} scene={scene} providerReady={storyboard.providerReady} onRefresh={onRefresh} onError={onError} />)}
    <ModuleIntro icon={<Layers3 size={18} />} title="No artwork is invented here" text={storyboard.providerReady ? "Generation remains an explicit, provider-backed action. Only persisted Asset records are previewed in this room." : "Storyboard generation is unavailable because no provider is enabled. Shot plans and persisted candidate records remain reviewable."} />
  </div>;
}

function StoryboardSceneCard({ scene, providerReady, onRefresh, onError }: { scene: MovieStoryboardScene; providerReady: boolean; onRefresh: () => void; onError: (message: string) => void }) {
  return <section className="movie-workspace-section movie-storyboard-scene"><div className="movie-section-head"><div><span className="movie-workspace-kicker">Scene {String(scene.sequence).padStart(2, "0")}</span><h3>{scene.title}</h3><p className="movie-storyboard-scene-summary">{scene.summary}</p></div><span className="movie-section-count">{scene.shots.length} shots</span></div>{scene.continuityNotes && <div className="movie-storyboard-scene-continuity"><AlertTriangle size={14} /> {scene.continuityNotes}</div>}<div className="movie-storyboard-shot-list">{scene.shots.map((shot) => <StoryboardShotCard key={shot.id} shot={shot} providerReady={providerReady} onRefresh={onRefresh} onError={onError} />)}</div></section>;
}

function StoryboardShotCard({ shot, providerReady, onRefresh, onError }: { shot: MovieStoryboardShot; providerReady: boolean; onRefresh: () => void; onError: (message: string) => void }) {
  const [working, setWorking] = useState(false);
  const [reviewing, setReviewing] = useState<string | null>(null);
  const [reason, setReason] = useState("");
  const approvedCandidate = shot.candidates.find((candidate) => candidate.id === shot.approvedCandidateId) ?? null;
  const latestCandidate = shot.candidates[0] ?? null;
  const hasPendingCandidate = shot.candidates.some((candidate) => candidate.status === "PendingApproval");
  const createCandidate = async () => {
    setWorking(true);
    try {
      await api.createMovieProductionVersion(shot.id, { stage: "StoryboardCandidate", label: `Shot ${String(shot.sequence).padStart(2, "0")} storyboard candidate`, compositionJson: JSON.stringify({ shotPlan: shot.description, cinematography: shot.cinematography }), stageProvenanceJson: JSON.stringify({ source: "shot-plan", workflow: "storyboard" }) });
      onRefresh();
    } catch (cause) {
      onError(cause instanceof Error ? cause.message : "The storyboard candidate could not be created.");
    } finally {
      setWorking(false);
    }
  };
  const reviewCandidate = async (candidate: MovieStoryboardCandidate, approve: boolean) => {
    setWorking(true);
    try {
      await api.reviewMovieProductionVersion(candidate.id, { approve, reason: approve ? "Storyboard composition approved." : reason.trim() || "Revision requested." });
      setReviewing(null);
      setReason("");
      onRefresh();
    } catch (cause) {
      onError(cause instanceof Error ? cause.message : "The storyboard review could not be saved.");
    } finally {
      setWorking(false);
    }
  };
  return <article className="movie-storyboard-shot-card"><div className="movie-storyboard-shot-heading"><div><span className="movie-storyboard-shot-number">Shot {String(shot.sequence).padStart(2, "0")}</span><h4>{shot.description}</h4></div><span className={`movie-storyboard-approval is-${shot.approvalStatus.toLowerCase()}`}>{shot.approvalStatus === "Approved" ? <CheckCircle2 size={12} /> : shot.approvalStatus === "Rejected" ? <X size={12} /> : null}{shot.approvalStatus === "NotStarted" ? "Not started" : shot.approvalStatus}</span></div>
    <div className="movie-storyboard-shot-grid"><div><span className="movie-inspector-label">Shot Plan</span><strong>{shot.shotPlanStatus}</strong><small>{formatDuration(shot.durationSeconds)} · Current stage: {shot.currentStage}</small></div><div><span className="movie-inspector-label">Cinematography</span><p>{cinematographyText(shot)}</p></div><div><span className="movie-inspector-label">Continuity warnings</span>{shot.continuityWarnings.length ? <ul className="movie-storyboard-warnings">{shot.continuityWarnings.map((warning) => <li key={warning}><AlertTriangle size={12} />{warning}</li>)}</ul> : <p className="movie-storyboard-clear"><Check size={12} /> No recorded warnings</p>}</div></div>
    <div className="movie-storyboard-candidates"><div className="movie-storyboard-candidates-heading"><span className="movie-inspector-label">Storyboard candidates</span><span>{shot.candidates.length} version{shot.candidates.length === 1 ? "" : "s"}</span></div>{shot.candidates.length ? shot.candidates.map((candidate) => <StoryboardCandidateCard key={candidate.id} candidate={candidate} approved={candidate.id === approvedCandidate?.id} reviewing={reviewing === candidate.id} reason={reason} working={working} onReviewStart={() => setReviewing(candidate.id)} onReasonChange={setReason} onReviewCancel={() => { setReviewing(null); setReason(""); }} onReview={(approve) => void reviewCandidate(candidate, approve)} />) : <div className="movie-storyboard-no-candidates"><ImageOff size={16} /><span>No storyboard has been generated for this shot.</span><small>Create a persisted candidate record from the Shot Plan; no artwork is fabricated.</small></div>}</div>
    <div className="movie-storyboard-shot-actions"><button type="button" className="movie-workspace-button is-primary" onClick={() => void createCandidate()} disabled={working || hasPendingCandidate}>{working ? "Saving…" : latestCandidate?.status === "Rejected" ? "Create revision candidate" : "Create candidate from Shot Plan"}</button>{!providerReady && <span className="movie-storyboard-provider-note"><span className="movie-live-dot" /> Generation provider disabled; no paid workflow will run.</span>}</div>
  </article>;
}

function StoryboardCandidateCard({ candidate, approved, reviewing, reason, working, onReviewStart, onReasonChange, onReviewCancel, onReview }: { candidate: MovieStoryboardCandidate; approved: boolean; reviewing: boolean; reason: string; working: boolean; onReviewStart: () => void; onReasonChange: (value: string) => void; onReviewCancel: () => void; onReview: (approve: boolean) => void }) {
  const assetId = candidate.assetId ?? candidate.firstFrameAssetId;
  return <div className={`movie-storyboard-candidate ${approved ? "is-approved" : ""} ${candidate.status === "Rejected" ? "is-rejected" : ""}`}><div className="movie-storyboard-candidate-preview">{assetId ? <img src={assetFileUrl(assetId, true)} alt={`Storyboard candidate ${candidate.versionNumber}`} /> : <div><ImageOff size={19} /><span>No Asset attached</span></div>}</div><div className="movie-storyboard-candidate-copy"><div className="movie-storyboard-candidate-topline"><strong>V{candidate.versionNumber} · {candidate.label || "Untitled candidate"}</strong><span>{approved ? "Approved storyboard" : candidate.status}</span></div>{candidate.rejectionReason && <p className="movie-storyboard-rejection"><X size={12} /> {candidate.rejectionReason}</p>}<div className="movie-storyboard-candidate-meta"><span>Recorded {new Date(candidate.createdAt).toLocaleDateString()}</span>{candidate.reviewedAt && <span>Reviewed {new Date(candidate.reviewedAt).toLocaleDateString()}</span>}</div>{reviewing ? <div className="movie-storyboard-review-form"><label><span>Revision reason</span><textarea value={reason} onChange={(event) => onReasonChange(event.target.value)} maxLength={2000} rows={2} placeholder="What needs another pass?" /></label><div><button type="button" className="movie-text-action" onClick={() => onReview(false)} disabled={working}>Request revision</button><button type="button" className="movie-text-action is-muted" onClick={onReviewCancel} disabled={working}>Cancel</button></div></div> : candidate.status === "PendingApproval" ? <div className="movie-storyboard-review-actions"><button type="button" className="movie-text-action" onClick={() => onReview(true)} disabled={working}><Check size={12} /> Approve</button><button type="button" className="movie-text-action is-reject" onClick={onReviewStart} disabled={working}><X size={12} /> Request revision</button></div> : null}</div></div>;
}

function cinematographyText(shot: MovieStoryboardShot) {
  const values = [shot.cinematography.cameraAndFraming, shot.cinematography.cameraMotion, shot.cinematography.intent, shot.cinematography.shotSize, shot.cinematography.focalLength, shot.cinematography.cameraAngle, shot.cinematography.lighting, shot.cinematography.paletteLook, shot.cinematography.compositionNotes].filter(Boolean);
  return values.length ? values.join(" · ") : "No cinematography intent recorded.";
}

function ProductionModule({ project, completionPercent, onRefresh }: { project: MovieProject; completionPercent: number; onRefresh: () => Promise<void> }) {
  const [busyKey, setBusyKey] = useState("");
  const [actionError, setActionError] = useState("");
  const shots = project.scenes.flatMap((scene) => scene.shots.map((shot) => ({ scene, shot }))).sort((a, b) => a.scene.sequence - b.scene.sequence || a.shot.sequence - b.shot.sequence);
  const versions = shots.flatMap(({ shot }) => shot.productionVersions ?? []);
  const takes = shots.flatMap(({ shot }) => shot.takes ?? []);
  const selectedTake = takes.find((take) => Boolean(take.selectedAt)) ?? null;
  const ready = takes.filter((take) => Boolean(take.assetId) && ["Ready", "Approved"].includes(take.status)).length;
  const inFlight = versions.filter((version) => ["Pending", "Queued", "Running"].includes(version.execution?.status ?? "")).length;
  const failed = versions.filter((version) => version.execution?.status === "Failed").length;
  const [selectedTier, setSelectedTier] = useState<MovieResolutionTier>(failed > 0 ? "Draft" : selectedTake ? "Master" : ready > 0 ? "Upgrade" : "Draft");

  async function action(key: string, work: () => Promise<unknown>) {
    setBusyKey(key);
    setActionError("");
    try {
      await work();
      await onRefresh();
    } catch (cause) {
      setActionError(cause instanceof Error ? cause.message : "The production action could not be completed.");
    } finally {
      setBusyKey("");
    }
  }

  return <div className="movie-module-stack">
    <section className="movie-production-hero">
      <div><span className="movie-workspace-kicker">Production desk</span><h3>{completionPercent}% of the current plan is ready to review</h3><p>Move from source intent to a selected take without losing the story, continuity, or a clear quality decision.</p></div>
      <div className="movie-production-ring"><strong>{completionPercent}%</strong><span>ready</span></div>
    </section>
    <MovieProductionResolutionPanel project={project} shotCount={shots.length} reviewableTakeCount={ready} selectedTake={selectedTake} failedPassCount={failed} selectedTier={selectedTier} onTierChange={setSelectedTier} />
    <div className="movie-metric-row"><Metric label="Scenes" value={project.scenes.length} /><Metric label="Available takes" value={takes.length} /><Metric label="Ready to review" value={ready} /><Metric label="Needs attention" value={failed} /></div>
    {actionError && <div className="movie-workspace-error-inline" role="alert"><XCircleIcon /> {actionError}</div>}
    <section className="movie-production-board">
      <div className="movie-section-head"><div><span className="movie-workspace-kicker">Scene / shot rail</span><h3>Review the work, then choose a take</h3></div><span className="movie-section-count">{versions.length} work passes · {inFlight} active</span></div>
      {shots.length ? <div className="movie-production-groups">{shots.map(({ scene, shot }) => <ProductionShotGroup key={shot.id} sceneTitle={scene.title} sceneSequence={scene.sequence} shot={shot} busyKey={busyKey} onAction={action} />)}</div> : <EmptyModule title="No shots are planned yet" text="Add a scene and shot before opening a production action." />}
    </section>
    <ModuleIntro icon={<ShieldCheck size={18} />} title="No footage is invented" text="This workspace only shows persisted work. Planning a finish is safe; a real output appears only after a deliberate, reviewable production step." />
  </div>;
}

function ProductionShotGroup({ sceneTitle, sceneSequence, shot, busyKey, onAction }: { sceneTitle: string; sceneSequence: number; shot: MovieShot; busyKey: string; onAction: (key: string, work: () => Promise<unknown>) => Promise<void> }) {
  const versions = [...(shot.productionVersions ?? [])].sort((a, b) => b.versionNumber - a.versionNumber);
  const keyframe = versions.find((version) => version.stage === "ProductionKeyframe" && version.status === "PendingApproval");
  const approvedKeyframe = versions.find((version) => version.stage === "ApprovedKeyframe" && version.status === "Approved");
  const motionPreview = versions.find((version) => version.stage === "MotionPreview" && ["Approved", "PendingApproval"].includes(version.status));
  const render = versions.find((version) => version.stage === "ProductionRender");
  const takes = [...(shot.takes ?? [])].sort((a, b) => b.versionNumber - a.versionNumber);
  const isBusy = (action: string) => busyKey === `${shot.id}:${action}`;

  return <article className="movie-production-group">
    <div className="movie-production-shot-heading"><div><span className="movie-production-scene-label">Scene {String(sceneSequence).padStart(2, "0")} · {sceneTitle}</span><h4>Shot {String(shot.sequence).padStart(2, "0")}</h4><p>{shot.description}</p></div><span className="movie-stage-pill">{displayProductionStage(shot.productionStage)}</span></div>
    <div className="movie-production-lifecycle">
      <ProductionCheckpoint label="Source frame" state={approvedKeyframe ? "Approved" : keyframe ? "Needs review" : "Not started"} tone={approvedKeyframe ? "ready" : keyframe ? "pending" : "quiet"} />
      <ProductionCheckpoint label="Motion check" state={motionPreview ? displayProductionStatus(motionPreview.status) : "Not started"} tone={motionPreview ? motionPreview.status === "Approved" ? "ready" : "pending" : "quiet"} />
      <ProductionCheckpoint label="Master pass" state={render?.execution?.status === "Succeeded" ? "Ready" : render?.execution?.status === "Failed" ? "Needs attention" : render ? "In progress" : "Not started"} tone={render?.execution?.status === "Succeeded" ? "ready" : render?.execution?.status === "Failed" ? "failed" : render ? "pending" : "quiet"} />
      <ProductionCheckpoint label="Selected take" state={takes.some((take) => Boolean(take.selectedAt)) ? "Selected" : takes.length ? `${takes.length} available` : "Not selected"} tone={takes.some((take) => Boolean(take.selectedAt)) ? "ready" : "quiet"} />
    </div>
    <div className="movie-production-action-row">
      {keyframe && <button type="button" className="movie-workspace-button is-primary" disabled={Boolean(busyKey)} onClick={() => void onAction(`${shot.id}:approve-keyframe`, () => api.reviewMovieProductionVersion(keyframe.id, { approve: true, reason: "Source frame approved in Production." }))}>{isBusy("approve-keyframe") ? "Approving…" : "Approve source frame"}</button>}
      {approvedKeyframe && !motionPreview && <button type="button" className="movie-workspace-button is-secondary" disabled={Boolean(busyKey)} onClick={() => void onAction(`${shot.id}:motion-preview`, () => api.createMovieMotionPreview(shot.id, { sourceVersionId: approvedKeyframe.id, label: "Motion check" }))}>{isBusy("motion-preview") ? "Preparing…" : "Create motion check"}</button>}
      {motionPreview?.status === "PendingApproval" && <button type="button" className="movie-workspace-button is-primary" disabled={Boolean(busyKey)} onClick={() => void onAction(`${shot.id}:approve-motion`, () => api.reviewMovieProductionVersion(motionPreview.id, { approve: true, reason: "Motion check approved in Production." }))}>{isBusy("approve-motion") ? "Approving…" : "Approve motion check"}</button>}
      {motionPreview?.status === "Approved" && (!render || render.execution?.status === "Failed") && <button type="button" className="movie-workspace-button is-secondary" disabled={Boolean(busyKey)} onClick={() => void onAction(`${shot.id}:render`, () => api.queueMovieProductionRender(shot.id, { sourceVersionId: motionPreview.id, label: render ? "Master retry" : "Master pass" }))}>{isBusy("render") ? "Queueing…" : render ? "Retry master pass" : "Create master pass"}</button>}
      {render?.execution?.status === "Succeeded" && !takes.some((take) => take.generationJobId === render.generationJobId) && <button type="button" className="movie-workspace-button is-primary" disabled={Boolean(busyKey)} onClick={() => void onAction(`${shot.id}:take`, () => api.createMovieTakeFromProduction(render.id, { label: "Master take" }))}>{isBusy("take") ? "Saving…" : "Save as take"}</button>}
    </div>
    {versions.length ? <div className="movie-production-version-list"><span className="movie-production-subhead">Work history</span>{versions.map((version) => <ProductionVersionCard key={version.id} version={version} shot={shot} busyKey={busyKey} onAction={onAction} />)}</div> : <div className="movie-production-empty-line">No work passes yet. Start from an approved visual plan.</div>}
    <div className="movie-production-takes" id="movie-takes"><div className="movie-production-subhead-row"><span className="movie-production-subhead">Take review</span><span className="movie-take-definition">Choose one take, then carry it into the master.</span></div>{takes.length ? takes.map((take) => <MovieTakeCard key={take.id} take={take} shot={shot} busyKey={busyKey} onAction={onAction} />) : <div className="movie-production-empty-line">A take appears only after a real pass has produced a private output.</div>}</div>
  </article>;
}

function ProductionCheckpoint({ label, state, tone }: { label: string; state: string; tone: "ready" | "pending" | "failed" | "quiet" }) {
  return <div className={`movie-production-checkpoint is-${tone}`}><span>{label}</span><strong>{state}</strong></div>;
}

function ProductionVersionCard({ version, shot, busyKey, onAction }: { version: MovieProductionVersion; shot: MovieShot; busyKey: string; onAction: (key: string, work: () => Promise<unknown>) => Promise<void> }) {
  const execution = version.execution;
  const outputAssetId = version.assetId ?? execution?.assetId ?? null;
  const outputIsVideo = version.stage === "ProductionRender" || execution?.assetType?.toLowerCase() === "video";
  const isBusy = (action: string) => busyKey === `${shot.id}:${action}`;
  return <div className="movie-production-version-card"><div className="movie-production-version-top"><div><span className="movie-production-version-number">Pass {version.versionNumber} · {displayProductionStage(version.stage)}</span><strong>{version.label || "Untitled pass"}</strong></div><span className={`movie-stage-pill is-${version.status.toLowerCase()}`}>{displayProductionStatus(version.status)}</span></div>{outputAssetId ? outputIsVideo ? <video className="movie-production-output" src={assetFileUrl(outputAssetId, true)} controls preload="metadata" aria-label={`${displayProductionStage(version.stage)} output`} /> : <img className="movie-production-output" src={assetFileUrl(outputAssetId, true)} alt={`${displayProductionStage(version.stage)} output`} /> : <div className="movie-production-preview-empty"><Film size={15} /><span>{execution ? `${execution.progressPercent}% complete` : "No output yet"}</span></div>}{execution && <ProductionExecutionSummary execution={execution} />}{version.rejectionReason && <div className="movie-production-failure"><XCircleIcon /> {version.rejectionReason}</div>}{version.stage === "ProductionKeyframe" && version.status === "PendingApproval" && <button type="button" className="movie-text-action" disabled={Boolean(busyKey)} onClick={() => void onAction(`${shot.id}:approve-keyframe`, () => api.reviewMovieProductionVersion(version.id, { approve: true, reason: "Source frame approved in Production." }))}>{isBusy("approve-keyframe") ? "Approving…" : "Approve source frame"}</button>}</div>;
}

function ProductionExecutionSummary({ execution }: { execution: NonNullable<MovieProductionVersion["execution"]> }) {
  const failures = execution.attempts.filter((attempt) => attempt.failureCode || attempt.qualityControlRejected);
  return <div className="movie-production-execution"><div><span>Progress</span><strong>{execution.status === "Succeeded" ? "Ready" : execution.status === "Failed" ? "Needs attention" : `${execution.progressPercent}%`}</strong></div><div><span>Quality check</span><strong>{execution.qualityControlStatus || "Pending"}</strong></div><div><span>Pass history</span><strong>{execution.attemptCount} recorded</strong></div>{(execution.errorCode || failures.length > 0) && <div className="movie-production-failure-detail"><span>Escalation</span><strong>Review this pass before continuing</strong>{execution.errorMessage && <small>{execution.errorMessage}</small>}</div>}</div>;
}

function MovieTakeCard({ take, shot, busyKey, onAction }: { take: MovieTake; shot: MovieShot; busyKey: string; onAction: (key: string, work: () => Promise<unknown>) => Promise<void> }) {
  const isBusy = (action: string) => busyKey === `${shot.id}:${action}`;
  const selected = Boolean(take.selectedAt);
  const finalized = Boolean(take.finalizedAt);
  return <div className={`movie-take-card ${selected ? "is-selected" : ""} ${finalized ? "is-final" : ""}`}><div className="movie-take-copy"><span>Take v{take.versionNumber}</span><strong>{take.label}</strong><small>{displayProductionStatus(take.status)} · {take.qualityLevel}{selected ? " · Selected" : ""}{finalized ? " · Mastered" : ""}</small></div>{take.assetId ? <video className="movie-take-video" src={assetFileUrl(take.assetId, true)} controls preload="metadata" aria-label={take.label} /> : <div className="movie-production-preview-empty"><Film size={15} /><span>Private output pending</span></div>}<div className="movie-take-actions">{take.status !== "Approved" && take.status !== "Rejected" && <button type="button" className="movie-text-action" disabled={Boolean(busyKey)} onClick={() => void onAction(`${shot.id}:approve-take`, () => api.approveMovieTake(take.id, { decision: "Approved", comment: "Take approved in Production." }))}>{isBusy("approve-take") ? "Approving…" : "Approve take"}</button>}{!selected && <button type="button" className="movie-text-action" disabled={Boolean(busyKey) || take.status === "Rejected"} onClick={() => void onAction(`${shot.id}:select-take`, () => api.selectMovieTake(take.id))}>{isBusy("select-take") ? "Selecting…" : "Choose take"}</button>}{!finalized && <button type="button" className="movie-workspace-button is-primary" disabled={Boolean(busyKey) || take.status !== "Approved"} onClick={() => void onAction(`${shot.id}:finalize-take`, () => api.finalizeMovieTake(take.id))}>{isBusy("finalize-take") ? "Finishing…" : "Carry into master"}</button>}</div></div>;
}

function EditModule({ project }: { project: MovieProject }) {
  return <div className="movie-module-stack"><section className="movie-workspace-section"><div className="movie-section-head"><div><span className="movie-workspace-kicker">Editorial timeline</span><h3>Timeline foundation</h3></div><span className="movie-section-count">{project.scenes.length ? `${project.scenes.length} scene blocks` : "No scene blocks"}</span></div>{project.scenes.length ? <div className="movie-timeline"><div className="movie-timeline-ruler"><span>00:00</span><span>00:30</span><span>01:00</span><span>01:30</span></div><div className="movie-timeline-track">{project.scenes.map((scene, index) => <div key={scene.id} className={`movie-timeline-block ${readyClipForScene(scene) ? "is-ready" : ""}`} style={{ width: `${Math.max(13, Math.min(34, (scene.durationSeconds ?? 12) / 2.2))}%`, marginInlineStart: index ? "2%" : 0 }}><span>{String(scene.sequence).padStart(2, "0")}</span><strong>{scene.title}</strong></div>)}</div><div className="movie-timeline-note"><PencilRuler size={15} /><span>Editing controls will appear when there is a real sequence to revise.</span></div></div> : <EmptyGeneratedStage title="No footage to edit" text="A timeline will be built from real scene clips, not simulated blocks." />}</section></div>;
}

function FutureModule({ icon, title, text }: { icon: ReactNode; title: string; text: string }) {
  return <div className="movie-module-stack"><section className="movie-future-module"><div className="movie-future-icon">{icon}</div><span className="movie-workspace-kicker">Foundation surface</span><h3>{title}</h3><p>{text}</p><div className="movie-future-rule"><span /> <small>Not available in this foundation</small> <span /></div></section></div>;
}

function ContinuityItem({ label, value }: { label: string; value: string | null | undefined }) {
  return <div className="movie-continuity-item"><span>{label}</span><p>{value || "Not set yet"}</p></div>;
}

function RecordLine({ label, value }: { label: string; value: string | null | undefined }) {
  return <div className="movie-record-line"><span>{label}</span><p>{value || "Not set yet"}</p></div>;
}

function ModuleIntro({ icon, title, text }: { icon: ReactNode; title: string; text: string }) {
  return <section className="movie-future-module movie-future-module-inline"><div className="movie-future-icon">{icon}</div><div><h3>{title}</h3><p>{text}</p></div></section>;
}

function EmptyGeneratedStage({ title = "No generated footage yet", text = "The plan is saved. Real scene output will appear here when it exists." }: { title?: string; text?: string }) {
  return <div className="movie-generated-empty"><div className="movie-empty-orbit"><Film size={25} /></div><h4>{title}</h4><p>{text}</p></div>;
}

function WorkspaceSkeleton() { return <div className="movie-studio-page movie-full-workspace" aria-busy="true" aria-label="Loading movie workspace"><div className="movie-workspace-skeleton-header"><span className="movie-skeleton-line is-short" /><span className="movie-skeleton-line is-title" /><span className="movie-skeleton-line is-copy" /></div><div className="movie-workspace-skeleton-layout"><div className="movie-workspace-skeleton-nav">{Array.from({ length: 8 }, (_, index) => <span className="movie-skeleton-line" key={index} />)}</div><div className="movie-workspace-skeleton-main"><span className="movie-skeleton-line is-kicker" /><span className="movie-skeleton-line is-heading" /><span className="movie-skeleton-line is-copy" /><div className="movie-skeleton-stage" /><div className="movie-skeleton-rows"><span /><span /><span /></div></div><div className="movie-workspace-skeleton-inspector"><span className="movie-skeleton-line is-short" /><span className="movie-skeleton-line" /><span className="movie-skeleton-line is-copy" /><span className="movie-skeleton-line" /></div></div></div>; }

function EmptyModule({ title = "Nothing here yet", text }: { title?: string; text: string }) {
  return <div className="movie-module-empty"><Film size={17} /><strong>{title}</strong><p>{text}</p></div>;
}

function Metric({ label, value }: { label: string; value: number }) {
  return <div className="movie-metric"><span>{label}</span><strong>{value}</strong></div>;
}

function XCircleIcon() {
  return <span className="movie-error-icon" aria-hidden="true">!</span>;
}
