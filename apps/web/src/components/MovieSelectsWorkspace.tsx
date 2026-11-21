"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import { AlertTriangle, Check, CheckCircle2, Clock3, Film, GitBranch, ListVideo, Plus, RefreshCw, ShieldAlert, Sparkles, X } from "lucide-react";
import { api, type MovieMissingInsertDecision, type MovieMissingInsertPlan, type MovieProject, type MovieTakeSelectRecord, type MovieTimeline } from "@/lib/api";
import { assetFileUrl } from "@/lib/apiBase";
import { useLocale } from "@/components/LocaleProvider";
import { buildMovieSubclips, buildSalvageRecommendations, formatSalvageTime, type MovieSubclip, type SalvageDecision, type SalvageRecommendation } from "@/lib/movieSalvage";

type SelectsFilter = "all" | "needs-review" | "accepted" | "timeline-ready";
type RangeOverride = { sourceInMilliseconds: number; sourceOutMilliseconds: number };
const filters: Array<{ id: SelectsFilter; label: string }> = [
  { id: "all", label: "All usable takes" },
  { id: "needs-review", label: "Needs review" },
  { id: "accepted", label: "Accepted" },
  { id: "timeline-ready", label: "Timeline ready" },
];

/**
 * Selects and salvage. A bounded range is persisted and reviewed first. The
 * canonical insert is never constructed in the browser: the server planner
 * materializes a grounded decision and the server applies it only if its
 * revision, continuity, source, and approval evidence are still fresh.
 */
