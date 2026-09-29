"use client";

import { Check, CircleAlert, Clock3, Film, LockKeyhole, ShieldCheck, Sparkles } from "lucide-react";
import type { MovieProject, MovieTake } from "@/lib/api";
import { buildMovieResolutionPlan, type MovieResolutionTier } from "@/lib/movieProductionResolution";

type MovieProductionResolutionPanelProps = {
  project: MovieProject;
  shotCount: number;
  reviewableTakeCount: number;
  selectedTake: MovieTake | null;
  failedPassCount: number;
  estimateAvailable?: boolean;
  selectedTier: MovieResolutionTier;
  onTierChange: (tier: MovieResolutionTier) => void;
};

export function MovieProductionResolutionPanel({
  project,
  shotCount,
  reviewableTakeCount,
  selectedTake,
  failedPassCount,
  estimateAvailable = false,
  selectedTier,
  onTierChange,
}: MovieProductionResolutionPanelProps) {
  const plan = buildMovieResolutionPlan({
    durationSeconds: project.durationSeconds,
    shotCount,
    reviewableTakeCount,
    selectedTakeCount: selectedTake ? 1 : 0,
    failedPassCount,
    estimateAvailable,
  });
  const sourceLabel = [project.aspectRatio, project.style, project.durationSeconds ? `${project.durationSeconds}s` : null].filter(Boolean).join(" · ");
  const selectedTierPlan = plan.tiers.find((tier) => tier.id === selectedTier) ?? plan.tiers[0];

  return (
    <section className="movie-resolution-panel" aria-labelledby="movie-resolution-title">
      <div className="movie-resolution-heading">
        <div>
          <span className="movie-workspace-kicker">Finish direction</span>
          <h3 id="movie-resolution-title">Choose how far to take this cut</h3>
          <p>Keep the creative decision simple. Your source intent stays intact while the finish changes around it.</p>
        </div>
        <div className="movie-resolution-safe-state" role="status"><LockKeyhole size={13} /><span>Planning only</span></div>
      </div>

      <div className="movie-resolution-intent-grid">
        <div className="movie-resolution-intent-card">
          <span className="movie-inspector-label">Source intent</span>
          <strong>{sourceLabel || "Source plan"}</strong>
          <p>{project.description || "The original creative brief remains the source of truth."}</p>
        </div>
        <div className="movie-resolution-intent-card is-master">
          <span className="movie-inspector-label">Master intent</span>
          <strong>Preserve the story, elevate the finish</strong>
          <p>Continuity, framing, and the selected take stay connected through the final decision.</p>
        </div>
      </div>

      <div className="movie-resolution-options" role="group" aria-label="Choose finish level">
        {plan.tiers.map((tier) => {
          const isSelected = tier.id === selectedTier;
          const isRecommended = tier.id === plan.recommended;
          return (
            <button
              key={tier.id}
              type="button"
              className={`movie-resolution-option ${isSelected ? "is-selected" : ""} ${isRecommended ? "is-recommended" : ""}`}
              aria-pressed={isSelected}
              onClick={() => onTierChange(tier.id)}
            >
              <span className="movie-resolution-option-topline"><span>{tier.title}</span>{isRecommended && <em>Recommended</em>}</span>
              <strong>{tier.summary}</strong>
              <small>{tier.detail}</small>
              <span className="movie-resolution-option-meta"><span>{tier.estimate}</span><span>{tier.estimateDetail}</span></span>
              {isSelected && <span className="movie-resolution-option-check" aria-hidden="true"><Check size={13} /></span>}
            </button>
          );
        })}
      </div>

      <div className="movie-resolution-decision-grid">
        <div className="movie-resolution-decision is-recommendation">
          <div className="movie-resolution-decision-icon"><Sparkles size={15} /></div>
          <div><span className="movie-inspector-label">Recommendation</span><strong>{plan.recommended} is the next sensible step</strong><p>{plan.recommendationReason}</p></div>
        </div>
        <div className="movie-resolution-decision">
          <div className={`movie-resolution-decision-icon is-${plan.qcTone}`}><ShieldCheck size={15} /></div>
          <div><span className="movie-inspector-label">Quality check</span><strong>{plan.qcLabel}</strong><p>{plan.qcDetail}</p></div>
        </div>
        <div className="movie-resolution-decision">
          <div className={`movie-resolution-decision-icon ${plan.qcTone === "blocked" ? "is-blocked" : ""}`}><CircleAlert size={15} /></div>
          <div><span className="movie-inspector-label">Escalation</span><strong>{plan.escalationLabel}</strong><p>{plan.escalationDetail}</p></div>
        </div>
      </div>

      <div className="movie-resolution-footer">
        <div className="movie-resolution-estimate"><Clock3 size={14} /><span><strong>{selectedTierPlan.title} estimate</strong>{selectedTierPlan.estimate} <small>{selectedTierPlan.estimateDetail}</small></span></div>
        <div className={`movie-resolution-selected-take ${selectedTake ? "is-ready" : ""}`} id="movie-selected-take">
          {selectedTake ? <><Check size={14} /><span><strong>Selected take ready</strong>{selectedTake.label || `Take ${selectedTake.versionNumber}`} · {selectedTake.status}</span></> : <><Film size={14} /><span><strong>No take selected yet</strong>Review a real take below before choosing Master.</span></>}
        </div>
      </div>
    </section>
  );
}
