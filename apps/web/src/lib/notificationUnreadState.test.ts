import { describe, expect, it } from "vitest";
import { resolveNotificationBadgeCount, resolveNotificationUnreadCount } from "./notificationUnreadState";

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

describe("notification badge ownership", () => {
  it("returns the badge to the live header count once the panel closes", () => {
    // Regression: a closed panel used to keep owning the badge forever, so
    // later polls and read events from another surface were invisible.
    expect(resolveNotificationBadgeCount({ panelOpen: false, panelLoading: false, panelUnreadCount: 5, liveUnreadCount: 6 })).toBe(6);
  });

  it("lets a loaded, visible panel own the badge", () => {
    expect(resolveNotificationBadgeCount({ panelOpen: true, panelLoading: false, panelUnreadCount: 3, liveUnreadCount: 0 })).toBe(3);
  });

  it("ignores a stale snapshot from a previous session while reopening", () => {
    expect(resolveNotificationBadgeCount({ panelOpen: true, panelLoading: true, panelUnreadCount: 9, liveUnreadCount: 2 })).toBe(2);
  });

  it("falls back to the live count when the panel has no usable snapshot", () => {
    expect(resolveNotificationBadgeCount({ panelOpen: true, panelLoading: false, panelUnreadCount: null, liveUnreadCount: 4 })).toBe(4);
    expect(resolveNotificationBadgeCount({ panelOpen: true, panelLoading: false, panelUnreadCount: Number.NaN, liveUnreadCount: 4 })).toBe(4);
  });

  it("normalizes hostile counts before display", () => {
    expect(resolveNotificationBadgeCount({ panelOpen: true, panelLoading: false, panelUnreadCount: -7, liveUnreadCount: 1 })).toBe(0);
    expect(resolveNotificationBadgeCount({ panelOpen: true, panelLoading: false, panelUnreadCount: 2.8, liveUnreadCount: 1 })).toBe(2);
    expect(resolveNotificationBadgeCount({ panelOpen: false, panelLoading: false, panelUnreadCount: 1, liveUnreadCount: -3 })).toBe(0);
  });

  it("walks a mark-all event through the full open, close and refresh lifecycle", () => {
    const session = { panelOpen: false, panelLoading: false, panelUnreadCount: null as number | null, liveUnreadCount: 1 };
    expect(resolveNotificationBadgeCount(session)).toBe(1);

    session.panelOpen = true;
    session.panelLoading = true;
    expect(resolveNotificationBadgeCount(session)).toBe(1);

    session.panelLoading = false;
    session.panelUnreadCount = 1;
    expect(resolveNotificationBadgeCount(session)).toBe(1);

    // Mark-all inside the panel clears both the snapshot and the live count.
    session.panelUnreadCount = 0;
    session.liveUnreadCount = 0;
    expect(resolveNotificationBadgeCount(session)).toBe(0);

    // The user closes the popover; the panel snapshot can no longer freeze the badge.
    session.panelOpen = false;
    session.panelUnreadCount = 0;
    expect(resolveNotificationBadgeCount(session)).toBe(0);

    // A generation completes afterwards and the poll raises the badge again.
    session.liveUnreadCount = 2;
    expect(resolveNotificationBadgeCount(session)).toBe(2);
  });
});
