"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import {
  AlertTriangle,
  Check,
  CheckCircle2,
  ChevronRight,
  CircleAlert,
  Clapperboard,
  Film,
  Flag,
  Layers3,
  LoaderCircle,
  LockKeyhole,
  Play,
  RefreshCw,
  ShieldCheck,
  TimerReset,
  WandSparkles,
  X,
} from "lucide-react";
import { useLocale } from "@/components/LocaleProvider";
import { api, type MovieOverviewCost, type MovieProductionVersion, type MovieProject, type MovieTake } from "@/lib/api";
import { assetFileUrl } from "@/lib/apiBase";
import { MovieBudgetReadinessPanel } from "@/components/MovieBudgetReadinessPanel";
import {
  buildMovieProductionWorkspaceModel,
  matchesProductionFilter,
  type MovieProductionWorkspaceAdapter,
  type ProductionReadiness,
  type ProductionWorkspaceFilter,
  type ProductionWorkspaceShot,
  type ProductionWorkspaceStage,
} from "@/lib/movieProductionWorkspace";
import { hasActiveMovieProductionExecution, nextMovieProductionPollDelay } from "@/lib/movieProductionPolling";
import { productionIntentForTier, type MovieResolutionTier } from "@/lib/movieProductionResolution";

const filters: Array<{ id: ProductionWorkspaceFilter; label: string }> = [
  { id: "all", label: "All shots" },
  { id: "needs-review", label: "Needs review" },
  { id: "ready", label: "Ready" },
  { id: "blocked", label: "Blocked" },
];

const stageRail: Array<{ id: ProductionWorkspaceStage; label: string; detail: string }> = [
  { id: "shot-plan", label: "Shot plan", detail: "Approved intent" },
  { id: "keyframe", label: "Keyframe", detail: "Source frame" },
  { id: "candidates", label: "Candidates", detail: "Working passes" },
  { id: "review", label: "Review", detail: "Human decision" },
  { id: "select", label: "Select", detail: "Canonical take" },
  { id: "finish", label: "Finish", detail: "Draft · Upgrade · Master" },
  { id: "timeline", label: "Timeline", detail: "Ready to edit" },
];

const stageOrder = stageRail.map((item) => item.id);

function createApiAdapter(): MovieProductionWorkspaceAdapter {
  return {
    createKeyframe: (shotId, sourceVersionId, compositionJson) => api.createMovieProductionVersion(shotId, { stage: "ProductionKeyframe", sourceVersionId, compositionJson, label: "Source frame" }),
    reviewVersion: (versionId, input) => api.reviewMovieProductionVersion(versionId, input),
    createMotionPreview: (shotId, sourceVersionId) => api.createMovieMotionPreview(shotId, { sourceVersionId, label: "Motion check" }),
    queueRender: (shotId, sourceVersionId, retry, intent) => { const renderIntent = intent ?? productionIntentForTier("Master"); return api.queueMovieProductionRender(shotId, { sourceVersionId, targetResolution: renderIntent.targetResolution, qualityTier: renderIntent.qualityTier, label: retry ? "Master retry" : "Master pass" }); },
    createTake: (versionId) => api.createMovieTakeFromProduction(versionId, { label: "Master take" }),
    approveTake: (takeId) => api.approveMovieTake(takeId, { decision: "Approved", comment: "Take approved in Production." }),
    selectTake: (takeId) => api.selectMovieTake(takeId),
    finalizeTake: (takeId) => api.finalizeMovieTake(takeId),
    requestMaster: (takeId) => api.requestMovieFinalMaster(takeId, { targetProfile: "Uhd4K" }),
  };
}

type PendingProductionConfirmation = { key: string; work: () => Promise<unknown>; title: string; detail: string };

function isExpensiveProductionAction(key: string) {
  return /:(keyframe|motion|render|retry|master|regenerate)(?:$|-)/.test(key);
}

function confirmationCopy(key: string) {
  if (key.includes(":master")) return { title: "Confirm the master hand-off", detail: "This keeps the selected take as the hand-off point for a higher-finish step." };
  if (key.includes(":render") || key.includes(":retry")) return { title: "Confirm this production pass", detail: "Review the budget and readiness panel before starting another pass. Only the affected shot will be sent forward." };
  if (key.includes(":regenerate")) return { title: "Confirm selective regeneration", detail: "Only this shot will be regenerated from its latest approved production source. Review the budget and readiness panel before continuing." };
  if (key.includes(":keyframe")) return { title: "Confirm the source frame", detail: "This prepares a new visual reference from the approved plan. Review the result before moving on." };
  return { title: "Confirm the motion check", detail: "This creates a reviewable motion pass from the approved source frame." };
}

