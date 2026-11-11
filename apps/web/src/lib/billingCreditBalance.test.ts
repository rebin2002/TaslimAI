import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { describeCreditBalance } from "./billingCreditBalance";
import type { BillingCredits } from "./api";

const viewSource = readFileSync(new URL("../components/BillingView.tsx", import.meta.url), "utf8");

/**
 * Payloads below are the exact shapes `BillingAccountService.GetAccountAsync`
 * returns once a workspace has used part of its allowance: `includedRemaining`
 * is the sum of ledger entries that point at the included entitlement, while
 * usage debits are written with no entitlement at all and are folded only into
 * `totalRemaining`.
 */
const credits = (value: Partial<BillingCredits>): BillingCredits => ({
  includedGranted: 0,
  includedRemaining: 0,
  purchasedRemaining: 0,
  adjustmentBalance: 0,
  totalRemaining: 0,
  ...value,
});

describe("credit balance display", () => {
  it("surfaces the usage recorded outside the per-category allocations", () => {
    // Pro plan: 10,000 included credits granted, 6,500 of them used.
    const balance = describeCreditBalance(credits({
      includedGranted: 10_000,
      includedRemaining: 10_000,
      totalRemaining: 3_500,
    }));

    expect(balance.usageRecorded).toBe(6_500);
    expect(balance.showUsageRow).toBe(true);
    expect(balance.reconciles).toBe(true);
    expect(balance.includedGranted + balance.purchasedRemaining + balance.adjustmentBalance - balance.usageRecorded)
      .toBe(balance.totalRemaining);
  });

  it("keeps an untouched allowance free of a phantom usage row", () => {
    const balance = describeCreditBalance(credits({
      includedGranted: 10_000,
      includedRemaining: 10_000,
      totalRemaining: 10_000,
    }));

    expect(balance.usageRecorded).toBe(0);
    expect(balance.showUsageRow).toBe(false);
    expect(balance.reconciles).toBe(true);
  });

  it("accounts for every credit once the allowance is exhausted", () => {
    const balance = describeCreditBalance(credits({
      includedGranted: 10_000,
      includedRemaining: 10_000,
      totalRemaining: 0,
    }));

    expect(balance.totalRemaining).toBe(0);
    expect(balance.usageRecorded).toBe(10_000);
    expect(balance.reconciles).toBe(true);
  });

  it("reconciles purchased credits and negative corrections against the headline balance", () => {
    // 1,000 included + 500 purchased - 400 correction = 1,100 allocated.
    // 700 of that was spent through unallocated usage debits, leaving 400.
    const balance = describeCreditBalance(credits({
      includedGranted: 1_000,
      includedRemaining: 1_000,
      purchasedRemaining: 500,
      adjustmentBalance: -400,
      totalRemaining: 400,
    }));

    expect(balance.usageRecorded).toBe(700);
    expect(balance.reconciles).toBe(true);
  });

  it("never invents negative usage when the balance exceeds the printed allocations", () => {
    const balance = describeCreditBalance(credits({
      includedGranted: 1_000,
      includedRemaining: 1_000,
      totalRemaining: 1_500,
    }));

    expect(balance.usageRecorded).toBe(0);
    expect(balance.reconciles).toBe(false);
  });

  it("degrades to an empty balance instead of rendering NaN", () => {
    const balance = describeCreditBalance(undefined);
    expect(balance.totalRemaining).toBe(0);
    expect(balance.usageRecorded).toBe(0);
    expect(balance.showUsageRow).toBe(false);
    expect(balance.reconciles).toBe(true);

    const broken = describeCreditBalance(credits({
      includedGranted: Number.NaN,
      purchasedRemaining: Number.POSITIVE_INFINITY,
      totalRemaining: 1_234.7,
    }));
    expect(broken.includedGranted).toBe(0);
    expect(broken.purchasedRemaining).toBe(0);
    expect(broken.totalRemaining).toBe(1_234);
    expect(broken.showUsageRow).toBe(false);
  });

  it("does not restate an allocation as a remaining balance", () => {
    // The included allocation for a spent allowance still reads 10,000 while the
    // headline says 3,500, so the card may not label it "remaining".
    expect(describeCreditBalance(credits({ includedGranted: 10_000, includedRemaining: 10_000, totalRemaining: 3_500 }))
      .includedAllocation).toBe(10_000);
    expect(viewSource).not.toContain(`t("billing.includedRemaining"`);
  });

  it("reconciles the credit card against the allowance breakdown", () => {
    expect(viewSource).toContain("describeCreditBalance");
    expect(viewSource).toContain("billingCreditBalance");
    expect(viewSource).toContain("balance.totalRemaining");
    expect(viewSource).toContain("balance.usageRecorded");
    expect(viewSource).toContain('t("billing.entry.debit")');
    expect(viewSource).toContain('t("billing.includedCredits", { count: formatCredits(balance.includedAllocation) })');
  });

  it("keeps the headline balance on the API figure rather than the capped history page", () => {
    // The API returns at most the latest 100 movements, so summing them in the
    // browser would silently understate spent credits.
    expect(viewSource).toContain("balance.totalRemaining");
    expect(viewSource).not.toMatch(/transactions\s*\.\s*reduce/);
  });
});
