"use client";
/* eslint-disable @next/next/no-img-element -- storyboard previews use authenticated Asset URLs. */

import Link from "next/link";
import { useEffect, useMemo, useState, type ReactNode } from "react";
import {
  ArrowLeft,
  AudioLines,
  AlertTriangle,
  BookOpen,
  Check,
  CheckCircle2,
  ChevronRight,
  Clapperboard,
  Film,
  Gauge,
  ImageOff,
  Layers3,
  ListChecks,
  Map,
  PencilRuler,
  Play,
  ShieldCheck,
  SlidersHorizontal,
  Sparkles,
  X,
  Users,
  Workflow,
} from "lucide-react";
import { api, type MovieProject, type MovieScene, type MovieStoryboardCandidate, type MovieStoryboardProject, type MovieStoryboardScene, type MovieStoryboardShot } from "@/lib/api";
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
  const [storyboard, setStoryboard] = useState<MovieStoryboardProject | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [selectedSceneId, setSelectedSceneId] = useState<string | null>(null);
  const [newScene, setNewScene] = useState({ title: "", summary: "" });
  const [addingScene, setAddingScene] = useState(false);

  useEffect(() => {
    let mounted = true;
    const load = activeModule === "storyboard" ? api.getMovieStoryboard(projectId) : api.getMovieProject(projectId);
    void load.then((result) => {
      if (!mounted) return;
      if (activeModule === "storyboard") {
        setStoryboard(result as MovieStoryboardProject);
      } else {
        setProject(result as MovieProject);
      }
      setSelectedSceneId(result.scenes[0]?.id ?? null);
    }).catch((cause) => {
      if (mounted) setError(cause instanceof Error ? cause.message : "This movie project could not be loaded.");
    }).finally(() => {
      if (mounted) setLoading(false);
    });
    return () => { mounted = false; };
  }, [activeModule, projectId]);

  const workspaceProject = project ?? storyboard;
  const selectedScene = useMemo(() => workspaceProject?.scenes.find((scene) => scene.id === selectedSceneId) ?? workspaceProject?.scenes[0] ?? null, [workspaceProject, selectedSceneId]);
  const readyClips = project?.clips.filter((clip) => hasReadyAsset(clip.status, clip.assetId)) ?? [];
  const outputAssetId = project?.assemblies.find((assembly) => hasReadyAsset(assembly.status, assembly.assetId))?.assetId ?? readyClips[0]?.assetId ?? null;
  const storyboardShots = storyboard?.scenes.flatMap((scene) => scene.shots) ?? [];
  const completionPercent = project
    ? Math.min(100, Math.round(((project.scenes.length ? readyClips.length : 0) / Math.max(project.scenes.length, 1)) * 100))
    : storyboardShots.length ? Math.round((storyboardShots.filter((shot) => shot.approvalStatus === "Approved").length / storyboardShots.length) * 100) : 0;
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

  async function refreshStoryboard() {
    const next = await api.getMovieStoryboard(projectId);
    setStoryboard(next);
  }

  if (loading) return <div className="movie-studio-page"><div className="movie-workspace-loading"><span className="loading-spinner" /><p>Loading the production workspace…</p></div></div>;
  if (!workspaceProject) return <div className="movie-studio-page"><div className="movie-workspace-error"><XCircleIcon /><h1>Workspace unavailable</h1><p>{error || "This movie project is not available in the current workspace."}</p><Link href="/create/movie" className="movie-workspace-button is-primary"><ArrowLeft size={14} /> Back to Movie Studio</Link></div></div>;

  return (
    <div className="movie-studio-page movie-full-workspace">
      <header className="movie-workspace-header">
        <Link href="/create/movie" className="movie-workspace-back"><ArrowLeft size={14} /> Movie Studio</Link>
        <div className="movie-workspace-heading">
          <div>
            <span className="movie-workspace-kicker">Full Movie Project · {workspaceProject.status}</span>
            <h1>{workspaceProject.title}</h1>
            <p>{workspaceProject.description}</p>
          </div>
          <div className="movie-workspace-meta"><span>{workspaceProject.aspectRatio}</span><span>{formatDuration(workspaceProject.durationSeconds)}</span><span>{workspaceProject.style}</span></div>
        </div>
      </header>

      <div className="movie-workspace-layout">
        <nav className="movie-workspace-nav" aria-label="Full Movie Project navigation">
          <div className="movie-workspace-nav-label">Project map</div>
          <div className="movie-workspace-nav-list">
            {fullMovieModules.map((item) => {
              const Icon = item.icon;
              const href = activeModule === "storyboard" ? `/create/movie/${storyboard!.id}/${item.slug}` : `/create/movie/${project!.id}/${item.slug}`;
              return <Link key={item.slug} href={href} className={`movie-workspace-nav-item ${activeModule === item.slug ? "is-active" : ""}`} aria-current={activeModule === item.slug ? "page" : undefined}><Icon size={15} /><span>{item.label}</span>{activeModule === item.slug && <ChevronRight size={13} />}</Link>;
            })}
          </div>
          <div className="movie-workspace-nav-foot"><span className="movie-live-dot" /> <span>Plan saved locally to this project</span></div>
        </nav>

        <main className="movie-workspace-main">
          <div className="movie-module-heading"><div><span className="movie-workspace-kicker">{copy.eyebrow}</span><h2>{copy.title}</h2><p>{copy.description}</p></div><span className="movie-module-index">{String(fullMovieModules.findIndex((item) => item.slug === activeModule) + 1).padStart(2, "0")} / 12</span></div>
          {activeModule === "overview" && project && <OverviewModule project={project} outputAssetId={outputAssetId} completionPercent={completionPercent} selectedScene={selectedScene as MovieScene | null} onSelectScene={setSelectedSceneId} />}
          {activeModule === "story" && project && <StoryModule project={project} />}
          {activeModule === "cast" && project && <CastModule project={project} />}
          {activeModule === "world" && project && <WorldModule project={project} />}
          {activeModule === "scenes" && project && <ScenesModule project={project} selectedSceneId={selectedScene?.id ?? null} newScene={newScene} addingScene={addingScene} onSelectScene={setSelectedSceneId} onChangeScene={setNewScene} onAddScene={() => void addScene()} onGenerate={generateScene} />}
          {activeModule === "storyboard" && storyboard && <StoryboardModule storyboard={storyboard} onRefresh={() => void refreshStoryboard()} onError={setError} />}
          {activeModule === "production" && project && <ProductionModule project={project} completionPercent={completionPercent} />}
          {activeModule === "edit" && project && <EditModule project={project} />}
          {activeModule === "audio" && <FutureModule icon={<AudioLines size={20} />} title="Audio is not connected yet" text="The sound stage is reserved for real narration, ambience, and music assets. Nothing is simulated here." />}
          {activeModule === "qc" && <FutureModule icon={<ShieldCheck size={20} />} title="QC is a future review gate" text="Continuity and delivery checks will appear once this project has a real cut to inspect." />}
          {activeModule === "exports" && <FutureModule icon={<Play size={20} />} title="Exports are not available yet" text="Final packaging stays unavailable until there is a reviewable project output." />}
          {activeModule === "team" && <FutureModule icon={<Users size={20} />} title="Team controls are not connected yet" text="This route is reserved for shared roles, review notes, and permissions. No access controls are implied by this shell." />}
          {error && <div className="movie-workspace-error-inline"><XCircleIcon /> {error}</div>}
        </main>

        <aside className="movie-director-panel">
          <div className="movie-director-heading"><span className="movie-workspace-kicker">Director / Inspector</span><SlidersHorizontal size={16} /></div>
          <div className="movie-director-section"><span className="movie-inspector-label">Current module</span><strong>{copy.title}</strong><p>{activeModule === "overview" ? "One place to see what is decided and what still needs a deliberate next step." : "Select a real project record to keep the next decision grounded."}</p></div>
          <div className="movie-director-section"><span className="movie-inspector-label">Continuity signal</span><div className="movie-inspector-meter"><span style={{ width: `${workspaceProject.scenes.length ? Math.max(16, completionPercent) : 16}%` }} /></div><div className="movie-inspector-meter-meta"><span>{activeModule === "storyboard" ? `${storyboardShots.length} shots planned` : `${readyClips.length} ready clips`}</span><strong>{completionPercent}%</strong></div></div>
          {selectedScene ? <div className="movie-director-section"><span className="movie-inspector-label">Selected scene</span><strong>{String(selectedScene.sequence).padStart(2, "0")} · {selectedScene.title}</strong><p>{selectedScene.summary}</p><span className="movie-inspector-detail">{formatDuration(selectedScene.durationSeconds)} · {selectedScene.shots.length} shots planned</span></div> : <div className="movie-director-empty"><Film size={18} /><p>Select a scene to inspect its intent and continuity notes.</p></div>}
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

function StoryboardModule({ storyboard, onRefresh, onError }: { storyboard: MovieStoryboardProject; onRefresh: () => void; onError: (message: string) => void }) {
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