export function MovieSelectsWorkspace({ project }: { project: MovieProject }) {
  const { t } = useLocale();
  const [timeline, setTimeline] = useState<MovieTimeline | null>(null);
  const [serverPlan, setServerPlan] = useState<MovieMissingInsertPlan | null>(null);
  const [serverDecisions, setServerDecisions] = useState<MovieMissingInsertDecision[]>([]);
  const [filter, setFilter] = useState<SelectsFilter>("all");
  const [decisions, setDecisions] = useState<Record<string, SalvageDecision>>({});
  const [rangeOverrides, setRangeOverrides] = useState<Record<string, RangeOverride>>({});
  const [selectsByTake, setSelectsByTake] = useState<Record<string, MovieTakeSelectRecord[]>>({});
  const [proposalId, setProposalId] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [reviewing, setReviewing] = useState(false);
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");
  const subclips = useMemo(() => buildMovieSubclips(project), [project]);

  const loadTimeline = useCallback(async () => {
    try {
      const value = await api.getMovieTimeline(project.id);
      setTimeline(value);
      return value;
    } catch {
      setTimeline(null);
      return null;
    }
  }, [project.id]);

  const loadSelects = useCallback(async () => {
    const entries = await Promise.all(subclips.map(async (subclip) => {
      const records = await api.getMovieTakeSelects(project.id, subclip.takeId).catch(() => []);
      return [subclip.takeId, records] as const;
    }));
    setSelectsByTake(Object.fromEntries(entries));
  }, [project.id, subclips]);

  const loadServerControl = useCallback(async (revisionId?: string | null) => {
    try {
      const [plan, persistedDecisions] = await Promise.all([
        api.getMovieMissingInsertPlan(project.id, revisionId),
        api.getMovieMissingInsertDecisions(project.id),
      ]);
      setServerPlan(plan);
      setServerDecisions(persistedDecisions);
    } catch {
      setServerPlan(null);
      setServerDecisions([]);
    }
  }, [project.id]);

  const reload = useCallback(async () => {
    const currentTimeline = await loadTimeline();
    await Promise.all([loadSelects(), loadServerControl(currentTimeline?.currentRevision?.id)]);
  }, [loadSelects, loadServerControl, loadTimeline]);

  useEffect(() => { void Promise.resolve().then(() => reload()); }, [reload]);

  const recommendations = useMemo(() => buildSalvageRecommendations(project).map((recommendation) => ({
    ...recommendation,
    decision: decisions[recommendation.id] ?? recommendation.decision,
  })), [decisions, project]);
  const visibleRecommendations = recommendations.filter((recommendation) => matchesFilter(recommendation, filter));
  const acceptedCount = recommendations.filter((recommendation) => recommendation.decision === "accepted").length;
  const readyCount = recommendations.filter((recommendation) => approvedSelectFor(recommendation.subclip, selectsByTake) !== null).length;
  const selectedRecommendation = recommendations.find((recommendation) => recommendation.id === proposalId) ?? null;
  const selectedRange = selectedRecommendation ? rangeFor(selectedRecommendation.subclip, rangeOverrides) : null;
  const selectedApprovedSelect = selectedRecommendation ? approvedSelectFor(selectedRecommendation.subclip, selectsByTake) : null;
  const selectedPendingSelect = selectedRecommendation ? pendingSelectFor(selectedRecommendation.subclip, selectsByTake) : null;
  const selectedServerDecision = selectedRecommendation ? serverDecisionFor(selectedRecommendation.subclip, selectedApprovedSelect, serverDecisions) : null;
  const timelineItems = timeline?.currentRevision?.tracks.flatMap((track) => track.items) ?? [];
  const proposalCanWrite = Boolean(
    selectedRecommendation && selectedRecommendation.subclip.selected && ["Approved", "Selected"].includes(selectedRecommendation.subclip.status)
      && selectedApprovedSelect && selectedServerDecision?.status === "Approved"
      && selectedServerDecision.anchorTakeId === selectedRecommendation.subclip.takeId
      && selectedApprovedSelect.durationMilliseconds === selectedServerDecision.durationMilliseconds,
  );

  function updateRange(subclipId: string, key: keyof RangeOverride, value: number) {
    const recommendation = recommendations.find((item) => item.subclip.id === subclipId);
    if (!recommendation) return;
    const current = rangeFor(recommendation.subclip, rangeOverrides);
    setRangeOverrides((previous) => ({ ...previous, [subclipId]: { ...current, [key]: Math.max(0, Math.round(value || 0)) } }));
  }

  async function decide(recommendation: SalvageRecommendation, decision: SalvageDecision) {
    setError("");
    setMessage("");
    if (decision === "accepted") {
      setBusy(true);
      try {
        const range = rangeFor(recommendation.subclip, rangeOverrides);
        if (range.sourceOutMilliseconds <= range.sourceInMilliseconds) throw new Error("Choose a positive source range before saving it.");
        const existing = (selectsByTake[recommendation.subclip.takeId] ?? []).find((record) => record.startMilliseconds === range.sourceInMilliseconds && record.endMilliseconds === range.sourceOutMilliseconds);
        const record = existing ?? await api.createMovieTakeSelect(project.id, recommendation.subclip.takeId, {
          label: `Salvage range · ${recommendation.subclip.label}`,
          startMilliseconds: range.sourceInMilliseconds,
          endMilliseconds: range.sourceOutMilliseconds,
          notes: recommendation.summary,
        });
        setSelectsByTake((current) => ({ ...current, [recommendation.subclip.takeId]: existing ? current[recommendation.subclip.takeId] ?? [] : [record, ...(current[recommendation.subclip.takeId] ?? [])] }));
        setDecisions((previous) => ({ ...previous, [recommendation.id]: "accepted" }));
        setProposalId(recommendation.id);
        setMessage(record.status === "Approved" ? "Bounded range saved and approved. The server proposal still requires review before apply." : "Bounded range saved as a draft select. Approve the persisted range before the server proposal can be reviewed.");
      } catch (cause) {
        setError(cause instanceof Error ? cause.message : "The bounded range could not be saved.");
      } finally {
        setBusy(false);
      }
      return;
    }
    setDecisions((previous) => ({ ...previous, [recommendation.id]: decision }));
    setMessage("Recommendation rejected. The original take remains untouched.");
    if (proposalId === recommendation.id) setProposalId(null);
  }

  async function prepareServerProposal(recommendation: SalvageRecommendation) {
    setBusy(true);
    setError("");
    setMessage("");
    try {
      const plan = await api.getMovieMissingInsertPlan(project.id, timeline?.currentRevision?.id);
      if (!plan.timelineRevisionId) throw new Error("The server planner needs a current canonical timeline revision before it can propose an insert.");
      const materialized = await api.materializeMovieMissingInsertDecisions(project.id, { timelineRevisionId: plan.timelineRevisionId });
      const decision = materialized.find((item) => item.anchorTakeId === recommendation.subclip.takeId && item.status !== "Applied");
      if (!decision) throw new Error("The server planner found no grounded gap for this take. No client-authored insert was created.");
      setServerPlan(plan);
      setServerDecisions(materialized);
      setProposalId(recommendation.id);
      setMessage("Server-grounded proposal loaded. Review the bounded range and the durable decision before applying it.");
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "The server proposal could not be prepared.");
    } finally {
      setBusy(false);
    }
  }

  async function reviewRange(recommendation: SalvageRecommendation, record: MovieTakeSelectRecord, decision: "Approved" | "Rejected") {
    setReviewing(true);
    setError("");
    setMessage("");
    try {
      const updated = await api.reviewMovieTakeSelect(project.id, recommendation.subclip.takeId, record.id, { decision });
      setSelectsByTake((current) => ({ ...current, [recommendation.subclip.takeId]: (current[recommendation.subclip.takeId] ?? []).map((item) => item.id === updated.id ? updated : item) }));
      setMessage(decision === "Approved" ? "Bounded range approved. Review the server-grounded insert decision next." : "Bounded range rejected.");
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "The range review could not be saved.");
    } finally {
      setReviewing(false);
    }
  }

  async function reviewServerDecision() {
    if (!selectedServerDecision || !selectedRecommendation || !selectedApprovedSelect) return;
    if (selectedApprovedSelect.durationMilliseconds !== selectedServerDecision.durationMilliseconds) {
      setError("The approved source range must exactly cover the server-planned gap before the insert can be approved.");
      return;
    }
    setReviewing(true);
    setError("");
    setMessage("");
    try {
      const updated = await api.reviewMovieMissingInsertDecision(selectedServerDecision.id, { decision: "Approved", takeId: selectedServerDecision.anchorTakeId, selectId: selectedApprovedSelect.id });
      setServerDecisions((current) => current.map((item) => item.id === updated.id ? updated : item));
      setMessage("Server-grounded insert approved. Apply it to let the server re-check the current revision and continuity evidence.");
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "The server insert decision could not be approved.");
    } finally {
      setReviewing(false);
    }
  }

  async function applyInsert() {
    if (!selectedServerDecision || !proposalCanWrite) return;
    setBusy(true);
    setError("");
    setMessage("");
    try {
      const applied = await api.applyMovieMissingInsertDecision(selectedServerDecision.id);
      setServerDecisions((current) => current.map((item) => item.id === applied.id ? applied : item));
      await reload();
      setMessage("Server-approved minimal insert applied to a new draft timeline revision. No footage was copied or regenerated.");
      setProposalId(null);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "The server insert could not be applied. The revision may be stale.");
    } finally {
      setBusy(false);
    }
  }

  return <div className="movie-selects-workspace" data-testid="movie-selects-workspace">
    <section className="movie-selects-command" aria-labelledby="movie-selects-title"><div><span className="movie-workspace-kicker">{t("movieBody.selects.salvage")}</span><h3 id="movie-selects-title">{t("movieBody.selects.keepSeconds")}</h3><p>Review generated takes as raw footage. Persist and approve a bounded range first; the server then plans and applies only a grounded minimal insert.</p></div><div className="movie-selects-command-mark"><ListVideo size={23} /><span>NON-DESTRUCTIVE</span></div></section>
    <section className="movie-selects-summary" aria-label="Selects summary"><SelectsMetric label="Usable takes" value={subclips.length} detail="private outputs" /><SelectsMetric label="Recommendations" value={recommendations.length} detail="review only" /><SelectsMetric label="Accepted" value={acceptedCount} detail="range persisted" /><SelectsMetric label="Range approved" value={readyCount} detail="cleared for timeline" /></section>
    <section className="movie-selects-boundary"><ShieldAlert size={16} /><div><strong>Salvage before regenerate</strong><span>A bounded range becomes a persisted, reviewable select. Accepting a recommendation does not start generation or replace the source take.</span></div></section>
    {message && <div className="movie-selects-notice is-success" role="status"><CheckCircle2 size={15} /><span>{message}</span></div>}
    {error && <div className="movie-selects-notice is-error" role="alert"><AlertTriangle size={15} /><span>{error}</span><button type="button" onClick={() => setError("")} aria-label="Dismiss error"><X size={13} /></button></div>}
    <section className="movie-selects-review" aria-labelledby="movie-selects-review-title"><div className="movie-selects-section-heading"><div><span className="movie-workspace-kicker">{t("movieDeep.reviewQueue")}</span><h3 id="movie-selects-review-title">{t("movieDeep.findSmallerFix")}</h3><p>Each card points to one persisted take and one bounded source range. The final insert is always planned by the server.</p></div><button type="button" className="movie-workspace-button is-quiet" onClick={() => void reload()}><RefreshCw size={13} /> {t("movieBody.refresh")}</button></div><div className="movie-selects-filters" role="tablist" aria-label="Filter select recommendations">{filters.map((item) => <button key={item.id} type="button" role="tab" aria-selected={filter === item.id} className={filter === item.id ? "is-active" : ""} onClick={() => setFilter(item.id)}>{item.label}<span>{filterCount(recommendations, item.id, selectsByTake)}</span></button>)}</div>{visibleRecommendations.length ? <div className="movie-selects-list">{visibleRecommendations.map((recommendation) => <SalvageRecommendationCard key={recommendation.id} recommendation={recommendation} range={rangeFor(recommendation.subclip, rangeOverrides)} selects={selectsByTake[recommendation.subclip.takeId] ?? []} onRangeChange={(key, value) => updateRange(recommendation.subclip.id, key, value)} onDecision={(decision) => void decide(recommendation, decision)} onPropose={() => void prepareServerProposal(recommendation)} proposing={proposalId === recommendation.id} busy={busy} />)}</div> : <SelectsEmptyState hasTakes={subclips.length > 0} />}</section>
    {selectedRecommendation && selectedRange && <InsertProposalPanel recommendation={selectedRecommendation} range={selectedRange} approvedSelect={selectedApprovedSelect} pendingSelect={selectedPendingSelect} serverPlan={serverPlan} serverDecision={selectedServerDecision} onApply={() => void applyInsert()} onApproveDecision={() => void reviewServerDecision()} onReview={(record, decision) => void reviewRange(selectedRecommendation, record, decision)} busy={busy} reviewing={reviewing} canWrite={proposalCanWrite} />}
    <section className="movie-selects-timeline" aria-labelledby="movie-selects-timeline-title"><div className="movie-selects-section-heading"><div><span className="movie-workspace-kicker">Canonical timeline hand-off</span><h3 id="movie-selects-timeline-title">Keep the edit model authoritative.</h3><p>{timeline?.currentRevision ? `Draft revision ${timeline.currentRevision.revisionNumber} · ${formatSalvageTime(timeline.currentRevision.durationMilliseconds)} total` : "No timeline revision exists yet. The server planner will wait for a canonical revision."}</p></div><GitBranch size={18} /></div>{timelineItems.length ? <div className="movie-selects-timeline-items">{timelineItems.map((item) => <div className="movie-selects-timeline-item" key={item.id}><span>{formatSalvageTime(item.timelineInMilliseconds)}–{formatSalvageTime(item.timelineOutMilliseconds)}</span><strong>{item.label || (item.isGap ? "Gap" : "Timeline item")}</strong><small>{item.kind === "VisualTake" ? "Visual take / subclip" : item.kind}</small></div>)}</div> : <div className="movie-selects-timeline-empty"><Film size={17} /><span>The existing timeline stays unchanged until a server-grounded decision is approved and applied.</span></div>}</section>
    <div className="movie-selects-footnote"><Clock3 size={14} /><span>Time values are source ranges in milliseconds. The full NLE remains out of scope; this room prepares reviewed, non-destructive hand-offs for the existing timeline.</span></div>
  </div>;
}

