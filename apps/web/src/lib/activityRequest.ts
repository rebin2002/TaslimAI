import type { ActivityStatusFilter } from "./activityCenter";

export type ActivityRequestToken = {
  sequence: number;
  workspaceId: string;
  filter: ActivityStatusFilter;
};

export function startActivityRequest(sequence: number, workspaceId: string, filter: ActivityStatusFilter): ActivityRequestToken {
  return { sequence: sequence + 1, workspaceId, filter };
}

export function isActivityRequestCurrent(token: ActivityRequestToken, currentSequence: number, activeWorkspaceId: string | null, activeFilter: ActivityStatusFilter): boolean {
  return token.sequence === currentSequence
    && token.workspaceId === activeWorkspaceId
    && token.filter === activeFilter;
}
