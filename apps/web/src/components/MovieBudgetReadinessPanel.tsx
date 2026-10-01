"use client";

import { AlertTriangle, Check, CircleAlert, CircleDollarSign, Film, Flag, LockKeyhole, ShieldCheck, Sparkles } from "lucide-react";
import type { MovieOverviewCost, MovieProject } from "@/lib/api";
import { buildMovieBudgetReadiness, formatMovieBudgetAmount } from "@/lib/movieBudgetReadiness";
import type { ProductionWorkspaceModel } from "@/lib/movieProductionWorkspace";
import type { MovieResolutionTier } from "@/lib/movieProductionResolution";

type MovieBudgetReadinessPanelProps = {
  project: MovieProject;
  workspace: ProductionWorkspaceModel;
  cost?: MovieOverviewCost | null;
  selectedTier: MovieResolutionTier;
  onTierChange: (tier: MovieResolutionTier) => void;
};

const tiers: Array<{ id: MovieResolutionTier; summary: string; detail: string }> = [
  { id: "Draft", summary: "Check rhythm and framing", detail: "Keep the next decision reversible." },
  { id: "Upgrade", summary: "Review a working cut", detail: "Carry forward only what holds up." },
  { id: "Master", summary: "Prepare selected material", detail: "Reserve for locked take decisions." },
];

export function MovieBudgetReadinessPanel({ project, workspace, cost, selectedTier, onTierChange }: MovieBudgetReadinessPanelProps) {
  const plan = buildMovieBudgetReadiness({ project, workspace, cost, selectedTier });
  const selectedTierLabel = tiers.find((tier) => tier.id === selectedTier)?.summary ?? "Choose a finish";
  return (
    <section className="movie-budget-panel" aria-labelledby="movie-budget-title">
      <div className="movie-budget-panel-heading">
        <div>
          <span className="movie-workspace-kicker">Budget & readiness</span>
          <h3 id="movie-budget-title">Spend effort where the cut proves it</h3>
          <p>Lock references first, review usable ranges, and upgrade only the material you intend to keep.</p>
        </div>
        <div className="movie-budget-planning-badge" role="status"><LockKeyhole size={13} /> Planning only</div>
      </div>

      <div className="movie-budget-tier-row" role="group" aria-label="Choose finish level">
        {tiers.map((tier) => {
          const selected = tier.id === selectedTier;
          const recommended = tier.id === plan.recommendedTier;
          return (
            <button key={tier.id} type="button" className={`movie-budget-tier ${selected ? "is-selected" : ""}`} aria-pressed={selected} onClick={() => onTierChange(tier.id)}>
              <span className="movie-budget-tier-icon">{tier.id === "Draft" ? <Film size={15} /> : tier.id === "Upgrade" ? <Sparkles size={15} /> : <Flag size={15} />}</span>
              <span className="movie-budget-tier-copy"><strong>{tier.id}</strong><small>{tier.summary}</small><em>{tier.detail}</em></span>
              {recommended && <span className="movie-budget-recommended">Next sensible step</span>}
              {selected && <Check className="movie-budget-tier-check" size={14} aria-hidden="true" />}
            </button>
          );
        })}
      </div>

      <div className="movie-budget-estimate-grid" aria-label="Planning cost comparison">
        <BudgetCard title="Optimized path" value={formatMovieBudgetAmount(plan.estimate.optimizedAmount, plan.estimate.currency)} tone="optimized" detail="Draft first, salvage usable ranges, then upgrade selected material." />
        <BudgetCard title="Naive path" value={formatMovieBudgetAmount(plan.estimate.naiveAmount, plan.estimate.currency)} tone="naive" detail="Regenerate full scenes and escalate every shot up front." />
        <div className="movie-budget-savings"><CircleDollarSign size={16} /><div><span>Potential planning difference</span><strong>{plan.estimate.savingsAmount === null ? "Compare when available" : `${formatMovieBudgetAmount(plan.estimate.savingsAmount, plan.estimate.currency)} · about ${plan.estimate.savingsPercent}%`}</strong><small>{plan.estimate.detail}</small></div></div>
      </div>

      <div className={`movie-budget-readiness is-${plan.readinessTone}`}>
        <div className="movie-budget-readiness-icon">{plan.readinessTone === "ready" ? <ShieldCheck size={16} /> : <CircleAlert size={16} />}</div>
        <div><span className="movie-inspector-label">{selectedTier} readiness</span><strong>{plan.readinessLabel}</strong><p>{plan.readinessDetail}</p></div>
      </div>

      {plan.blockers.length > 0 && <div className="movie-budget-blockers" aria-label="Readiness blockers"><div className="movie-budget-blockers-heading"><span>Before {selectedTierLabel.toLowerCase()}</span><small>{plan.blockers.length} item{plan.blockers.length === 1 ? "" : "s"}</small></div>{plan.blockers.map((blocker) => <div className={`movie-budget-blocker is-${blocker.severity}`} key={blocker.key}><AlertTriangle size={14} /><div><strong>{blocker.label}</strong><p>{blocker.detail}</p></div></div>)}</div>}

      <div className={`movie-budget-warning ${plan.warningRequired ? "is-elevated" : ""}`} role="note">
        <AlertTriangle size={15} />
        <div><strong>{plan.warningTitle}</strong><p>{plan.warningDetail}</p></div>
      </div>
      <div className="movie-budget-footer"><span><ShieldCheck size={13} /> No work is queued by choosing a tier.</span><span>Project: {project.title}</span></div>
    </section>
  );
}

function BudgetCard({ title, value, detail, tone }: { title: string; value: string; detail: string; tone: "optimized" | "naive" }) {
  return <div className={`movie-budget-card is-${tone}`}><span>{title}</span><strong>{value}</strong><small>{detail}</small></div>;
}
