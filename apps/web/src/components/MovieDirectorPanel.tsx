"use client";

import { useEffect, useMemo, useState } from "react";
import {
  AlertTriangle,
  Check,
  ChevronDown,
  Clock3,
  History,
  Lightbulb,
  LockKeyhole,
  Play,
  RotateCcw,
  Send,
  ShieldAlert,
  Sparkles,
  Target,
  X,
} from "lucide-react";
import {
  api,
  type DirectorAction,
  type DirectorHistoryEvent,
  type DirectorProposal,
  type DirectorQualityLevel,
  type DirectorProposalResponse,
  type MovieProject,
  type MovieScene,
  type MovieShot,
} from "@/lib/api";
import {
  directorActionTypeLabel,
  directorHistoryLabel,
  directorProposalStatusLabel,
  directorRoomForModule,
  type DirectorRoom,
} from "@/lib/movieDirector";

type DirectorPanelProps = {
  project: MovieProject;
  activeModule: string;
  selectedScene: MovieScene | null;
  selectedShot: MovieShot | null;
  onProjectRefresh: () => Promise<void>;
};

type SignalTone = "next" | "warning" | "suggestion" | "approval" | "status";

type Signal = {
  tone: SignalTone;
  label: string;
  text: string;
};

const qualityLevels: DirectorQualityLevel[] = ["Fast", "Standard", "Cinematic", "Studio"];

function qualityLabel(level: string) {
  return level === "Studio" ? "Studio" : level;
}

function actionForProposal(proposal: DirectorProposal | null): DirectorAction | null {
  return proposal?.actions[0] ?? null;
}

function timeLabel(value: string) {
  return new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" }).format(new Date(value));
}

function signalIcon(tone: SignalTone) {
  if (tone === "warning") return <AlertTriangle size={13} />;
  if (tone === "suggestion") return <Lightbulb size={13} />;
  if (tone === "approval") return <ShieldAlert size={13} />;
  if (tone === "status") return <Clock3 size={13} />;
  return <Target size={13} />;
}

function roomTarget(room: DirectorRoom, project: MovieProject, selectedScene: MovieScene | null, selectedShot: MovieShot | null) {
  switch (room) {
    case "Story":
      return { label: project.title, detail: "creative brief and continuity guide" };
    case "Cast":
      return { label: selectedScene?.title ?? "Cast continuity", detail: `${project.characters.length} character records` };
    case "World":
      return { label: selectedScene?.title ?? "World continuity", detail: `${project.locations.length} location records` };
    case "Scene":
      return selectedScene ? { label: `${String(selectedScene.sequence).padStart(2, "0")} · ${selectedScene.title}`, detail: `${selectedScene.shots.length} shots planned` } : { label: "No scene selected", detail: "Choose a scene from the production map" };
    case "Shot":
      return selectedShot ? { label: `Shot ${String(selectedShot.sequence).padStart(2, "0")}`, detail: selectedShot.description } : { label: "No shot selected", detail: "Choose or add a shot in the Scene room" };
    case "Storyboard":
      return selectedScene ? { label: selectedScene.title, detail: "storyboard candidates and visual continuity" } : { label: "Storyboard pass", detail: "scene-level visual plan" };
    case "Production":
      return selectedShot ? { label: `Shot ${String(selectedShot.sequence).padStart(2, "0")}`, detail: selectedShot.productionStage } : { label: project.title, detail: "production readiness" };
  }
}

