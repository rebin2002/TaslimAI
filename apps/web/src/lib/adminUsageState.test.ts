import { describe, expect, it } from "vitest";
import { dailyCostMaximum, inspectableTransaction, isAdminUsageRequestCurrent, presetRange, reportHasProviderDetails, startAdminUsageRequest } from "./adminUsageState";
import type { AdminUsageReport, AdminUsageTransaction } from "./api";

const transaction: AdminUsageTransaction = {
  id: "tx-1", createdAt: "2026-09-22T00:00:00Z", completedAt: "2026-09-22T00:00:01Z", workspaceId: "ws-1", workspaceName: "Workspace", userId: "u-1", userEmail: "admin@example.com", userDisplayName: "Admin", projectId: null, conversationId: null, generationJobId: null, feature: "Image", status: "Completed", provider: "openai", model: "gpt-image-2.5-sunburst", inputTokens: 10, cachedInputTokens: 0, outputTokens: 0, imageInputTokens: null, imageOutputTokens: 100, latencyMs: 500, estimatedProviderCostUsd: 0.001, providerCostUsd: 0.002, chargedAmount: 0, currency: "USD", costBasis: "Actual", pricingVersion: "pricing-v1", pricingSnapshotJson: "{\"version\":\"pricing-v1\"}", safeMetadataJson: null, failureCode: null, isAnomalous: false, anomalyCode: null, refundedAt: null,
};

const report: AdminUsageReport = {
  summary: { fromUtc: "2026-09-01T00:00:00Z", toUtc: "2026-09-22T00:00:00Z", transactionCount: 1, successfulCount: 1, failedCount: 0, cancelledCount: 0, refundedCount: 0, totalProviderCostUsd: 0.002, totalCustomerChargesUsd: 0, pendingEstimatedProviderCostUsd: 0, anomalousCount: 0, currency: "USD" },
  breakdowns: { byFeature: [], byDay: [{ dayUtc: "2026-09-22T00:00:00Z", transactionCount: 1, providerCostUsd: 0.002, customerChargesUsd: 0 }], byWorkspace: [], byUser: [], byStatus: [] },
  transactions: { items: [transaction], page: 1, pageSize: 20, totalCount: 1, totalPages: 1 },
};

describe("admin usage state", () => {
  it("creates bounded date ranges for presets", () => {
    const range = presetRange("sevenDays", new Date("2026-09-22T12:00:00Z"));
    expect(new Date(range.fromUtc).toISOString()).toBe("2026-09-16T12:00:00.000Z");
    expect(new Date(range.toUtc).toISOString()).toBe("2026-09-22T12:00:00.000Z");
  });

  it("finds the daily chart maximum and preserves internal detail for inspectors", () => {
    expect(dailyCostMaximum(report.breakdowns.byDay)).toBe(0.002);
    expect(inspectableTransaction(transaction)?.model).toBe("gpt-image-2.5-sunburst");
    expect(reportHasProviderDetails(report)).toBe(true);
  });
});


describe("admin usage request ordering", () => {
  it("ignores an older report when a newer range request is active", () => {
    const first = startAdminUsageRequest(0, { fromUtc: "2026-09-01T00:00:00.000Z", toUtc: "2026-09-30T23:59:59.999Z" });
    const second = startAdminUsageRequest(first.sequence, { fromUtc: "2026-10-01T00:00:00.000Z", toUtc: "2026-10-31T23:59:59.999Z" });
    expect(isAdminUsageRequestCurrent(first, second.sequence)).toBe(false);
    expect(isAdminUsageRequestCurrent(second, second.sequence)).toBe(true);
  });
});
