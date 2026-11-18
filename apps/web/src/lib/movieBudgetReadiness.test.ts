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

  it("refuses a path comparison when recorded spend nets below zero", () => {
    const model = buildMovieBudgetReadiness({ project, workspace: workspace(), selectedTier: "Master", cost: cost({ recordedProviderCostUsd: -12, estimatedRemainingProviderCostUsd: 5 }) });
    expect(model.estimate.state).toBe("unavailable");
    expect(model.estimate.optimizedAmount).toBeNull();
    expect(model.estimate.naiveAmount).toBeNull();
    expect(model.estimate.savingsAmount).toBeNull();
    expect(model.estimate.savingsPercent).toBeNull();
    expect(JSON.stringify(model)).not.toMatch(/provider|model|prompt/i);
  });

  it("refuses a path comparison when the spend figures sum outside the finite range", () => {
    // Largest finite double. Summing two of them overflows to +Infinity, which is the signal the
    // guard must reject. Written as a literal because Number.MAX_VALUE is an Annex B compatibility
    // alias: if a runtime ever dropped it the sample would become undefined and this case would
    // pass vacuously through the unknown path instead of the non-finite one it names.
    const largestFinite = 1.7976931348623157e308;
    const model = buildMovieBudgetReadiness({ project, workspace: workspace(), selectedTier: "Upgrade", cost: cost({ recordedProviderCostUsd: largestFinite, estimatedRemainingProviderCostUsd: largestFinite }) });
    expect(model.estimate.state).toBe("unavailable");
    expect(model.estimate.optimizedAmount).toBeNull();
    expect(model.estimate.naiveAmount).toBeNull();
  });

  it("refuses a path comparison when a finite spend cannot be scaled into cents", () => {
    // The base is a legitimate finite number here, so the sum guard cannot see it. Converting it to
    // cents leaves the finite range, and an unpriceable figure must not be reported as a known amount.
    const unpriceable = 1.7e308;
    const model = buildMovieBudgetReadiness({ project, workspace: workspace(), selectedTier: "Draft", cost: cost({ recordedProviderCostUsd: unpriceable, estimatedRemainingProviderCostUsd: 0 }) });
    expect(model.estimate.state).toBe("unavailable");
    expect(model.estimate.optimizedAmount).toBeNull();
    expect(model.estimate.naiveAmount).toBeNull();
    expect(model.estimate.savingsAmount).toBeNull();
    expect(model.estimate.savingsPercent).toBeNull();
    expect(formatMovieBudgetAmount(model.estimate.optimizedAmount, model.estimate.currency)).toBe("Estimate pending");
  });

  it("never hands the panel a negative, signed-zero or non-finite money figure", () => {
    const samples = [
      cost({ recordedProviderCostUsd: -0.004, estimatedRemainingProviderCostUsd: 0.001 }),
      cost({ recordedProviderCostUsd: 0, estimatedRemainingProviderCostUsd: -3 }),
      cost({ recordedProviderCostUsd: Number.NaN, estimatedRemainingProviderCostUsd: 4 }),
      cost({ recordedProviderCostUsd: -12, estimatedRemainingProviderCostUsd: 5 }),
      cost({ recordedProviderCostUsd: Number.POSITIVE_INFINITY, estimatedRemainingProviderCostUsd: 4 }),
      cost({ recordedProviderCostUsd: 1.7e308, estimatedRemainingProviderCostUsd: 0 }),
    ];
    for (const selectedTier of ["Draft", "Upgrade", "Master"] as const) {
      for (const sample of samples) {
        const estimate = buildMovieBudgetReadiness({ project, workspace: workspace(), selectedTier, cost: sample }).estimate;
        for (const value of [estimate.optimizedAmount, estimate.naiveAmount, estimate.savingsAmount, estimate.savingsPercent]) {
          if (value === null) continue;
          expect(Number.isFinite(value)).toBe(true);
          expect(Object.is(value, -0)).toBe(false);
          expect(value).toBeGreaterThanOrEqual(0);
        }
      }
    }
  });

  it("keeps a genuine zero-spend plan known instead of failing it closed", () => {
    const model = buildMovieBudgetReadiness({ project, workspace: workspace(), selectedTier: "Master", cost: cost({ recordedProviderCostUsd: 0, estimatedRemainingProviderCostUsd: 0 }) });
    expect(model.estimate.state).toBe("known");
    expect(model.estimate.optimizedAmount).toBe(0);
    expect(model.estimate.naiveAmount).toBe(0);
    expect(model.estimate.savingsAmount).toBe(0);
    expect(model.estimate.savingsPercent).toBe(0);
    expect(formatMovieBudgetAmount(model.estimate.optimizedAmount, model.estimate.currency)).toBe("USD 0.00");
  });

  it("refuses to render a signed, negative or non-finite figure as money at the display step", () => {
    // The estimate guard is not the only boundary: a figure reaching the formatter from any other
    // caller still has to fail closed rather than print "USD -7.00" or "USD NaN" into the panel.
    expect(formatMovieBudgetAmount(-7)).toBe("Estimate pending");
    expect(formatMovieBudgetAmount(-0.004)).toBe("Estimate pending");
    expect(formatMovieBudgetAmount(Number.NaN)).toBe("Estimate pending");
    expect(formatMovieBudgetAmount(Number.POSITIVE_INFINITY)).toBe("Estimate pending");
    // Signed zero is not negative, so it renders as the honest zero figure rather than "USD -0.00".
    expect(formatMovieBudgetAmount(-0)).toBe("USD 0.00");
  });
});
