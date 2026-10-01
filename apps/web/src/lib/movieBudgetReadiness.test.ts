import { describe, expect, it } from "vitest";
import type { MovieOverviewCost, MovieProject } from "@/lib/api";
import { buildMovieBudgetReadiness, formatMovieBudgetAmount } from "./movieBudgetReadiness";
import type { ProductionWorkspaceModel } from "./movieProductionWorkspace";

const project = { durationSeconds: 60, title: "Night train" } as MovieProject;

function workspace(overrides: Partial<ProductionWorkspaceModel> = {}): ProductionWorkspaceModel {
  return {
    shots: [],
    timeline: [],
    counts: { scenes: 1, shots: 2, shotPlanReady: 2, keyframesReady: 2, candidates: 2, takes: 2, selectedTakes: 1, readyForTimeline: 1, needsReview: 0, blocked: 0 },
    currentStage: "select",
    progressPercent: 70,
    timelineReady: false,
    timelineSummary: "1 of 2 shots are ready for the timeline.",
    ...overrides,
  };
}

function cost(overrides: Partial<MovieOverviewCost> = {}): MovieOverviewCost {
  return { isKnown: true, recordedProviderCostUsd: 2, estimatedRemainingProviderCostUsd: 8, currency: "USD", note: null, ...overrides };
}

describe("movie budget and readiness planning", () => {
  it("compares an optimized path with a more expensive naive path without exposing internals", () => {
    const model = buildMovieBudgetReadiness({ project, workspace: workspace(), selectedTier: "Upgrade", cost: cost() });
    expect(model.estimate.state).toBe("known");
    expect(model.estimate.optimizedAmount).toBeGreaterThan(0);
    expect(model.estimate.naiveAmount).toBeGreaterThan(model.estimate.optimizedAmount!);
    expect(model.estimate.savingsAmount).toBeGreaterThan(0);
    expect(JSON.stringify(model)).not.toMatch(/provider|model|prompt/i);
  });

  it("keeps unknown estimates honest instead of turning them into zero", () => {
    const model = buildMovieBudgetReadiness({ project, workspace: workspace(), selectedTier: "Draft", cost: cost({ isKnown: false, recordedProviderCostUsd: null, estimatedRemainingProviderCostUsd: null }) });
    expect(model.estimate.state).toBe("unavailable");
    expect(model.estimate.optimizedAmount).toBeNull();
    expect(model.estimate.naiveAmount).toBeNull();
    expect(formatMovieBudgetAmount(null)).toBe("Estimate pending");
  });

  it("blocks Master until every shot has a selected take and preserves review blockers", () => {
    const model = buildMovieBudgetReadiness({ project, workspace: workspace({ counts: { ...workspace().counts, selectedTakes: 0, needsReview: 1 } }), selectedTier: "Master", cost: cost() });
    expect(model.hasBlockingItems).toBe(true);
    expect(model.blockers.map((item) => item.key)).toEqual(expect.arrayContaining(["review-open", "takes-unselected"]));
    expect(model.warningRequired).toBe(true);
  });

  it("keeps an empty workspace blocked even when the selected tier is Draft", () => {
    const model = buildMovieBudgetReadiness({ project, workspace: workspace({ shots: [], counts: { ...workspace().counts, shots: 0, shotPlanReady: 0, keyframesReady: 0, candidates: 0, takes: 0, selectedTakes: 0, readyForTimeline: 0 } }), selectedTier: "Draft", cost: null });
    expect(model.hasBlockingItems).toBe(true);
    expect(model.blockers[0].key).toBe("shots-missing");
    expect(model.warningRequired).toBe(false);
  });
});