function getSignals(room: DirectorRoom, project: MovieProject, selectedScene: MovieScene | null, selectedShot: MovieShot | null, proposal: DirectorProposal | null, completionPercent: number): Signal[] {
  const signals: Signal[] = [];
  const action = actionForProposal(proposal);
  if (!project.guide.lockedRevisionNumber) signals.push({ tone: "warning", label: "Warning", text: "Lock the current Movie Guide before the Director can assemble authoritative context." });
  if (!project.scenes.length) signals.push({ tone: "warning", label: "Warning", text: "Add a scene before asking the Director for a shot proposal." });
  else if (!project.scenes.some((scene) => scene.shots.length)) signals.push({ tone: "warning", label: "Warning", text: "Add at least one shot in the Scene room. Proposals target real shots only." });
  if (proposal?.status === "PendingApproval") signals.push({ tone: "approval", label: "Approval required", text: "Review the typed proposal below. Nothing will execute until you approve it." });
  else if (proposal?.status === "Approved" && action?.status === "Ready") signals.push({ tone: "next", label: "Next action", text: "The proposal is approved. Execute the ready action when you want to queue it." });
  else if (action?.status === "Succeeded") signals.push({ tone: "status", label: "Production status", text: "The Director action completed and its result is recorded in history." });
  else if (room === "Story") signals.push({ tone: "next", label: "Next action", text: "Lock the guide when the creative rules are ready to travel with the production." });
  else if (room === "Cast" && !project.characters.length) signals.push({ tone: "suggestion", label: "Suggestion", text: "Define the first character so continuity decisions have a durable anchor." });
  else if (room === "World" && !project.locations.length) signals.push({ tone: "suggestion", label: "Suggestion", text: "Define a location before planning shots that depend on a consistent world." });
  else if (room === "Scene" && selectedScene && !selectedScene.shots.length) signals.push({ tone: "next", label: "Next action", text: "Add a shot to this scene to make the Director's proposal targetable." });
  else if (room === "Shot" && selectedShot) signals.push({ tone: "suggestion", label: "Suggestion", text: "Use Auto Director to recommend a quality level from importance, complexity, and continuity sensitivity." });
  else if (room === "Storyboard") signals.push({ tone: "suggestion", label: "Suggestion", text: "Review the visual plan before moving a candidate into production." });
  else if (room === "Production") signals.push({ tone: "status", label: "Production status", text: `${completionPercent}% of the current plan has a reviewable clip.` });
  return signals.slice(0, 3);
}

