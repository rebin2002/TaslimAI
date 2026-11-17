export type HomeDashboardRequestToken = {
  sequence: number;
  workspaceId: string;
};

/**
 * The home dashboard fans a single load out into four workspace-scoped reads
 * (projects, conversations, activity and assets) and publishes the combined
 * result. Every load therefore takes a token, and only the token that is still
 * the newest one for the workspace that is actually mounted may publish: a
 * workspace the user has left, or a load retired by unmounting, can neither
 * replace the current dashboard nor report a failure against it. The sequence
 * makes the decision by request identity rather than by which response happens
 * to arrive last, so an out-of-order reply is rejected too.
 */
export function startHomeDashboardRequest(sequence: number, workspaceId: string): HomeDashboardRequestToken {
  return { sequence: sequence + 1, workspaceId };
}

export function isHomeDashboardRequestCurrent(token: HomeDashboardRequestToken, currentSequence: number, activeWorkspaceId: string | null): boolean {
  return token.sequence === currentSequence && token.workspaceId === activeWorkspaceId;
}
