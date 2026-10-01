"use client";
/* eslint-disable react-hooks/set-state-in-effect -- timeline read model synchronizes with the active movie project. */

import { useCallback, useEffect, useMemo, useState } from "react";
import {
  AlertTriangle,
  Check,
  CheckCircle2,
  Clock3,
  Film,
  GitBranch,
  ListVideo,
  Plus,
  RefreshCw,
  ShieldAlert,
  Sparkles,
  X,
} from "lucide-react";
import { api, type MovieProject, type MovieTimeline } from "@/lib/api";
import { assetFileUrl } from "@/lib/apiBase";
import {
  buildMovieSubclips,
  buildSalvageRecommendations,
  buildSalvageTimelineRevision,
  formatSalvageTime,
  type MovieSubclip,
  type SalvageDecision,
  type SalvageInsertProposal,
  type SalvageRecommendation,
} from "@/lib/movieSalvage";

type SelectsFilter = "all" | "needs-review" | "accepted" | "timeline-ready";
type RangeOverride = { sourceInMilliseconds: number; sourceOutMilliseconds: number };
const filters: Array<{ id: SelectsFilter; label: string }> = [
  { id: "all", label: "All usable takes" },
  { id: "needs-review", label: "Needs review" },
  { id: "accepted", label: "Accepted" },
  { id: "timeline-ready", label: "Timeline ready" },
];