export function MovieDirectorPanel({ project, activeModule, selectedScene, selectedShot, onProjectRefresh }: DirectorPanelProps) {
  const room = directorRoomForModule(activeModule);
  const target = roomTarget(room, project, selectedScene, selectedShot);
  const [proposal, setProposal] = useState<DirectorProposalResponse | null>(null);
  const [history, setHistory] = useState<DirectorHistoryEvent[]>([]);
  const [loading, setLoading] = useState(true);
  const [historyOpen, setHistoryOpen] = useState(false);
  const [working, setWorking] = useState<"proposal" | "approve" | "reject" | "execute" | "lock" | null>(null);
  const [error, setError] = useState("");
  const [goal, setGoal] = useState("Prepare the current shot for review");
  const [autoDirector, setAutoDirector] = useState(true);
  const [quality, setQuality] = useState<DirectorQualityLevel>("Cinematic");
  const [importance, setImportance] = useState(70);
  const [complexity, setComplexity] = useState(60);
  const [budgetSensitivity, setBudgetSensitivity] = useState(50);

  const completionPercent = Math.min(100, Math.round(((project.scenes.length ? project.clips.filter((clip) => Boolean(clip.assetId) && ["Completed", "Succeeded", "Ready"].includes(clip.status)).length : 0) / Math.max(project.scenes.length, 1)) * 100));
  const signals = useMemo(() => getSignals(room, project, selectedScene, selectedShot, proposal?.proposal ?? null, completionPercent), [room, project, selectedScene, selectedShot, proposal, completionPercent]);
  const proposalAction = actionForProposal(proposal?.proposal ?? null);
  const planItem = proposal?.proposal.plan[0] ?? null;
  const canPropose = Boolean(selectedShot && project.guide.lockedRevisionNumber && !working);

  useEffect(() => {
    let mounted = true;
    void Promise.all([
      api.getMovieDirectorHistory(project.id),
      typeof window === "undefined" ? Promise.resolve<string | null>(null) : Promise.resolve(window.sessionStorage.getItem(`taslim:movie-director:proposal:${project.id}`)),
    ]).then(async ([events, proposalId]) => {
      if (!mounted) return;
      setHistory(events);
      if (proposalId) {
        try {
          const current = await api.getMovieDirectorProposal(proposalId);
          if (mounted) setProposal(current);
        } catch {
          if (typeof window !== "undefined") window.sessionStorage.removeItem(`taslim:movie-director:proposal:${project.id}`);
        }
      }
    }).catch((cause) => {
      if (mounted) setError(cause instanceof Error ? cause.message : "Director history is unavailable.");
    }).finally(() => {
      if (mounted) setLoading(false);
    });
    return () => { mounted = false; };
  }, [project.id]);

  async function createProposal() {
    if (!selectedShot) {
      setError("Select or add a shot before creating a Director proposal.");
      return;
    }
    setWorking("proposal");
    setError("");
    try {
      const next = await api.createMovieDirectorProposal(project.id, {
        shotId: selectedShot.id,
        goal: goal.trim() || null,
        requestedQuality: autoDirector ? "Auto" : quality,
        budgetLimitUsd: null,
        importance,
        complexity,
        budgetSensitivity,
      });
      setProposal(next);
      if (typeof window !== "undefined") window.sessionStorage.setItem(`taslim:movie-director:proposal:${project.id}`, next.proposal.id);
      setHistory(await api.getMovieDirectorHistory(project.id));
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "The Director proposal could not be created.");
    } finally {
      setWorking(null);
    }
  }

  async function approveProposal() {
    if (!proposal) return;
    setWorking("approve");
    setError("");
    try {
      const next = await api.approveMovieDirectorProposal(proposal.proposal.id);
      setProposal({ ...proposal, proposal: next });
      setHistory(await api.getMovieDirectorHistory(project.id));
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "The proposal could not be approved.");
    } finally {
      setWorking(null);
    }
  }

  async function rejectProposal() {
    if (!proposal) return;
    setWorking("reject");
    setError("");
    try {
      const next = await api.rejectMovieDirectorProposal(proposal.proposal.id);
      setProposal({ ...proposal, proposal: next });
      setHistory(await api.getMovieDirectorHistory(project.id));
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "The proposal could not be rejected.");
    } finally {
      setWorking(null);
    }
  }

  async function executeAction() {
    if (!proposalAction) return;
    setWorking("execute");
    setError("");
    try {
      const result = await api.executeMovieDirectorAction(proposalAction.id);
      setProposal((current) => current ? { ...current, proposal: { ...current.proposal, actions: current.proposal.actions.map((action) => action.id === result.action.id ? result.action : action) } } : current);
      setHistory(await api.getMovieDirectorHistory(project.id));
      await onProjectRefresh();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "The Director action could not be executed.");
    } finally {
      setWorking(null);
    }
  }

  async function lockGuide() {
    setWorking("lock");
    setError("");
    try {
      await api.lockMovieGuide(project.id, project.guide.currentRevisionNumber ?? null);
      await onProjectRefresh();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "The Movie Guide could not be locked.");
    } finally {
      setWorking(null);
    }
  }

  return (
    <aside className="movie-director-panel" aria-label="Taslim Movie Director">
      <div className="movie-director-heading">
        <div><span className="movie-workspace-kicker">Director / Inspector</span><strong>Taslim Movie Director</strong></div>
        <Sparkles size={16} />
      </div>
      <div className="movie-director-context">
        <div><span className="movie-inspector-label">Current room</span><strong>{room}</strong></div>
        <div><span className="movie-inspector-label">Target</span><strong>{target.label}</strong><small>{target.detail}</small></div>
      </div>
      {signals.map((signal) => <div className={`movie-director-signal is-${signal.tone}`} key={`${signal.tone}-${signal.text}`}><span>{signalIcon(signal.tone)}</span><p><strong>{signal.label}</strong>{signal.text}</p></div>)}
      {!project.guide.lockedRevisionNumber && <button type="button" className="movie-director-inline-action" onClick={() => void lockGuide()} disabled={working !== null}><LockKeyhole size={13} /> {working === "lock" ? "Locking guide…" : "Lock current guide"}</button>}

      <section className="movie-director-proposal">
        <div className="movie-director-section-heading"><div><span className="movie-inspector-label">Proposal</span><strong>{proposal ? proposal.proposal.title : "Ask the Director for a plan"}</strong></div>{proposal && <span className={`movie-director-status is-${proposal.proposal.status.toLowerCase()}`}>{directorProposalStatusLabel(proposal.proposal.status)}</span>}</div>
        {proposal ? <>
          <p className="movie-director-summary">{proposal.proposal.summary}</p>
          {planItem && <div className="movie-director-change"><span>Typed change</span><strong>{directorActionTypeLabel(proposalAction?.actionType ?? "")}</strong><p>Shot {planItem.sequence} · {planItem.description}</p><div className="movie-director-quality-line"><span>Quality</span><strong>{qualityLabel(planItem.recommendation.qualityLevel)}</strong><small>{planItem.recommendation.selectionMode === "Auto" ? "Recommended by Auto Director" : "Selected quality"}</small></div></div>}
          <div className="movie-director-rationale"><span>Why this plan</span>{proposal.proposal.rationale.map((reason) => <small key={reason}>{reason.replaceAll("_", " ")}</small>)}</div>
          <div className="movie-director-boundary"><Check size={13} /><span>Review → explicit approval → execute. No silent mutations.</span></div>
          {proposal.proposal.status === "PendingApproval" && <div className="movie-director-actions"><button type="button" className="movie-workspace-button is-primary" onClick={() => void approveProposal()} disabled={working !== null}><Check size={13} /> {working === "approve" ? "Approving…" : "Approve proposal"}</button><button type="button" className="movie-workspace-button is-quiet" onClick={() => void rejectProposal()} disabled={working !== null}><X size={13} /> Reject</button></div>}
          {proposal.proposal.status === "Approved" && proposalAction?.status === "Ready" && <div className="movie-director-actions"><button type="button" className="movie-workspace-button is-primary" onClick={() => void executeAction()} disabled={working !== null}><Play size={13} /> {working === "execute" ? "Executing…" : "Execute ready action"}</button></div>}
          {proposalAction?.results.at(-1) && <div className={`movie-director-result is-${proposalAction.results.at(-1)?.status.toLowerCase()}`}><span>{proposalAction.results.at(-1)?.status === "Succeeded" ? <Check size={13} /> : <AlertTriangle size={13} />}</span><p>{proposalAction.results.at(-1)?.safeMessage}</p></div>}
        </> : <>
          <p className="movie-director-summary">Turn the current shot into a reviewable plan. The Director will explain the change, quality choice, and rationale before anything can run.</p>
          <label className="movie-director-field"><span>Goal</span><input value={goal} onChange={(event) => setGoal(event.target.value)} maxLength={160} /></label>
          <div className="movie-director-mode"><div><span className="movie-inspector-label">Intelligent mode</span><strong>Auto Director</strong><small>Recommends one of the four quality levels.</small></div><button type="button" className={`movie-director-toggle ${autoDirector ? "is-on" : ""}`} aria-pressed={autoDirector} onClick={() => setAutoDirector((current) => !current)}><span /></button></div>
          <div className="movie-director-quality"><span className="movie-inspector-label">Quality</span><div>{qualityLevels.map((level) => <button type="button" key={level} className={!autoDirector && quality === level ? "is-selected" : ""} onClick={() => { setQuality(level); setAutoDirector(false); }}>{level}</button>)}</div><small>{autoDirector ? "Auto Director is active; quality remains Fast / Standard / Cinematic / Studio." : "Choose a quality level explicitly."}</small></div>
          <div className="movie-director-sliders"><Slider label="Importance" value={importance} onChange={setImportance} /><Slider label="Complexity" value={complexity} onChange={setComplexity} /><Slider label="Budget sensitivity" value={budgetSensitivity} onChange={setBudgetSensitivity} /></div>
          <button type="button" className="movie-workspace-button is-primary movie-director-propose" onClick={() => void createProposal()} disabled={!canPropose}>{working === "proposal" ? <><RotateCcw size={13} className="movie-director-spin" /> Preparing…</> : <><Send size={13} /> Create typed proposal</>}</button>
          {!selectedShot && <small className="movie-director-help">The Director needs a real shot target. Open Scenes, select a scene, and add a shot.</small>}
        </>}
      </section>

      <section className="movie-director-production"><span className="movie-inspector-label">Production status</span><div className="movie-inspector-meter"><span style={{ width: `${Math.max(8, completionPercent)}%` }} /></div><div className="movie-inspector-meter-meta"><span>{project.clips.filter((clip) => Boolean(clip.assetId)).length} reviewable clips</span><strong>{completionPercent}%</strong></div></section>
      <button type="button" className="movie-director-history-toggle" onClick={() => setHistoryOpen((current) => !current)} aria-expanded={historyOpen}><span><History size={13} /> Director history</span><ChevronDown size={13} className={historyOpen ? "is-open" : ""} /></button>
      {historyOpen && <div className="movie-director-history">{loading ? <small>Loading history…</small> : history.length ? history.slice(0, 8).map((event) => <div className="movie-director-history-item" key={event.id}><span /><div><strong>{directorHistoryLabel(event.eventType)}</strong><small>{timeLabel(event.createdAt)}</small></div></div>) : <small>No Director events yet.</small>}</div>}
      {error && <div className="movie-director-error"><AlertTriangle size={13} /> {error}</div>}
      <div className="movie-director-note"><Sparkles size={14} /><p>One Director, grounded in this room and target. It never changes production records without your approval.</p></div>
    </aside>
  );
}

function Slider({ label, value, onChange }: { label: string; value: number; onChange: (value: number) => void }) {
  return <label className="movie-director-slider"><span>{label}</span><strong>{value}</strong><input type="range" min="0" max="100" value={value} onChange={(event) => onChange(Number(event.target.value))} /></label>;
}
