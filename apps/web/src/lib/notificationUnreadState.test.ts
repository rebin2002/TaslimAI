import { describe, expect, it } from "vitest";
import { resolveNotificationBadgeCount, resolveNotificationUnreadCount, resolveNotificationUnreadCountAfterPollFailure } from "./notificationUnreadState";

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

describe("notification unread poll failure handling", () => {
  it("fails closed when the server definitively rejects inbox access", () => {
    // 403: the member was removed from the workspace that owns the count.
    expect(resolveNotificationUnreadCountAfterPollFailure(5, { status: 403 })).toBe(0);
    // 401: the session behind the membership check ended.
    expect(resolveNotificationUnreadCountAfterPollFailure(5, { status: 401 })).toBe(0);
    // Regression: these decisive rejections used to fall into the transient
    // branch and pin the previous workspace's unread count forever.
  });

  it("preserves the last observed badge across transient failures", () => {
    expect(resolveNotificationUnreadCountAfterPollFailure(5, { status: 503 })).toBe(5);
    expect(resolveNotificationUnreadCountAfterPollFailure(5, { status: 500 })).toBe(5);
    expect(resolveNotificationUnreadCountAfterPollFailure(5, { status: 429 })).toBe(5);
    expect(resolveNotificationUnreadCountAfterPollFailure(5, new Error("offline"))).toBe(5);
    expect(resolveNotificationUnreadCountAfterPollFailure(5, undefined)).toBe(5);
    expect(resolveNotificationUnreadCountAfterPollFailure(5, "not an object")).toBe(5);
  });

  it("only trusts a numeric status value for the decisive decision", () => {
    expect(resolveNotificationUnreadCountAfterPollFailure(2, { status: "403" })).toBe(2);
    expect(resolveNotificationUnreadCountAfterPollFailure(2, { status: Number.NaN })).toBe(2);
    expect(resolveNotificationUnreadCountAfterPollFailure(2, {})).toBe(2);
  });

  it("normalizes invalid retained counts to zero", () => {
    expect(resolveNotificationUnreadCountAfterPollFailure(-3, { status: 500 })).toBe(0);
    expect(resolveNotificationUnreadCountAfterPollFailure(4.8, { status: 500 })).toBe(4);
    expect(resolveNotificationUnreadCountAfterPollFailure(Number.NaN, { status: 500 })).toBe(0);
  });

  it("walks success, revocation and recovery without leaving a pinned count", () => {
    // A poll observes five unread receipts for the current membership scope.
    let badge = resolveNotificationUnreadCount(0, 5);
    expect(badge).toBe(5);

    // The user is removed from the workspace; the polls turn decisively 403.
    badge = resolveNotificationUnreadCountAfterPollFailure(badge, { status: 403 });
    expect(badge).toBe(0);

    // Re-invited and the next poll succeeds: the count comes back normally.
    badge = resolveNotificationUnreadCount(badge, 3);
    expect(badge).toBe(3);
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