export function MovieSelectsWorkspace({ project }: { project: MovieProject }) {
  const [timeline, setTimeline] = useState<MovieTimeline | null>(null);
  const [filter, setFilter] = useState<SelectsFilter>("all");
  const [decisions, setDecisions] = useState<Record<string, SalvageDecision>>({});
  const [rangeOverrides, setRangeOverrides] = useState<Record<string, RangeOverride>>({});
  const [proposalId, setProposalId] = useState<string | null>(null);
  const [timelinePosition, setTimelinePosition] = useState(0);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");

  const loadTimeline = useCallback(async () => {
    try {
      setTimeline(await api.getMovieTimeline(project.id));
    } catch {
      // A timeline is created by the first accepted insert. An empty Selects room is valid.
      setTimeline(null);
    }
  }, [project.id]);

  useEffect(() => {
    void loadTimeline();
  }, [loadTimeline]);

  const subclips = useMemo(() => buildMovieSubclips(project), [project]);
  const recommendations = useMemo(
    () => buildSalvageRecommendations(project).map((recommendation) => ({
      ...recommendation,
      decision: decisions[recommendation.id] ?? recommendation.decision,
    })),
    [decisions, project],
  );
  const visibleRecommendations = recommendations.filter((recommendation) => matchesFilter(recommendation, filter));
  const acceptedCount = recommendations.filter((recommendation) => recommendation.decision === "accepted").length;
  const readyCount = recommendations.filter((recommendation) => recommendation.subclip.selected && ["Approved", "Selected"].includes(recommendation.subclip.status)).length;
  const selectedRecommendation = recommendations.find((recommendation) => recommendation.id === proposalId) ?? null;
  const selectedRange = selectedRecommendation ? rangeFor(selectedRecommendation.subclip, rangeOverrides) : null;
  const timelineItems = timeline?.currentRevision?.tracks.flatMap((track) => track.items) ?? [];
  const proposalCanWrite = Boolean(selectedRecommendation && selectedRange && selectedRecommendation.subclip.selected && ["Approved", "Selected"].includes(selectedRecommendation.subclip.status));

  function updateRange(subclipId: string, key: keyof RangeOverride, value: number) {
    const recommendation = recommendations.find((item) => item.subclip.id === subclipId);
    if (!recommendation) return;
    const current = rangeFor(recommendation.subclip, rangeOverrides);
    setRangeOverrides((previous) => ({ ...previous, [subclipId]: { ...current, [key]: Math.max(0, Math.round(value || 0)) } }));
  }

  function decide(recommendation: SalvageRecommendation, decision: SalvageDecision) {
    setDecisions((previous) => ({ ...previous, [recommendation.id]: decision }));
    setError("");
    setMessage(decision === "accepted" ? "Recommendation accepted. Review the minimal insert before writing a timeline revision." : "Recommendation rejected. The original take remains untouched.");
    if (decision === "accepted") {
      setProposalId(recommendation.id);
      setTimelinePosition(timeline?.currentRevision?.durationMilliseconds ?? 0);
    } else if (proposalId === recommendation.id) {
      setProposalId(null);
    }
  }

  async function applyInsert() {
    if (!selectedRecommendation || !selectedRange) return;
    setBusy(true);
    setError("");
    setMessage("");
    const proposal: SalvageInsertProposal = {
      recommendationId: selectedRecommendation.id,
      takeId: selectedRecommendation.subclip.takeId,
      sourceInMilliseconds: selectedRange.sourceInMilliseconds,
      sourceOutMilliseconds: selectedRange.sourceOutMilliseconds,
      timelineInMilliseconds: Math.max(0, Math.round(timelinePosition || 0)),
      label: `Salvage insert · ${selectedRecommendation.subclip.label}`,
    };
    try {
      await api.createMovieTimelineRevision(project.id, buildSalvageTimelineRevision(timeline, proposal, selectedRecommendation));
      await loadTimeline();
      setMessage("Minimal insert saved to a new draft timeline revision. No footage was copied or regenerated.");
      setProposalId(null);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "The insert could not be added to the draft timeline.");
    } finally {
      setBusy(false);
    }
  }

  return <div className="movie-selects-workspace" data-testid="movie-selects-workspace">
    <section className="movie-selects-command" aria-labelledby="movie-selects-title">
      <div>
        <span className="movie-workspace-kicker">Selects / subclips</span>
        <h3 id="movie-selects-title">Keep the good seconds.</h3>
        <p>Review generated takes as raw footage. Salvage a usable range first, carry the QC evidence with it, and only add a small insert to the canonical timeline when the decision is deliberate.</p>
      </div>
      <div className="movie-selects-command-mark"><ListVideo size={23} /><span>NON-DESTRUCTIVE</span></div>
    </section>

    <section className="movie-selects-summary" aria-label="Selects summary">
      <SelectsMetric label="Usable takes" value={subclips.length} detail="private outputs" />
      <SelectsMetric label="Recommendations" value={recommendations.length} detail="review only" />
      <SelectsMetric label="Accepted" value={acceptedCount} detail="awaiting insert review" />
      <SelectsMetric label="Timeline ready" value={readyCount} detail="approved + selected" />
    </section>

    <section className="movie-selects-boundary"><ShieldAlert size={16} /><div><strong>Salvage before regenerate</strong><span>Ranges and recommendations are review data. Accepting a recommendation does not start generation or replace the source take.</span></div></section>

    {message && <div className="movie-selects-notice is-success" role="status"><CheckCircle2 size={15} /><span>{message}</span></div>}
    {error && <div className="movie-selects-notice is-error" role="alert"><AlertTriangle size={15} /><span>{error}</span><button type="button" onClick={() => setError("")} aria-label="Dismiss error"><X size={13} /></button></div>}

    <section className="movie-selects-review" aria-labelledby="movie-selects-review-title">
      <div className="movie-selects-section-heading"><div><span className="movie-workspace-kicker">Salvage Director · review queue</span><h3 id="movie-selects-review-title">Find a smaller fix than a new scene.</h3><p>Each card points to one persisted take and one bounded source range. Review stays grounded in the project record.</p></div><button type="button" className="movie-workspace-button is-quiet" onClick={() => void loadTimeline()}><RefreshCw size={13} /> Refresh timeline</button></div>
      <div className="movie-selects-filters" role="tablist" aria-label="Filter select recommendations">{filters.map((item) => <button key={item.id} type="button" role="tab" aria-selected={filter === item.id} className={filter === item.id ? "is-active" : ""} onClick={() => setFilter(item.id)}>{item.label}<span>{filterCount(recommendations, item.id)}</span></button>)}</div>
      {visibleRecommendations.length ? <div className="movie-selects-list">{visibleRecommendations.map((recommendation) => <SalvageRecommendationCard key={recommendation.id} recommendation={recommendation} range={rangeFor(recommendation.subclip, rangeOverrides)} onRangeChange={(key, value) => updateRange(recommendation.subclip.id, key, value)} onDecision={(decision) => decide(recommendation, decision)} onPropose={() => { setProposalId(recommendation.id); setTimelinePosition(timeline?.currentRevision?.durationMilliseconds ?? 0); setMessage(""); }} proposing={proposalId === recommendation.id} />)}</div> : <SelectsEmptyState hasTakes={subclips.length > 0} filter={filter} />}
    </section>

    {selectedRecommendation && selectedRange && <InsertProposalPanel recommendation={selectedRecommendation} range={selectedRange} timelinePosition={timelinePosition} onTimelinePositionChange={setTimelinePosition} onApply={() => void applyInsert()} busy={busy} canWrite={proposalCanWrite} />}

    <section className="movie-selects-timeline" aria-labelledby="movie-selects-timeline-title">
      <div className="movie-selects-section-heading"><div><span className="movie-workspace-kicker">Canonical timeline hand-off</span><h3 id="movie-selects-timeline-title">Keep the edit model authoritative.</h3><p>{timeline?.currentRevision ? `Draft revision ${timeline.currentRevision.revisionNumber} · ${formatSalvageTime(timeline.currentRevision.durationMilliseconds)} total` : "No timeline revision exists yet. An accepted insert creates the first draft revision."}</p></div><GitBranch size={18} /></div>
      {timelineItems.length ? <div className="movie-selects-timeline-items">{timelineItems.map((item) => <div className="movie-selects-timeline-item" key={item.id}><span>{formatSalvageTime(item.timelineInMilliseconds)}–{formatSalvageTime(item.timelineOutMilliseconds)}</span><strong>{item.label || (item.isGap ? "Gap" : "Timeline item")}</strong><small>{item.kind === "VisualTake" ? "Visual take / subclip" : item.kind}</small></div>)}</div> : <div className="movie-selects-timeline-empty"><Film size={17} /><span>The existing timeline stays empty until a human accepts a minimal insert.</span></div>}
    </section>

    <div className="movie-selects-footnote"><Clock3 size={14} /><span>Time values are source ranges in milliseconds. The full NLE remains out of scope; this room prepares reviewable, non-destructive hand-offs for the existing timeline.</span></div>
  </div>;
}

