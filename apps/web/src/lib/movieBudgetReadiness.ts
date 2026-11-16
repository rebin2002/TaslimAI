import type { MovieOverviewCost, MovieProject } from "@/lib/api";
import { buildMovieResolutionPlan, type MovieResolutionTier } from "@/lib/movieProductionResolution";
import type { ProductionWorkspaceModel } from "@/lib/movieProductionWorkspace";

export type MovieBudgetEstimateState = "known" | "unavailable";
export type MovieReadinessBlockerSeverity = "blocked" | "review";

export type MovieBudgetEstimate = {
  state: MovieBudgetEstimateState;
  currency: string;
  optimizedAmount: number | null;
  naiveAmount: number | null;
  savingsAmount: number | null;
  savingsPercent: number | null;
  detail: string;
};

export type MovieReadinessBlocker = {
  key: string;
  severity: MovieReadinessBlockerSeverity;
  label: string;
  detail: string;
};

export type MovieBudgetReadiness = {
  recommendedTier: MovieResolutionTier;
  readinessLabel: string;
  readinessTone: "ready" | "review" | "blocked";
  readinessDetail: string;
  blockers: MovieReadinessBlocker[];
  hasBlockingItems: boolean;
  warningRequired: boolean;
  warningTitle: string;
  warningDetail: string;
  estimate: MovieBudgetEstimate;
};

export type BuildMovieBudgetReadinessInput = {
  project: MovieProject;
  workspace: ProductionWorkspaceModel;
  selectedTier: MovieResolutionTier;
  cost?: MovieOverviewCost | null;
};

const tierFactors: Record<MovieResolutionTier, number> = {
  Draft: 0.45,
  Upgrade: 0.72,
  Master: 1,
};

export function buildMovieBudgetReadiness({ project, workspace, selectedTier, cost }: BuildMovieBudgetReadinessInput): MovieBudgetReadiness {
  const resolutionPlan = buildMovieResolutionPlan({
    durationSeconds: project.durationSeconds,
    shotCount: workspace.counts.shots,
    reviewableTakeCount: workspace.counts.takes,
    selectedTakeCount: workspace.counts.selectedTakes,
    failedPassCount: workspace.shots.filter((item) => item.render?.execution?.status === "Failed").length,
    estimateAvailable: Boolean(cost?.isKnown),
  });
  const blockers = buildBlockers(workspace, selectedTier);
  const hasBlockingItems = blockers.some((item) => item.severity === "blocked");
  const estimate = buildEstimate(workspace, selectedTier, cost);
  const readinessLabel = hasBlockingItems ? "Not ready for this finish" : blockers.length ? "Review before continuing" : "Ready for this finish";
  const readinessTone = hasBlockingItems ? "blocked" : blockers.length ? "review" : "ready";
  const readinessDetail = hasBlockingItems
    ? "Resolve the items below before starting a higher-finish pass."
    : blockers.length
      ? "The plan can stay intact, but a review decision is still needed before escalating."
      : "The persisted plan has the decisions needed for this finish level.";
  const warningRequired = selectedTier !== "Draft";

  return {
    recommendedTier: resolutionPlan.recommended,
    readinessLabel,
    readinessTone,
    readinessDetail,
    blockers,
    hasBlockingItems,
    warningRequired,
    warningTitle: selectedTier === "Draft" ? "Draft keeps the next step reversible" : `${selectedTier} is a higher-finish decision`,
    warningDetail: selectedTier === "Draft"
      ? "This choice only sets direction. Nothing is queued from the budget panel."
      : "Review the estimate and open decisions before continuing. The next generation action will ask you to confirm again; this panel never queues work or charges an account.",
    estimate,
  };
}

