import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { describeCreditBalance } from "./billingCreditBalance";
import type { BillingCredits } from "./api";

const viewSource = readFileSync(new URL("../components/BillingView.tsx", import.meta.url), "utf8");

const credits = (value: Partial<BillingCredits>): BillingCredits => ({
  includedGranted: 0,
  includedRemaining: 0,
  purchasedRemaining: 0,
  adjustmentBalance: 0,
  totalRemaining: 0,
  ...value,
});

describe("credit balance truth", () => {
  it("uses the authoritative API remaining amount after usage", () => {
    const balance = describeCreditBalance(credits({
      includedGranted: 10_000,
      includedRemaining: 10_000,
      totalRemaining: 3_500,
    }));
    expect(balance.totalRemaining).toBe(3_500);
    expect(balance.includedGranted).toBe(10_000);
    expect(balance.includedAllocation).toBe(10_000);
    // An allocation minus a balance is NOT proof of a debit from the ledger.
    expect(balance).not.toHaveProperty("usageRecorded");
    expect(balance).not.toHaveProperty("reconciles");
  });

  it("does not invent usage or remaining credits after entitlement expiry", () => {
    const balance = describeCreditBalance(credits({
      includedGranted: 1_000,
      includedRemaining: 0,
      totalRemaining: 0,
    }));
    expect(balance.includedGranted).toBe(1_000);
    expect(balance.totalRemaining).toBe(0);
    expect(balance).not.toHaveProperty("usageRecorded");
  });

  it("cannot mistake historical unallocated movements for current-period usage", () => {
    // After a new period is granted, a previous period's unattributed movements
    // may still affect the API balance. Their period and type aren't in this DTO.
    const balance = describeCreditBalance(credits({
      includedGranted: 1_000,
      includedRemaining: 1_000,
      totalRemaining: 300,
    }));
    expect(balance.totalRemaining).toBe(300);
    expect(balance).not.toHaveProperty("usageRecorded");
  });

  it("does not convert a positive refund into impossible negative usage", () => {
    const balance = describeCreditBalance(credits({
      includedGranted: 1_000,
      includedRemaining: 1_000,
      totalRemaining: 1_500,
    }));
    expect(balance.totalRemaining).toBe(1_500);
    expect(balance).not.toHaveProperty("reconciles");
  });

  it("preserves purchased and negative correction allocations without pretending they sum to spendable credits", () => {
    const balance = describeCreditBalance(credits({
      includedGranted: 1_000,
      includedRemaining: 1_000,
      purchasedRemaining: 500,
      adjustmentBalance: -400,
      totalRemaining: 400,
    }));
    expect(balance.purchasedRemaining).toBe(500);
    expect(balance.adjustmentBalance).toBe(-400);
    expect(balance.totalRemaining).toBe(400);
  });

  it("normalizes unavailable and malformed fields without rendering NaN", () => {
    expect(describeCreditBalance(undefined).totalRemaining).toBe(0);
    const balance = describeCreditBalance(credits({
      includedGranted: Number.NaN,
      purchasedRemaining: Number.POSITIVE_INFINITY,
      totalRemaining: 1_234.7,
    }));
    expect(balance.includedGranted).toBe(0);
    expect(balance.purchasedRemaining).toBe(0);
    expect(balance.totalRemaining).toBe(1_234);
  });

  it("keeps the headline and detail balance authoritative and never labels an allocation as remaining", () => {
    expect(viewSource).toContain("balance.totalRemaining");
    expect(viewSource).toContain('<dt>{t("billing.remainingCredits")}</dt><dd>{formatCredits(balance.totalRemaining)}</dd>');
    expect(viewSource).not.toContain('t("billing.includedRemaining"');
    expect(viewSource).toContain('t("billing.includedCredits", { count: formatCredits(balance.includedAllocation) })');
  });

  it("does not derive a made-up debit from the gap between allocations and the headline", () => {
    expect(viewSource).toContain("describeCreditBalance");
    expect(viewSource).not.toContain("balance.usageRecorded");
    expect(viewSource).not.toContain("balance.showUsageRow");
    expect(viewSource).not.toMatch(/transactions\s*\.\s*reduce/);
  });
});
