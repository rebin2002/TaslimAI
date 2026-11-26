import { describe, expect, it } from "vitest";
import { failUsageLoad, hasUsageLoadError, initialUsageLoadState, retryUsageLoad, usageRequestKey } from "./usageViewState";

describe("usage view load recovery", () => {
  it("makes a transient failure retryable without carrying the error to another request", () => {
    const firstRequest = usageRequestKey("workspace-1", 1);
    const failed = failUsageLoad(initialUsageLoadState, firstRequest);

    expect(hasUsageLoadError(failed, firstRequest)).toBe(true);
    expect(hasUsageLoadError(failed, usageRequestKey("workspace-2", 1))).toBe(false);

    const retried = retryUsageLoad(failed);
    expect(retried).toEqual({ errorKey: null, attempt: 1 });
    expect(hasUsageLoadError(retried, firstRequest)).toBe(false);
  });

  it("keeps page changes independent from a failed page request", () => {
    const failed = failUsageLoad(initialUsageLoadState, usageRequestKey("workspace-1", 2));

    expect(hasUsageLoadError(failed, usageRequestKey("workspace-1", 1))).toBe(false);
    expect(hasUsageLoadError(failed, usageRequestKey("workspace-1", 2))).toBe(true);
  });
});
