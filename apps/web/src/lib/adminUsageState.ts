import type { AdminUsageDailyBreakdown, AdminUsageReport, AdminUsageTransaction } from "./api";

export type AdminUsageRange = { fromUtc: string; toUtc: string };

export function presetRange(preset: "today" | "sevenDays" | "thirtyDays", now = new Date()): AdminUsageRange {
  const end = new Date(now);
  const start = new Date(now);
  if (preset === "today") start.setHours(0, 0, 0, 0);
  if (preset === "sevenDays") start.setDate(start.getDate() - 6);
  if (preset === "thirtyDays") start.setDate(start.getDate() - 29);
  return { fromUtc: start.toISOString(), toUtc: end.toISOString() };
}

export function dailyCostMaximum(days: AdminUsageDailyBreakdown[]): number {
  return Math.max(0, ...days.map((day) => day.providerCostUsd));
}

export function inspectableTransaction(transaction: AdminUsageTransaction | null) {
  if (!transaction) return null;
  return {
    ...transaction,
    provider: transaction.provider || "unknown",
    model: transaction.model || "unknown",
    pricingSnapshotJson: transaction.pricingSnapshotJson || null,
  };
}

export function reportHasProviderDetails(report: AdminUsageReport | null): boolean {
  return Boolean(report?.transactions.items.some((item) => item.provider || item.model || item.providerCostUsd >= 0));
}
