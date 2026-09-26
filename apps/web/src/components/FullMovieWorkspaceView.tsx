"use client";

import Link from "next/link";
import { useEffect, useMemo, useState, type ReactNode } from "react";
import {
  ArrowDown,
  ArrowLeft,
  ArrowUp,
  ArrowUpRight,
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
  Layers3,
  LockKeyhole,
  Map,
  PencilRuler,
  Plus,
  Play,
  Save,
  ShieldCheck,
  SlidersHorizontal,
  Sparkles,
  Target,
  Image as ImageIcon,
  Trash2,
  Users,
  Workflow,
} from "lucide-react";
import { api, type Asset, type DirectorProposal, type DirectorStoryAction, type MovieCast, type MovieCharacter, type MovieCharacterState, type MovieOverview, type MovieProject, type MovieProjectShell, type MovieScene, type MovieSceneWorkspace, type MovieScenesWorkspace, type MovieScreenplayElementType, type MovieStory, type MovieStoryRevision, type MovieStoryRevisionInput } from "@/lib/api";
import { assetFileUrl } from "@/lib/apiBase";
import { MovieWorldWorkspace } from "@/components/MovieWorldWorkspace";

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
  if (moduleFromSlug(module) === "world") return <WorldOnlyWorkspace projectId={projectId} />;
  return <FullMovieProjectWorkspace projectId={projectId} module={module} />;
}

function WorldOnlyWorkspace({ projectId }: { projectId: string }) {
  return <div className="movie-studio-page movie-full-workspace"><header className="movie-workspace-header"><Link href={`/create/movie/${projectId}/overview`} className="movie-workspace-back"><ArrowLeft size={14} /> Movie Studio</Link><div className="movie-workspace-heading"><div><span className="movie-workspace-kicker">Focused production read model</span><h1>World room</h1><p>Locations, sets, props, references, usage, and continuity — without loading the complete project graph.</p></div><div className="movie-workspace-meta"><span>World V2</span><span>Asset-backed</span></div></div></header><main className="movie-world-only-main"><MovieWorldWorkspace projectId={projectId} /></main></div>;
}

