import { describe, expect, it } from "vitest";
import { isNotificationRequestCurrent, startNotificationRequest } from "./notificationRequest";

describe("notification request freshness", () => {
  it("accepts only the latest request for the active workspace", () => {
    const first = startNotificationRequest(0, "workspace-1");
    const second = startNotificationRequest(first.sequence, "workspace-1");

    expect(isNotificationRequestCurrent(first, second.sequence, "workspace-1")).toBe(false);
    expect(isNotificationRequestCurrent(second, second.sequence, "workspace-1")).toBe(true);
  });

  it("rejects a response when the active workspace changes", () => {
    const request = startNotificationRequest(0, "workspace-1");

    expect(isNotificationRequestCurrent(request, request.sequence, "workspace-2")).toBe(false);
    expect(isNotificationRequestCurrent(request, request.sequence, null)).toBe(false);
  });
});
