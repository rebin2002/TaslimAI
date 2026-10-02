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
  const recorded = cost?.recordedProviderCostUsd ?? null;
  const remaining = cost?.estimatedRemainingProviderCostUsd ?? null;
  const base = cost?.isKnown && (recorded !== null || remaining !== null) ? (recorded ?? 0) + (remaining ?? 0) : null;
  if (base === null) {
    return {
      state: "unavailable",
      currency: cost?.currency || "USD",
      optimizedAmount: null,
      naiveAmount: null,
      savingsAmount: null,
      savingsPercent: null,
      detail: "A vetted planning estimate is not available yet. Unknown is kept as unknown; no zero-cost assumption is shown.",
    };
  }

  const shotComplexity = Math.min(0.35, workspace.counts.shots * 0.025);
  const openDecisionPenalty = Math.min(0.3, (workspace.counts.blocked + workspace.counts.needsReview) * 0.08);
  const naiveMultiplier = 1.45 + shotComplexity + openDecisionPenalty;
  const optimizedAmount = round(base * tierFactors[selectedTier]);
  const naiveAmount = round(optimizedAmount * naiveMultiplier);
  const savingsAmount = round(Math.max(0, naiveAmount - optimizedAmount));
  return {
    state: "known",
    currency: cost?.currency || "USD",
    optimizedAmount,
    naiveAmount,
    savingsAmount,
    savingsPercent: naiveAmount > 0 ? Math.round((savingsAmount / naiveAmount) * 100) : 0,
    detail: "Planning comparison based on persisted project work. It is not a customer price, reservation, or charge.",
  };
}

function round(value: number) {
  return Math.round(value * 100) / 100;
}

export function formatMovieBudgetAmount(amount: number | null, currency = "USD") {
  return amount === null ? "Estimate pending" : `${currency} ${amount.toFixed(2)}`;
}