function SalvageRecommendationCard({ recommendation, range, onRangeChange, onDecision, onPropose, proposing }: { recommendation: SalvageRecommendation; range: RangeOverride; onRangeChange: (key: keyof RangeOverride, value: number) => void; onDecision: (decision: SalvageDecision) => void; onPropose: () => void; proposing: boolean }) {
  const { subclip } = recommendation;
  const decisionTone = recommendation.decision === "accepted" ? "is-accepted" : recommendation.decision === "rejected" ? "is-rejected" : "";
  return <article className={`movie-selects-card ${decisionTone}`} aria-labelledby={`salvage-${recommendation.id}`}>
    <header className="movie-selects-card-header"><div><span className="movie-selects-card-kicker">Scene {String(subclip.sceneSequence).padStart(2, "0")} · Shot {String(subclip.shotSequence).padStart(2, "0")}</span><h4 id={`salvage-${recommendation.id}`}>{recommendation.title}</h4><p>{subclip.shotDescription}</p></div><DecisionBadge decision={recommendation.decision} /></header>
    <div className="movie-selects-card-body">
      <div className="movie-selects-preview">{subclip.assetId ? <video src={assetFileUrl(subclip.assetId, true)} controls preload="metadata" aria-label={`${subclip.label} source preview`} /> : <Film size={18} />}</div>
      <div className="movie-selects-card-copy"><p>{recommendation.summary}</p><div className="movie-selects-rationale">{recommendation.rationale.map((reason) => <span key={reason}>{reason}</span>)}</div></div>
      <div className="movie-selects-range"><span className="movie-inspector-label">Usable source range</span><div><label>In<input type="number" min={0} value={range.sourceInMilliseconds} onChange={(event) => onRangeChange("sourceInMilliseconds", Number(event.target.value))} /><em>ms</em></label><span>→</span><label>Out<input type="number" min={1} max={subclip.durationMilliseconds} value={range.sourceOutMilliseconds} onChange={(event) => onRangeChange("sourceOutMilliseconds", Number(event.target.value))} /><em>ms</em></label></div><small>{formatSalvageTime(range.sourceInMilliseconds)} – {formatSalvageTime(range.sourceOutMilliseconds)} · {formatSalvageTime(Math.max(0, range.sourceOutMilliseconds - range.sourceInMilliseconds))}</small></div>
    </div>
    {subclip.issues.length > 0 && <div className="movie-selects-qc"><span className="movie-inspector-label">QC evidence</span><div>{subclip.issues.map((issue) => <span key={issue.code} className={`is-${issue.severity}`}><AlertTriangle size={11} /><strong>{issue.label}</strong><small>{issue.detail}</small></span>)}</div></div>}
    <footer className="movie-selects-card-actions"><span>{subclip.selected ? "Selected take" : "Not selected"}{subclip.finalized ? " · Final take" : ""}</span>{recommendation.decision === "rejected" ? <button type="button" className="movie-text-action" onClick={() => onDecision("pending")}>Reconsider</button> : <><button type="button" className="movie-text-action is-danger" onClick={() => onDecision("rejected")}><X size={12} /> Reject</button>{recommendation.decision !== "accepted" && <button type="button" className="movie-workspace-button is-secondary" onClick={() => onDecision("accepted")}><Check size={12} /> Accept recommendation</button>}{recommendation.decision === "accepted" && <button type="button" className="movie-workspace-button is-primary" onClick={onPropose}><Plus size={12} /> {proposing ? "Review insert below" : "Propose minimal insert"}</button>}</>}</footer>
  </article>;
}