export function MovieProductionWorkspace({ project, completionPercent, onRefresh, cost = null, adapter = createApiAdapter() }: { project: MovieProject; completionPercent: number; onRefresh: () => Promise<void>; cost?: MovieOverviewCost | null; adapter?: MovieProductionWorkspaceAdapter }) {
  const { t } = useLocale();
  const model = useMemo(() => buildMovieProductionWorkspaceModel(project), [project]);
  const displayProgress = model.shots.length ? model.progressPercent : completionPercent;
  const hasActiveExecution = useMemo(() => hasActiveMovieProductionExecution(project), [project]);
  const [filter, setFilter] = useState<ProductionWorkspaceFilter>("all");
  const [selectedTier, setSelectedTier] = useState<MovieResolutionTier>(model.counts.selectedTakes > 0 ? "Master" : model.counts.candidates > 0 ? "Upgrade" : "Draft");
  const [busyKey, setBusyKey] = useState("");
  const [error, setError] = useState("");
  const [refreshError, setRefreshError] = useState("");
  const [pollRetry, setPollRetry] = useState(0);
  const [lastAction, setLastAction] = useState<(() => Promise<void>) | null>(null);
  const [confirmation, setConfirmation] = useState<PendingProductionConfirmation | null>(null);
  const refreshRef = useRef(onRefresh);
  const visibleShots = model.shots.filter((item) => matchesProductionFilter(item, filter));

  useEffect(() => { refreshRef.current = onRefresh; }, [onRefresh]);
  useEffect(() => {
    if (!hasActiveExecution) return;
    let active = true;
    const timer = window.setTimeout(async () => {
      try {
        await refreshRef.current();
        if (!active) return;
        setPollRetry(0);
        setRefreshError("");
      } catch {
        if (!active) return;
        setPollRetry((attempt) => attempt + 1);
        setRefreshError("Production status could not be refreshed. Retrying automatically.");
      }
    }, nextMovieProductionPollDelay(hasActiveExecution, pollRetry) ?? 1_000);
    return () => { active = false; window.clearTimeout(timer); };
  }, [hasActiveExecution, pollRetry]);

  const regenerateShot = (item: ProductionWorkspaceShot) => async () => {
    const sourceVersion = item.motion ?? item.keyframe ?? item.render;
    const response = await api.createMovieRegenerationRequest(item.shot.id, {
      actionType: "production_render", requestedStage: "ProductionRender",
      reason: "Recover the failed production render from the latest approved source.",
      sourceVersionId: sourceVersion?.id ?? null, changedInputsJson: JSON.stringify({ recovery: "production_render", sourceVersionId: sourceVersion?.id ?? null }),
      compositionJson: sourceVersion?.compositionJson ?? "{}",
    });
    await api.confirmMovieRegeneration(response.request.id, true);
  };

  async function executeAction(key: string, work: () => Promise<unknown>) {
    setBusyKey(key);
    setError("");
    setLastAction(() => async () => { await runAction(key, work); });
    try {
      await work();
      await onRefresh();
      setRefreshError("");
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "That production decision could not be saved.");
    } finally {
      setBusyKey("");
    }
  }

  async function runAction(key: string, work: () => Promise<unknown>) {
    if (isExpensiveProductionAction(key)) {
      const copy = confirmationCopy(key);
      setConfirmation({ key, work, ...copy });
      return;
    }
    await executeAction(key, work);
  }

  return <div className="movie-production-workspace" data-testid="movie-production-workspace">
    <section className="movie-production-command" aria-labelledby="production-command-title">
      <div className="movie-production-command-copy">
        <div className="movie-production-command-eyebrow"><span className="movie-workspace-kicker">{t("movieModule.production.eyebrow")}</span><span className="movie-production-private"><LockKeyhole size={12} /> Private plan</span></div>
        <h3 id="production-command-title">From approved shot plan to a timeline-ready cut.</h3>
        <p>Work one shot at a time. Source frames, candidate takes, review decisions, and finish intent stay connected to the approved plan.</p>
      </div>
      <div className="movie-production-command-progress" role="status" aria-label={`${displayProgress}% production progress`}><span>{displayProgress}%</span><small>workflow progress</small><div className="movie-production-progress-track"><span style={{ width: `${displayProgress}%` }} /></div></div>
    </section>

    <ProductionStageRail currentStage={model.currentStage} counts={model.counts} />

    <MovieBudgetReadinessPanel project={project} workspace={model} cost={cost} selectedTier={selectedTier} onTierChange={setSelectedTier} />
    {confirmation && <section className="movie-production-confirmation" role="dialog" aria-modal="false" aria-labelledby="movie-production-confirmation-title"><div className="movie-budget-readiness-icon"><AlertTriangle size={16} /></div><div><span className="movie-workspace-kicker">Review before continuing</span><h3 id="movie-production-confirmation-title">{confirmation.title}</h3><p>{confirmation.detail} Nothing is charged from this planning surface.</p><div className="movie-production-confirmation-actions"><button type="button" className="movie-workspace-button is-primary" onClick={() => { const pending = confirmation; setConfirmation(null); void executeAction(pending.key, pending.work); }}>Continue</button><button type="button" className="movie-workspace-button is-secondary" onClick={() => setConfirmation(null)}>Not now</button></div></div></section>}

    <div className="movie-production-summary-grid" aria-label="Production summary">
      <SummaryMetric label="Shot plan" value={`${model.counts.shotPlanReady}/${model.counts.shots}`} detail="approved intent" />
      <SummaryMetric label="Keyframes" value={`${model.counts.keyframesReady}/${model.counts.shots}`} detail="source frames" />
      <SummaryMetric label="Candidate takes" value={String(model.counts.takes)} detail="reviewable history" />
      <SummaryMetric label="Timeline ready" value={`${model.counts.readyForTimeline}/${model.counts.shots}`} detail="selected takes" />
    </div>

    {error && <div className="movie-production-recovery" role="alert"><CircleAlert size={16} /><div><strong>We kept your plan safe.</strong><span>{error}</span></div><div className="movie-production-recovery-actions">{lastAction && <button type="button" onClick={() => void lastAction()} disabled={Boolean(busyKey)}><RefreshCw size={13} /> Retry last action</button>}<button type="button" onClick={() => void onRefresh()} disabled={Boolean(busyKey)}><RefreshCw size={13} /> Reload workspace</button></div></div>}
    {refreshError && <div className="movie-production-recovery" role="status"><RefreshCw size={16} /><div><strong>Live status is temporarily unavailable.</strong><span>{refreshError}</span></div><div className="movie-production-recovery-actions"><button type="button" onClick={() => void onRefresh()} disabled={Boolean(busyKey)}><RefreshCw size={13} /> Reload workspace</button></div></div>}

    <section className="movie-production-shot-workspace" aria-labelledby="production-shot-list-title">
      <div className="movie-production-section-heading"><div><span className="movie-workspace-kicker">Shot review</span><h3 id="production-shot-list-title">{t("movieModule.production.title")}</h3><p>Implementation details stay behind the stage labels; you only see what needs your review next.</p></div><span className="movie-production-filter-count">{visibleShots.length} of {model.counts.shots} shots</span></div>
      <div className="movie-production-filters" role="tablist" aria-label="Filter shots">{filters.map((item) => <button key={item.id} type="button" role="tab" aria-selected={filter === item.id} className={filter === item.id ? "is-active" : ""} onClick={() => setFilter(item.id)}>{item.label}{item.id === "needs-review" && model.counts.needsReview > 0 && <span>{model.counts.needsReview}</span>}{item.id === "ready" && model.counts.readyForTimeline > 0 && <span>{model.counts.readyForTimeline}</span>}</button>)}</div>
      {visibleShots.length ? <div className="movie-production-shot-list">{visibleShots.map((item) => <ProductionShotCard key={item.shot.id} item={item} busyKey={busyKey} selectedTier={selectedTier} adapter={adapter} onAction={runAction} onRegenerate={regenerateShot(item)} />)}</div> : <ProductionEmptyState filter={filter} hasShots={model.counts.shots > 0} />}
    </section>

    <TimelineReadiness model={model} />
    <div className="movie-production-safety-note"><ShieldCheck size={17} /><div><strong>Only persisted work appears here.</strong><span>A failed or unavailable pass stays visible as a review state. No placeholder footage, provider names, or billing details are shown.</span></div></div>
  </div>;
}

