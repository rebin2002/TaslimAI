import type { MovieRegenerationCostPreview, MovieSelectiveRegenerationResponse } from "@/lib/api";
import { localeTag, type Locale } from "@/lib/i18n";

export type MovieRegenerationIdempotencyKeys = { create: string; confirm: string };
export type MovieRegenerationAttempt = { key: string; generation: number; keys: MovieRegenerationIdempotencyKeys };

export function movieRegenerationAttemptIsCurrent(current: MovieRegenerationAttempt | undefined, candidate: MovieRegenerationAttempt): boolean {
  return current?.key === candidate.key && current.generation === candidate.generation;
}

export function retireMovieRegenerationAttempt(current: MovieRegenerationAttempt | undefined, candidate: MovieRegenerationAttempt): MovieRegenerationAttempt | undefined {
  return movieRegenerationAttemptIsCurrent(current, candidate) ? undefined : current;
}

export type MovieRegenerationConfirmationState = {
  requestId: string;
  preview: MovieRegenerationCostPreview;
  canConfirm: boolean;
  blockedReason: "readiness-unavailable" | "server-blocked" | "confirmation-required" | null;
};

export function buildMovieRegenerationConfirmation(response: MovieSelectiveRegenerationResponse): MovieRegenerationConfirmationState {
  const preview = response.request.costPreview;
  const blockedReason = !preview.guardrails
    ? "readiness-unavailable"
    : !preview.guardrails.canProceed
      ? "server-blocked"
      : !preview.confirmationRequired
        ? "confirmation-required"
        : null;
  return {
    requestId: response.request.id,
    preview,
    canConfirm: blockedReason === null,
    blockedReason,
  };
}

export async function confirmMovieRegenerationIfAllowed(state: MovieRegenerationConfirmationState, confirm: (requestId: string) => Promise<unknown>): Promise<boolean> {
  if (!state.canConfirm) return false;
  await confirm(state.requestId);
  return true;
}

export function formatMovieRegenerationCost(preview: MovieRegenerationCostPreview, locale: Locale): string | null {
  const currency = preview.currency || "USD";
  const formatter = (amount: number) => {
    try {
      return new Intl.NumberFormat(localeTag(locale), { style: "currency", currency, maximumFractionDigits: 4 }).format(amount);
    } catch {
      return `${amount} ${currency}`;
    }
  };
  if (preview.estimatedProviderCostKnown && preview.estimatedProviderCostUsd !== null) return formatter(preview.estimatedProviderCostUsd);
  const minimum = preview.minimumEstimatedProviderCostUsd;
  const maximum = preview.maximumEstimatedProviderCostUsd;
  if (typeof minimum === "number" && typeof maximum === "number") {
    return minimum === maximum
      ? formatter(minimum)
      : `${formatter(minimum)}–${formatter(maximum)}`;
  }
  return null;
}
