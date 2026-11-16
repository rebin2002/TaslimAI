import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { describeCreditBalance, scopedBillingAccount } from "./billingCreditBalance";
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

  it("never renders a prior identity's Billing snapshot, even in the same workspace", () => {
    const accountA = { transactions: [{ reason: "Private user A credit history" }] };
    const accountB = { transactions: [{ reason: "Private user B credit history" }] };
    const a = { userId: "user-A", workspaceId: "workspace-shared", account: accountA };
    const b = { userId: "user-B", workspaceId: "workspace-shared", account: accountB };
    expect(scopedBillingAccount(a, "user-A", "workspace-shared")).toBe(accountA);
    expect(scopedBillingAccount(a, "user-B", "workspace-shared")).toBeNull();
    expect(scopedBillingAccount(a, "user-A", "workspace-other")).toBeNull();
    expect(scopedBillingAccount(a, null, "workspace-shared")).toBeNull();
    expect(scopedBillingAccount(a, "user-A", null)).toBeNull();
    expect(scopedBillingAccount(a, undefined, "workspace-shared")).toBeNull();
    expect(scopedBillingAccount(a, "user-A", undefined)).toBeNull();
    expect(scopedBillingAccount(b, "user-B", "workspace-shared")).toBe(accountB);
    expect(scopedBillingAccount(b, "user-A", "workspace-shared")).toBeNull();
    expect(scopedBillingAccount(null, "user-A", "workspace-shared")).toBeNull();
    expect(viewSource).toContain("scopedBillingAccount(snapshot, userId, workspaceId)");
    expect(viewSource).toContain("failedScope?.userId === userId && failedScope?.workspaceId === workspaceId");
    expect(viewSource).toContain("}, [userId, workspaceId]);");
  });

  it("remounts the ledger on every principal/workspace switch and hides old balances after fetch failure", () => {
    // A → B → A used to rematch A's cached snapshot before the new A request
    // resolved, briefly implying an old spendable balance was current.
    // The identity key forces a fresh component instance with no prior ledger.
    expect(viewSource).toContain("function ScopedBillingView({ userId, workspaceId }");
    expect(viewSource).toContain('key={`${user.id}:${workspace.id}`}');
    expect(viewSource).toContain("if (loading || !user?.id || !workspace?.id) return null;");
    expect(viewSource).toContain("const account = error ? null : scopedBillingAccount(snapshot, userId, workspaceId);");
    expect(viewSource).not.toContain("const { user, workspace } = useAuth();");
  });

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

  it("keeps unwired billing mutation placeholders disabled regardless of API action flags", () => {
    // Until a verified checkout/upgrade/cancel client is implemented, presenting
    // a clickable button without an action is misleading and not release-ready.
    for (const flag of ["checkoutAvailable", "upgradeAvailable", "downgradeAvailable", "cancelAvailable"]) {
      expect(viewSource).not.toContain(`disabled={!account.actions.${flag}}`);
    }
    expect(viewSource.match(/<button[^>]*\sdisabled>/g)).toHaveLength(4);
    expect(viewSource).toContain("The billing action buttons have no mutation handlers yet.");
  });

  it("does not derive a made-up debit from the gap between allocations and the headline", () => {
    expect(viewSource).toContain("describeCreditBalance");
    expect(viewSource).not.toContain("balance.usageRecorded");
    expect(viewSource).not.toContain("balance.showUsageRow");
    expect(viewSource).not.toMatch(/transactions\s*\.\s*reduce/);
  });
});
