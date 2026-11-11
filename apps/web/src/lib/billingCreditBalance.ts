import type { BillingCredits } from "./api";

/**
 * What Account > Billing is allowed to claim about a workspace's credits.
 *
 * The API reports five figures (`apps/web/src/lib/api.ts` -> `BillingCredits`):
 * the included allowance granted for the period, and three per-category
 * balances, plus one authoritative `totalRemaining`.
 *
 * Usage debits are written to the credit ledger *without* an entitlement
 * allocation (`BillingServices.RecordUsageDebitAsync` passes `null` for
 * `entitlementId`), and `BillingAccountService.GetAccountAsync` only folds
 * those unallocated movements into `totalRemaining`. The per-category
 * balances therefore never decrease when the workspace spends credits, so
 * printing one of them as a "remaining" figure makes the page contradict
 * its own headline balance (for example "3,500 remaining" next to
 * "10,000 included credits remaining" once the plan allowance is spent).
 *
 * This module keeps the arithmetic in one place: the headline stays the only
 * "remaining" claim, the allowance card keeps its granted/purchased/correction
 * rows, and the difference between those rows and the headline is surfaced as
 * the usage the ledger recorded outside the allocations.
 */
export type CreditBalanceDisplay = {
  /** Authoritative spendable balance reported by the API. */
  totalRemaining: number;
  /** Included allowance granted for the current billing period. */
  includedGranted: number;
  /** Included allowance net of allocated movements; never a spendable claim. */
  includedAllocation: number;
  /** Purchased entitlement balance. */
  purchasedRemaining: number;
  /** Administrative correction balance, which may legitimately be negative. */
  adjustmentBalance: number;
  /** Usage recorded outside the per-category allocations, floored at zero. */
  usageRecorded: number;
  /** Whether `usageRecorded` is worth a row of its own. */
  showUsageRow: boolean;
  /** Whether the printed rows add back up to `totalRemaining`. */
  reconciles: boolean;
};

const whole = (value: number | undefined | null): number =>
  typeof value === "number" && Number.isFinite(value) ? Math.trunc(value) : 0;

const nonNegative = (value: number | undefined | null): number => Math.max(0, whole(value));

export function describeCreditBalance(credits: BillingCredits | null | undefined): CreditBalanceDisplay {
  const totalRemaining = nonNegative(credits?.totalRemaining);
  const includedGranted = nonNegative(credits?.includedGranted);
  const includedAllocation = nonNegative(credits?.includedRemaining);
  const purchasedRemaining = nonNegative(credits?.purchasedRemaining);
  const adjustmentBalance = whole(credits?.adjustmentBalance);

  const allocated = includedGranted + purchasedRemaining + adjustmentBalance;
  const usageRecorded = Math.max(0, allocated - totalRemaining);

  return {
    totalRemaining,
    includedGranted,
    includedAllocation,
    purchasedRemaining,
    adjustmentBalance,
    usageRecorded,
    showUsageRow: usageRecorded > 0,
    reconciles: allocated - usageRecorded === totalRemaining,
  };
}
