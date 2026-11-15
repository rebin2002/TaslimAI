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
 * A Billing account response is private to its workspace. React can retain a
 * resolved response across a workspace switch until the next request finishes;
 * never render that old snapshot or its transaction reasons under the new
 * workspace, even for a single frame.
 */
export function scopedBillingAccount<T>(
  snapshot: Readonly<{ workspaceId: string; account: T }> | null,
  workspaceId: string | null | undefined,
): T | null {
  return workspaceId && snapshot?.workspaceId === workspaceId ? snapshot.account : null;
}
