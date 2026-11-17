import { describe, expect, it } from "vitest";
import type { GenerationCostPreview } from "./api";
import { generationEstimateLabel, isGenerationCapBlocked, requiresGenerationConfirmation, safeGenerationCostPreview } from "./generationCostGuardrails";

const preview = (overrides: Partial<GenerationCostPreview> = {}): GenerationCostPreview => ({
  estimateStatus: "known",
  estimatedProviderCostUsd: 1.25,
  estimatedProviderCostKnown: true,
  currency: "USD",
  unknownReason: null,
  warning: false,
  confirmationRequired: false,
  canProceed: true,
  workspaceCap: null,
  userCap: null,
  projectCap: null,
  warnings: [],
  ...overrides,
});

describe("generation cost guardrails", () => {
  it("renders unknown estimates honestly instead of treating them as zero", () => {
    const value = preview({ estimateStatus: "unknown", estimatedProviderCostUsd: null, estimatedProviderCostKnown: false, unknownReason: "pricing_rule_missing" });
    expect(generationEstimateLabel(value)).toBe("Estimate unavailable");
    expect(safeGenerationCostPreview(value)?.estimatedProviderCostUsd).toBeNull();
  });

  it("fails closed when a non-known status carries a contradictory numeric estimate", () => {
    const value = safeGenerationCostPreview(preview({ estimateStatus: "unknown", estimatedProviderCostUsd: 999, estimatedProviderCostKnown: true }));
    expect(value?.estimatedProviderCostUsd).toBeNull();
    expect(value?.estimatedProviderCostKnown).toBe(false);
    expect(generationEstimateLabel(preview({ estimateStatus: "unknown", estimatedProviderCostUsd: 999, estimatedProviderCostKnown: true }))).toBe("Estimate unavailable");
  });

  it("requires an explicit confirmation for a warning and blocks an exceeded cap", () => {
    const value = preview({
      warning: true,
      confirmationRequired: true,
      canProceed: false,
      userCap: { scope: "user", limitUsd: 5, usedUsd: 4.5, usageKnown: true, remainingUsd: 0.5, wouldExceed: true },
      warnings: [{ code: "EXPENSIVE_GENERATION", severity: "warning", message: "Review the estimate." }],
    });
    expect(requiresGenerationConfirmation(value)).toBe(true);
    expect(isGenerationCapBlocked(value)).toBe(true);
  });

  it("keeps normal-user preview fields provider/model-free", () => {
    const value = safeGenerationCostPreview(preview()) as Record<string, unknown>;
    expect(value).not.toHaveProperty("provider");
    expect(value).not.toHaveProperty("providerKey");
    expect(value).not.toHaveProperty("model");
    expect(value).not.toHaveProperty("modelKey");
    expect(value).not.toHaveProperty("prompt");
  });

  it("renders a sub-cent estimate at the ledger scale instead of reporting it as free", () => {
    expect(generationEstimateLabel(preview({ estimatedProviderCostUsd: 0.004 }))).toBe("USD 0.004");
    expect(generationEstimateLabel(preview({ estimatedProviderCostUsd: 0.0004 }))).toBe("USD 0.0004");
    expect(generationEstimateLabel(preview({ estimatedProviderCostUsd: 0.00000001 }))).toBe("USD 0.00000001");
  });

  it("never rounds a known estimate up into a figure the server did not quote", () => {
    expect(generationEstimateLabel(preview({ estimatedProviderCostUsd: 1.005 }))).toBe("USD 1.005");
    expect(generationEstimateLabel(preview({ estimatedProviderCostUsd: 0.1 }))).toBe("USD 0.10");
  });

  it("keeps cent-scale and whole-dollar estimates exactly as they were quoted", () => {
    expect(generationEstimateLabel(preview())).toBe("USD 1.25");
    expect(generationEstimateLabel(preview({ estimatedProviderCostUsd: 1234.5 }))).toBe("USD 1234.50");
    expect(generationEstimateLabel(preview({ estimatedProviderCostUsd: 0 }))).toBe("USD 0.00");
  });

  it("formats the estimate crossing the expensive-generation threshold with the same scale", () => {
    const crossingThreshold = preview({
      estimatedProviderCostUsd: 0.004,
      warning: true,
      confirmationRequired: true,
      warnings: [{ code: "EXPENSIVE_GENERATION", severity: "warning", message: "Review the estimate before continuing." }],
    });
    expect(requiresGenerationConfirmation(crossingThreshold)).toBe(true);
    expect(generationEstimateLabel(crossingThreshold)).toBe("USD 0.004");
  });

  it("keeps the largest fixed-point quote readable at the ledger scale", () => {
    expect(generationEstimateLabel(preview({ estimatedProviderCostUsd: 1e21 - 131072 }))).toBe("USD 999999999999999868928.00");
  });

  it("reports a quote beyond fixed-point notation as unavailable rather than malformed", () => {
    expect(generationEstimateLabel(preview({ estimatedProviderCostUsd: 1e21 }))).toBe("Estimate unavailable");
    expect(generationEstimateLabel(preview({ estimatedProviderCostUsd: Number.MAX_VALUE }))).toBe("Estimate unavailable");
    expect(generationEstimateLabel(preview({ estimatedProviderCostUsd: Number.POSITIVE_INFINITY }))).toBe("Estimate unavailable");
    expect(generationEstimateLabel(preview({ estimatedProviderCostUsd: Number.NaN }))).toBe("Estimate unavailable");
  });

});
