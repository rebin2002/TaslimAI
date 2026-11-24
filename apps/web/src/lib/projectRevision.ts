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
 * A session holds a PAIR: the revision token and the exact draft base - the
 * editable fields - that the token certifies. That pairing is the whole point.
 * A token read from the server at the moment a keystroke happens describes the
 * row as it is NOW, not the copy the member is looking at; binding that token to
 * a draft built from an older page would let the stale draft overwrite whatever
 * arrived in between, which is the very lost update the fence exists to prevent.
 * So a token is only ever held together with the content it was published for,
 * and it is only captured after the server's copy has been compared with what
 * the member is being shown.
 *
 * The token is carried verbatim. The published format is
 * `yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'` on a 100-nanosecond grid while a
 * JavaScript `Date` keeps three fractional digits, so any value rebuilt from a
 * timestamp would be a token the store can never match.
 */

/** The conflict code the fence returns when the declared revision has expired. */
export const REVISION_CONFLICT_CODE = "PROJECT_REVISION_CONFLICT";
/** Client-detected equivalent: the row already differs from what is displayed. */
export const REVISION_DIVERGED_CODE = "REVISION_DIVERGED";
/** The header value that means "apply over whatever the row holds" (RFC 9110). */
export const REVISION_WILDCARD = "*";

/**
 * The fields an edit draft can carry, in the shape the server publishes them.
 * `status`/`archivedAt` are excluded on purpose: they change through archive and
 * restore, which are documented as unconditional, and a status flip must not be
 * mistaken for a content divergence of an open draft.
 */
export type ProjectDraftBase = {
  name: string;
  description: string | null;
  instructions: string | null;
  contextNotes: string | null;
  type: string;
};

const blank = (value: string | null | undefined): string | null => {
  const text = typeof value === "string" ? value.trim() : "";
  return text === "" ? null : text;
};

export function draftBaseOf(project: Project): ProjectDraftBase {
  return {
    name: project.name,
    description: blank(project.description),
    instructions: blank(project.instructions),
    contextNotes: blank(project.contextNotes),
    type: project.type,
  };
}

/** True when two published copies carry the same editable content. */
export function sameDraftBase(left: ProjectDraftBase, right: ProjectDraftBase): boolean {
  return left.name === right.name
    && blank(left.description) === blank(right.description)
    && blank(left.instructions) === blank(right.instructions)
    && blank(left.contextNotes) === blank(right.contextNotes)
    && left.type === right.type;
}

/**
 * The status/code pair the fence returns, read structurally rather than by
 * `instanceof ApiError`: the failure may arrive through a mocked transport in a
 * component test, and an identity check would then misread a real conflict as an
 * ordinary save error and lose the conflict handling with it.
 */
type CodedFailure = { status?: unknown; code?: unknown };
function codedAs(error: unknown, status: number, ...codes: string[]): boolean {
  if (typeof error !== "object" || error === null) return false;
  const failure = error as CodedFailure;
  return failure.status === status && codes.includes(String(failure.code));
}

/**
 * True when the member must decide which copy wins - either because the server
 * refused the declared revision, or because this client found the row already
 * differs from the copy being edited and therefore refuses to certify the draft
 * at all. Both are answered by the same prompt: the draft stays on screen,
 * nothing was written, and the choices are reload or overwrite deliberately.
 */
export function isRevisionConflict(error: unknown): boolean {
  return codedAs(error, 409, REVISION_CONFLICT_CODE, REVISION_DIVERGED_CODE);
}

/** True for the client-detected case specifically, which has different copy. */
export function isRevisionDiverged(error: unknown): boolean {
  return codedAs(error, 409, REVISION_DIVERGED_CODE);
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
  readonly status = 400;
  readonly code = "REVISION_UNAVAILABLE";
  constructor() {
    super("revision-unavailable");
    this.name = "RevisionUnavailableError";
  }
}