function ProductionStageRail({ currentStage, counts }: { currentStage: ProductionWorkspaceStage; counts: ReturnType<typeof buildMovieProductionWorkspaceModel>["counts"] }) {
  const activeIndex = stageOrder.indexOf(currentStage);
  return <nav className="movie-production-stage-rail" aria-label="Production stages">{stageRail.map((stage, index) => { const complete = index < activeIndex || (stage.id === "timeline" && counts.readyForTimeline > 0 && counts.readyForTimeline === counts.shots); const active = index === activeIndex; return <div className={`movie-production-stage-step ${complete ? "is-complete" : ""} ${active ? "is-active" : ""}`} key={stage.id} aria-current={active ? "step" : undefined}><span className="movie-production-stage-index">{complete ? <Check size={13} /> : String(index + 1).padStart(2, "0")}</span><div><strong>{stage.label}</strong><small>{stage.detail}</small></div>{index < stageRail.length - 1 && <ChevronRight size={13} className="movie-production-stage-arrow" aria-hidden="true" />}</div>; })}</nav>;
}

function ProductionShotCard({ item, busyKey, selectedTier, adapter, onAction, onRegenerate }: { item: ProductionWorkspaceShot; busyKey: string; selectedTier: MovieResolutionTier; adapter: MovieProductionWorkspaceAdapter; onAction: (key: string, work: () => Promise<unknown>) => Promise<void>; onRegenerate: () => Promise<void> }) {
  const { shot } = item;
  const sourceStoryboard = item.storyboard;
  const isBusy = (action: string) => busyKey === `${shot.id}:${action}`;
  const renderFailed = item.render?.execution?.status === "Failed";
  const renderReady = item.render?.execution?.status === "Succeeded";
  const canCreateTake = Boolean(item.render && renderReady && !item.takes.some((take) => take.generationJobId === item.render?.generationJobId));
  const approvedTake = item.takes.find((take) => take.status === "Approved" || take.status === "Selected") ?? null;
  const masterAllowed = Boolean(item.selectedTake && (approvedTake || item.finalTake));
  return <article className={`movie-production-shot-card is-${item.readiness}`} aria-labelledby={`production-shot-${shot.id}`}>
    <header className="movie-production-shot-card-header"><div className="movie-production-shot-title"><span className="movie-production-shot-kicker">Scene {String(item.sceneSequence).padStart(2, "0")} · {item.sceneTitle}</span><h4 id={`production-shot-${shot.id}`}>Shot {String(shot.sequence).padStart(2, "0")}</h4><p>{shot.description}</p></div><ReadinessBadge readiness={item.readiness} label={item.readinessLabel} /></header>
    <div className="movie-production-shot-context"><div><span>Approved shot plan</span><strong>{shot.planState === "Draft" ? "Draft" : "Ready"}</strong><small>{shot.durationSeconds ? `${shot.durationSeconds}s` : "Duration not set"}{shot.cameraAndFraming ? ` · ${shot.cameraAndFraming}` : ""}</small></div><div><span>Next step</span><strong>{item.nextAction}</strong><small>{shot.readiness.summary}</small></div>{item.selectedTake && <div className="is-selected-context"><span>Selected take</span><strong>{item.selectedTake.label}</strong><small>{item.finalTake ? "Final take recorded" : "Ready for finish review"}</small></div>}</div>
    <div className="movie-production-shot-stages">
      <StageCard label="Source frame" state={item.keyframe ? "Approved" : item.pendingKeyframe ? "Needs review" : sourceStoryboard ? "Ready to prepare" : "Waiting for visual plan"} tone={item.keyframe ? "ready" : item.pendingKeyframe ? "review" : sourceStoryboard ? "next" : "blocked"} />
      <StageCard label="Candidate takes" state={item.takes.length ? `${item.takes.length} saved` : "None yet"} tone={item.takes.length ? "ready" : "blocked"} />
      <StageCard label="Review & select" state={item.selectedTake ? "Selected" : item.takes.length ? "Decision needed" : "Waiting"} tone={item.selectedTake ? "ready" : item.takes.length ? "review" : "blocked"} />
      <StageCard label="Timeline" state={item.readiness === "ready" ? "Ready" : "Not ready"} tone={item.readiness === "ready" ? "ready" : "blocked"} />
    </div>
    <div className="movie-production-shot-actions" aria-label={`Actions for shot ${shot.sequence}`}>
      {!item.keyframe && sourceStoryboard && <button type="button" className="movie-workspace-button is-secondary" disabled={Boolean(busyKey)} onClick={() => void onAction(`${shot.id}:keyframe`, () => adapter.createKeyframe(shot.id, sourceStoryboard.id, sourceStoryboard.compositionJson))}><Layers3 size={14} /> Prepare source frame</button>}
      {item.pendingKeyframe && <button type="button" className="movie-workspace-button is-primary" disabled={Boolean(busyKey)} onClick={() => void onAction(`${shot.id}:approve-keyframe`, () => adapter.reviewVersion(item.pendingKeyframe!.id, { approve: true, reason: "Source frame approved in Production." }))}>{isBusy("approve-keyframe") ? "Saving…" : "Approve source frame"}</button>}
      {item.keyframe && !item.motion && <button type="button" className="movie-workspace-button is-secondary" disabled={Boolean(busyKey)} onClick={() => void onAction(`${shot.id}:motion`, () => adapter.createMotionPreview(shot.id, item.keyframe!.id))}><Play size={14} /> Create motion check</button>}
      {item.motion?.status === "PendingApproval" && <button type="button" className="movie-workspace-button is-primary" disabled={Boolean(busyKey)} onClick={() => void onAction(`${shot.id}:approve-motion`, () => adapter.reviewVersion(item.motion!.id, { approve: true, reason: "Motion check approved in Production." }))}>{isBusy("approve-motion") ? "Saving…" : "Approve motion check"}</button>}
      {item.motion?.status === "Approved" && (!item.render || renderFailed) && <button type="button" className="movie-workspace-button is-secondary" disabled={Boolean(busyKey)} onClick={() => void onAction(`${shot.id}:render`, () => adapter.queueRender(shot.id, item.motion!.id, Boolean(item.render && renderFailed), productionIntentForTier(selectedTier)))}><WandSparkles size={14} /> {renderFailed ? "Retry master pass" : "Create master pass"}</button>}
      {canCreateTake && <button type="button" className="movie-workspace-button is-primary" disabled={Boolean(busyKey)} onClick={() => void onAction(`${shot.id}:take`, () => adapter.createTake(item.render!.id))}><CheckCircle2 size={14} /> Save as take</button>}
      {selectedTier === "Master" && item.selectedTake && <button type="button" className="movie-workspace-button is-finish" disabled={Boolean(busyKey) || !masterAllowed} onClick={() => void onAction(`${shot.id}:master`, () => adapter.requestMaster(item.selectedTake!.id))}><Flag size={14} /> Record master hand-off</button>}
      {renderFailed && <><button type="button" className="movie-workspace-button is-secondary" disabled={Boolean(busyKey)} onClick={() => void onAction(`${shot.id}:regenerate`, onRegenerate)}><RefreshCw size={14} /> Regenerate failed pass</button><span className="movie-production-inline-recovery"><AlertTriangle size={13} /> Review the pass note before retrying.</span></>}
    </div>
    {item.versions.length > 0 && <details className="movie-production-history"><summary><TimerReset size={14} /> Show saved pass history <span>{item.versions.length}</span></summary><div>{item.versions.map((version) => <ProductionHistoryRow key={version.id} version={version} />)}</div></details>}
    <div className="movie-production-take-review"><div className="movie-production-take-heading"><div><span className="movie-workspace-kicker">Candidate takes</span><h5>Review, approve, then select one</h5></div><span>{item.takes.length} saved</span></div>{item.takes.length ? item.takes.map((take) => <CandidateTakeRow key={take.id} take={take} busyKey={busyKey} adapter={adapter} onAction={onAction} shotId={shot.id} />) : <div className="movie-production-subtle-empty"><Film size={15} /><span>A candidate take appears only after a real pass is saved. Nothing is simulated.</span></div>}</div>
  </article>;
}

