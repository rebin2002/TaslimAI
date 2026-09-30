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
});