/** Read structurally, like the other predicates, so mocked transports match. */
export function isRevisionUnavailable(error: unknown): boolean {
  return error instanceof Error && (error.name === "RevisionUnavailableError" || error.message === "revision-unavailable");
}

/**
 * The server's copy no longer matches the copy the member is editing, so no
 * revision this client holds can certify their draft. Attaching the live stamp
 * gives the caller the option to reload it; it is never attached to the draft.
 */
export class RevisionDivergedError extends Error {
  readonly status = 409;
  readonly code = REVISION_DIVERGED_CODE;
  readonly liveRevision: string | null;
  constructor(liveRevision: string | null) {
    super("revision-diverged");
    this.name = "RevisionDivergedError";
    this.liveRevision = liveRevision;
  }
}

export type RevisionSaveResult = { project: Project; revision: string | null };

export class ProjectRevisionSession {
  private revision: string | null = null;
  private diverged: RevisionDivergedError | null = null;
  private base: ProjectDraftBase | null = null;
  private pendingCapture: Promise<Project> | null = null;
  // A divergence is a decision point, not a transient error: it stays raised
  // until the member resolves it, so continued typing neither re-reads the row on
  // every keystroke nor quietly retries a write this client refused.

  /**
   * One session per EDITABLE DRAFT, not one per project. Two forms on the same
   * page - project details and project context - each submit the whole row, so a
   * token earned by one draft's write would otherwise certify the other draft's
   * independently populated fields as if they had been read together.
   */
  constructor(readonly projectId: string, readonly purpose: string = "draft") {}

  /** The token that will be declared on the next save, if any is held. */
  get heldRevision(): string | null { return this.revision; }

  /** The content the held token certifies, or null when nothing is bound. */
  get heldBase(): ProjectDraftBase | null { return this.base; }

  /** True once this draft has a token that was published for its own base. */
  get isBound(): boolean { return this.revision !== null && this.base !== null; }

  /**
   * Binds the revision of the row that still holds `displayed`'s content.
   *
   * Called when the member begins editing, and again at save time if nothing was
   * bound. The read is what makes the binding honest: the server's copy is
   * compared with the copy on screen, and only a match lets this draft claim the
   * published token. A mismatch raises the conflict without touching the row, so
   * an edit typed into a page that is already out of date can never be stamped as
   * current. Concurrent beginnings share one read.
   *
   * Once bound, the pair is kept until it is spent, refused as unusable, or
   * replaced by a write this session made. The check belongs to the moment the
   * token is TAKEN, not to every save afterwards: a draft that reloaded the row,
   * or saved through a parent that has not yet re-rendered its copy, is still
   * editing exactly the content its token was published for. Re-comparing a stale
   * prop would refuse honest saves, and it would add nothing - a token that the
   * row has moved past is caught by the server's own fence on the next write.
   */
  begin(displayed: Project): Promise<Project> {
    const displayedBase = draftBaseOf(displayed);
    if (this.diverged) return Promise.reject(this.diverged);
    if (this.isBound) return Promise.resolve(displayed);
    if (this.pendingCapture) {
      // A capture started for another keystroke is only reusable by a draft that
      // is showing the same content it was compared against.
      return this.pendingCapture.then((project) => {
        if (this.base && !sameDraftBase(this.base, displayedBase)) {
          this.diverged ??= new RevisionDivergedError(this.revision);
          throw this.diverged;
        }
        return project;
      });
    }
    this.pendingCapture = (async () => {
      const { project, revision } = await api.getProjectWithRevision(this.projectId);
      if (!revision) throw new RevisionUnavailableError();
      const serverBase = draftBaseOf(project);
      if (!sameDraftBase(serverBase, displayedBase)) {
        // The row moved since this page was rendered. The token just read
        // certifies the NEW content, not the draft in front of the member, so it
        // is not bound: the caller shows the conflict and the member picks.
        this.diverged = new RevisionDivergedError(revision);
        throw this.diverged;
      }
      this.revision = revision;
      this.base = serverBase;
      return project;
    })().finally(() => { this.pendingCapture = null; });
    return this.pendingCapture;
  }

