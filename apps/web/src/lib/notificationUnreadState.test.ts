import { describe, expect, it } from "vitest";
import { resolveNotificationUnreadCount } from "./notificationUnreadState";

describe("notification unread count refreshes", () => {
  it("preserves the last known badge when a refresh fails", () => {
    expect(resolveNotificationUnreadCount(4, null)).toBe(4);
  });

  it("accepts fresh counts while preventing invalid badge values", () => {
    expect(resolveNotificationUnreadCount(4, 7)).toBe(7);
    expect(resolveNotificationUnreadCount(4, -2)).toBe(0);
    expect(resolveNotificationUnreadCount(4, Number.NaN)).toBe(4);
    expect(resolveNotificationUnreadCount(4, 2.8)).toBe(2);
  });
});
