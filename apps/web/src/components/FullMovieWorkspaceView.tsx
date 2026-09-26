"use client";

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
  Image as ImageIcon,
  Layers3,
  ListChecks,
  LockKeyhole,
  Map,
  PencilRuler,
  Play,
  Plus,
  Save,
  ShieldCheck,
  SlidersHorizontal,
  Sparkles,
  Users,
  Workflow,
} from "lucide-react";
import { api, type Asset, type MovieCast, type MovieCharacter, type MovieCharacterState, type MovieProject, type MovieScene } from "@/lib/api";
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

function projectShellFromCast(cast: MovieCast): MovieProject {
  return { ...cast.project, additionalInstructions: null, guide: { id: "cast-room", visualLanguage: "", cameraLanguage: "", colorAndLighting: "", soundAndNarration: "", continuityRules: "", updatedAt: cast.project.updatedAt }, scenes: [], characters: [], locations: [], clips: [], assemblies: [], world: { locations: [], sets: [], props: [], references: [], usages: [], facts: [], locks: [] } };
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
    const load = activeModule === "cast" ? api.getMovieCast(projectId).then(projectShellFromCast) : api.getMovieProject(projectId);
    void load.then((result) => {
      if (!mounted) return;
      setProject(result);
      setSelectedSceneId(result.scenes[0]?.id ?? null);
    }).catch((cause) => {
      if (mounted) setError(cause instanceof Error ? cause.message : "This movie project could not be loaded.");
    }).finally(() => {
      if (mounted) setLoading(false);
    });
    return () => { mounted = false; };
  }, [activeModule, projectId]);

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
          {activeModule === "cast" && <CastModule projectId={project.id} />}
          {activeModule === "world" && <WorldModule project={project} />}
          {activeModule === "scenes" && <ScenesModule project={project} selectedSceneId={selectedScene?.id ?? null} newScene={newScene} addingScene={addingScene} onSelectScene={setSelectedSceneId} onChangeScene={setNewScene} onAddScene={() => void addScene()} onGenerate={generateScene} />}
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
