export type NotificationRequestToken = {
  sequence: number;
  workspaceId: string;
};

export function startNotificationRequest(sequence: number, workspaceId: string): NotificationRequestToken {
  return { sequence: sequence + 1, workspaceId };
}

export function isNotificationRequestCurrent(token: NotificationRequestToken, currentSequence: number, activeWorkspaceId: string | null): boolean {
  return token.sequence === currentSequence && token.workspaceId === activeWorkspaceId;
}

/**
 * Freshness fence for the header unread badge.
 *
 * The badge is fed by two independent sources: the periodic workspace-scoped
 * `unread-count` poll, and the counts published locally the moment a read is
 * applied in the popover or in the full notification centre. A poll snapshot is
 * only the newest fact while nothing has been published locally after it was
 * issued - the server answered it from a snapshot taken before that read. So a
 * published count advances the authority and supersedes every poll issued under
 * an earlier version, instead of letting a late reply repaint a count that has
 * already moved on. The authority also rebinds when the mounted workspace
 * changes, because an unread count is a membership-scoped aggregate.
 */
export type NotificationCountAuthority = {
  workspaceId: string | null;
  version: number;
};

export type NotificationCountSnapshot = {
  workspaceId: string;
  version: number;
};

export type NotificationCountPublication = {
  workspaceId: string | null;
  unreadCount: number;
};

export function beginNotificationCountWorkspace(authority: NotificationCountAuthority, workspaceId: string | null): NotificationCountAuthority {
  if (authority.workspaceId === workspaceId) return authority;
  return { workspaceId, version: authority.version + 1 };
}

export function issueNotificationCountSnapshot(authority: NotificationCountAuthority, workspaceId: string): NotificationCountSnapshot {
  return { workspaceId, version: authority.version };
}

export function isNotificationCountSnapshotCurrent(snapshot: NotificationCountSnapshot, authority: NotificationCountAuthority): boolean {
  return snapshot.version === authority.version && snapshot.workspaceId === authority.workspaceId;
}

export function acceptNotificationCountPublication(
  authority: NotificationCountAuthority,
  publication: NotificationCountPublication,
): { authority: NotificationCountAuthority; unreadCount: number } | null {
  if (authority.workspaceId === null) return null;
  if (publication.workspaceId !== authority.workspaceId) return null;
  if (!Number.isFinite(publication.unreadCount)) return null;
  return {
    authority: { workspaceId: authority.workspaceId, version: authority.version + 1 },
    unreadCount: publication.unreadCount,
  };
}
