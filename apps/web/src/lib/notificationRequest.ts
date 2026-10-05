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
