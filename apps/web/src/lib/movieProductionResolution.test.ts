import { describe, expect, it } from "vitest";
import { buildMovieResolutionPlan, displayProductionStage, displayProductionStatus } from "./movieProductionResolution";

describe("movie production resolution planning", () => {
  it("starts a clean project in Draft and keeps estimate honest without a render path", () => {
    const plan = buildMovieResolutionPlan({ durationSeconds: 30, shotCount: 2, reviewableTakeCount: 0, selectedTakeCount: 0, failedPassCount: 0 });
    expect(plan.recommended).toBe("Draft");
    expect(plan.qcLabel).toBe("Waiting for source");
    expect(plan.escalationLabel).toBe("Keep it reversible");
    expect(plan.tiers.every((tier) => tier.estimate === "Estimate pending")).toBe(true);
  });

  it("recommends Upgrade when a real source is ready for review", () => {
    const plan = buildMovieResolutionPlan({ durationSeconds: 45, shotCount: 4, reviewableTakeCount: 1, selectedTakeCount: 0, failedPassCount: 0, estimateAvailable: true });
    expect(plan.recommended).toBe("Upgrade");
    expect(plan.qcLabel).toBe("Ready to review");
    expect(plan.tiers.find((tier) => tier.id === "Upgrade")?.estimate).toMatch(/^About /);
  });

  it("recommends Master after selection and downgrades to Draft when QC escalates", () => {
    const selected = buildMovieResolutionPlan({ durationSeconds: 90, shotCount: 8, reviewableTakeCount: 2, selectedTakeCount: 1, failedPassCount: 0 });
    expect(selected.recommended).toBe("Master");
    expect(selected.escalationLabel).toBe("No escalation needed");

    const escalated = buildMovieResolutionPlan({ durationSeconds: 90, shotCount: 8, reviewableTakeCount: 2, selectedTakeCount: 1, failedPassCount: 1 });
    expect(escalated.recommended).toBe("Draft");
    expect(escalated.qcTone).toBe("blocked");
    expect(escalated.escalationLabel).toBe("Escalation suggested");
  });

  it("keeps internal stage names out of the visible production vocabulary", () => {
    expect(displayProductionStage("ProductionKeyframe")).toBe("Source frame");
    expect(displayProductionStage("ProductionRender")).toBe("Master pass");
    expect(displayProductionStage("SelectedFinalTake")).toBe("Selected take");
    expect(displayProductionStatus("PendingApproval")).toBe("Needs review");
  });
});
