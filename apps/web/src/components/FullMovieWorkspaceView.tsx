"use client";
/* eslint-disable @next/next/no-img-element -- production previews use authenticated API URLs with session cookies. */
import Link from "next/link";
import { useEffect, useMemo, useState, type ReactNode } from "react";
import {
  ArrowLeft,
  AudioLines,
  BookOpen,
  Check,
  ChevronRight,
  Clapperboard,
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
  Users,
  Workflow,
} from "lucide-react";
import { api, type MovieProductionVersion, type MovieProject, type MovieScene, type MovieShot, type MovieTake } from "@/lib/api";
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
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [selectedSceneId, setSelectedSceneId] = useState<string | null>(null);
  const [newScene, setNewScene] = useState({ title: "", summary: "" });
  const [addingScene, setAddingScene] = useState(false);

  useEffect(() => {
    let mounted = true;
    void api.getMovieProject(projectId).then((result) => {
      if (!mounted) return;
      setProject(result);
      setSelectedSceneId(result.scenes[0]?.id ?? null);
    }).catch((cause) => {
      if (mounted) setError(cause instanceof Error ? cause.message : "This movie project could not be loaded.");
    }).finally(() => {
      if (mounted) setLoading(false);
    });
    return () => { mounted = false; };
  }, [projectId]);

  const selectedScene = useMemo(() => project?.scenes.find((scene) => scene.id === selectedSceneId) ?? project?.scenes[0] ?? null, [project, selectedSceneId]);
  const readyClips = project?.clips.filter((clip) => hasReadyAsset(clip.status, clip.assetId)) ?? [];
  const outputAssetId = project?.assemblies.find((assembly) => hasReadyAsset(assembly.status, assembly.assetId))?.assetId ?? readyClips[0]?.assetId ?? null;
  const completionPercent = project ? Math.min(100, Math.round(((project.scenes.length ? readyClips.length : 0) / Math.max(project.scenes.length, 1)) * 100)) : 0;
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

  async function refreshProject() {
    const result = await api.getMovieProject(projectId);
    setProject(result);
  }

  if (loading) return <div className="movie-studio-page"><div className="movie-workspace-loading"><span className="loading-spinner" /><p>Loading the production workspace…</p></div></div>;
  if (!project) return <div className="movie-studio-page"><div className="movie-workspace-error"><XCircleIcon /><h1>Workspace unavailable</h1><p>{error || "This movie project is not available in the current workspace."}</p><Link href="/create/movie" className="movie-workspace-button is-primary"><ArrowLeft size={14} /> Back to Movie Studio</Link></div></div>;

  return (
    <div className="movie-studio-page movie-full-workspace">
      <header className="movie-workspace-header">
        <Link href="/create/movie" className="movie-workspace-back"><ArrowLeft size={14} /> Movie Studio</Link>
        <div className="movie-workspace-heading">
          <div>
            <span className="movie-workspace-kicker">Full Movie Project · {project.status}</span>
            <h1>{project.title}</h1>
            <p>{project.description}</p>
          </div>
          <div className="movie-workspace-meta"><span>{project.aspectRatio}</span><span>{formatDuration(project.durationSeconds)}</span><span>{project.style}</span></div>
        </div>
      </header>

      <div className="movie-workspace-layout">
        <nav className="movie-workspace-nav" aria-label="Full Movie Project navigation">
          <div className="movie-workspace-nav-label">Project map</div>
          <div className="movie-workspace-nav-list">
            {fullMovieModules.map((item) => {
              const Icon = item.icon;
              const href = `/create/movie/${project.id}/${item.slug}`;
              return <Link key={item.slug} href={href} className={`movie-workspace-nav-item ${activeModule === item.slug ? "is-active" : ""}`} aria-current={activeModule === item.slug ? "page" : undefined}><Icon size={15} /><span>{item.label}</span>{activeModule === item.slug && <ChevronRight size={13} />}</Link>;
            })}
          </div>
          <div className="movie-workspace-nav-foot"><span className="movie-live-dot" /> <span>Plan saved locally to this project</span></div>
        </nav>

        <main className="movie-workspace-main">
          <div className="movie-module-heading"><div><span className="movie-workspace-kicker">{copy.eyebrow}</span><h2>{copy.title}</h2><p>{copy.description}</p></div><span className="movie-module-index">{String(fullMovieModules.findIndex((item) => item.slug === activeModule) + 1).padStart(2, "0")} / 12</span></div>
          {activeModule === "overview" && <OverviewModule project={project} outputAssetId={outputAssetId} completionPercent={completionPercent} selectedScene={selectedScene} onSelectScene={setSelectedSceneId} />}
          {activeModule === "story" && <StoryModule project={project} />}
          {activeModule === "cast" && <CastModule project={project} />}
          {activeModule === "world" && <WorldModule project={project} />}
          {activeModule === "scenes" && <ScenesModule project={project} selectedSceneId={selectedScene?.id ?? null} newScene={newScene} addingScene={addingScene} onSelectScene={setSelectedSceneId} onChangeScene={setNewScene} onAddScene={() => void addScene()} onGenerate={generateScene} />}
          {activeModule === "storyboard" && <StoryboardModule project={project} />}
          {activeModule === "production" && <ProductionModule project={project} completionPercent={completionPercent} onRefresh={refreshProject} />}
          {activeModule === "edit" && <EditModule project={project} />}
          {activeModule === "audio" && <FutureModule icon={<AudioLines size={20} />} title="Audio is not connected yet" text="The sound stage is reserved for real narration, ambience, and music assets. Nothing is simulated here." />}
          {activeModule === "qc" && <FutureModule icon={<ShieldCheck size={20} />} title="QC is a future review gate" text="Continuity and delivery checks will appear once this project has a real cut to inspect." />}
          {activeModule === "exports" && <FutureModule icon={<Play size={20} />} title="Exports are not available yet" text="Final packaging stays unavailable until there is a reviewable project output." />}
          {activeModule === "team" && <FutureModule icon={<Users size={20} />} title="Team controls are not connected yet" text="This route is reserved for shared roles, review notes, and permissions. No access controls are implied by this shell." />}
          {error && <div className="movie-workspace-error-inline"><XCircleIcon /> {error}</div>}
        </main>

        <aside className="movie-director-panel">
          <div className="movie-director-heading"><span className="movie-workspace-kicker">Director / Inspector</span><SlidersHorizontal size={16} /></div>
          <div className="movie-director-section"><span className="movie-inspector-label">Current module</span><strong>{copy.title}</strong><p>{activeModule === "overview" ? "One place to see what is decided and what still needs a deliberate next step." : "Select a real project record to keep the next decision grounded."}</p></div>
          <div className="movie-director-section"><span className="movie-inspector-label">Continuity signal</span><div className="movie-inspector-meter"><span style={{ width: `${project.scenes.length ? Math.max(16, completionPercent) : 16}%` }} /></div><div className="movie-inspector-meter-meta"><span>{readyClips.length} ready clips</span><strong>{completionPercent}%</strong></div></div>
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

function StoryboardModule({ project }: { project: MovieProject }) {
  return <div className="movie-module-stack"><section className="movie-workspace-section"><div className="movie-section-head"><div><span className="movie-workspace-kicker">Filmstrip</span><h3>Scenes, not placeholders</h3></div><span className="movie-section-count">{project.scenes.length} frames planned</span></div>{project.scenes.length ? <div className="movie-filmstrip">{project.scenes.map((scene) => { const clip = readyClipForScene(scene); return <article className="movie-filmstrip-card" key={scene.id}>{clip?.assetId ? <video src={assetFileUrl(clip.assetId, true)} preload="metadata" muted aria-label={scene.title} /> : <div className="movie-filmstrip-placeholder"><Film size={19} /><span>No footage</span></div>}<div><span>{String(scene.sequence).padStart(2, "0")}</span><strong>{scene.title}</strong><small>{formatDuration(scene.durationSeconds)}</small></div></article>; })}</div> : <EmptyGeneratedStage title="Your storyboard is empty" text="Planned scenes will become the first visual pass here." />}</section><ModuleIntro icon={<Layers3 size={18} />} title="Storyboard foundation" text="This surface is ready for real frames. It will not manufacture thumbnails for scenes that have no generated footage." /></div>;
}

function ProductionModule({ project, completionPercent, onRefresh }: { project: MovieProject; completionPercent: number; onRefresh: () => Promise<void> }) {
  const [busyKey, setBusyKey] = useState("");
  const [actionError, setActionError] = useState("");
  const shots = project.scenes.flatMap((scene) => scene.shots.map((shot) => ({ scene, shot }))).sort((a, b) => a.scene.sequence - b.scene.sequence || a.shot.sequence - b.shot.sequence);
  const versions = shots.flatMap(({ shot }) => shot.productionVersions ?? []);
  const takes = shots.flatMap(({ shot }) => shot.takes ?? []);
  const ready = takes.filter((take) => Boolean(take.assetId) && ["Ready", "Approved"].includes(take.status)).length;
  const inFlight = versions.filter((version) => ["Pending", "Queued", "Running"].includes(version.execution?.status ?? "")).length;
  const failed = versions.filter((version) => version.execution?.status === "Failed").length;

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
      <div><span className="movie-workspace-kicker">Production signal</span><h3>{completionPercent}% of the current plan has a reviewable clip</h3><p>Production stays deliberate: approved keyframes lead to motion previews, renders, and canonical takes. Nothing renders because a shot exists.</p></div>
      <div className="movie-production-ring"><strong>{completionPercent}%</strong><span>ready</span></div>
    </section>
    <div className="movie-metric-row"><Metric label="Scenes" value={project.scenes.length} /><Metric label="MovieTakes" value={takes.length} /><Metric label="Reviewable takes" value={ready} /><Metric label="Render failures" value={failed} /></div>
    {actionError && <div className="movie-workspace-error-inline" role="alert"><XCircleIcon /> {actionError}</div>}
    <section className="movie-production-board">
      <div className="movie-section-head"><div><span className="movie-workspace-kicker">Scene / shot rail</span><h3>Production candidates and canonical takes</h3></div><span className="movie-section-count">{versions.length} versions · {inFlight} active</span></div>
      {shots.length ? <div className="movie-production-groups">{shots.map(({ scene, shot }) => <ProductionShotGroup key={shot.id} sceneTitle={scene.title} sceneSequence={scene.sequence} shot={shot} busyKey={busyKey} onAction={action} />)}</div> : <EmptyModule title="No shots are planned yet" text="Add a scene and shot before opening a production action." />}
    </section>
    <ModuleIntro icon={<ShieldCheck size={18} />} title="Shared Wave 5 controls remain in charge" text="Generation Jobs, cost guardrails, provider attempts, resilience, QC, Assets, and the Usage Ledger remain the only execution path. A failed provider attempt is visible here, never replaced by fake footage." />
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
    <div className="movie-production-shot-heading"><div><span className="movie-production-scene-label">Scene {String(sceneSequence).padStart(2, "0")} · {sceneTitle}</span><h4>Shot {String(shot.sequence).padStart(2, "0")}</h4><p>{shot.description}</p></div><span className="movie-stage-pill">{shot.productionStage}</span></div>
    <div className="movie-production-lifecycle">
      <ProductionCheckpoint label="Keyframe" state={approvedKeyframe ? "Approved" : keyframe ? "Pending approval" : "Not started"} tone={approvedKeyframe ? "ready" : keyframe ? "pending" : "quiet"} />
      <ProductionCheckpoint label="Motion preview" state={motionPreview?.status ?? "Not started"} tone={motionPreview ? motionPreview.status === "Approved" ? "ready" : "pending" : "quiet"} />
      <ProductionCheckpoint label="Production render" state={render?.execution?.status ?? "Not started"} tone={render?.execution?.status === "Succeeded" ? "ready" : render?.execution?.status === "Failed" ? "failed" : render ? "pending" : "quiet"} />
      <ProductionCheckpoint label="MovieTake" state={takes.length ? `${takes.length} canonical` : "Not created"} tone={takes.length ? "ready" : "quiet"} />
    </div>
    <div className="movie-production-action-row">
      {keyframe && <button type="button" className="movie-workspace-button is-primary" disabled={Boolean(busyKey)} onClick={() => void onAction(`${shot.id}:approve-keyframe`, () => api.reviewMovieProductionVersion(keyframe.id, { approve: true, reason: "Keyframe approved in Production." }))}>{isBusy("approve-keyframe") ? "Approving…" : "Approve keyframe"}</button>}
      {approvedKeyframe && !motionPreview && <button type="button" className="movie-workspace-button is-secondary" disabled={Boolean(busyKey)} onClick={() => void onAction(`${shot.id}:motion-preview`, () => api.createMovieMotionPreview(shot.id, { sourceVersionId: approvedKeyframe.id, label: "Motion preview" }))}>{isBusy("motion-preview") ? "Preparing…" : "Create motion preview"}</button>}
      {motionPreview?.status === "PendingApproval" && <button type="button" className="movie-workspace-button is-primary" disabled={Boolean(busyKey)} onClick={() => void onAction(`${shot.id}:approve-motion`, () => api.reviewMovieProductionVersion(motionPreview.id, { approve: true, reason: "Motion preview approved in Production." }))}>{isBusy("approve-motion") ? "Approving…" : "Approve motion preview"}</button>}
      {motionPreview?.status === "Approved" && (!render || render.execution?.status === "Failed") && <button type="button" className="movie-workspace-button is-secondary" disabled={Boolean(busyKey)} onClick={() => void onAction(`${shot.id}:render`, () => api.queueMovieProductionRender(shot.id, { sourceVersionId: motionPreview.id, label: render ? "Render retry" : "Production render" }))}>{isBusy("render") ? "Queueing…" : render ? "Retry render" : "Start production render"}</button>}
      {render?.execution?.status === "Succeeded" && !takes.some((take) => take.generationJobId === render.generationJobId) && <button type="button" className="movie-workspace-button is-primary" disabled={Boolean(busyKey)} onClick={() => void onAction(`${shot.id}:take`, () => api.createMovieTakeFromProduction(render.id, { label: "Rendered take" }))}>{isBusy("take") ? "Saving…" : "Create MovieTake"}</button>}
    </div>
    {versions.length ? <div className="movie-production-version-list"><span className="movie-production-subhead">Version history</span>{versions.map((version) => <ProductionVersionCard key={version.id} version={version} shot={shot} busyKey={busyKey} onAction={onAction} />)}</div> : <div className="movie-production-empty-line">No production versions yet. Start from an approved storyboard/keyframe hand-off.</div>}
    <div className="movie-production-takes"><div className="movie-production-subhead-row"><span className="movie-production-subhead">Canonical MovieTake</span><span className="movie-take-definition">artifact candidates stay separate from rendered takes</span></div>{takes.length ? takes.map((take) => <MovieTakeCard key={take.id} take={take} shot={shot} busyKey={busyKey} onAction={onAction} />) : <div className="movie-production-empty-line">A MovieTake appears only after a real render has produced a private Asset.</div>}</div>
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
  return <div className="movie-production-version-card"><div className="movie-production-version-top"><div><span className="movie-production-version-number">v{version.versionNumber} · {version.stage}</span><strong>{version.label || "Untitled candidate"}</strong></div><span className={`movie-stage-pill is-${version.status.toLowerCase()}`}>{version.status}</span></div>{outputAssetId ? outputIsVideo ? <video className="movie-production-output" src={assetFileUrl(outputAssetId, true)} controls preload="metadata" aria-label={`${version.stage} output`} /> : <img className="movie-production-output" src={assetFileUrl(outputAssetId, true)} alt={`${version.stage} output`} /> : <div className="movie-production-preview-empty"><Film size={15} /><span>{execution ? `${execution.status} · ${execution.progressPercent}%` : "No generated output"}</span></div>}{execution && <ProductionExecutionSummary execution={execution} />}{version.rejectionReason && <div className="movie-production-failure"><XCircleIcon /> {version.rejectionReason}</div>}{version.stage === "ProductionKeyframe" && version.status === "PendingApproval" && <button type="button" className="movie-text-action" disabled={Boolean(busyKey)} onClick={() => void onAction(`${shot.id}:approve-keyframe`, () => api.reviewMovieProductionVersion(version.id, { approve: true, reason: "Keyframe approved in Production." }))}>{isBusy("approve-keyframe") ? "Approving…" : "Approve keyframe"}</button>}</div>;
}

function ProductionExecutionSummary({ execution }: { execution: NonNullable<MovieProductionVersion["execution"]> }) {
  const failures = execution.attempts.filter((attempt) => attempt.failureCode || attempt.qualityControlRejected);
  return <div className="movie-production-execution"><div><span>Job</span><strong>{execution.status} · {execution.progressPercent}%</strong></div><div><span>QC</span><strong>{execution.qualityControlStatus}</strong></div><div><span>Retries</span><strong>{execution.retryCount} · {execution.attemptCount} attempts</strong></div>{(execution.errorCode || failures.length > 0) && <div className="movie-production-failure-detail"><span>Failure / resilience</span><strong>{execution.errorCode || failures.at(-1)?.failureCode || "Provider attempt failed"}</strong>{execution.errorMessage && <small>{execution.errorMessage}</small>}</div>}</div>;
}

function MovieTakeCard({ take, shot, busyKey, onAction }: { take: MovieTake; shot: MovieShot; busyKey: string; onAction: (key: string, work: () => Promise<unknown>) => Promise<void> }) {
  const isBusy = (action: string) => busyKey === `${shot.id}:${action}`;
  const selected = Boolean(take.selectedAt);
  const finalized = Boolean(take.finalizedAt);
  return <div className={`movie-take-card ${selected ? "is-selected" : ""} ${finalized ? "is-final" : ""}`}><div className="movie-take-copy"><span>MovieTake v{take.versionNumber}</span><strong>{take.label}</strong><small>{take.status} · {take.qualityLevel}{selected ? " · Selected" : ""}{finalized ? " · Final" : ""}</small></div>{take.assetId ? <video className="movie-take-video" src={assetFileUrl(take.assetId, true)} controls preload="metadata" aria-label={take.label} /> : <div className="movie-production-preview-empty"><Film size={15} /><span>Private output pending</span></div>}<div className="movie-take-actions">{take.status !== "Approved" && take.status !== "Rejected" && <button type="button" className="movie-text-action" disabled={Boolean(busyKey)} onClick={() => void onAction(`${shot.id}:approve-take`, () => api.approveMovieTake(take.id, { decision: "Approved", comment: "Take approved in Production." }))}>{isBusy("approve-take") ? "Approving…" : "Approve take"}</button>}{!selected && <button type="button" className="movie-text-action" disabled={Boolean(busyKey) || take.status === "Rejected"} onClick={() => void onAction(`${shot.id}:select-take`, () => api.selectMovieTake(take.id))}>{isBusy("select-take") ? "Selecting…" : "Select take"}</button>}{!finalized && <button type="button" className="movie-workspace-button is-primary" disabled={Boolean(busyKey) || take.status !== "Approved"} onClick={() => void onAction(`${shot.id}:finalize-take`, () => api.finalizeMovieTake(take.id))}>{isBusy("finalize-take") ? "Finalizing…" : "Finalize take"}</button>}</div></div>;
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
