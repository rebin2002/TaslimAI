"use client";

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
import { useLocale } from "@/components/LocaleProvider";
import { api, type MovieProject, type MovieTakeSelectRecord, type MovieTimeline } from "@/lib/api";
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

/**
 * Selects and salvage. A bounded range is only trusted once it exists as a
 * persisted MovieTakeSelect record and a reviewer has approved it; the canonical
 * timeline insert then references that approved select.
 */
export function MovieSelectsWorkspace({ project }: { project: MovieProject }) {
  const { t } = useLocale();
  const [timeline, setTimeline] = useState<MovieTimeline | null>(null);
  const [filter, setFilter] = useState<SelectsFilter>("all");
  const [decisions, setDecisions] = useState<Record<string, SalvageDecision>>({});
  const [rangeOverrides, setRangeOverrides] = useState<Record<string, RangeOverride>>({});
  const [selectsByTake, setSelectsByTake] = useState<Record<string, MovieTakeSelectRecord[]>>({});
  const [proposalId, setProposalId] = useState<string | null>(null);
  const [timelinePosition, setTimelinePosition] = useState(0);
  const [busy, setBusy] = useState(false);
  const [reviewing, setReviewing] = useState(false);
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");

  const subclips = useMemo(() => buildMovieSubclips(project), [project]);

  const loadTimeline = useCallback(async () => {
    try {
      setTimeline(await api.getMovieTimeline(project.id));
    } catch {
      // A timeline is created by the first accepted insert. An empty Selects room is valid.
      setTimeline(null);
    }
  }, [project.id]);

  const loadSelects = useCallback(async () => {
    const entries = await Promise.all(
      subclips.map(async (subclip) => {
        const records = await api.getMovieTakeSelects(project.id, subclip.takeId).catch(() => []);
        return [subclip.takeId, records] as const;
      }),
    );
    setSelectsByTake(Object.fromEntries(entries));
  }, [project.id, subclips]);

  const reload = useCallback(async () => {
    await Promise.all([loadTimeline(), loadSelects()]);
  }, [loadSelects, loadTimeline]);

  useEffect(() => {
    void Promise.resolve().then(() => reload());
  }, [reload]);

  const recommendations = useMemo(
    () => buildSalvageRecommendations(project).map((recommendation) => ({
      ...recommendation,
      decision: decisions[recommendation.id] ?? recommendation.decision,
    })),
    [decisions, project],
  );
  const visibleRecommendations = recommendations.filter((recommendation) => matchesFilter(recommendation, filter));
  const acceptedCount = recommendations.filter((recommendation) => recommendation.decision === "accepted").length;
  const readyCount = recommendations.filter((recommendation) => approvedSelectFor(recommendation.subclip, selectsByTake) !== null).length;
  const selectedRecommendation = recommendations.find((recommendation) => recommendation.id === proposalId) ?? null;
  const selectedRange = selectedRecommendation ? rangeFor(selectedRecommendation.subclip, rangeOverrides) : null;
  const selectedApprovedSelect = selectedRecommendation ? approvedSelectFor(selectedRecommendation.subclip, selectsByTake) : null;
  const timelineItems = timeline?.currentRevision?.tracks.flatMap((track) => track.items) ?? [];
  const proposalCanWrite = Boolean(
    selectedRecommendation
      && selectedRange
      && selectedRecommendation.subclip.selected
      && ["Approved", "Selected"].includes(selectedRecommendation.subclip.status)
      && selectedApprovedSelect
      && selectedApprovedSelect.startMilliseconds === selectedRange.sourceInMilliseconds
      && selectedApprovedSelect.endMilliseconds === selectedRange.sourceOutMilliseconds,
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
      // Accepting a recommendation persists the bounded range as a real select
      // record so the decision survives the session and can be reviewed.
      setBusy(true);
      try {
        const range = rangeFor(recommendation.subclip, rangeOverrides);
        const existing = (selectsByTake[recommendation.subclip.takeId] ?? []).find(
          (record) => record.startMilliseconds === range.sourceInMilliseconds && record.endMilliseconds === range.sourceOutMilliseconds,
        );
        const record = existing ?? await api.createMovieTakeSelect(project.id, recommendation.subclip.takeId, {
          label: `Salvage range · ${recommendation.subclip.label}`,
          startMilliseconds: range.sourceInMilliseconds,
          endMilliseconds: range.sourceOutMilliseconds,
          notes: recommendation.summary,
        });
        setSelectsByTake((current) => ({
          ...current,
          [recommendation.subclip.takeId]: existing
            ? current[recommendation.subclip.takeId] ?? []
            : [record, ...(current[recommendation.subclip.takeId] ?? [])],
        }));
        setDecisions((previous) => ({ ...previous, [recommendation.id]: "accepted" }));
        setProposalId(recommendation.id);
        setTimelinePosition(timeline?.currentRevision?.durationMilliseconds ?? 0);
        setMessage(
          record.status === "Approved"
            ? "Bounded range saved and approved. Review the minimal insert before writing a timeline revision."
            : "Bounded range saved as a draft select. A reviewer must approve it before the insert can be written.",
        );
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

  async function reviewRange(recommendation: SalvageRecommendation, record: MovieTakeSelectRecord, decision: "Approved" | "Rejected") {
    setReviewing(true);
    setError("");
    setMessage("");
    try {
      const updated = await api.reviewMovieTakeSelect(project.id, recommendation.subclip.takeId, record.id, { decision });
      setSelectsByTake((current) => ({
        ...current,
        [recommendation.subclip.takeId]: (current[recommendation.subclip.takeId] ?? []).map((item) => (item.id === updated.id ? updated : item)),
      }));
      setMessage(decision === "Approved" ? "Bounded range approved. The insert can now be written to a draft revision." : "Bounded range rejected.");
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "The range review could not be saved.");
    } finally {
      setReviewing(false);
    }
  }

  async function applyInsert() {
    if (!selectedRecommendation || !selectedRange || !selectedApprovedSelect) return;
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
      await api.createMovieTimelineRevision(
        project.id,
        buildSalvageTimelineRevision(timeline, proposal, selectedRecommendation, selectedApprovedSelect.id),
      );
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
        <p>{t("movieModule.selects.description").split(".")[0]}. Salvage a usable range first, persist it as a reviewed select, carry the QC evidence with it, and only add a small insert to the canonical timeline when the decision is deliberate.</p>
      </div>
      <div className="movie-selects-command-mark"><ListVideo size={23} /><span>NON-DESTRUCTIVE</span></div>
    </section>

    <section className="movie-selects-summary" aria-label="Selects summary">
      <SelectsMetric label="Usable takes" value={subclips.length} detail="private outputs" />
      <SelectsMetric label="Recommendations" value={recommendations.length} detail="review only" />
      <SelectsMetric label="Accepted" value={acceptedCount} detail="range persisted" />
      <SelectsMetric label="Range approved" value={readyCount} detail="cleared for timeline" />
    </section>

    <section className="movie-selects-boundary"><ShieldAlert size={16} /><div><strong>Salvage before regenerate</strong><span>A bounded range becomes a persisted, reviewable select. Accepting a recommendation does not start generation or replace the source take.</span></div></section>

    {message && <div className="movie-selects-notice is-success" role="status"><CheckCircle2 size={15} /><span>{message}</span></div>}
    {error && <div className="movie-selects-notice is-error" role="alert"><AlertTriangle size={15} /><span>{error}</span><button type="button" onClick={() => setError("")} aria-label="Dismiss error"><X size={13} /></button></div>}

    <section className="movie-selects-review" aria-labelledby="movie-selects-review-title">
      <div className="movie-selects-section-heading"><div><span className="movie-workspace-kicker">Salvage Director · review queue</span><h3 id="movie-selects-review-title">Find a smaller fix than a new scene.</h3><p>Each card points to one persisted take and one bounded source range. Review stays grounded in the project record.</p></div><button type="button" className="movie-workspace-button is-quiet" onClick={() => void reload()}><RefreshCw size={13} /> Refresh</button></div>
      <div className="movie-selects-filters" role="tablist" aria-label="Filter select recommendations">{filters.map((item) => <button key={item.id} type="button" role="tab" aria-selected={filter === item.id} className={filter === item.id ? "is-active" : ""} onClick={() => setFilter(item.id)}>{item.label}<span>{filterCount(recommendations, item.id, selectsByTake)}</span></button>)}</div>
      {visibleRecommendations.length ? <div className="movie-selects-list">{visibleRecommendations.map((recommendation) => <SalvageRecommendationCard key={recommendation.id} recommendation={recommendation} range={rangeFor(recommendation.subclip, rangeOverrides)} selects={selectsByTake[recommendation.subclip.takeId] ?? []} onRangeChange={(key, value) => updateRange(recommendation.subclip.id, key, value)} onDecision={(decision) => void decide(recommendation, decision)} onPropose={() => { setProposalId(recommendation.id); setTimelinePosition(timeline?.currentRevision?.durationMilliseconds ?? 0); setMessage(""); }} proposing={proposalId === recommendation.id} busy={busy} />)}</div> : <SelectsEmptyState hasTakes={subclips.length > 0} />}
    </section>

    {selectedRecommendation && selectedRange && <InsertProposalPanel recommendation={selectedRecommendation} range={selectedRange} approvedSelect={selectedApprovedSelect} timelinePosition={timelinePosition} onTimelinePositionChange={setTimelinePosition} onApply={() => void applyInsert()} onReview={(record, decision) => void reviewRange(selectedRecommendation, record, decision)} busy={busy} reviewing={reviewing} canWrite={proposalCanWrite} />}

    <section className="movie-selects-timeline" aria-labelledby="movie-selects-timeline-title">
      <div className="movie-selects-section-heading"><div><span className="movie-workspace-kicker">Canonical timeline hand-off</span><h3 id="movie-selects-timeline-title">Keep the edit model authoritative.</h3><p>{timeline?.currentRevision ? `Draft revision ${timeline.currentRevision.revisionNumber} · ${formatSalvageTime(timeline.currentRevision.durationMilliseconds)} total` : "No timeline revision exists yet. An approved insert creates the first draft revision."}</p></div><GitBranch size={18} /></div>
      {timelineItems.length ? <div className="movie-selects-timeline-items">{timelineItems.map((item) => <div className="movie-selects-timeline-item" key={item.id}><span>{formatSalvageTime(item.timelineInMilliseconds)}–{formatSalvageTime(item.timelineOutMilliseconds)}</span><strong>{item.label || (item.isGap ? "Gap" : "Timeline item")}</strong><small>{item.kind === "VisualTake" ? "Visual take / subclip" : item.kind}</small></div>)}</div> : <div className="movie-selects-timeline-empty"><Film size={17} /><span>The existing timeline stays empty until a human approves a bounded range and writes a minimal insert.</span></div>}
    </section>

    <div className="movie-selects-footnote"><Clock3 size={14} /><span>Time values are source ranges in milliseconds. The full NLE remains out of scope; this room prepares reviewed, non-destructive hand-offs for the existing timeline.</span></div>
  </div>;
}

function approvedSelectFor(subclip: MovieSubclip, selectsByTake: Record<string, MovieTakeSelectRecord[]>) {
  return (selectsByTake[subclip.takeId] ?? []).find((record) => record.status === "Approved") ?? null;
}

function SalvageRecommendationCard({ recommendation, range, selects, onRangeChange, onDecision, onPropose, proposing, busy }: { recommendation: SalvageRecommendation; range: RangeOverride; selects: MovieTakeSelectRecord[]; onRangeChange: (key: keyof RangeOverride, value: number) => void; onDecision: (decision: SalvageDecision) => void; onPropose: () => void; proposing: boolean; busy: boolean }) {
  const { subclip } = recommendation;
  const decisionTone = recommendation.decision === "accepted" ? "is-accepted" : recommendation.decision === "rejected" ? "is-rejected" : "";
  const approved = selects.find((record) => record.status === "Approved") ?? null;
  const pending = selects.find((record) => record.status !== "Approved" && record.status !== "Rejected") ?? null;
  return <article className={`movie-selects-card ${decisionTone}`} aria-labelledby={`salvage-${recommendation.id}`}>
    <header className="movie-selects-card-header"><div><span className="movie-selects-card-kicker">Scene {String(subclip.sceneSequence).padStart(2, "0")} · Shot {String(subclip.shotSequence).padStart(2, "0")}</span><h4 id={`salvage-${recommendation.id}`}>{recommendation.title}</h4><p>{subclip.shotDescription}</p></div><DecisionBadge decision={recommendation.decision} /></header>
    <div className="movie-selects-card-body">
      <div className="movie-selects-preview">{subclip.assetId ? <video src={assetFileUrl(subclip.assetId, true)} controls preload="metadata" aria-label={`${subclip.label} source preview`} /> : <Film size={18} />}</div>
      <div className="movie-selects-card-copy"><p>{recommendation.summary}</p><div className="movie-selects-rationale">{recommendation.rationale.map((reason) => <span key={reason}>{reason}</span>)}</div>{selects.length > 0 && <div className="movie-selects-rationale">{selects.slice(0, 2).map((record) => <span key={record.id}>{record.status === "Approved" ? "Approved range" : record.status} · {formatSalvageTime(record.startMilliseconds)}–{formatSalvageTime(record.endMilliseconds)}</span>)}</div>}</div>
      <div className="movie-selects-range"><span className="movie-inspector-label">Usable source range</span><div><label>In<input type="number" min={0} value={range.sourceInMilliseconds} onChange={(event) => onRangeChange("sourceInMilliseconds", Number(event.target.value))} /><em>ms</em></label><span>→</span><label>Out<input type="number" min={1} max={subclip.durationMilliseconds} value={range.sourceOutMilliseconds} onChange={(event) => onRangeChange("sourceOutMilliseconds", Number(event.target.value))} /><em>ms</em></label></div><small>{formatSalvageTime(range.sourceInMilliseconds)} – {formatSalvageTime(range.sourceOutMilliseconds)} · {formatSalvageTime(Math.max(0, range.sourceOutMilliseconds - range.sourceInMilliseconds))}</small></div>
    </div>
    {subclip.issues.length > 0 && <div className="movie-selects-qc"><span className="movie-inspector-label">QC evidence</span><div>{subclip.issues.map((issue) => <span key={issue.code} className={`is-${issue.severity}`}><AlertTriangle size={11} /><strong>{issue.label}</strong><small>{issue.detail}</small></span>)}</div></div>}
    <footer className="movie-selects-card-actions"><span>{subclip.selected ? "Selected take" : "Not selected"}{subclip.finalized ? " · Final take" : ""}{approved ? " · Range approved" : pending ? " · Range awaiting review" : ""}</span>{recommendation.decision === "rejected" ? <button type="button" className="movie-text-action" onClick={() => onDecision("pending")}>Reconsider</button> : <><button type="button" className="movie-text-action is-danger" onClick={() => onDecision("rejected")}><X size={12} /> Reject</button>{recommendation.decision !== "accepted" && <button type="button" className="movie-workspace-button is-secondary" onClick={() => onDecision("accepted")} disabled={busy}><Check size={12} /> Accept and persist range</button>}{recommendation.decision === "accepted" && <button type="button" className="movie-workspace-button is-primary" onClick={onPropose}><Plus size={12} /> {proposing ? "Review insert below" : "Propose minimal insert"}</button>}</>}</footer>
  </article>;
}

function InsertProposalPanel({ recommendation, range, approvedSelect, timelinePosition, onTimelinePositionChange, onApply, onReview, busy, reviewing, canWrite }: { recommendation: SalvageRecommendation; range: RangeOverride; approvedSelect: MovieTakeSelectRecord | null; timelinePosition: number; onTimelinePositionChange: (value: number) => void; onApply: () => void; onReview: (record: MovieTakeSelectRecord, decision: "Approved" | "Rejected") => void; busy: boolean; reviewing: boolean; canWrite: boolean }) {
  const pending = !approvedSelect;
  return <section className="movie-selects-proposal" aria-labelledby="movie-selects-proposal-title"><div className="movie-selects-proposal-heading"><div><span className="movie-workspace-kicker">Minimal insert proposal</span><h3 id="movie-selects-proposal-title">Add only the missing beat.</h3><p>{recommendation.title} will be added as a new draft revision using the existing take and the approved bounded range. Earlier timeline revisions remain immutable.</p></div><Sparkles size={18} /></div><div className="movie-selects-proposal-grid"><div><span>Source range</span><strong>{formatSalvageTime(range.sourceInMilliseconds)} – {formatSalvageTime(range.sourceOutMilliseconds)}</strong><small>{formatSalvageTime(Math.max(0, range.sourceOutMilliseconds - range.sourceInMilliseconds))} insert duration</small></div><label><span>Timeline position</span><input type="number" min={0} value={timelinePosition} onChange={(event) => onTimelinePositionChange(Number(event.target.value))} /><small>milliseconds · defaults to the end of the current draft</small></label><div><span>Persisted select</span><strong>{approvedSelect ? `Approved · ${formatSalvageTime(approvedSelect.startMilliseconds)}–${formatSalvageTime(approvedSelect.endMilliseconds)}` : "Not approved yet"}</strong><small>{approvedSelect ? "The server re-checks source ownership and bounds on write." : "Approve the persisted range before writing the insert."}</small></div></div><div className="movie-selects-proposal-actions">{pending && <span>The range is not approved for this exact window yet. Approve the persisted select to continue.</span>}{!canWrite && !pending && <span>Open Production to approve and select this take before applying the insert.</span>}{approvedSelect && <button type="button" className="movie-text-action is-danger" onClick={() => onReview(approvedSelect, "Rejected")} disabled={reviewing}><X size={12} /> Withdraw range approval</button>}<button type="button" className="movie-workspace-button is-primary" onClick={onApply} disabled={busy || !canWrite}>{busy ? "Saving draft revision…" : "Apply to draft timeline"}</button></div></section>;
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

function filterCount(recommendations: SalvageRecommendation[], filter: SelectsFilter, selectsByTake: Record<string, MovieTakeSelectRecord[]>) {
  if (filter === "timeline-ready") return recommendations.filter((recommendation) => approvedSelectFor(recommendation.subclip, selectsByTake) !== null).length;
  return recommendations.filter((recommendation) => matchesFilter(recommendation, filter)).length;
}

function DecisionBadge({ decision }: { decision: SalvageDecision }) {
  const label = decision === "accepted" ? "Accepted" : decision === "rejected" ? "Rejected" : "Review needed";
  return <span className={`movie-selects-decision-badge is-${decision}`}>{decision === "accepted" ? <Check size={11} /> : decision === "rejected" ? <X size={11} /> : <AlertTriangle size={11} />}{label}</span>;
}

function SelectsMetric({ label, value, detail }: { label: string; value: number; detail: string }) {
  return <div className="movie-selects-metric"><span>{label}</span><strong>{value}</strong><small>{detail}</small></div>;
}

function SelectsEmptyState({ hasTakes }: { hasTakes: boolean }) {
  return <div className="movie-selects-empty"><ListVideo size={20} /><strong>{hasTakes ? "No recommendations match this view" : "No reviewable takes yet"}</strong><p>{hasTakes ? "Try another filter or return to All usable takes." : "A Selects card appears after a real render has produced a private take with a usable duration. Nothing is fabricated here."}</p></div>;
}