function CandidateTakeRow({ take, busyKey, adapter, onAction, shotId }: { take: MovieTake; busyKey: string; adapter: MovieProductionWorkspaceAdapter; onAction: (key: string, work: () => Promise<unknown>) => Promise<void>; shotId: string }) {
  const selected = Boolean(take.selectedAt);
  const final = Boolean(take.finalizedAt);
  const readyForReview = ["Ready", "ReviewRequired", "Approved", "Selected"].includes(take.status);
  const isBusy = (action: string) => busyKey === `${shotId}:${action}`;
  return <div className={`movie-production-candidate-row ${selected ? "is-selected" : ""} ${final ? "is-final" : ""}`}><div className="movie-production-candidate-preview">{take.assetId ? <video src={assetFileUrl(take.assetId, true)} controls preload="metadata" aria-label={`${take.label} preview`} /> : <Film size={17} />}</div><div className="movie-production-candidate-copy"><span>Take v{take.versionNumber}</span><strong>{take.label}</strong><small>{take.status}{selected ? " · Selected" : ""}{final ? " · Final" : ""}</small></div><div className="movie-production-candidate-actions">{!selected && take.status !== "Rejected" && <button type="button" className="movie-text-action" disabled={Boolean(busyKey) || !readyForReview} onClick={() => void onAction(`${shotId}:select-${take.id}`, () => adapter.selectTake(take.id))}>{isBusy(`select-${take.id}`) ? "Selecting…" : "Select"}</button>}{take.status !== "Approved" && take.status !== "Rejected" && <button type="button" className="movie-text-action" disabled={Boolean(busyKey) || !readyForReview} onClick={() => void onAction(`${shotId}:approve-${take.id}`, () => adapter.approveTake(take.id))}>{isBusy(`approve-${take.id}`) ? "Saving…" : "Approve"}</button>}{selected && !final && <button type="button" className="movie-text-action is-finish" disabled={Boolean(busyKey) || take.status !== "Approved"} onClick={() => void onAction(`${shotId}:finalize-${take.id}`, () => adapter.finalizeTake(take.id))}>{isBusy(`finalize-${take.id}`) ? "Saving…" : "Carry to finish"}</button>}</div></div>;
}