function buildBlockers(workspace: ProductionWorkspaceModel, selectedTier: MovieResolutionTier): MovieReadinessBlocker[] {
  const blockers: MovieReadinessBlocker[] = [];
  const missingShotPlans = workspace.shots.filter((item) => !item.shot.readiness.ready && !["ReadyForStoryboard", "Storyboard", "Production"].includes(item.shot.planState));

  if (workspace.counts.shots === 0) {
    blockers.push({ key: "shots-missing", severity: "blocked", label: "Add an approved shot plan", detail: "A finish decision starts with at least one persisted shot and its creative intent." });
  } else if (missingShotPlans.length > 0) {
    blockers.push({ key: "shot-plans-missing", severity: "blocked", label: `${missingShotPlans.length} shot plan${missingShotPlans.length === 1 ? "" : "s"} still need approval`, detail: "Complete the shot plan before spending on a pass that may need to be replaced." });
  }

  if (workspace.counts.needsReview > 0) {
    blockers.push({ key: "review-open", severity: "review", label: `${workspace.counts.needsReview} pass${workspace.counts.needsReview === 1 ? "" : "es"} need review`, detail: "Review the usable range first and retry only the affected shot instead of regenerating the whole scene." });
  }

  if (selectedTier !== "Draft" && workspace.counts.shots > workspace.counts.keyframesReady) {
    const remaining = workspace.counts.shots - workspace.counts.keyframesReady;
    blockers.push({ key: "source-frames-missing", severity: "review", label: `${remaining} source frame${remaining === 1 ? "" : "s"} not approved`, detail: "Lock the visual reference before moving into a higher-finish pass." });
  }

  if (selectedTier === "Master" && workspace.counts.selectedTakes < workspace.counts.shots) {
    const remaining = workspace.counts.shots - workspace.counts.selectedTakes;
    blockers.push({ key: "takes-unselected", severity: "blocked", label: `${remaining} take${remaining === 1 ? "" : "s"} still need selection`, detail: "Master is reserved for selected takes; keep unresolved shots in Draft or Upgrade until the edit decision is clear." });
  }

  return blockers;
}

function buildEstimate(workspace: ProductionWorkspaceModel, selectedTier: MovieResolutionTier, cost?: MovieOverviewCost | null): MovieBudgetEstimate {
  const unavailable = (detail: string): MovieBudgetEstimate => ({
    state: "unavailable",
    currency: cost?.currency || "USD",
    optimizedAmount: null,
    naiveAmount: null,
    savingsAmount: null,
    savingsPercent: null,
    detail,
  });

  const recorded = cost?.recordedProviderCostUsd ?? null;
  const remaining = cost?.estimatedRemainingProviderCostUsd ?? null;
  const base = cost?.isKnown && (recorded !== null || remaining !== null) ? (recorded ?? 0) + (remaining ?? 0) : null;
  if (base === null) {
    return unavailable("A vetted planning estimate is not available yet. Unknown is kept as unknown; no zero-cost assumption is shown.");
  }
  // A net figure below zero, or outside the finite range, is a data-integrity signal rather than a budget:
  // the recorded sum is net of refunds and reversals. Scaling it would price the regenerate-everything path
  // below the optimized path and report a zero planning difference instead of declining to compare.
  if (!Number.isFinite(base) || base < 0) {
    return unavailable("The recorded spend for this project is not a usable planning figure, so no comparison is shown. Unknown is kept as unknown.");
  }

  const shotComplexity = Math.min(0.35, Math.max(0, workspace.counts.shots) * 0.025);
  const openDecisionPenalty = Math.min(0.3, Math.max(0, workspace.counts.blocked) * 0.08 + Math.max(0, workspace.counts.needsReview) * 0.08);
  const naiveMultiplier = 1.45 + shotComplexity + openDecisionPenalty;
  const optimizedAmount = round(base * tierFactors[selectedTier]);
  const naiveAmount = optimizedAmount === null ? null : round(optimizedAmount * naiveMultiplier);
  const savingsAmount = optimizedAmount === null || naiveAmount === null ? null : round(Math.max(0, naiveAmount - optimizedAmount));
  if (optimizedAmount === null || naiveAmount === null || savingsAmount === null) {
    return unavailable("The planning figures for this finish level cannot be compared safely. Unknown is kept as unknown; no zero-cost assumption is shown.");
  }
  return {
    state: "known",
    currency: cost?.currency || "USD",
    optimizedAmount,
    naiveAmount,
    savingsAmount,
    savingsPercent: naiveAmount > 0 ? Math.min(100, Math.max(0, Math.round((savingsAmount / naiveAmount) * 100))) : 0,
    detail: "Planning comparison based on persisted project work. It is not a customer price, reservation, or charge.",
  };
}

/**
 * Rounds a money figure to two decimals, or returns null when the value is not a usable amount.
 * Math.round yields signed zero for a sub-cent negative value, so zero is returned explicitly.
 */
function round(value: number): number | null {
  if (!Number.isFinite(value)) return null;
  const cents = Math.round(value * 100);
  return cents === 0 ? 0 : cents / 100;
}

export function formatMovieBudgetAmount(amount: number | null, currency = "USD") {
  // The display step refuses to render a value it cannot present as money, rather than emitting a
  // signed, negative or NaN amount into the panel.
  if (amount === null || !Number.isFinite(amount) || amount < 0) return "Estimate pending";
  const cents = Math.round(amount * 100);
  return `${currency} ${(cents === 0 ? 0 : cents / 100).toFixed(2)}`;
}
