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
  Layers3,
  ListChecks,
  LockKeyhole,
  Map,
  PencilRuler,
  Plus,
  Play,
  Save,
  ShieldCheck,
  SlidersHorizontal,
  Sparkles,
  Trash2,
  Users,
  Workflow,
} from "lucide-react";
import { api, type MovieProject, type MovieProjectShell, type MovieScene, type MovieScreenplayElementType, type MovieStory, type MovieStoryRevision, type MovieStoryRevisionInput } from "@/lib/api";
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
  const [projectShell, setProjectShell] = useState<MovieProjectShell | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [selectedSceneId, setSelectedSceneId] = useState<string | null>(null);
  const [newScene, setNewScene] = useState({ title: "", summary: "" });
  const [addingScene, setAddingScene] = useState(false);

  useEffect(() => {
    let mounted = true;
    const load = activeModule === "story" ? api.getMovieProjectShell(projectId) : api.getMovieProject(projectId);
    void load.then((result) => {
      if (!mounted) return;
      if (activeModule === "story") setProjectShell(result as MovieProjectShell);
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
  const workspace = project ?? projectShell;
  const fullProject = project as MovieProject;
  if (!workspace) return <div className="movie-studio-page"><div className="movie-workspace-error"><XCircleIcon /><h1>Workspace unavailable</h1><p>{error || "This movie project is not available in the current workspace."}</p><Link href="/create/movie" className="movie-workspace-button is-primary"><ArrowLeft size={14} /> Back to Movie Studio</Link></div></div>;

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
          {activeModule === "overview" && <OverviewModule project={fullProject} outputAssetId={outputAssetId} completionPercent={completionPercent} selectedScene={selectedScene} onSelectScene={setSelectedSceneId} />}
          {activeModule === "story" && <StoryModule projectId={workspace.id} />}
          {activeModule === "cast" && <CastModule project={fullProject} />}
          {activeModule === "world" && <WorldModule project={fullProject} />}
          {activeModule === "scenes" && <ScenesModule project={fullProject} selectedSceneId={selectedScene?.id ?? null} newScene={newScene} addingScene={addingScene} onSelectScene={setSelectedSceneId} onChangeScene={setNewScene} onAddScene={() => void addScene()} onGenerate={generateScene} />}
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
          <div className="movie-director-section"><span className="movie-inspector-label">Continuity signal</span><div className="movie-inspector-meter"><span style={{ width: `${project?.scenes.length ? Math.max(16, completionPercent) : 16}%` }} /></div><div className="movie-inspector-meter-meta"><span>{readyClips.length} ready clips</span><strong>{completionPercent}%</strong></div></div>
          {activeModule === "story" ? <div className="movie-director-section"><span className="movie-inspector-label">Writing focus</span><strong>Approved story is the production source</strong><p>Drafts stay separate until a reviewer approves them. Director and breakdown surfaces should use the approved revision when one exists.</p></div> : selectedScene ? <div className="movie-director-section"><span className="movie-inspector-label">Selected scene</span><strong>{String(selectedScene.sequence).padStart(2, "0")} · {selectedScene.title}</strong><p>{selectedScene.summary}</p><span className="movie-inspector-detail">{formatDuration(selectedScene.durationSeconds)} · {selectedScene.shots.length} shots planned</span></div> : <div className="movie-director-empty"><Film size={18} /><p>Select a scene to inspect its intent and continuity notes.</p></div>}
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
