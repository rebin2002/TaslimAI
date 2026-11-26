/**
 * Decides what a failed project-detail request does to the cached
 * membership-scoped snapshot: the project copy, the conversation and
 * activity lists, and the workspace member role badge rendered from the
 * overview response.
 *
 * A network blip or a server-side hiccup (5xx, 429) is transient: the
 * snapshot stays on screen so the role badge does not flicker away, the
 * same contract the notification unread badge already follows. A
 * definitive 401, 403 or 404 is not transient. 401 says the session
 * ended; 403 is the overview read saying the workspace membership was
 * revoked; 404 is the mutation/read path saying the server can no
 * longer resolve this project for this principal at all (a removed
 * member sees PROJECT_NOT_FOUND from the archive and restore routes,
 * which fold "no membership" and "gone" into one answer). In every
 * definitive case the displayed role is no longer what the server
 * believes: the badge must fail closed with the snapshot instead of
 * pinning a membership-scoped role - an "Owner" claim, for example -
 * for a principal who can no longer open the page.
 */
export type ProjectDetailSnapshotDisposition = "keep" | "invalidate";

export function resolveProjectDetailSnapshotAfterFailure(error: unknown): ProjectDetailSnapshotDisposition {
  const status = error instanceof Object && "status" in error ? (error as { status: unknown }).status : undefined;
  if (status === 401 || status === 403 || status === 404) return "invalidate";
  return "keep";
}
