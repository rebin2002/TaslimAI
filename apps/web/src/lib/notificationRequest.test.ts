import { describe, expect, it } from "vitest";
import {
  acceptNotificationCountPublication,
  beginNotificationCountWorkspace,
  isNotificationCountSnapshotCurrent,
  isNotificationRequestCurrent,
  issueNotificationCountSnapshot,
  startNotificationRequest,
  type NotificationCountAuthority,
} from "./notificationRequest";

const unsignedAuthority: NotificationCountAuthority = { workspaceId: null, version: 0 };

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

describe("unread count authority fence", () => {
  const mounted = beginNotificationCountWorkspace(unsignedAuthority, "workspace-1");

  it("retires a poll snapshot issued before a locally published count", () => {
    const snapshot = issueNotificationCountSnapshot(mounted, "workspace-1");
    expect(isNotificationCountSnapshotCurrent(snapshot, mounted)).toBe(true);

    const accepted = acceptNotificationCountPublication(mounted, { workspaceId: "workspace-1", unreadCount: 4 });
    expect(accepted).not.toBeNull();
    expect(accepted?.unreadCount).toBe(4);

    // The poll reply was computed from a pre-read snapshot, so it no longer owns the badge.
    expect(isNotificationCountSnapshotCurrent(snapshot, accepted!.authority)).toBe(false);
  });

  it("still applies a poll issued after the published count", () => {
    const published = acceptNotificationCountPublication(mounted, { workspaceId: "workspace-1", unreadCount: 4 })!;
    const later = issueNotificationCountSnapshot(published.authority, "workspace-1");

    expect(isNotificationCountSnapshotCurrent(later, published.authority)).toBe(true);
  });

  it("ignores a publication from another workspace or an unusable count", () => {
    expect(acceptNotificationCountPublication(mounted, { workspaceId: "workspace-2", unreadCount: 0 })).toBeNull();
    expect(acceptNotificationCountPublication(mounted, { workspaceId: null, unreadCount: 0 })).toBeNull();
    expect(acceptNotificationCountPublication(mounted, { workspaceId: "workspace-1", unreadCount: Number.NaN })).toBeNull();
    expect(acceptNotificationCountPublication(unsignedAuthority, { workspaceId: "workspace-1", unreadCount: 0 })).toBeNull();
  });

  it("rebinds the authority when the mounted workspace changes", () => {
    const other = beginNotificationCountWorkspace(mounted, "workspace-2");
    const firstWorkspaceSnapshot = issueNotificationCountSnapshot(mounted, "workspace-1");

    expect(other.version).toBe(mounted.version + 1);
    expect(isNotificationCountSnapshotCurrent(firstWorkspaceSnapshot, other)).toBe(false);
    // Rebinding the same workspace does not retire its own in-flight polls.
    expect(beginNotificationCountWorkspace(mounted, "workspace-1")).toBe(mounted);
  });

  it("signing out supersedes every poll issued for the signed-in workspace", () => {
    const snapshot = issueNotificationCountSnapshot(mounted, "workspace-1");
    const signedOut = beginNotificationCountWorkspace(mounted, null);

    expect(isNotificationCountSnapshotCurrent(snapshot, signedOut)).toBe(false);
    expect(acceptNotificationCountPublication(signedOut, { workspaceId: "workspace-1", unreadCount: 0 })).toBeNull();
  });
});
