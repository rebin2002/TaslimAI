import { describe, expect, it } from "vitest";
import { isActivityRequestCurrent, startActivityRequest } from "./activityRequest";

describe("activity request freshness", () => {
  it("accepts only the latest request for the active workspace and filter", () => {
    const first = startActivityRequest(0, "workspace-1", "All");
    const second = startActivityRequest(first.sequence, "workspace-1", "Running");

    expect(isActivityRequestCurrent(first, second.sequence, "workspace-1", "Running")).toBe(false);
    expect(isActivityRequestCurrent(second, second.sequence, "workspace-1", "Running")).toBe(true);
  });

  it("rejects a response when the active workspace changes", () => {
    const request = startActivityRequest(0, "workspace-1", "All");

    expect(isActivityRequestCurrent(request, request.sequence, "workspace-2", "All")).toBe(false);
    expect(isActivityRequestCurrent(request, request.sequence, null, "All")).toBe(false);
  });
});
