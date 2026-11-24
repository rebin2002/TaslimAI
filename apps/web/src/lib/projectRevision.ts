import { api, type Project, type ProjectInput } from "./api";

/**
 * Client side of the project revision fence.
 *
 * The API publishes a project row's revision as an `ETag` and refuses a
 * `PATCH` whose `If-Match` names a revision that no longer holds the row
 * (`409 PROJECT_REVISION_CONFLICT`, nothing written). Before this module the
 * browser never sent `If-Match` at all, so two open tabs silently resolved into
 * the server's last-write-wins path and an older edit could overwrite a newer
 * one that the user had already seen rejected.
 *
 * A session therefore owns exactly one thing: the revision token of one project,
 * captured at the moment an edit of that project begins. It is never rebuilt
 * from `project.updatedAt`, because that body field is a display value while the
 * header is the persisted `timestamp with time zone` stamp at microsecond
 * precision, and it is never refreshed at save time, because a token fetched
 * after another writer committed would certify a stale draft as current.
 *
 * Archive and restore are deliberately outside the fence: the API runs them on
 * its `RevisionFence.LiveRow` path, which applies over whatever the row holds,
 * so a status change can never raise a conflict and no precondition is declared
 * for it here. Their answers still carry the revision they committed, and
 * `adopt` folds that into an open session, which keeps a member's own archive
 * click from manufacturing a conflict against their own unsaved edit.
 */

/** The conflict code the fence returns when the declared revision has expired. */
export const REVISION_CONFLICT_CODE = "PROJECT_REVISION_CONFLICT";
/** The header value that means "apply over whatever the row holds" (RFC 9110). */
export const REVISION_WILDCARD = "*";

/**
 * The status/code pair the fence returns, read structurally rather than by
 * `instanceof ApiError`: the failure may arrive through a mocked transport in
 * a component test, and an identity check would then misread a real conflict
 * as an ordinary save error and lose the conflict handling with it.
 */
type CodedFailure = { status?: unknown; code?: unknown };
function codedAs(error: unknown, status: number, code: string): boolean {
  if (typeof error !== "object" || error === null) return false;
  const failure = error as CodedFailure;
  return failure.status === status && failure.code === code;
}

/** True when a failure is the fence rejecting a declared revision. */
export function isRevisionConflict(error: unknown): boolean {
  return codedAs(error, 409, REVISION_CONFLICT_CODE);
}

/**
 * True when the server refused the token itself rather than the revision it
 * names: the value was never an `ETag` this API published for that row, so
 * holding on to it can only repeat the rejection.
 */
export function isUnusableRevision(error: unknown): boolean {
  return codedAs(error, 400, "INVALID_IF_MATCH");
}

/**
 * The read that should have captured a revision answered without one. Declaring
 * an invented token is impossible and an unfenced write would defeat the fence,
 * so the save refuses and the view explains it in the member's language.
 */
export class RevisionUnavailableError extends Error {
  constructor() {
    super("revision-unavailable");
    this.name = "RevisionUnavailableError";
  }
}

/** Read structurally, like the other predicates, so mocked transports match. */
export function isRevisionUnavailable(error: unknown): boolean {
  return error instanceof Error && (error.name === "RevisionUnavailableError" || error.message === "revision-unavailable");
}

export type RevisionSaveResult = { project: Project; revision: string | null };

export class ProjectRevisionSession {
  private revision: string | null = null;
  private pendingCapture: Promise<Project> | null = null;

  constructor(readonly projectId: string) {}

  /** The token that will be declared on the next save, if any is held. */
  get heldRevision(): string | null { return this.revision; }

  /**
   * Captures the live revision for the row this session edits. Called when the
   * member begins editing, and returns the server's copy so the caller can
   * rebase what is on screen. Concurrent beginnings share one read.
   */
  begin(): Promise<Project> {
    if (this.pendingCapture) return this.pendingCapture;
    this.pendingCapture = (async () => {
      const { project, revision } = await api.getProjectWithRevision(this.projectId);
      this.revision = revision;
      return project;
    })().finally(() => { this.pendingCapture = null; });
    return this.pendingCapture;
  }

  /** Folds in a revision the server published for a write this client made. */
  adopt(revision: string | null): void {
    if (revision) this.revision = revision;
  }

  /** Drops the token; the next save re-captures before it declares anything. */
  clear(): void { this.revision = null; }

  /**
   * Saves the draft against the revision it was built on. A session that never
   * captured successfully still declares a revision rather than sending an
   * unfenced write, so the fence is never bypassed by a client bug; that
   * capture happens before the write, not as a retry of a rejected one.
   */
  save(input: ProjectInput): Promise<RevisionSaveResult> {
    return this.declared().then(async (ifMatch) => {
      try {
        const saved = await api.updateProjectWithRevision(this.projectId, input, ifMatch);
        this.adopt(saved.revision);
        return saved;
      } catch (error) {
        if (isUnusableRevision(error)) this.clear();
        // A conflict leaves the held revision exactly as it was: an expired
        // token is never quietly replaced by the live one, because saving with
        // a newer revision has to be the member's decision, not the client's.
        throw error;
      }
    });
  }

  /**
   * Explicit user decision to apply this draft over whatever the row now holds.
   * It sends the wildcard, not a newer token: the draft is kept, the newer
   * revision is discarded knowingly, and the answer's revision becomes current.
   */
  async saveOverLatest(input: ProjectInput): Promise<RevisionSaveResult> {
    const saved = await api.updateProjectWithRevision(this.projectId, input, REVISION_WILDCARD);
    this.adopt(saved.revision);
    return saved;
  }

  /**
   * Explicit user decision to abandon the draft and continue from the server's
   * copy. Returns the newer project; the caller replaces what is on screen.
   */
  async reloadLatest(): Promise<Project> { return this.begin(); }

  private declared(): Promise<string> {
    if (this.revision) return Promise.resolve(this.revision);
    return this.begin().then(() => {
      if (!this.revision) throw new RevisionUnavailableError();
      return this.revision;
    });
  }
}
