"use client";

import Link from "next/link";
import { useEffect, useMemo, useState, type ReactNode } from "react";
import {
  ArrowLeft,
  ArrowUpRight,
  AudioLines,
  BookOpen,
  CheckCircle2,
  Check,
  ChevronRight,
  Clapperboard,
  CircleAlert,
  Clock3,
  Film,
  Gauge,
  Layers3,
  ListChecks,
  Map,
  PencilRuler,
  Play,
  ShieldCheck,
  SlidersHorizontal,
  Sparkles,
  Target,
  Users,
  Workflow,
} from "lucide-react";
import { api, type MovieOverview, type MovieProject, type MovieScene } from "@/lib/api";
import { assetFileUrl } from "@/lib/apiBase";

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

function formatDuration(seconds: number | null | undefined) {
  if (!seconds) return "Duration unset";
  const minutes = Math.floor(seconds / 60);
  const remainder = seconds % 60;
  return minutes ? `${minutes}m ${remainder}s` : `${remainder}s`;
}

export function FullMovieWorkspaceView({ projectId, module }: { projectId: string; module: string }) {
  const activeModule = moduleFromSlug(module);
  const [project, setProject] = useState<MovieProject | null>(null);
  const [overview, setOverview] = useState<MovieOverview | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [selectedSceneId, setSelectedSceneId] = useState<string | null>(null);
  const [newScene, setNewScene] = useState({ title: "", summary: "" });
  const [addingScene, setAddingScene] = useState(false);

  useEffect(() => {
    let mounted = true;
    const loader = activeModule === "overview" ? api.getMovieOverview(projectId) : api.getMovieProject(projectId);
    void loader.then((result) => {
      if (!mounted) return;
      if (activeModule === "overview") {
        setOverview(result as MovieOverview);
      } else {
        const nextProject = result as MovieProject;
        setProject(nextProject);
        setSelectedSceneId(nextProject.scenes[0]?.id ?? null);
      }
    }).catch((cause) => {
      if (mounted) setError(cause instanceof Error ? cause.message : "This movie project could not be loaded.");
    }).finally(() => {
      if (mounted) setLoading(false);
    });
    return () => { mounted = false; };
  }, [activeModule, projectId]);

  const selectedScene = useMemo(() => project?.scenes.find((scene) => scene.id === selectedSceneId) ?? project?.scenes[0] ?? null, [project, selectedSceneId]);
  const readyClips = project?.clips.filter((clip) => hasReadyAsset(clip.status, clip.assetId)) ?? [];
  const outputAssetId = overview?.latestOutputAssetId ?? project?.assemblies.find((assembly) => hasReadyAsset(assembly.status, assembly.assetId))?.assetId ?? readyClips[0]?.assetId ?? null;
  const completionPercent = overview?.progress.production.percent ?? (project ? Math.min(100, Math.round(((project.scenes.length ? readyClips.length : 0) / Math.max(project.scenes.length, 1)) * 100)) : 0);
  const projectIdentity = overview?.project ?? project;
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

  if (loading) return <div className="movie-studio-page"><div className="movie-workspace-loading"><span className="loading-spinner" /><p>Loading the production workspace…</p></div></div>;
  if (!projectIdentity || (activeModule !== "overview" && !project)) return <div className="movie-studio-page"><div className="movie-workspace-error"><XCircleIcon /><h1>Workspace unavailable</h1><p>{error || "This movie project is not available in the current workspace."}</p><Link href="/create/movie" className="movie-workspace-button is-primary"><ArrowLeft size={14} /> Back to Movie Studio</Link></div></div>;

  return (
    <div className="movie-studio-page movie-full-workspace">
      <header className="movie-workspace-header">
        <Link href="/create/movie" className="movie-workspace-back"><ArrowLeft size={14} /> Movie Studio</Link>
        <div className="movie-workspace-heading">
          <div>
            <span className="movie-workspace-kicker">Full Movie Project · {projectIdentity.status}</span>
            <h1>{projectIdentity.title}</h1>
            <p>{projectIdentity.description}</p>
          </div>
          <div className="movie-workspace-meta"><span>{projectIdentity.aspectRatio}</span><span>{formatDuration(projectIdentity.durationSeconds)}</span><span>{projectIdentity.style}</span>{overview && <span>{overview.project.qualityLevel}</span>}</div>
        </div>
      </header>

      <div className="movie-workspace-layout">
        <nav className="movie-workspace-nav" aria-label="Full Movie Project navigation">
          <div className="movie-workspace-nav-label">Project map</div>
          <div className="movie-workspace-nav-list">
            {fullMovieModules.map((item) => {
              const Icon = item.icon;
              // Legacy room links retain the `/create/movie/${project.id}/${item.slug}` route shape.
              const href = `/create/movie/${projectId}/${item.slug}`;
              return <Link key={item.slug} href={href} className={`movie-workspace-nav-item ${activeModule === item.slug ? "is-active" : ""}`} aria-current={activeModule === item.slug ? "page" : undefined}><Icon size={15} /><span>{item.label}</span>{activeModule === item.slug && <ChevronRight size={13} />}</Link>;
            })}
          </div>
          <div className="movie-workspace-nav-foot"><span className="movie-live-dot" /> <span>Plan saved locally to this project</span></div>
        </nav>

        <main className="movie-workspace-main">
          <div className="movie-module-heading"><div><span className="movie-workspace-kicker">{copy.eyebrow}</span><h2>{copy.title}</h2><p>{copy.description}</p></div><span className="movie-module-index">{String(fullMovieModules.findIndex((item) => item.slug === activeModule) + 1).padStart(2, "0")} / 12</span></div>
          {activeModule === "overview" && overview && <OverviewModule overview={overview} outputAssetId={outputAssetId} />}
          {activeModule === "story" && <StoryModule project={project!} />}
          {activeModule === "cast" && <CastModule project={project!} />}
          {activeModule === "world" && <WorldModule project={project!} />}
          {activeModule === "scenes" && <ScenesModule project={project!} selectedSceneId={selectedScene?.id ?? null} newScene={newScene} addingScene={addingScene} onSelectScene={setSelectedSceneId} onChangeScene={setNewScene} onAddScene={() => void addScene()} onGenerate={generateScene} />}
          {activeModule === "storyboard" && <StoryboardModule project={project!} />}
          {activeModule === "production" && <ProductionModule project={project!} completionPercent={completionPercent} />}
          {activeModule === "edit" && <EditModule project={project!} />}
          {activeModule === "audio" && <FutureModule icon={<AudioLines size={20} />} title="Audio is not connected yet" text="The sound stage is reserved for real narration, ambience, and music assets. Nothing is simulated here." />}
          {activeModule === "qc" && <FutureModule icon={<ShieldCheck size={20} />} title="QC is a future review gate" text="Continuity and delivery checks will appear once this project has a real cut to inspect." />}
          {activeModule === "exports" && <FutureModule icon={<Play size={20} />} title="Exports are not available yet" text="Final packaging stays unavailable until there is a reviewable project output." />}
          {activeModule === "team" && <FutureModule icon={<Users size={20} />} title="Team controls are not connected yet" text="This route is reserved for shared roles, review notes, and permissions. No access controls are implied by this shell." />}
          {error && <div className="movie-workspace-error-inline"><XCircleIcon /> {error}</div>}
        </main>

        <aside className="movie-director-panel">
          <div className="movie-director-heading"><span className="movie-workspace-kicker">Director / Inspector</span><SlidersHorizontal size={16} /></div>
          <div className="movie-director-section"><span className="movie-inspector-label">Current module</span><strong>{copy.title}</strong><p>{activeModule === "overview" ? "One place to see what is decided and what still needs a deliberate next step." : "Select a real project record to keep the next decision grounded."}</p></div>
          <div className="movie-director-section"><span className="movie-inspector-label">Continuity signal</span><div className="movie-inspector-meter"><span style={{ width: `${Math.max(16, completionPercent)}%` }} /></div><div className="movie-inspector-meter-meta"><span>{overview ? `${overview.takes.finalized} final takes` : `${readyClips.length} ready clips`}</span><strong>{completionPercent}%</strong></div></div>
          {overview ? <div className="movie-director-section"><span className="movie-inspector-label">Next decision</span><strong>{overview.nextActions[0]?.label ?? "No action queued"}</strong><p>{overview.nextActions[0]?.reason ?? "The persisted project state has no outstanding setup recommendation."}</p></div> : selectedScene ? <div className="movie-director-section"><span className="movie-inspector-label">Selected scene</span><strong>{String(selectedScene.sequence).padStart(2, "0")} · {selectedScene.title}</strong><p>{selectedScene.summary}</p><span className="movie-inspector-detail">{formatDuration(selectedScene.durationSeconds)} · {selectedScene.shots.length} shots planned</span></div> : <div className="movie-director-empty"><Film size={18} /><p>Select a scene to inspect its intent and continuity notes.</p></div>}
          <div className="movie-director-note"><Sparkles size={14} /><p>The Director region stays quiet until the project has a decision to make.</p></div>
        </aside>
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
      <section className="movie-command-section"><CommandSectionTitle eyebrow="Cost / usage" title={overview.cost.isKnown ? "Recorded generation state" : "No cost recorded yet"} detail="Wave 5 provider ledger" /><div className="movie-cost-readout"><div><span>Recorded provider cost</span><strong>{formatCost(overview.cost.recordedProviderCostUsd, overview.cost.currency)}</strong></div><div><span>Estimated remaining</span><strong>{formatCost(overview.cost.estimatedRemainingProviderCostUsd, overview.cost.currency)}</strong></div></div><p className="movie-command-note">{overview.cost.note ?? "This surface reports provider cost only; it does not create a second charging system."}</p></section>
    </div>

    <div className="movie-command-grid movie-command-grid-bottom"><section className="movie-command-section"><CommandSectionTitle eyebrow="Warnings & blockers" title={overview.warnings.length ? `${overview.warnings.length} signals need attention` : "No active warnings"} detail="Continuity and production" />{overview.warnings.length ? <div className="movie-warning-list">{overview.warnings.map((warning) => <div className={`movie-warning-row is-${warning.severity}`} key={warning.key}><CircleAlert size={14} /><div><strong>{warning.label}</strong><p>{warning.detail}</p></div></div>)}</div> : <HonestEmpty text="No unresolved warnings have been recorded for this project." />}</section><section className="movie-command-section"><CommandSectionTitle eyebrow="Recent activity" title="Meaningful changes" detail="Latest persisted records" />{overview.recentActivity.length ? <div className="movie-activity-list">{overview.recentActivity.map((item) => <div className="movie-activity-row" key={item.key}><Clock3 size={14} /><div><strong>{item.label}</strong><span>{item.detail}</span></div><time dateTime={item.occurredAt}>{formatRelativeDate(item.occurredAt)}</time></div>)}</div> : <HonestEmpty text="Activity will appear after a project record changes." />}</section></div>

    {outputAssetId ? <section className="movie-generated-surface movie-command-output"><div className="movie-surface-heading"><div><span className="movie-workspace-kicker">Latest output</span><h3>Reviewable project output</h3></div><span className="movie-surface-status"><span className="is-ready" /> Ready</span></div><div className="movie-generated-video"><video src={assetFileUrl(outputAssetId, true)} controls preload="metadata" aria-label={project.title} /><div className="movie-generated-video-caption"><Play size={14} /> {project.title}</div></div></section> : <section className="movie-command-empty"><Film size={20} /><div><span className="movie-workspace-kicker">Latest output</span><h3>No final output yet</h3><p>The plan is visible above. A reviewable output will appear here when the persisted production workflow creates one.</p></div></section>}
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

function StoryModule({ project }: { project: MovieProject }) {
  return <div className="movie-module-stack"><section className="movie-story-hero"><span className="movie-workspace-kicker">Creative brief</span><h3>{project.title}</h3><p>{project.description}</p><div className="movie-story-facts"><span>{formatDuration(project.durationSeconds)}</span><span>{project.aspectRatio}</span><span>{project.style}</span><span>{project.language.toUpperCase()}</span></div></section><section className="movie-workspace-section"><div className="movie-section-head"><div><span className="movie-workspace-kicker">Continuity guide</span><h3>Rules that travel with the story</h3></div><BookOpen size={17} /></div><div className="movie-continuity-grid movie-continuity-grid-wide"><ContinuityItem label="Visual language" value={project.guide.visualLanguage} /><ContinuityItem label="Camera language" value={project.guide.cameraLanguage} /><ContinuityItem label="Color & lighting" value={project.guide.colorAndLighting} /><ContinuityItem label="Sound & narration" value={project.guide.soundAndNarration} /><ContinuityItem label="Continuity rules" value={project.guide.continuityRules} /></div></section></div>;
}

function CastModule({ project }: { project: MovieProject }) {
  return <div className="movie-module-stack"><ModuleIntro icon={<Users size={18} />} title="Characters stay intentional" text="The cast surface shows durable character records only. It does not invent visual references or performances." />{project.characters.length ? <div className="movie-record-grid">{project.characters.map((character) => <article className="movie-record-card" key={character.id}><span className="movie-record-index">Character</span><h3>{character.name}</h3><p>{character.description}</p><RecordLine label="Appearance" value={character.appearance} /><RecordLine label="Performance" value={character.voiceAndPerformance} /><RecordLine label="Continuity" value={character.continuityNotes} /></article>)}</div> : <EmptyModule title="No cast records yet" text="Add character records when the story has a person worth keeping consistent." />}</div>;
}

function WorldModule({ project }: { project: MovieProject }) {
  return <div className="movie-module-stack"><ModuleIntro icon={<Map size={18} />} title="The world is a continuity decision" text="Locations are kept separate from scene execution so visual identity can travel with the project." />{project.locations.length ? <div className="movie-record-grid">{project.locations.map((location) => <article className="movie-record-card" key={location.id}><span className="movie-record-index">Location</span><h3>{location.name}</h3><p>{location.description}</p><RecordLine label="Visual continuity" value={location.visualContinuityNotes} /></article>)}</div> : <EmptyModule title="No locations defined yet" text="World records will appear here once the first location is part of the plan." />}</div>;
}

function ScenesModule({ project, selectedSceneId, newScene, addingScene, onSelectScene, onChangeScene, onAddScene, onGenerate }: { project: MovieProject; selectedSceneId: string | null; newScene: { title: string; summary: string }; addingScene: boolean; onSelectScene: (sceneId: string) => void; onChangeScene: (value: { title: string; summary: string }) => void; onAddScene: () => void; onGenerate: (sceneId: string) => Promise<void> }) {
  return <div className="movie-module-stack"><div className="movie-scene-workspace"><section className="movie-workspace-section movie-scene-list-panel"><div className="movie-section-head"><div><span className="movie-workspace-kicker">Ordered story</span><h3>{project.scenes.length} scenes planned</h3></div><ListChecks size={17} /></div>{project.scenes.length ? <div className="movie-scene-list-modern">{project.scenes.map((scene) => <SceneListItem key={scene.id} scene={scene} isSelected={scene.id === selectedSceneId} onSelect={() => onSelectScene(scene.id)} onGenerate={() => void onGenerate(scene.id)} />)}</div> : <EmptyModule text="Add the first scene to give the project a beginning." />}</section><section className="movie-workspace-section movie-scene-inspector"><span className="movie-workspace-kicker">Inspector</span>{project.scenes.find((scene) => scene.id === selectedSceneId) ? <SceneInspector scene={project.scenes.find((scene) => scene.id === selectedSceneId)!} /> : <EmptyModule title="Select a scene" text="The inspector will show scene intent, continuity, and shot count." />}</section></div><form className="movie-add-scene-modern" onSubmit={(event) => { event.preventDefault(); onAddScene(); }}><div><span className="movie-workspace-kicker">Planning action</span><h3>Add a scene</h3></div><label><span className="sr-only">Scene title</span><input value={newScene.title} onChange={(event) => onChangeScene({ ...newScene, title: event.target.value })} placeholder="Scene title" /></label><label><span className="sr-only">Scene summary</span><input value={newScene.summary} onChange={(event) => onChangeScene({ ...newScene, summary: event.target.value })} placeholder="One-line scene intent" /></label><button className="movie-workspace-button is-primary" type="submit" disabled={addingScene || !newScene.title.trim() || !newScene.summary.trim()}>{addingScene ? "Saving…" : "Add scene"}</button></form></div>;
}

function StoryboardModule({ project }: { project: MovieProject }) {
  return <div className="movie-module-stack"><section className="movie-workspace-section"><div className="movie-section-head"><div><span className="movie-workspace-kicker">Filmstrip</span><h3>Scenes, not placeholders</h3></div><span className="movie-section-count">{project.scenes.length} frames planned</span></div>{project.scenes.length ? <div className="movie-filmstrip">{project.scenes.map((scene) => { const clip = readyClipForScene(scene); return <article className="movie-filmstrip-card" key={scene.id}>{clip?.assetId ? <video src={assetFileUrl(clip.assetId, true)} preload="metadata" muted aria-label={scene.title} /> : <div className="movie-filmstrip-placeholder"><Film size={19} /><span>No footage</span></div>}<div><span>{String(scene.sequence).padStart(2, "0")}</span><strong>{scene.title}</strong><small>{formatDuration(scene.durationSeconds)}</small></div></article>; })}</div> : <EmptyGeneratedStage title="Your storyboard is empty" text="Planned scenes will become the first visual pass here." />}</section><ModuleIntro icon={<Layers3 size={18} />} title="Storyboard foundation" text="This surface is ready for real frames. It will not manufacture thumbnails for scenes that have no generated footage." /></div>;
}

function ProductionModule({ project, completionPercent }: { project: MovieProject; completionPercent: number }) {
  const ready = project.clips.filter((clip) => hasReadyAsset(clip.status, clip.assetId)).length;
  const inFlight = project.clips.filter((clip) => ["Pending", "Queued", "Generating", "Running"].includes(clip.status)).length;
  return <div className="movie-module-stack"><section className="movie-production-hero"><div><span className="movie-workspace-kicker">Production signal</span><h3>{completionPercent}% of the current plan has a reviewable clip</h3><p>Only durable project records are counted here. Empty stages stay visible instead of looking complete.</p></div><div className="movie-production-ring"><strong>{completionPercent}%</strong><span>ready</span></div></section><div className="movie-metric-row"><Metric label="Scenes" value={project.scenes.length} /><Metric label="Ready clips" value={ready} /><Metric label="In progress" value={inFlight} /><Metric label="Assemblies" value={project.assemblies.length} /></div><ModuleIntro icon={<Workflow size={18} />} title="Production controls are intentionally restrained" text="Queueing a scene is supported where the API is available. Batch planning, approvals, and scheduling remain future surfaces." /></div>;
}

function EditModule({ project }: { project: MovieProject }) {
  return <div className="movie-module-stack"><section className="movie-workspace-section"><div className="movie-section-head"><div><span className="movie-workspace-kicker">Editorial timeline</span><h3>Timeline foundation</h3></div><span className="movie-section-count">{project.scenes.length ? `${project.scenes.length} scene blocks` : "No scene blocks"}</span></div>{project.scenes.length ? <div className="movie-timeline"><div className="movie-timeline-ruler"><span>00:00</span><span>00:30</span><span>01:00</span><span>01:30</span></div><div className="movie-timeline-track">{project.scenes.map((scene, index) => <div key={scene.id} className={`movie-timeline-block ${readyClipForScene(scene) ? "is-ready" : ""}`} style={{ width: `${Math.max(13, Math.min(34, (scene.durationSeconds ?? 12) / 2.2))}%`, marginInlineStart: index ? "2%" : 0 }}><span>{String(scene.sequence).padStart(2, "0")}</span><strong>{scene.title}</strong></div>)}</div><div className="movie-timeline-note"><PencilRuler size={15} /><span>Editing controls will appear when there is a real sequence to revise.</span></div></div> : <EmptyGeneratedStage title="No footage to edit" text="A timeline will be built from real scene clips, not simulated blocks." />}</section></div>;
}

function FutureModule({ icon, title, text }: { icon: ReactNode; title: string; text: string }) {
  return <div className="movie-module-stack"><section className="movie-future-module"><div className="movie-future-icon">{icon}</div><span className="movie-workspace-kicker">Foundation surface</span><h3>{title}</h3><p>{text}</p><div className="movie-future-rule"><span /> <small>Not available in this foundation</small> <span /></div></section></div>;
}

function SceneListItem({ scene, isSelected, onSelect, onGenerate }: { scene: MovieScene; isSelected: boolean; onSelect: () => void; onGenerate: () => void }) {
  const clip = readyClipForScene(scene);
  const hasPending = scene.clips.some((item) => ["Pending", "Queued", "Generating", "Running"].includes(item.status));
  return <article className={`movie-scene-list-item ${isSelected ? "is-selected" : ""}`}><button type="button" className="movie-scene-list-main" onClick={onSelect}><span className="movie-scene-sequence">{String(scene.sequence).padStart(2, "0")}</span><span><strong>{scene.title}</strong><small>{scene.summary}</small></span><ChevronRight size={14} /></button><div className="movie-scene-list-actions"><span className={`movie-scene-state ${clip ? "is-ready" : hasPending ? "is-pending" : ""}`}>{clip ? <><Check size={12} /> Ready</> : hasPending ? "In progress" : "Planned"}</span>{!clip && <button type="button" className="movie-text-action" onClick={onGenerate} disabled={hasPending}>{hasPending ? "Queued" : <><Sparkles size={12} /> Generate</>}</button>}</div></article>;
}

function SceneInspector({ scene }: { scene: MovieScene }) {
  return <div className="movie-inspector-content"><h3>{scene.title}</h3><p>{scene.summary}</p><div className="movie-inspector-facts"><span><strong>{formatDuration(scene.durationSeconds)}</strong> duration</span><span><strong>{scene.shots.length}</strong> shots</span></div><RecordLine label="Continuity" value={scene.continuityNotes} /><RecordLine label="Narration" value={scene.narration} /><RecordLine label="Dialogue" value={scene.dialogue} /></div>;
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

function EmptyModule({ title = "Nothing here yet", text }: { title?: string; text: string }) {
  return <div className="movie-module-empty"><Film size={17} /><strong>{title}</strong><p>{text}</p></div>;
}

function Metric({ label, value }: { label: string; value: number }) {
  return <div className="movie-metric"><span>{label}</span><strong>{value}</strong></div>;
}

function XCircleIcon() {
  return <span className="movie-error-icon" aria-hidden="true">!</span>;
}
