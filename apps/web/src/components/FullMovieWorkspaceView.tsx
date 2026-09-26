"use client";

import Link from "next/link";
import { useEffect, useMemo, useState, type ReactNode } from "react";
import {
  ArrowDown,
  ArrowLeft,
  ArrowUp,
  AudioLines,
  BookOpen,
  Check,
  ChevronRight,
  Clapperboard,
  Film,
  Gauge,
  Layers3,
  Map,
  PencilRuler,
  Play,
  ShieldCheck,
  SlidersHorizontal,
  Sparkles,
  Users,
  Workflow,
} from "lucide-react";
import { api, type MovieProject, type MovieScene, type MovieSceneWorkspace, type MovieScenesWorkspace } from "@/lib/api";
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
          {activeModule === "scenes" && <ScenesModule projectId={project.id} selectedSceneId={selectedScene?.id ?? null} onSelectScene={setSelectedSceneId} onGenerate={generateScene} />}
          {activeModule === "storyboard" && <StoryboardModule project={project} />}
          {activeModule === "production" && <ProductionModule project={project} completionPercent={completionPercent} />}
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
