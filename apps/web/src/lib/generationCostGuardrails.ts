import type { GenerationCostPreview } from "./api";

export type SafeGenerationCostPreview = Pick<
  GenerationCostPreview,
  "estimateStatus" | "estimatedProviderCostUsd" | "estimatedProviderCostKnown" | "currency" | "unknownReason" | "warning" | "confirmationRequired" | "canProceed" | "workspaceCap" | "userCap" | "projectCap" | "warnings"
>;

export function safeGenerationCostPreview(preview: GenerationCostPreview | null | undefined): SafeGenerationCostPreview | null {
  if (!preview) return null;
  const estimateIsKnown = preview.estimatedProviderCostKnown && preview.estimateStatus === "known";
  return {
    estimateStatus: preview.estimateStatus,
    estimatedProviderCostUsd: estimateIsKnown ? preview.estimatedProviderCostUsd : null,
    estimatedProviderCostKnown: estimateIsKnown,
    currency: preview.currency,
    unknownReason: preview.unknownReason,
    warning: preview.warning,
    confirmationRequired: preview.confirmationRequired,
    canProceed: preview.canProceed,
    workspaceCap: preview.workspaceCap,
    userCap: preview.userCap,
    projectCap: preview.projectCap,
    warnings: preview.warnings.map(({ code, severity, message }) => ({ code, severity, message })),
  };
}

export function generationEstimateLabel(preview: GenerationCostPreview | null | undefined, unknownLabel = "Estimate unavailable") {
  const safe = safeGenerationCostPreview(preview);
  if (!safe || !safe.estimatedProviderCostKnown || safe.estimatedProviderCostUsd === null) return unknownLabel;
  return `${safe.currency} ${safe.estimatedProviderCostUsd.toFixed(2)}`;
}

export function requiresGenerationConfirmation(preview: GenerationCostPreview | null | undefined) {
  return safeGenerationCostPreview(preview)?.confirmationRequired === true;
}

export function isGenerationCapBlocked(preview: GenerationCostPreview | null | undefined) {
  const safe = safeGenerationCostPreview(preview);
  if (!safe) return false;
  return [safe.workspaceCap, safe.userCap, safe.projectCap].some((cap) => cap?.wouldExceed === true || (cap?.limitUsd !== null && cap?.usageKnown === false));
}