function InsertProposalPanel({ recommendation, range, timelinePosition, onTimelinePositionChange, onApply, busy, canWrite }: { recommendation: SalvageRecommendation; range: RangeOverride; timelinePosition: number; onTimelinePositionChange: (value: number) => void; onApply: () => void; busy: boolean; canWrite: boolean }) {
  return <section className="movie-selects-proposal" aria-labelledby="movie-selects-proposal-title"><div className="movie-selects-proposal-heading"><div><span className="movie-workspace-kicker">Minimal insert proposal</span><h3 id="movie-selects-proposal-title">Add only the missing beat.</h3><p>{recommendation.title} will be added as a new draft revision using the existing take and selected range. Earlier timeline revisions remain immutable.</p></div><Sparkles size={18} /></div><div className="movie-selects-proposal-grid"><div><span>Source range</span><strong>{formatSalvageTime(range.sourceInMilliseconds)} – {formatSalvageTime(range.sourceOutMilliseconds)}</strong><small>{formatSalvageTime(Math.max(0, range.sourceOutMilliseconds - range.sourceInMilliseconds))} insert duration</small></div><label><span>Timeline position</span><input type="number" min={0} value={timelinePosition} onChange={(event) => onTimelinePositionChange(Number(event.target.value))} /><small>milliseconds · defaults to the end of the current draft</small></label><div><span>Decision boundary</span><strong>{canWrite ? "Ready for selected take" : "Select and approve this take first"}</strong><small>{canWrite ? "The server will re-check source ownership and bounds." : "The proposal stays review-only until the canonical take pointer is set."}</small></div></div><div className="movie-selects-proposal-actions"><button type="button" className="movie-workspace-button is-primary" onClick={onApply} disabled={busy || !canWrite}>{busy ? "Saving draft revision…" : "Apply to draft timeline"}</button>{!canWrite && <span>Open Production to approve and select this take before applying the insert.</span>}</div></section>;
}

function rangeFor(subclip: MovieSubclip, overrides: Record<string, RangeOverride>): RangeOverride {
  return overrides[subclip.id] ?? { sourceInMilliseconds: subclip.sourceInMilliseconds, sourceOutMilliseconds: subclip.sourceOutMilliseconds };
}

function matchesFilter(recommendation: SalvageRecommendation, filter: SelectsFilter) {
  if (filter === "needs-review") return recommendation.subclip.issues.length > 0 || recommendation.decision === "pending";
  if (filter === "accepted") return recommendation.decision === "accepted";
  if (filter === "timeline-ready") return recommendation.subclip.selected && ["Approved", "Selected"].includes(recommendation.subclip.status);
  return true;
}

function filterCount(recommendations: SalvageRecommendation[], filter: SelectsFilter) {
  return recommendations.filter((recommendation) => matchesFilter(recommendation, filter)).length;
}

function DecisionBadge({ decision }: { decision: SalvageDecision }) {
  const label = decision === "accepted" ? "Accepted" : decision === "rejected" ? "Rejected" : "Review needed";
  return <span className={`movie-selects-decision-badge is-${decision}`}>{decision === "accepted" ? <Check size={11} /> : decision === "rejected" ? <X size={11} /> : <AlertTriangle size={11} />}{label}</span>;
}

function SelectsMetric({ label, value, detail }: { label: string; value: number; detail: string }) {
  return <div className="movie-selects-metric"><span>{label}</span><strong>{value}</strong><small>{detail}</small></div>;
}

function SelectsEmptyState({ hasTakes, filter }: { hasTakes: boolean; filter: SelectsFilter }) {
  return <div className="movie-selects-empty"><ListVideo size={20} /><strong>{hasTakes ? "No recommendations match this view" : "No reviewable takes yet"}</strong><p>{hasTakes ? "Try another filter or return to All usable takes." : "A Selects card appears after a real render has produced a private take with a usable duration. Nothing is fabricated here."}</p></div>;
}
