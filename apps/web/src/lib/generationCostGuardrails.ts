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

/**
 * The API quotes an estimate already rounded to the eight-decimal scale the
 * usage ledger stores, so a genuine per-request cost is routinely below a
 * cent. Rendering that quote at two decimals would show "USD 0.00" for a paid
 * generation and round "USD 1.005" down to "USD 1.00", telling the user a
 * different figure than the one the preflight threshold was evaluated on.
 * Keep the quoted precision and only trim trailing zeros beyond the cents.
 */
const ledgerScaleDecimals = 8;
const centsDecimals = 2;

/**
 * `toFixed` leaves fixed-point notation at 1e21 and above, so a quote that far
 * outside the ledger's range would render as "USD 1e+21.00" — a malformed money
 * figure rather than an honest one. Report it the way an unknown estimate is
 * already reported instead of showing a number nobody can act on.
 */
const maxFixedPointAmountUsd = 1e21;

function formatQuotedAmount(amountUsd: number, currency: string) {
  if (!Number.isFinite(amountUsd) || Math.abs(amountUsd) >= maxFixedPointAmountUsd) return null;
  const [whole, fraction = ""] = amountUsd.toFixed(ledgerScaleDecimals).split(".");
  return `${currency} ${whole}.${fraction.replace(/0+$/, "").padEnd(centsDecimals, "0")}`;
}

export function generationEstimateLabel(preview: GenerationCostPreview | null | undefined, unknownLabel = "Estimate unavailable") {
  const safe = safeGenerationCostPreview(preview);
  if (!safe || !safe.estimatedProviderCostKnown || safe.estimatedProviderCostUsd === null) return unknownLabel;
  return formatQuotedAmount(safe.estimatedProviderCostUsd, safe.currency) ?? unknownLabel;
}

export function requiresGenerationConfirmation(preview: GenerationCostPreview | null | undefined) {
  return safeGenerationCostPreview(preview)?.confirmationRequired === true;
}

export function isGenerationCapBlocked(preview: GenerationCostPreview | null | undefined) {
  const safe = safeGenerationCostPreview(preview);
  if (!safe) return false;
  return [safe.workspaceCap, safe.userCap, safe.projectCap].some((cap) => cap?.wouldExceed === true || (cap?.limitUsd !== null && cap?.usageKnown === false));
}