function approvedSelectFor(subclip: MovieSubclip, selectsByTake: Record<string, MovieTakeSelectRecord[]>) { return (selectsByTake[subclip.takeId] ?? []).find((record) => record.status === "Approved") ?? null; }
function pendingSelectFor(subclip: MovieSubclip, selectsByTake: Record<string, MovieTakeSelectRecord[]>) { return (selectsByTake[subclip.takeId] ?? []).find((record) => record.status !== "Approved" && record.status !== "Rejected") ?? null; }
function serverDecisionFor(subclip: MovieSubclip, approvedSelect: MovieTakeSelectRecord | null, decisions: MovieMissingInsertDecision[]) { return decisions.find((decision) => decision.anchorTakeId === subclip.takeId && decision.status !== "Applied" && (!decision.anchorSelectId || decision.anchorSelectId === approvedSelect?.id)) ?? decisions.find((decision) => decision.anchorTakeId === subclip.takeId && decision.status !== "Applied") ?? null; }
function SalvageRecommendationCard({ recommendation, range, selects, onRangeChange, onDecision, onPropose, proposing, busy }: { recommendation: SalvageRecommendation; range: RangeOverride; selects: MovieTakeSelectRecord[]; onRangeChange: (key: keyof RangeOverride, value: number) => void; onDecision: (decision: SalvageDecision) => void; onPropose: () => void; proposing: boolean; busy: boolean }) {
  const { subclip } = recommendation;
  const decisionTone = recommendation.decision === "accepted" ? "is-accepted" : recommendation.decision === "rejected" ? "is-rejected" : "";
  const approved = selects.find((record) => record.status === "Approved") ?? null;
  const pending = selects.find((record) => record.status !== "Approved" && record.status !== "Rejected") ?? null;
  return <article className={`movie-selects-card ${decisionTone}`} aria-labelledby={`salvage-${recommendation.id}`}><header className="movie-selects-card-header"><div><span className="movie-selects-card-kicker">Scene {String(subclip.sceneSequence).padStart(2, "0")} · Shot {String(subclip.shotSequence).padStart(2, "0")}</span><h4 id={`salvage-${recommendation.id}`}>{recommendation.title}</h4><p>{subclip.shotDescription}</p></div><DecisionBadge decision={recommendation.decision} /></header><div className="movie-selects-card-body"><div className="movie-selects-preview">{subclip.assetId ? <video src={assetFileUrl(subclip.assetId, true)} controls preload="metadata" aria-label={`${subclip.label} source preview`} /> : <Film size={18} />}</div><div className="movie-selects-card-copy"><p>{recommendation.summary}</p><div className="movie-selects-rationale">{recommendation.rationale.map((reason) => <span key={reason}>{reason}</span>)}</div>{selects.length > 0 && <div className="movie-selects-rationale">{selects.slice(0, 2).map((record) => <span key={record.id}>{record.status === "Approved" ? "Approved range" : record.status} · {formatSalvageTime(record.startMilliseconds)}–{formatSalvageTime(record.endMilliseconds)}</span>)}</div>}</div><div className="movie-selects-range"><span className="movie-inspector-label">Usable source range</span><div><label>In<input type="number" min={0} value={range.sourceInMilliseconds} onChange={(event) => onRangeChange("sourceInMilliseconds", Number(event.target.value))} /><em>ms</em></label><span>→</span><label>Out<input type="number" min={1} max={subclip.durationMilliseconds} value={range.sourceOutMilliseconds} onChange={(event) => onRangeChange("sourceOutMilliseconds", Number(event.target.value))} /><em>ms</em></label></div><small>{formatSalvageTime(range.sourceInMilliseconds)} – {formatSalvageTime(range.sourceOutMilliseconds)} · {formatSalvageTime(Math.max(0, range.sourceOutMilliseconds - range.sourceInMilliseconds))}</small></div></div>{subclip.issues.length > 0 && <div className="movie-selects-qc"><span className="movie-inspector-label">QC evidence</span><div>{subclip.issues.map((issue) => <span key={issue.code} className={`is-${issue.severity}`}><AlertTriangle size={11} /><strong>{issue.label}</strong><small>{issue.detail}</small></span>)}</div></div>}<footer className="movie-selects-card-actions"><span>{subclip.selected ? "Selected take" : "Not selected"}{subclip.finalized ? " · Final take" : ""}{approved ? " · Range approved" : pending ? " · Range awaiting review" : ""}</span>{recommendation.decision === "rejected" ? <button type="button" className="movie-text-action" onClick={() => onDecision("pending")}>Reconsider</button> : <><button type="button" className="movie-text-action is-danger" onClick={() => onDecision("rejected")}><X size={12} /> Reject</button>{recommendation.decision !== "accepted" && <button type="button" className="movie-workspace-button is-secondary" onClick={() => onDecision("accepted")} disabled={busy}><Check size={12} /> Accept and persist range</button>}{recommendation.decision === "accepted" && <button type="button" className="movie-workspace-button is-primary" onClick={onPropose} disabled={busy}><Plus size={12} /> {proposing ? "Review server proposal below" : "Propose minimal insert"}</button>}</>}</footer></article>;
}
function InsertProposalPanel({ recommendation, range, approvedSelect, pendingSelect, serverPlan, serverDecision, onApply, onApproveDecision, onReview, busy, reviewing, canWrite }: { recommendation: SalvageRecommendation; range: RangeOverride; approvedSelect: MovieTakeSelectRecord | null; pendingSelect: MovieTakeSelectRecord | null; serverPlan: MovieMissingInsertPlan | null; serverDecision: MovieMissingInsertDecision | null; onApply: () => void; onApproveDecision: () => void; onReview: (record: MovieTakeSelectRecord, decision: "Approved" | "Rejected") => void; busy: boolean; reviewing: boolean; canWrite: boolean }) {
  const { t } = useLocale();
  const durationMatches = Boolean(approvedSelect && serverDecision && approvedSelect.durationMilliseconds === serverDecision.durationMilliseconds);
  const decisionPending = serverDecision?.status === "PendingReview";
  const decisionApproved = serverDecision?.status === "Approved";
  const plannerHasProposal = Boolean(serverPlan?.proposals.some((proposal) => proposal.anchorTakeId === recommendation.subclip.takeId));
  return <section className="movie-selects-proposal" aria-labelledby="movie-selects-proposal-title"><div className="movie-selects-proposal-heading"><div><span className="movie-workspace-kicker">{t("movieBody.selects.serverPlan")} · insert proposal</span><h3 id="movie-selects-proposal-title">Add only the missing beat.</h3><p>The browser never constructs a timeline revision. The server planner records the gap, its continuity anchors, and the approved source before apply.</p></div><Sparkles size={18} /></div><div className="movie-selects-proposal-grid"><div><span>Source range</span><strong>{formatSalvageTime(range.sourceInMilliseconds)} – {formatSalvageTime(range.sourceOutMilliseconds)}</strong><small>{formatSalvageTime(Math.max(0, range.sourceOutMilliseconds - range.sourceInMilliseconds))} persisted select duration</small></div><div><span>{t("movieBody.selects.serverPlan")}</span><strong>{serverDecision ? `${serverDecision.status} · ${formatSalvageTime(serverDecision.durationMilliseconds)} gap` : plannerHasProposal ? t("movieBody.selects.serverPlanPending") : t("movieBody.selects.serverPlanUnavailable")}</strong><small>{serverDecision ? "Revision and continuity are re-checked on apply." : "No client-supplied timeline position is accepted."}</small></div><div><span>Persisted select</span><strong>{approvedSelect ? `Approved · ${formatSalvageTime(approvedSelect.startMilliseconds)}–${formatSalvageTime(approvedSelect.endMilliseconds)}` : pendingSelect ? "Draft · review required" : "Not saved"}</strong><small>{durationMatches ? "The range exactly matches the server-planned gap." : serverDecision ? "The approved range must exactly match the server-planned gap." : "Persist and approve the bounded range first."}</small></div></div><div className="movie-selects-proposal-actions">{pendingSelect && <button type="button" className="movie-workspace-button is-secondary" onClick={() => onReview(pendingSelect, "Approved")} disabled={reviewing}><Check size={12} /> {t("movieBody.selects.approveRange")}</button>}{approvedSelect && <button type="button" className="movie-text-action is-danger" onClick={() => onReview(approvedSelect, "Rejected")} disabled={reviewing}><X size={12} /> Withdraw range approval</button>}{decisionPending && approvedSelect && <button type="button" className="movie-workspace-button is-secondary" onClick={onApproveDecision} disabled={reviewing || !durationMatches}><Check size={12} /> {t("movieBody.selects.approveInsert")}</button>}{decisionApproved && !canWrite && <span>The server proposal is approved, but source selection or range evidence is no longer ready.</span>}{decisionApproved && <button type="button" className="movie-workspace-button is-primary" onClick={onApply} disabled={busy || !canWrite}>{busy ? "Applying server decision…" : t("movieBody.selects.applyInsert")}</button>}</div></section>;
}
function rangeFor(subclip: MovieSubclip, overrides: Record<string, RangeOverride>): RangeOverride { return overrides[subclip.id] ?? { sourceInMilliseconds: subclip.sourceInMilliseconds, sourceOutMilliseconds: subclip.sourceOutMilliseconds }; }
function matchesFilter(recommendation: SalvageRecommendation, filter: SelectsFilter) { if (filter === "needs-review") return recommendation.subclip.issues.length > 0 || recommendation.decision === "pending"; if (filter === "accepted") return recommendation.decision === "accepted"; if (filter === "timeline-ready") return recommendation.subclip.selected && ["Approved", "Selected"].includes(recommendation.subclip.status); return true; }
function filterCount(recommendations: SalvageRecommendation[], filter: SelectsFilter, selectsByTake: Record<string, MovieTakeSelectRecord[]>) { if (filter === "timeline-ready") return recommendations.filter((recommendation) => approvedSelectFor(recommendation.subclip, selectsByTake) !== null).length; return recommendations.filter((recommendation) => matchesFilter(recommendation, filter)).length; }
function DecisionBadge({ decision }: { decision: SalvageDecision }) { const label = decision === "accepted" ? "Accepted" : decision === "rejected" ? "Rejected" : "Review needed"; return <span className={`movie-selects-decision-badge is-${decision}`}>{decision === "accepted" ? <Check size={11} /> : decision === "rejected" ? <X size={11} /> : <AlertTriangle size={11} />}{label}</span>; }
function SelectsMetric({ label, value, detail }: { label: string; value: number; detail: string }) { return <div className="movie-selects-metric"><span>{label}</span><strong>{value}</strong><small>{detail}</small></div>; }
function SelectsEmptyState({ hasTakes }: { hasTakes: boolean }) { return <div className="movie-selects-empty"><ListVideo size={20} /><strong>{hasTakes ? "No recommendations match this view" : "No reviewable takes yet"}</strong><p>{hasTakes ? "Try another filter or return to All usable takes." : "A Selects card appears after a real render has produced a private take with a usable duration. Nothing is fabricated here."}</p></div>; }