function ProductionHistoryRow({ version }: { version: MovieProductionVersion }) {
  const execution = version.execution;
  const state = execution?.status === "Failed" ? "Needs attention" : execution?.status === "Succeeded" ? "Ready" : version.status === "Rejected" ? "Revision needed" : version.status;
  return <div className="movie-production-history-row"><span>{version.label || "Saved pass"}</span><strong>{state}</strong><small>{version.stage.replace(/([a-z])([A-Z])/g, "$1 $2")}</small></div>;
}

function TimelineReadiness({ model }: { model: ReturnType<typeof buildMovieProductionWorkspaceModel> }) {
  return <section className={`movie-production-timeline-readiness ${model.timelineReady ? "is-ready" : ""}`} aria-labelledby="timeline-readiness-title"><div className="movie-production-section-heading"><div><span className="movie-workspace-kicker">Editorial hand-off</span><h3 id="timeline-readiness-title">Timeline readiness</h3><p>{model.timelineSummary} A timeline is only ready when each shot points to a selected take.</p></div><span className="movie-production-timeline-status">{model.timelineReady ? <><CheckCircle2 size={14} /> Ready to edit</> : <><TimerReset size={14} /> Still in production</>}</span></div>{model.timeline.length ? <div className="movie-production-timeline-rows">{model.timeline.map((row) => <div className={`movie-production-timeline-row is-${row.readiness}`} key={row.sceneId}><span className="movie-production-timeline-sequence">{String(row.sequence).padStart(2, "0")}</span><div><strong>{row.title}</strong><small>{row.detail} · {row.durationSeconds ? `${row.durationSeconds}s planned` : "Duration not set"}</small></div><ReadinessBadge readiness={row.readiness} label={row.readiness === "ready" ? "Ready" : row.readiness === "needs-review" ? "Needs review" : row.readiness === "blocked" ? "Blocked" : "In progress"} /></div>)}</div> : <ProductionEmptyState filter="all" hasShots={false} />}</section>;
}

