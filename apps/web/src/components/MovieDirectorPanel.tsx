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
import { useLocale } from "@/components/LocaleProvider";
import {
  formatMovieDirectorDate,
  formatMovieDirectorNumber,
  movieDirectorActionLabel,
  movieDirectorHistoryLabel,
  movieDirectorPrerequisite,
  movieDirectorProposalStatusLabel,
  movieDirectorQualityLabel,
  movieDirectorReasonLabel,
  movieDirectorRoomDetail,
  movieDirectorRoomLabel,
  movieDirectorText,
} from "@/lib/movieDirectorI18n";
import {
  directorRoomPlan,
  directorRoomForModule,
  type DirectorRoom,
  type DirectorRoomAction,
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

function actionForProposal(proposal: DirectorProposal | null): DirectorAction | null {
  return proposal?.actions[0] ?? null;
}

function signalIcon(tone: SignalTone) {
  if (tone === "warning") return <AlertTriangle size={13} />;
  if (tone === "suggestion") return <Lightbulb size={13} />;
  if (tone === "approval") return <ShieldAlert size={13} />;
  if (tone === "status") return <Clock3 size={13} />;
  return <Target size={13} />;
}

function roomTarget(locale: Parameters<typeof movieDirectorRoomLabel>[0], room: DirectorRoom, project: MovieProject, selectedScene: MovieScene | null, selectedShot: MovieShot | null) {
  const count = (value: number) => formatMovieDirectorNumber(locale, value);
  switch (room) {
    case "Overview":
      return { label: project.title, detail: movieDirectorRoomDetail(locale, "overview") };
    case "Story":
      return { label: project.title, detail: movieDirectorRoomDetail(locale, "story") };
    case "Cast":
      return { label: movieDirectorRoomLabel(locale, room), detail: movieDirectorRoomDetail(locale, "cast", { count: count(project.characters.length) }) };
    case "World":
      return { label: selectedScene?.title ?? movieDirectorRoomLabel(locale, room), detail: movieDirectorRoomDetail(locale, "world", { count: count(project.locations.length) }) };
    case "Scene":
      return selectedShot
        ? { label: movieDirectorText(locale, "shot", { number: String(selectedShot.sequence).padStart(2, "0") }), detail: movieDirectorRoomDetail(locale, "sceneShot") }
        : selectedScene
          ? { label: `${String(selectedScene.sequence).padStart(2, "0")} · ${selectedScene.title}`, detail: movieDirectorRoomDetail(locale, "sceneSelected", { count: count(selectedScene.shots.length) }) }
          : { label: movieDirectorRoomDetail(locale, "noScene"), detail: movieDirectorRoomDetail(locale, "chooseScene") };
    case "Shot":
      return selectedShot
        ? { label: movieDirectorText(locale, "shot", { number: String(selectedShot.sequence).padStart(2, "0") }), detail: selectedShot.description }
        : { label: movieDirectorRoomDetail(locale, "noShot"), detail: movieDirectorRoomDetail(locale, "chooseShot") };
    case "Storyboard":
      return selectedShot
        ? { label: movieDirectorText(locale, "shot", { number: String(selectedShot.sequence).padStart(2, "0") }), detail: movieDirectorRoomDetail(locale, "storyboardShot") }
        : selectedScene
          ? { label: selectedScene.title, detail: movieDirectorRoomDetail(locale, "storyboardScene") }
          : { label: movieDirectorRoomDetail(locale, "storyboardPass"), detail: movieDirectorRoomDetail(locale, "storyboardPass") };
    case "Production":
      return selectedShot
        ? { label: movieDirectorText(locale, "shot", { number: String(selectedShot.sequence).padStart(2, "0") }), detail: selectedShot.productionStage }
        : { label: project.title, detail: movieDirectorRoomDetail(locale, "production") };
  }
}

function getSignals(locale: Parameters<typeof movieDirectorText>[0], room: DirectorRoom, project: MovieProject, selectedScene: MovieScene | null, selectedShot: MovieShot | null, roomPlan: ReturnType<typeof directorRoomPlan>, proposal: DirectorProposal | null, completionPercent: number): Signal[] {
  const signals: Signal[] = [];
  const action = actionForProposal(proposal);
  const missing = roomPlan.prerequisites.filter((item) => !item.satisfied);
  if (missing.length && (room === "Scene" || room === "Storyboard" || room === "Production" || room === "Cast")) {
    signals.push({ tone: "warning", label: movieDirectorText(locale, "signalPrerequisite"), text: movieDirectorPrerequisite(locale, missing[0].key, false).detail });
  }
  if (!project.guide.lockedRevisionNumber) signals.push({ tone: "warning", label: movieDirectorText(locale, "signalWarning"), text: movieDirectorText(locale, "signalLockGuide") });
  if (proposal?.status === "PendingApproval") signals.push({ tone: "approval", label: movieDirectorText(locale, "signalApprovalRequired"), text: movieDirectorText(locale, "boundary") });
  else if (proposal?.status === "Approved" && action?.status === "Ready") signals.push({ tone: "next", label: movieDirectorText(locale, "signalNextAction"), text: movieDirectorText(locale, "signalApproved") });
  else if (action?.status === "Succeeded") signals.push({ tone: "status", label: movieDirectorText(locale, "signalProductionStatus"), text: movieDirectorText(locale, "signalSucceeded") });
  else if (room === "Story" || room === "Overview") signals.push({ tone: "next", label: movieDirectorText(locale, "signalNextAction"), text: movieDirectorText(locale, "signalStoryOverview") });
  else if (room === "Cast" && !project.characters.length) signals.push({ tone: "suggestion", label: movieDirectorText(locale, "signalSuggestion"), text: movieDirectorText(locale, "signalCast") });
  else if (room === "World" && !project.locations.length) signals.push({ tone: "suggestion", label: movieDirectorText(locale, "signalSuggestion"), text: movieDirectorText(locale, "signalWorld") });
  else if (room === "Scene" && selectedScene && !selectedScene.shots.length) signals.push({ tone: "next", label: movieDirectorText(locale, "signalNextAction"), text: movieDirectorText(locale, "signalScene") });
  else if (room === "Shot" && selectedShot) signals.push({ tone: "suggestion", label: movieDirectorText(locale, "signalSuggestion"), text: movieDirectorText(locale, "signalShot") });
  else if (room === "Storyboard") signals.push({ tone: "suggestion", label: movieDirectorText(locale, "signalSuggestion"), text: movieDirectorText(locale, "signalStoryboard") });
  else if (room === "Production") signals.push({ tone: "status", label: movieDirectorText(locale, "signalProductionStatus"), text: movieDirectorText(locale, "signalProduction", { percent: formatMovieDirectorNumber(locale, completionPercent) }) });
  return signals.slice(0, 3);
}

export function MovieDirectorPanel({ project, activeModule, selectedScene, selectedShot, onProjectRefresh }: DirectorPanelProps) {
  const { locale } = useLocale();
  const room = directorRoomForModule(activeModule);
  const target = roomTarget(locale, room, project, selectedScene, selectedShot);
  const [proposal, setProposal] = useState<DirectorProposalResponse | null>(null);
  const [history, setHistory] = useState<DirectorHistoryEvent[]>([]);
  const [loading, setLoading] = useState(true);
  const [historyOpen, setHistoryOpen] = useState(false);
  const [working, setWorking] = useState<"proposal" | "regenerate" | "approve" | "reject" | "execute" | "lock" | null>(null);
  const [error, setError] = useState("");
  const [goal, setGoal] = useState(() => movieDirectorText(locale, "defaultGoal"));
  const [autoDirector, setAutoDirector] = useState(true);
  const [quality, setQuality] = useState<DirectorQualityLevel>("Cinematic");
  const [importance, setImportance] = useState(70);
  const [complexity, setComplexity] = useState(60);
  const [budgetSensitivity, setBudgetSensitivity] = useState(50);
  const completionPercent = Math.min(100, Math.round(((project.scenes.length ? project.clips.filter((clip) => Boolean(clip.assetId) && ["Completed", "Succeeded", "Ready"].includes(clip.status)).length : 0) / Math.max(project.scenes.length, 1)) * 100));
  const roomPlan = useMemo(() => directorRoomPlan(room, project, selectedScene, selectedShot), [room, project, selectedScene, selectedShot]);
  const [roomAction, setRoomAction] = useState<string>("");
  const activeRoomAction = roomPlan.validActions.includes(roomAction as DirectorRoomAction) ? roomAction : roomPlan.validActions[0] ?? "";
  const signals = useMemo(() => getSignals(locale, room, project, selectedScene, selectedShot, roomPlan, proposal?.proposal ?? null, completionPercent), [locale, room, project, selectedScene, selectedShot, roomPlan, proposal, completionPercent]);
  const proposalAction = actionForProposal(proposal?.proposal ?? null);
  const planItem = proposal?.proposal.plan[0] ?? null;
  const castRoomStoryAssistance = room === "Cast";
  const roomPlanning = room === "Scene" || room === "Storyboard" || room === "Production";
  const canPropose = Boolean(roomPlan.validActions.length && project.guide.lockedRevisionNumber && !working);
  const t = (key: Parameters<typeof movieDirectorText>[1], variables?: Record<string, string | number>) => movieDirectorText(locale, key, variables);

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
      if (mounted) setError(cause instanceof Error ? cause.message : movieDirectorText(locale, "errorHistoryUnavailable"));
    }).finally(() => {
      if (mounted) setLoading(false);
    });
    return () => { mounted = false; };
  }, [locale, project.id]);

  async function createProposal() {
    if (!roomPlan.validActions.length) {
      setError(roomPlan.prerequisites.find((item) => !item.satisfied) ? movieDirectorPrerequisite(locale, roomPlan.prerequisites.find((item) => !item.satisfied)?.key ?? "", false).detail : t("errorNoValidAction"));
      return;
    }
    setWorking("proposal");
    setError("");
    try {
      const next = await api.createMovieDirectorProposal(project.id, castRoomStoryAssistance
        ? { storyAction: "develop_premise", contextRoom: room, contextTargetType: "project", contextTargetId: project.id, goal: goal.trim() || "Review the current Cast continuity context." }
        : roomPlanning
          ? { contextRoom: room, roomAction: activeRoomAction, contextTargetType: roomPlan.targetType, contextTargetId: roomPlan.targetType === "shot" ? selectedShot?.id : roomPlan.targetType === "scene" ? selectedScene?.id : project.id, selectedSceneId: selectedScene?.id ?? null, selectedShotId: selectedShot?.id ?? null, goal: goal.trim() || null }
        : { contextRoom: room, shotId: selectedShot?.id, selectedSceneId: selectedScene?.id ?? null, selectedShotId: selectedShot?.id ?? null, goal: goal.trim() || null, requestedQuality: autoDirector ? "Auto" : quality, budgetLimitUsd: null, importance, complexity, budgetSensitivity });
      setProposal(next);
      if (typeof window !== "undefined") window.sessionStorage.setItem(`taslim:movie-director:proposal:${project.id}`, next.proposal.id);
      setHistory(await api.getMovieDirectorHistory(project.id));
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : t("errorProposalCreate"));
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
      setError(cause instanceof Error ? cause.message : t("errorProposalApprove"));
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
      setError(cause instanceof Error ? cause.message : t("errorProposalReject"));
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
      setError(cause instanceof Error ? cause.message : t("errorActionExecute"));
    } finally {
      setWorking(null);
    }
  }

  async function regenerateProposal() {
    if (!selectedShot || working) return;
    setWorking("regenerate");
    setError("");
    try {
      const next = await api.createMovieDirectorProposal(project.id, { shotId: selectedShot.id, goal: goal.trim() || null, requestedQuality: autoDirector ? "Auto" : quality, budgetLimitUsd: null, importance, complexity, budgetSensitivity });
      setProposal(next);
      if (typeof window !== "undefined") window.sessionStorage.setItem(`taslim:movie-director:proposal:${project.id}`, next.proposal.id);
      setHistory(await api.getMovieDirectorHistory(project.id));
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : t("errorProposalRegenerate"));
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
      setError(cause instanceof Error ? cause.message : t("errorGuideLock"));
    } finally {
      setWorking(null);
    }
  }

  return (
    <aside className="movie-director-panel" aria-label={t("ariaLabel")}>
      <div className="movie-director-heading"><div><span className="movie-workspace-kicker">{t("kicker")}</span><strong>{t("title")}</strong></div><Sparkles size={16} /></div>
      <div className="movie-director-context"><div><span className="movie-inspector-label">{t("currentRoom")}</span><strong>{movieDirectorRoomLabel(locale, room)}</strong></div><div><span className="movie-inspector-label">{t("target")}</span><strong>{target.label}</strong><small>{target.detail}</small></div></div>
      {signals.map((signal) => <div className={`movie-director-signal is-${signal.tone}`} key={`${signal.tone}-${signal.text}`}><span>{signalIcon(signal.tone)}</span><p><strong>{signal.label}</strong>{signal.text}</p></div>)}
      {!project.guide.lockedRevisionNumber && <button type="button" className="movie-director-inline-action" onClick={() => void lockGuide()} disabled={working !== null}><LockKeyhole size={13} /> {working === "lock" ? t("lockingGuide") : t("lockGuide")}</button>}

      <section className="movie-director-proposal">
        <div className="movie-director-section-heading"><div><span className="movie-inspector-label">{t("proposal")}</span><strong>{proposal ? proposal.proposal.title : t("askPlan")}</strong></div>{proposal && <span className={`movie-director-status is-${proposal.proposal.status.toLowerCase()}`}>{movieDirectorProposalStatusLabel(locale, proposal.proposal.status)}</span>}</div>
        {proposal ? <>
          <p className="movie-director-summary">{proposal.proposal.summary}</p>
          {proposal.proposal.shotPlan?.length ? <div className="movie-director-change"><span>{t("productionReadyShotPlan")}</span><strong>{t("proposedShots", { count: formatMovieDirectorNumber(locale, proposal.proposal.shotPlan.length) })}</strong>{proposal.proposal.shotPlan.map((shot) => <details key={shot.shotNumber}><summary>{t("shot", { number: shot.shotNumber })} · {formatMovieDirectorNumber(locale, shot.estimatedDurationSeconds)}s · {shot.shotSize}</summary><p><b>{t("purpose")}</b> {shot.narrativePurpose}</p><p><b>{t("subjectAction")}</b> {shot.subject} — {shot.characterAction}</p><p><b>{t("camera")}</b> {shot.framing}; {shot.cameraAngle}; {shot.cameraMovement}</p><p><b>{t("lightDepth")}</b> {shot.lightingIntent}; {shot.depthBackgroundIntent}</p><p><b>{t("continuityAudio")}</b> {shot.continuityRequirements}; {shot.dialogueAudioDependency}</p><p><b>{t("propsVfxNotes")}</b> {shot.importantProps.join(", ") || t("none")}; {shot.vfxRequirements}; {shot.productionNotes}</p><small>{t("groundedIn", { evidence: shot.groundingEvidence.join(" · ") })}</small></details>)}</div> : planItem && <div className="movie-director-change"><span>{t("typedChange")}</span><strong>{movieDirectorActionLabel(locale, proposalAction?.actionType ?? "")}</strong><p>{t("shot", { number: planItem.sequence })} · {planItem.description}</p><div className="movie-director-quality-line"><span>{t("quality")}</span><strong>{movieDirectorQualityLabel(locale, planItem.recommendation.qualityLevel)}</strong><small>{planItem.recommendation.selectionMode === "Auto" ? t("recommendedAuto") : t("selectedQuality")}</small></div></div>}
          <div className="movie-director-rationale"><span>{t("whyPlan")}</span>{proposal.proposal.rationale.map((reason) => <small key={reason}>{movieDirectorReasonLabel(locale, reason)}</small>)}</div>
          <div className="movie-director-boundary"><Check size={13} /><span>{t("boundary")}</span></div>
          {proposal.proposal.status === "PendingApproval" && <div className="movie-director-actions"><button type="button" className="movie-workspace-button is-primary" onClick={() => void approveProposal()} disabled={working !== null}><Check size={13} /> {working === "approve" ? t("approving") : t("approveProposal")}</button><button type="button" className="movie-workspace-button is-quiet" onClick={() => void rejectProposal()} disabled={working !== null}><X size={13} /> {t("reject")}</button></div>}
          {proposal.proposal.status === "Approved" && proposalAction?.status === "Ready" && <div className="movie-director-actions"><button type="button" className="movie-workspace-button is-primary" onClick={() => void executeAction()} disabled={working !== null}><Play size={13} /> {working === "execute" ? t("applying") : proposal.proposal.shotPlan?.length ? t("applyShotPlan") : t("executeReadyAction")}</button></div>}
          {selectedShot && <button type="button" className="movie-text-action movie-director-regenerate" onClick={() => void regenerateProposal()} disabled={working !== null}><RotateCcw size={12} /> {working === "regenerate" ? t("regenerating") : t("regenerateProposal")}</button>}
          {proposalAction?.results.at(-1) && <div className={`movie-director-result is-${proposalAction.results.at(-1)?.status.toLowerCase()}`}><span>{proposalAction.results.at(-1)?.status === "Succeeded" ? <Check size={13} /> : <AlertTriangle size={13} />}</span><p>{proposalAction.results.at(-1)?.safeMessage}</p></div>}
        </> : <>
          <p className="movie-director-summary">{castRoomStoryAssistance ? t("summaryCast") : roomPlanning ? t("summaryPlanning") : t("summaryShot")}</p>
          {roomPlanning && <label className="movie-director-field"><span>{t("validRoomAction")}</span><select value={activeRoomAction} onChange={(event) => setRoomAction(event.target.value)}>{roomPlan.validActions.map((action) => <option key={action} value={action}>{movieDirectorActionLabel(locale, action)}</option>)}</select></label>}
          {roomPlanning && roomPlan.prerequisites.some((item) => !item.satisfied) && <div className="movie-director-prerequisites"><span className="movie-inspector-label">{t("availablePrerequisites")}</span>{roomPlan.prerequisites.map((item) => { const prerequisite = movieDirectorPrerequisite(locale, item.key, item.satisfied); return <small key={item.key} className={item.satisfied ? "is-satisfied" : "is-missing"}>{item.satisfied ? "✓" : "!"} {prerequisite.label} · {prerequisite.detail}</small>; })}</div>}
          <label className="movie-director-field"><span>{t("goal")}</span><input value={goal} onChange={(event) => setGoal(event.target.value)} maxLength={160} /></label>
          <div className="movie-director-mode"><div><span className="movie-inspector-label">{t("intelligentMode")}</span><strong>{t("autoDirector")}</strong><small>{t("autoDirectorHint")}</small></div><button type="button" className={`movie-director-toggle ${autoDirector ? "is-on" : ""}`} aria-pressed={autoDirector} onClick={() => setAutoDirector((current) => !current)}><span /></button></div>
          <div className="movie-director-quality"><span className="movie-inspector-label">{t("quality")}</span><div>{qualityLevels.map((level) => <button type="button" key={level} className={!autoDirector && quality === level ? "is-selected" : ""} onClick={() => { setQuality(level); setAutoDirector(false); }}>{movieDirectorQualityLabel(locale, level)}</button>)}</div><small>{autoDirector ? t("qualityAutoActive") : t("chooseQuality")}</small></div>
          <div className="movie-director-sliders"><Slider locale={locale} label={t("importance")} value={importance} onChange={setImportance} /><Slider locale={locale} label={t("complexity")} value={complexity} onChange={setComplexity} /><Slider locale={locale} label={t("budgetSensitivity")} value={budgetSensitivity} onChange={setBudgetSensitivity} /></div>
          <button type="button" className="movie-workspace-button is-primary movie-director-propose" onClick={() => void createProposal()} disabled={!canPropose}>{working === "proposal" ? <><RotateCcw size={13} className="movie-director-spin" /> {t("preparing")}</> : <><Send size={13} /> {t("createTypedProposal")}</>}</button>
          {!roomPlan.validActions.length && !castRoomStoryAssistance && <small className="movie-director-help">{roomPlan.prerequisites.find((item) => !item.satisfied) ? movieDirectorPrerequisite(locale, roomPlan.prerequisites.find((item) => !item.satisfied)?.key ?? "", false).detail : t("noValidRoomHelp")}</small>}
        </>}
      </section>

      <section className="movie-director-production"><span className="movie-inspector-label">{t("signalProductionStatus")}</span><div className="movie-inspector-meter"><span style={{ width: `${Math.max(8, completionPercent)}%` }} /></div><div className="movie-inspector-meter-meta"><span>{t("reviewableClips", { count: formatMovieDirectorNumber(locale, project.clips.filter((clip) => Boolean(clip.assetId)).length) })}</span><strong>{formatMovieDirectorNumber(locale, completionPercent)}%</strong></div></section>
      <button type="button" className="movie-director-history-toggle" onClick={() => setHistoryOpen((current) => !current)} aria-expanded={historyOpen}><span><History size={13} /> {t("directorHistory")}</span><ChevronDown size={13} className={historyOpen ? "is-open" : ""} /></button>
      {historyOpen && <div className="movie-director-history">{loading ? <small>{t("loadingHistory")}</small> : history.length ? history.slice(0, 8).map((event) => <div className="movie-director-history-item" key={event.id}><span /><div><strong>{movieDirectorHistoryLabel(locale, event.eventType)}</strong><small>{formatMovieDirectorDate(locale, event.createdAt)}</small></div></div>) : <small>{t("noEvents")}</small>}</div>}
      {error && <div className="movie-director-error"><AlertTriangle size={13} /> {error}</div>}
      <div className="movie-director-note"><Sparkles size={14} /><p>{t("note")}</p></div>
    </aside>
  );
}

function Slider({ locale, label, value, onChange }: { locale: Parameters<typeof movieDirectorText>[0]; label: string; value: number; onChange: (value: number) => void }) {
  return <label className="movie-director-slider"><span>{label}</span><strong>{formatMovieDirectorNumber(locale, value)}</strong><input type="range" min="0" max="100" value={value} onChange={(event) => onChange(Number(event.target.value))} /></label>;
}
