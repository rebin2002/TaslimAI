import type { BillingCredits } from "./api";

/**
 * Credit figures shown in Account > Billing.
 *
 * `totalRemaining` is the API's authoritative spendable balance. The other
 * numbers are allocations/grants, NOT a decomposition of this balance: usage
 * debits and refunds are written without entitlement IDs, and an expired or
 * renewed entitlement changes which ledger entries count as spendable.
 *
 * Do not infer "credits used" from the difference between unrelated values.
 * A truthful usage figure requires a separately verified server-side ledger
 * aggregate with the correct period, expiry and refund semantics.
 */
export type CreditBalanceDisplay = {
  totalRemaining: number;
  includedGranted: number;
  includedAllocation: number;
  purchasedRemaining: number;
  adjustmentBalance: number;
};

const whole = (value: number | undefined | null): number =>
  typeof value === "number" && Number.isFinite(value) ? Math.trunc(value) : 0;

const nonNegative = (value: number | undefined | null): number => Math.max(0, whole(value));

export function describeCreditBalance(credits: BillingCredits | null | undefined): CreditBalanceDisplay {
  return {
    totalRemaining: nonNegative(credits?.totalRemaining),
    includedGranted: nonNegative(credits?.includedGranted),
    includedAllocation: nonNegative(credits?.includedRemaining),
    purchasedRemaining: nonNegative(credits?.purchasedRemaining),
    adjustmentBalance: whole(credits?.adjustmentBalance),
  };
}


/**
 * A Billing account response is private to the authenticated principal and
 * workspace. Two different users can belong to the SAME workspace, while an
 * in-place account switch may retain a previous session's resolved response.
 * Fail closed synchronously until both authenticated scope keys match; do not
 * wait for a React effect to clear a previous user's transaction reasons.
 */
export function scopedBillingAccount<T>(
  snapshot: Readonly<{ userId: string; workspaceId: string; account: T }> | null,
  userId: string | null | undefined,
  workspaceId: string | null | undefined,
): T | null {
  return userId && workspaceId && snapshot?.userId === userId && snapshot.workspaceId === workspaceId
    ? snapshot.account : null;
}

/** Invalid or absent API timestamps must not crash the private Billing ledger.
 * Display an unknown date instead of fabricating a billing/renewal date.
 */
export function formatBillingDate(value: string | null | undefined, formatter: Intl.DateTimeFormat): string {
  const date = typeof value === "string" && value.trim() ? new Date(value) : null;
  return date && Number.isFinite(date.getTime()) ? formatter.format(date) : "—";
}