function FullMovieProjectWorkspace({ projectId, module }: { projectId: string; module: string }) {
  const activeModule = moduleFromSlug(module);
  const [project, setProject] = useState<MovieProject | null>(null);
  const [overview, setOverview] = useState<MovieOverview | null>(null);
  const [projectShell, setProjectShell] = useState<MovieProjectShell | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [selectedSceneId, setSelectedSceneId] = useState<string | null>(null);
  const [newScene, setNewScene] = useState({ title: "", summary: "" });
  const [addingScene, setAddingScene] = useState(false);

  useEffect(() => {
    let mounted = true;
    const load = activeModule === "overview" ? api.getMovieOverview(projectId) : activeModule === "story" ? api.getMovieProjectShell(projectId) : api.getMovieProject(projectId);
    void load.then((result) => {
      if (!mounted) return;
      if (activeModule === "overview") setOverview(result as MovieOverview);
      else if (activeModule === "story") setProjectShell(result as MovieProjectShell);
      else {
        const fullProject = result as MovieProject;
        setProject(fullProject);
        setSelectedSceneId(fullProject.scenes[0]?.id ?? null);
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
  const workspace = overview?.project ?? project ?? projectShell;
  const fullProject = project as MovieProject;
  if (!workspace || (activeModule !== "overview" && activeModule !== "story" && !project)) return <div className="movie-studio-page"><div className="movie-workspace-error"><XCircleIcon /><h1>Workspace unavailable</h1><p>{error || "This movie project is not available in the current workspace."}</p><Link href="/create/movie" className="movie-workspace-button is-primary"><ArrowLeft size={14} /> Back to Movie Studio</Link></div></div>;

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

      <div className="movie-workspace-layout">
        <nav className="movie-workspace-nav" aria-label="Full Movie Project navigation">
          <div className="movie-workspace-nav-label">Project map</div>
          <div className="movie-workspace-nav-list">
            {fullMovieModules.map((item) => {
              const Icon = item.icon;
              const href = `/create/movie/${workspace.id}/${item.slug}`;
              return <Link key={item.slug} href={href} className={`movie-workspace-nav-item ${activeModule === item.slug ? "is-active" : ""}`} aria-current={activeModule === item.slug ? "page" : undefined}><Icon size={15} /><span>{item.label}</span>{activeModule === item.slug && <ChevronRight size={13} />}</Link>;
            })}
          </div>
          <div className="movie-workspace-nav-foot"><span className="movie-live-dot" /> <span>Plan saved locally to this project</span></div>
        </nav>

        <main className="movie-workspace-main">
          <div className="movie-module-heading"><div><span className="movie-workspace-kicker">{copy.eyebrow}</span><h2>{copy.title}</h2><p>{copy.description}</p></div><span className="movie-module-index">{String(fullMovieModules.findIndex((item) => item.slug === activeModule) + 1).padStart(2, "0")} / 12</span></div>
          {activeModule === "overview" && overview && <OverviewModule overview={overview} outputAssetId={outputAssetId} />}
          {activeModule === "story" && <StoryModule projectId={workspace.id} />}
          {activeModule === "cast" && <CastModule projectId={workspace.id} />}
          {activeModule === "world" && <WorldModule projectId={fullProject.id} />}
          {activeModule === "scenes" && <ScenesModule projectId={fullProject.id} selectedSceneId={selectedScene?.id ?? null} onSelectScene={setSelectedSceneId} onGenerate={generateScene} />}
          {activeModule === "storyboard" && <StoryboardModule project={fullProject} />}
          {activeModule === "production" && <ProductionModule project={fullProject} completionPercent={completionPercent} />}
          {activeModule === "edit" && <EditModule project={fullProject} />}
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
          {overview ? <div className="movie-director-section"><span className="movie-inspector-label">Next decision</span><strong>{overview.nextActions[0]?.label ?? "No action queued"}</strong><p>{overview.nextActions[0]?.reason ?? "The persisted project state has no outstanding setup recommendation."}</p></div> : activeModule === "story" ? <div className="movie-director-section"><span className="movie-inspector-label">Writing focus</span><strong>Approved story is the production source</strong><p>Drafts stay separate until a reviewer approves them. Director and breakdown surfaces should use the approved revision when one exists.</p></div> : selectedScene ? <div className="movie-director-section"><span className="movie-inspector-label">Selected scene</span><strong>{String(selectedScene.sequence).padStart(2, "0")} · {selectedScene.title}</strong><p>{selectedScene.summary}</p><span className="movie-inspector-detail">{formatDuration(selectedScene.durationSeconds)} · {selectedScene.shots.length} shots planned</span></div> : <div className="movie-director-empty"><Film size={18} /><p>Select a scene to inspect its intent and continuity notes.</p></div>}
          <div className="movie-director-note"><Sparkles size={14} /><p>The Director region stays quiet until the project has a decision to make.</p></div>
        </aside>
      </div>
    </div>
  );
}

function OverviewModule({ project, outputAssetId, completionPercent, selectedScene, onSelectScene }: { project: MovieProject; outputAssetId: string | null; completionPercent: number; selectedScene: MovieScene | null; onSelectScene: (sceneId: string) => void }) {
  return <div className="movie-module-stack">
    <section className="movie-generated-surface">
      <div className="movie-surface-heading"><div><span className="movie-workspace-kicker">Generated work</span><h3>{outputAssetId ? "Latest project output" : "The stage is ready for footage"}</h3></div><span className="movie-surface-status"><span className={outputAssetId ? "is-ready" : ""} />{outputAssetId ? "Ready to review" : "No output yet"}</span></div>
      {outputAssetId ? <div className="movie-generated-video"><video src={assetFileUrl(outputAssetId, true)} controls preload="metadata" aria-label={project.title} /><div className="movie-generated-video-caption"><Play size={14} /> {project.title}</div></div> : <EmptyGeneratedStage />}
      <div className="movie-generated-footer"><span>{project.scenes.length} scenes planned</span><span>{completionPercent}% of planned footage ready</span></div>
    </section>
    <div className="movie-overview-grid">
      <section className="movie-workspace-section"><div className="movie-section-head"><div><span className="movie-workspace-kicker">Story rail</span><h3>Scenes in order</h3></div><Link href={`/create/movie/${project.id}/scenes`}>Open scenes <ChevronRight size={13} /></Link></div>{project.scenes.length ? <div className="movie-scene-rail">{project.scenes.slice(0, 4).map((scene) => <button key={scene.id} type="button" className={`movie-scene-rail-item ${selectedScene?.id === scene.id ? "is-selected" : ""}`} onClick={() => onSelectScene(scene.id)}><span>{String(scene.sequence).padStart(2, "0")}</span><strong>{scene.title}</strong><small>{formatDuration(scene.durationSeconds)}</small></button>)}</div> : <EmptyModule text="Scenes will become the spine of the project here." />}</section>
      <section className="movie-workspace-section"><div className="movie-section-head"><div><span className="movie-workspace-kicker">Continuity</span><h3>Guide at a glance</h3></div><Link href={`/create/movie/${project.id}/story`}>Open story <ChevronRight size={13} /></Link></div><div className="movie-continuity-grid"><ContinuityItem label="Visual language" value={project.guide.visualLanguage} /><ContinuityItem label="Camera language" value={project.guide.cameraLanguage} /><ContinuityItem label="Color & lighting" value={project.guide.colorAndLighting} /><ContinuityItem label="Sound & narration" value={project.guide.soundAndNarration} /></div></section>
    </div>
  </div>;
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


type StorySection = "Premise" | "Logline" | "Synopsis" | "Treatment" | "Screenplay";
const storySections: StorySection[] = ["Premise", "Logline", "Synopsis", "Treatment", "Screenplay"];
const screenplayElementTypes: MovieScreenplayElementType[] = ["Action", "Dialogue", "Parenthetical", "Transition", "Note"];

function emptyStoryDraft(): MovieStoryRevisionInput {
  return { premise: "", logline: "", synopsis: "", treatment: "", authorship: "Human", changeSummary: "", scenes: [] };
}

function revisionToDraft(revision: MovieStoryRevision): MovieStoryRevisionInput {
  return { premise: revision.premise, logline: revision.logline, synopsis: revision.synopsis, treatment: revision.treatment, authorship: revision.authorship, parentRevisionId: revision.parentRevisionId, changeSummary: revision.changeSummary ?? "", scenes: revision.scenes.map((scene) => ({ sceneIdentifier: scene.sceneIdentifier, actNumber: scene.actNumber, sequenceNumber: scene.sequenceNumber, movieSceneId: scene.movieSceneId, slugline: scene.slugline, synopsis: scene.synopsis, elements: scene.elements.map((element) => ({ elementType: element.elementType, content: element.content, characterName: element.characterName, parenthetical: element.parenthetical })) })) };
}

function StoryModule({ projectId }: { projectId: string }) {
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
  const [storyAction, setStoryAction] = useState<DirectorStoryAction>("improve_logline");
  const [storySceneId, setStorySceneId] = useState<string | null>(null);
  const [storyBusy, setStoryBusy] = useState(false);

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

  async function saveDraft(auto = false) {
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
      if (!auto) setSection("Screenplay");
    }
  }

  useEffect(() => {
    if (!dirty || !story?.canEdit || !draft.premise.trim() || !draft.logline.trim() || !draft.synopsis.trim() || !draft.treatment.trim()) return;
    const timer = window.setTimeout(() => { void saveDraft(true); }, 1800);
    return () => window.clearTimeout(timer);
    // The draft object is intentionally observed through the dirty flag to debounce editor input.
    // eslint-disable-next-line react-hooks/exhaustive-deps
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
    if (!action || storyProposal.status !== "Approved") return;
    setStoryBusy(true);
    try {
      const result = await api.executeMovieDirectorAction(action.id);
      if (result.action.status !== "Succeeded") throw new Error(result.result.safeMessage);
      const nextStory = await api.getMovieStory(projectId);
      setStory(nextStory);
      setSelectedRevision(nextStory.currentRevision);
      setDraft(nextStory.currentRevision ? revisionToDraft(nextStory.currentRevision) : emptyStoryDraft());
      setDraftRevisionId(nextStory.currentRevision?.status === "Draft" ? nextStory.currentRevision.id : null);
      setDirty(false);
      setStoryProposal(null);
    } catch (cause) { setError(cause instanceof Error ? cause.message : "The approved Story proposal could not be applied."); }
    finally { setStoryBusy(false); }
  }

  if (loading) return <div className="movie-story-loading"><span className="loading-spinner" /><p>Opening the story room…</p></div>;
  if (!story) return <div className="movie-module-empty"><BookOpen size={20} /><strong>Story unavailable</strong><p>{error || "This story is not available in the current workspace."}</p></div>;

  const current = story.currentRevision;
  const approved = story.approvedRevision;
  const status = selectedRevision?.status ?? "New draft";
  const canEdit = story.canEdit && (!selectedRevision || (selectedRevision.status === "Draft" && selectedRevision.id === story.currentRevisionId));

  return <div className="movie-story-workspace">
    <section className="movie-story-statusbar">
      <div><span className="movie-workspace-kicker">Story workspace</span><h3>Write the film, one deliberate pass at a time.</h3><p>Structured screenplay elements keep the writing useful to the production rooms that follow.</p></div>
      <div className="movie-story-actions"><span className={`movie-story-state is-${story.approvalState.toLowerCase()}`}><span />{story.approvalState === "Approved" ? "Approved story" : story.approvalState === "InReview" ? "In review" : "Draft workspace"}</span>{savedAt && <small>Saved {savedAt.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}</small>}{story.canEdit && <button type="button" className="movie-workspace-button is-quiet" onClick={() => void saveDraft()} disabled={saving || !dirty}>{saving ? "Saving…" : <><Save size={13} /> Save draft</>}</button>}{story.canEdit && current?.status === "Approved" && <button type="button" className="movie-workspace-button is-quiet" onClick={newDraftFromCurrent}><Plus size={13} /> New revision</button>}{story.canEdit && draftRevisionId && current?.status === "Draft" && <button type="button" className="movie-workspace-button is-primary" onClick={() => void submit()} disabled={saving}>Send for review</button>}{story.canApprove && draftRevisionId && <button type="button" className="movie-workspace-button is-approve" onClick={() => void approve()} disabled={saving}>Approve revision</button>}</div>
    </section>
    {error && <div className="movie-workspace-error-inline"><XCircleIcon /> {error}</div>}
    {approved && <div className="movie-story-approval-banner"><LockKeyhole size={15} /><div><strong>Approved screenplay · Revision {approved.revisionNumber}</strong><span>Downstream Director and scene breakdown should use this revision. Your current draft remains separate.</span></div></div>}
    {!approved && <div className="movie-story-approval-banner is-muted"><BookOpen size={15} /><div><strong>No approved screenplay yet</strong><span>Finish a human review before production decisions treat this story as authoritative.</span></div></div>}
    <section className="movie-workspace-section movie-director-story-assist"><div className="movie-section-head"><div><span className="movie-workspace-kicker">Director assistance</span><h3>Propose, review, then apply</h3></div><Sparkles size={17} /></div><p className="movie-story-safety-note">The Director uses the locked guide, current or approved Story, selected scene, and bounded Cast / World references. It never silently rewrites an approved revision.</p><div className="movie-story-assist-controls"><label><span>Action</span><select value={storyAction} onChange={(event) => setStoryAction(event.target.value as DirectorStoryAction)}><option value="develop_premise">Develop premise</option><option value="improve_logline">Improve logline</option><option value="expand_synopsis">Expand synopsis</option><option value="create_refine_treatment">Create / refine treatment</option><option value="propose_screenplay_scene">Propose screenplay scene</option><option value="rewrite_selected_passage">Rewrite selected passage</option><option value="improve_dialogue">Improve dialogue</option><option value="tighten_pacing">Tighten pacing</option><option value="identify_story_inconsistencies">Identify story inconsistencies</option></select></label>{(current?.scenes.length ?? approved?.scenes.length ?? 0) > 0 && <label><span>Target scene</span><select value={storySceneId ?? ""} onChange={(event) => setStorySceneId(event.target.value || null)}><option value="">Choose a scene</option>{(current?.scenes ?? approved?.scenes ?? []).map((scene) => <option key={scene.id} value={scene.id}>{scene.sceneIdentifier} · {scene.slugline}</option>)}</select></label>}<button className="movie-workspace-button is-primary" type="button" onClick={() => void createStoryProposal()} disabled={storyBusy}>{storyBusy ? "Working…" : "Create proposal"}</button></div></section>
    {storyProposal?.storyReview && <StoryProposalReview proposal={storyProposal} busy={storyBusy} onReview={(approveProposal) => void reviewStoryProposal(approveProposal)} onApply={() => void applyStoryProposal()} />}
    <div className="movie-story-editor-layout">
      <nav className="movie-story-sections" aria-label="Story sections"><span className="movie-inspector-label">Manuscript</span>{storySections.map((item) => <button type="button" key={item} className={section === item ? "is-active" : ""} onClick={() => setSection(item)}>{item}<ChevronRight size={13} /></button>)}<div className="movie-story-provenance"><span className="movie-inspector-label">Revision provenance</span><strong>{draft.authorship}</strong><p>{draft.authorship === "AiSuggested" ? "AI suggestion — review before treating as authored." : draft.authorship === "HumanEdited" ? "Human-edited from an earlier suggestion or pass." : "Written or materially authored by a human."}</p></div></nav>
      <div className="movie-story-manuscript">
        <div className="movie-story-manuscript-head"><div><span className="movie-workspace-kicker">{section}</span><h4>{section === "Screenplay" ? "Screenplay" : "Story foundation"}</h4></div><span className="movie-story-revision-chip">{status} · {draft.authorship}</span></div>
        {section === "Screenplay" ? <ScreenplayEditor draft={draft} editable={canEdit} onUpdateDraft={updateDraft} onUpdateScene={updateScene} onUpdateElement={updateElement} onAddScene={addScene} /> : <StoryTextEditor section={section} draft={draft} editable={canEdit} onUpdate={(value) => updateDraft({ [section.toLowerCase()]: value } as Partial<MovieStoryRevisionInput>)} />}
      </div>
      <aside className="movie-story-history"><div className="movie-section-head"><div><span className="movie-workspace-kicker">Version control</span><h4>Revision history</h4></div><span className="movie-section-count">{story.revisions.length} passes</span></div>{story.revisions.length ? story.revisions.map((revision) => <button type="button" className={`movie-story-history-item ${revision.id === story.currentRevisionId ? "is-current" : ""} ${revision.id === story.approvedRevisionId ? "is-approved" : ""}`} key={revision.id} onClick={() => void inspectRevision(revision.id)}><span>Revision {revision.revisionNumber}</span><strong>{revision.status}</strong><small>{revision.authorship} · {revision.changeSummary || "No change note"}</small></button>) : <p className="movie-story-history-empty">Your first saved pass will appear here.</p>}<div className="movie-story-history-note"><ShieldCheck size={14} /><span>Approved and rejected revisions are immutable.</span></div></aside>
    </div>
  </div>;
}

function StoryProposalReview({ proposal, busy, onReview, onApply }: { proposal: DirectorProposal; busy: boolean; onReview: (approve: boolean) => void; onApply: () => void }) {
  const review = proposal.storyReview!;
  return <section className="movie-workspace-section movie-story-proposal-review"><div className="movie-section-head"><div><span className="movie-workspace-kicker">Proposal review · {proposal.status}</span><h3>{proposal.title}</h3></div><span className="movie-section-count">{review.appliesToStory ? "Revision candidate" : "Review only"}</span></div><p>{proposal.summary}</p>{review.changes.map((change) => <div className="movie-story-diff" key={`${change.field}-${change.target}`}><span className="movie-inspector-label">{change.field}</span><div><article><small>Existing content</small><p>{change.existingContent}</p></article><article className="is-proposed"><small>Proposed content</small><p>{change.proposedContent}</p></article></div></div>)}{review.findings.length > 0 && <div className="movie-story-findings"><strong>Director findings</strong><ul>{review.findings.map((finding) => <li key={finding}>{finding.replaceAll("_", " ")}</li>)}</ul></div>}<div className="movie-story-review-actions">{proposal.status === "PendingApproval" && <><button className="movie-workspace-button is-primary" type="button" onClick={() => onReview(true)} disabled={busy}>Accept proposal</button><button className="movie-workspace-button" type="button" onClick={() => onReview(false)} disabled={busy}>Reject</button></>}{proposal.status === "Approved" && review.appliesToStory && <button className="movie-workspace-button is-primary" type="button" onClick={onApply} disabled={busy}>Apply to editable Story revision</button>}{proposal.status === "Approved" && !review.appliesToStory && <span className="movie-story-approved-note">Findings accepted. No Story text was changed.</span>}</div></section>;
}


function StoryTextEditor({ section, draft, editable, onUpdate }: { section: Exclude<StorySection, "Screenplay">; draft: MovieStoryRevisionInput; editable: boolean; onUpdate: (value: string) => void }) {
  const key = section.toLowerCase() as "premise" | "logline" | "synopsis" | "treatment";
  const hints: Record<typeof key, string> = { premise: "What is the human truth or dramatic engine?", logline: "Who wants what, what stands in the way, and why now?", synopsis: "The complete story arc in clear, grounded prose.", treatment: "A scene-aware prose map of the story before pages." };
  return <div className="movie-story-text-editor"><p className="movie-story-writing-prompt">{hints[key]}</p><textarea aria-label={section} value={draft[key]} onChange={(event) => onUpdate(event.target.value)} disabled={!editable} placeholder={`Write the ${section.toLowerCase()}…`} /><span className="movie-story-character-count">{draft[key].length.toLocaleString()} characters</span></div>;
}

function ScreenplayEditor({ draft, editable, onUpdateDraft, onUpdateScene, onUpdateElement, onAddScene }: { draft: MovieStoryRevisionInput; editable: boolean; onUpdateDraft: (patch: Partial<MovieStoryRevisionInput>) => void; onUpdateScene: (index: number, patch: Partial<MovieStoryRevisionInput["scenes"][number]>) => void; onUpdateElement: (sceneIndex: number, elementIndex: number, patch: Partial<MovieStoryRevisionInput["scenes"][number]["elements"][number]>) => void; onAddScene: () => void }) {
  return <div className="movie-screenplay-editor"><div className="movie-screenplay-toolbar"><span>{draft.scenes.length} scenes</span><span>Structured pages</span><label>Authorship<select aria-label="Revision authorship" value={draft.authorship} onChange={(event) => onUpdateDraft({ authorship: event.target.value as MovieStoryRevisionInput["authorship"] })} disabled={!editable}><option value="Human">Human</option><option value="HumanEdited">Human edited</option><option value="AiSuggested">AI suggested</option></select></label></div>{draft.scenes.length ? draft.scenes.map((scene, sceneIndex) => <article className="movie-screenplay-scene" key={`${scene.sceneIdentifier}-${sceneIndex}`}><header><span className="movie-screenplay-scene-number">{String(sceneIndex + 1).padStart(2, "0")}</span><div><input aria-label={`Scene ${sceneIndex + 1} identifier`} value={scene.sceneIdentifier} onChange={(event) => onUpdateScene(sceneIndex, { sceneIdentifier: event.target.value })} disabled={!editable} /><input className="movie-screenplay-slugline" aria-label={`Scene ${sceneIndex + 1} heading`} value={scene.slugline} onChange={(event) => onUpdateScene(sceneIndex, { slugline: event.target.value })} disabled={!editable} /></div><span className="movie-screenplay-scene-meta">Act {scene.actNumber ?? "—"} · Seq {scene.sequenceNumber ?? "—"}</span></header><textarea className="movie-screenplay-scene-note" aria-label={`Scene ${sceneIndex + 1} synopsis`} value={scene.synopsis ?? ""} onChange={(event) => onUpdateScene(sceneIndex, { synopsis: event.target.value })} disabled={!editable} placeholder="Scene intention / beat" />{scene.elements.map((element, elementIndex) => <div className={`movie-screenplay-element is-${element.elementType.toLowerCase()}`} key={`${scene.sceneIdentifier}-${elementIndex}`}><select aria-label={`Scene ${sceneIndex + 1} element ${elementIndex + 1} type`} value={element.elementType} onChange={(event) => onUpdateElement(sceneIndex, elementIndex, { elementType: event.target.value as MovieScreenplayElementType })} disabled={!editable}>{screenplayElementTypes.map((type) => <option key={type} value={type}>{type}</option>)}</select>{element.elementType === "Dialogue" && <input aria-label={`Scene ${sceneIndex + 1} element ${elementIndex + 1} character`} value={element.characterName ?? ""} onChange={(event) => onUpdateElement(sceneIndex, elementIndex, { characterName: event.target.value })} disabled={!editable} placeholder="CHARACTER" />}{element.elementType === "Dialogue" && <input aria-label={`Scene ${sceneIndex + 1} element ${elementIndex + 1} parenthetical`} value={element.parenthetical ?? ""} onChange={(event) => onUpdateElement(sceneIndex, elementIndex, { parenthetical: event.target.value })} disabled={!editable} placeholder="(parenthetical)" />}{element.elementType === "Transition" ? <input aria-label={`Scene ${sceneIndex + 1} transition`} value={element.content} onChange={(event) => onUpdateElement(sceneIndex, elementIndex, { content: event.target.value })} disabled={!editable} placeholder="CUT TO:" /> : <textarea aria-label={`Scene ${sceneIndex + 1} element ${elementIndex + 1} content`} value={element.content} onChange={(event) => onUpdateElement(sceneIndex, elementIndex, { content: event.target.value })} disabled={!editable} placeholder={element.elementType === "Action" ? "Describe what we see and hear…" : "Write the page…"} />}{editable && <button type="button" aria-label="Remove screenplay element" onClick={() => onUpdateScene(sceneIndex, { elements: scene.elements.filter((_, index) => index !== elementIndex) })}><Trash2 size={13} /></button>}</div>)}<div className="movie-screenplay-scene-actions">{editable && <button type="button" className="movie-text-action" onClick={() => onUpdateScene(sceneIndex, { elements: [...scene.elements, { elementType: "Action", content: "", characterName: null, parenthetical: null }] })}><Plus size={12} /> Add element</button>}</div></article>) : <div className="movie-screenplay-empty"><BookOpen size={20} /><strong>Your first scene starts the pages.</strong><p>Use typed screenplay blocks instead of flattening the script into a generic document.</p></div>}{editable && <button type="button" className="movie-add-screenplay-scene" onClick={onAddScene}><Plus size={14} /> Add scene</button>}</div>;
}

function CastModule({ project }: { project: MovieProject }) {
  return <div className="movie-module-stack"><ModuleIntro icon={<Users size={18} />} title="Characters stay intentional" text="The cast surface shows durable character records only. It does not invent visual references or performances." />{project.characters.length ? <div className="movie-record-grid">{project.characters.map((character) => <article className="movie-record-card" key={character.id}><span className="movie-record-index">Character</span><h3>{character.name}</h3><p>{character.description}</p><RecordLine label="Appearance" value={character.appearance} /><RecordLine label="Performance" value={character.voiceAndPerformance} /><RecordLine label="Continuity" value={character.continuityNotes} /></article>)}</div> : <EmptyModule title="No cast records yet" text="Add character records when the story has a person worth keeping consistent." />}</div>;
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

function ScenesModule({ projectId, selectedSceneId, onSelectScene, onGenerate }: { projectId: string; selectedSceneId: string | null; onSelectScene: (sceneId: string) => void; onGenerate: (sceneId: string) => Promise<void> }) {
  const [workspace, setWorkspace] = useState<MovieScenesWorkspace | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [breakdownBusy, setBreakdownBusy] = useState(false);
  const [newScene, setNewScene] = useState({ title: "", summary: "", sequenceId: "" });
  const [addingScene, setAddingScene] = useState(false);
  const selectedScene = useMemo(() => workspace?.acts.flatMap((act) => act.sequences.flatMap((sequence) => sequence.scenes)).find((scene) => scene.id === selectedSceneId) ?? workspace?.acts[0]?.sequences[0]?.scenes[0] ?? null, [workspace, selectedSceneId]);
  const sequences = workspace?.acts.flatMap((act) => act.sequences) ?? [];
  const refresh = async () => {
    setLoading(true);
    try { setWorkspace(await api.getMovieScenesWorkspace(projectId)); } catch (cause) { setError(cause instanceof Error ? cause.message : "The Scenes room could not be loaded."); } finally { setLoading(false); }
  };
  useEffect(() => { void refresh(); }, [projectId]);
  useEffect(() => { if (!selectedSceneId && selectedScene) onSelectScene(selectedScene.id); }, [selectedScene, selectedSceneId, onSelectScene]);
  async function breakDown() {
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
      await refresh(); onSelectScene(scene.id); setNewScene({ title: "", summary: "", sequenceId });
    } catch (cause) { setError(cause instanceof Error ? cause.message : "The production scene could not be saved."); }
    finally { setAddingScene(false); }
  }
  if (loading) return <section className="movie-workspace-section"><div className="movie-workspace-loading"><span className="loading-spinner" /><p>Loading the scene map…</p></div></section>;
  if (!workspace) return <EmptyModule title="Scenes workspace unavailable" text={error || "The production hierarchy could not be loaded."} />;
  return <div className="movie-module-stack">
    <section className="movie-scene-command-bar"><div><span className="movie-workspace-kicker">Production bridge</span><h3>Screenplay → production scenes → shots</h3><p>Break down approved story material into durable production records. Existing scenes and shots are never silently replaced.</p></div><button type="button" className="movie-workspace-button is-primary" onClick={() => void breakDown()} disabled={breakdownBusy || workspace.screenplayApprovalState !== "Approved"}>{breakdownBusy ? "Mapping…" : workspace.linkedSceneCount ? "Map unlinked screenplay scenes" : "Break down approved screenplay"}</button></section>
    <div className="movie-scenes-summary"><Metric label="Acts" value={workspace.acts.length} /><Metric label="Sequences" value={sequences.length} /><Metric label="Scenes" value={workspace.sceneCount} /><Metric label="Shots ready for planning" value={workspace.shotCount} /></div>
    {workspace.screenplayApprovalState !== "Approved" && <div className="movie-scenes-callout"><BookOpen size={15} /><span>{workspace.screenplayApprovalState === "NotAvailable" ? "No approved screenplay is linked yet. Manual production scene creation remains available." : `Screenplay status: ${workspace.screenplayApprovalState}. Approve a revision before mapping it.`}</span></div>}
    <div className="movie-scene-workspace movie-scenes-hierarchy-layout"><section className="movie-workspace-section movie-scene-list-panel"><div className="movie-section-head"><div><span className="movie-workspace-kicker">Canonical hierarchy</span><h3>Acts, sequences, scenes</h3></div><span className="movie-section-count">{workspace.linkedSceneCount} linked</span></div>{workspace.acts.length ? workspace.acts.map((act) => <SceneAct key={act.id} act={act} selectedSceneId={selectedScene?.id ?? null} onSelect={onSelectScene} onGenerate={onGenerate} onReorder={reorderScene} />) : <EmptyModule title="No production hierarchy yet" text="Create a scene below or map an approved screenplay to establish the production spine." />}</section><section className="movie-workspace-section movie-scene-inspector"><span className="movie-workspace-kicker">Scene inspector</span>{selectedScene ? <ScenesInspector scene={selectedScene} /> : <EmptyModule title="Select a scene" text="The inspector will show screenplay source, continuity, world records, and shot readiness." />}</section></div>
    <form className="movie-add-scene-modern movie-scenes-add-form" onSubmit={(event) => { event.preventDefault(); void addScene(); }}><div><span className="movie-workspace-kicker">Manual production scene</span><h3>Keep editing possible</h3></div><label><span className="sr-only">Scene title</span><input value={newScene.title} onChange={(event) => setNewScene({ ...newScene, title: event.target.value })} placeholder="Scene title" /></label><label><span className="sr-only">Scene purpose</span><input value={newScene.summary} onChange={(event) => setNewScene({ ...newScene, summary: event.target.value })} placeholder="Description / purpose" /></label><label><span className="sr-only">Sequence</span><select value={newScene.sequenceId} onChange={(event) => setNewScene({ ...newScene, sequenceId: event.target.value })}><option value="">First sequence</option>{sequences.map((sequence) => <option key={sequence.id} value={sequence.id}>{sequence.sequence}. {sequence.title}</option>)}</select></label><button className="movie-workspace-button is-primary" type="submit" disabled={addingScene || !newScene.title.trim() || !newScene.summary.trim()}>{addingScene ? "Saving…" : "Add scene"}</button></form>
    {error && <div className="movie-workspace-error-inline"><XCircleIcon /> {error}</div>}
  </div>;
}
function SceneAct({ act, selectedSceneId, onSelect, onGenerate, onReorder }: { act: import("@/lib/api").MovieScenesAct; selectedSceneId: string | null; onSelect: (id: string) => void; onGenerate: (id: string) => Promise<void>; onReorder: (scene: MovieSceneWorkspace, delta: number) => Promise<void> }) {
  return <div className="movie-hierarchy-act"><div className="movie-hierarchy-label"><span>ACT {String(act.sequence).padStart(2, "0")}</span><strong>{act.title}</strong><small className={`movie-hierarchy-status is-${act.status.toLowerCase()}`}>{act.status}</small></div>{act.sequences.map((sequence) => <div className="movie-hierarchy-sequence" key={sequence.id}><div className="movie-hierarchy-sequence-head"><span>SEQ {String(sequence.sequence).padStart(2, "0")}</span><strong>{sequence.title}</strong><small>{sequence.scenes.length} scenes</small></div>{sequence.scenes.map((scene, index) => <SceneWorkspaceItem key={scene.id} scene={scene} isSelected={scene.id === selectedSceneId} canMoveUp={index > 0} canMoveDown={index < sequence.scenes.length - 1} onSelect={() => onSelect(scene.id)} onGenerate={() => void onGenerate(scene.id)} onReorder={(delta) => void onReorder(scene, delta)} />)}</div>)}</div>;
}
function SceneWorkspaceItem({ scene, isSelected, canMoveUp, canMoveDown, onSelect, onGenerate, onReorder }: { scene: MovieSceneWorkspace; isSelected: boolean; canMoveUp: boolean; canMoveDown: boolean; onSelect: () => void; onGenerate: () => void; onReorder: (delta: number) => void }) {
  return <article className={`movie-scene-list-item ${isSelected ? "is-selected" : ""}`}><button type="button" className="movie-scene-list-main" onClick={onSelect}><span className="movie-scene-sequence">{String(scene.sequence).padStart(2, "0")}</span><span><strong>{scene.title}</strong><small>{scene.slug}</small></span><ChevronRight size={14} /></button><div className="movie-scene-list-actions"><span className={`movie-scene-state ${scene.approvalState === "Linked" ? "is-ready" : ""}`}>{scene.approvalState === "Linked" ? <><Check size={12} /> Screenplay linked</> : "Production only"}</span><span className="movie-scene-shot-count">{scene.shotCount} shots</span><span className="movie-scene-order-actions"><button type="button" aria-label={`Move ${scene.title} up`} onClick={() => onReorder(-1)} disabled={!canMoveUp}><ArrowUp size={11} /></button><button type="button" aria-label={`Move ${scene.title} down`} onClick={() => onReorder(1)} disabled={!canMoveDown}><ArrowDown size={11} /></button></span><button type="button" className="movie-text-action" onClick={onGenerate}><Sparkles size={12} /> Generate</button></div></article>;
}
function ScenesInspector({ scene }: { scene: MovieSceneWorkspace }) {
  return <div className="movie-inspector-content"><div className="movie-scenes-inspector-topline"><span className="movie-scene-state is-ready">{scene.productionStatus}</span><span className="movie-scene-state">{scene.approvalState}</span></div><h3>{scene.title}</h3><p>{scene.description}</p><div className="movie-inspector-facts"><span><strong>{scene.shotCount}</strong> shots</span><span><strong>{scene.storyPosition}</strong> position</span></div><div className="movie-scenes-detail-grid"><RecordLine label="Slug / source" value={scene.slug} /><RecordLine label="Purpose" value={scene.purpose} /><RecordLine label="Characters" value={scene.characters.join(", ") || null} /><RecordLine label="Location" value={scene.locations.map((item) => item.name).join(", ") || null} /><RecordLine label="Set" value={scene.sets.map((item) => item.name).join(", ") || null} /><RecordLine label="Screenplay" value={scene.screenplaySource ? `${scene.screenplaySource}${scene.screenplayRevisionNumber ? ` · revision ${scene.screenplayRevisionNumber}` : ""}` : null} /></div>{scene.screenplaySynopsis && <div className="movie-scenes-source-note"><BookOpen size={14} /><p>{scene.screenplaySynopsis}</p></div>}{scene.continuityWarnings.length ? <div className="movie-scenes-warning-list"><span className="movie-inspector-label">Continuity warnings</span>{scene.continuityWarnings.map((warning) => <p key={warning}>{warning}</p>)}</div> : <div className="movie-scenes-clear-signal"><ShieldCheck size={14} /> No persisted continuity warnings</div>}</div>;
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
