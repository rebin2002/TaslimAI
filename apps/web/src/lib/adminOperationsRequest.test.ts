import { describe, expect, it } from "vitest";
import { isAdminOperationsRequestCurrent, startAdminOperationsRequest } from "./adminOperationsRequest";

describe("admin operations request freshness", () => {
  it("rejects a delayed response after the selected range changes", () => {
    const thirtyDays = { fromUtc: "2026-09-28T00:00:00.000Z", toUtc: "2026-10-27T23:59:59.999Z" };
    const today = { fromUtc: "2026-10-27T00:00:00.000Z", toUtc: "2026-10-27T23:59:59.999Z" };
    const initial = startAdminOperationsRequest(0, thirtyDays);
    const selected = startAdminOperationsRequest(initial.sequence, today);

    expect(isAdminOperationsRequestCurrent(initial, selected.sequence, today)).toBe(false);
    expect(isAdminOperationsRequestCurrent(selected, selected.sequence, today)).toBe(true);
  });

  it("rejects a response after the component invalidates its requests", () => {
    const range = { fromUtc: "2026-10-27T00:00:00.000Z", toUtc: "2026-10-27T23:59:59.999Z" };
    const request = startAdminOperationsRequest(4, range);

    expect(isAdminOperationsRequestCurrent(request, request.sequence + 1, range)).toBe(false);
  });
});