function ProductionEmptyState({ filter, hasShots }: { filter: ProductionWorkspaceFilter; hasShots: boolean }) {
  const message = !hasShots ? "Start in Scenes with an approved shot plan. This room will follow it automatically." : filter === "needs-review" ? "No shots are waiting for review." : filter === "ready" ? "No shot is timeline-ready yet. Select a take to unlock the hand-off." : "No blocked shots in this view.";
  return <div className="movie-production-empty-state"><Clapperboard size={21} /><strong>{!hasShots ? "No approved shot plan yet" : "Nothing needs attention here"}</strong><p>{message}</p></div>;
}

function ReadinessBadge({ readiness, label }: { readiness: ProductionReadiness; label: string }) { return <span className={`movie-production-readiness-badge is-${readiness}`}>{readiness === "ready" ? <Check size={12} /> : readiness === "needs-review" ? <CircleAlert size={12} /> : readiness === "blocked" ? <X size={12} /> : <LoaderCircle size={12} />} {label}</span>; }
function StageCard({ label, state, tone }: { label: string; state: string; tone: "ready" | "review" | "next" | "blocked" }) { return <div className={`movie-production-stage-card is-${tone}`}><span>{label}</span><strong>{state}</strong></div>; }
function SummaryMetric({ label, value, detail }: { label: string; value: string; detail: string }) { return <div className="movie-production-summary-metric"><span>{label}</span><strong>{value}</strong><small>{detail}</small></div>; }