  /** Drops the binding; the next save re-captures before it declares anything. */
  clear(): void {
    this.revision = null;
    this.base = null;
    this.diverged = null;
  }

  /**
   * Folds in the answer to a write made OUTSIDE this session - an archive or a
   * restore, which change the row's status and therefore its revision, or a save
   * whose result another draft on the same page learned about.
   *
   * Adoption is conditional, and that is the whole point of the pair. A status
   * write answers with the row it changed: adopting its stamp is safe only while
   * the row's editable content still matches what this draft was built from. When
   * it does not, the row moved through a change this draft never saw, and blessing
   * that stale draft with a brand-new token is precisely the lost update the fence
   * exists to prevent - so the binding is dropped and the next save re-verifies
   * (and refuses) instead of writing.
   *
   * A write this session made itself is NOT adopted this way: its answer carries
   * the content this draft just submitted, which differs from the base by design.
   * See bindOwnWrite below.
   */
  adopt(answer: RevisionSaveResult): void {
    if (!answer.revision) return;
    const published = draftBaseOf(answer.project);
    if (this.base && !sameDraftBase(this.base, published)) {
      // The row carries content this draft never saw: refuse to bless it.
      this.clear();
      return;
    }
    this.diverged = null;
    this.revision = answer.revision;
    this.base = published;
  }

  /**
   * Binds the answer to this session's own fenced write. The row now holds the
   * content the draft submitted, so the published stamp certifies it directly and
   * editing continues without another read. An answer with no token (a stripped
   * header) leaves nothing to bind, so the binding is dropped and the next save
   * re-captures rather than declaring a stale value.
   */
  private bindOwnWrite(answer: RevisionSaveResult): void {
    if (!answer.revision) {
      this.clear();
      return;
    }
    this.diverged = null;
    this.revision = answer.revision;
    this.base = draftBaseOf(answer.project);
  }

  /**
   * Saves the draft against the revision that certifies it. A draft that never
   * captured successfully binds at this point - against the content on screen,
   * never a stamp taken from a row that has since moved.
   */
  async save(input: ProjectInput, displayed: Project): Promise<RevisionSaveResult> {
    await this.begin(displayed);
    const ifMatch = this.revision;
    if (!ifMatch || !this.base) throw new RevisionUnavailableError();
    try {
      const saved = await api.updateProjectWithRevision(this.projectId, input, ifMatch);
      this.bindOwnWrite(saved);
      return saved;
    } catch (error) {
      if (isUnusableRevision(error)) this.clear();
      // A conflict leaves the binding exactly as it was: an expired token is
      // never quietly replaced by the live one, because saving against a newer
      // revision has to be the member's decision, not the client's.
      throw error;
    }
  }

  /**
   * Explicit user decision to apply this draft over whatever the row now holds.
   * It sends the wildcard, not a newer token: the draft is kept, the newer
   * revision is discarded knowingly, and the answer's revision becomes current.
   */
  async saveOverLatest(input: ProjectInput): Promise<RevisionSaveResult> {
    const saved = await api.updateProjectWithRevision(this.projectId, input, REVISION_WILDCARD);
    this.bindOwnWrite(saved);
    return saved;
  }

  /**
   * Explicit user decision to abandon the draft and continue from the server's
   * copy. Binds the re-read content and its stamp together, so the next save is
   * fenced on what is now on screen.
   */
  async reloadLatest(): Promise<Project> {
    this.clear();
    const { project, revision } = await api.getProjectWithRevision(this.projectId);
    if (!revision) throw new RevisionUnavailableError();
    this.revision = revision;
    this.base = draftBaseOf(project);
    return project;
  }
}
