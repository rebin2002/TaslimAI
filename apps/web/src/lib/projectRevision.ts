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
 * and it is only captured after the server's copy has been compared with what the
 * member is being shown.
 *
 * "What the member is being shown" is the copy the draft's own fields were SEEDED
 * from, reported once through {@link ProjectRevisionSession.noteDraft} - not the
 * component's live `project` prop. That prop is replaced by every other writer's
 * answer: a sibling form's save, or a status write on the same page, updates the
 * parent's copy while a mounted textarea still displays the old one. Comparing the
 * row against the newer prop would then certify the older fields, which is the same
 * lost update arriving by another route. A draft that has never reported its seed
 * holds no token at all, and no one else's write can give it one.
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

/**
 * Anything carrying the editable fields: a full row as published, or the base a
 * draft was seeded from. A component may only ever hand this module the copy its
 * OWN LOCAL FIELDS were initialized from - never the row its parent happens to be
 * re-rendering with - because those two diverge the moment another writer lands,
 * and comparing against the newer one is what let a stale draft be certified.
 */
export type ProjectSnapshot = Project | ProjectDraftBase;

export function draftBaseOf(snapshot: ProjectSnapshot): ProjectDraftBase {
  return {
    name: snapshot.name,
    description: blank(snapshot.description),
    instructions: blank(snapshot.instructions),
    contextNotes: blank(snapshot.contextNotes),
    type: snapshot.type,
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
  private draft: ProjectDraftBase | null = null;
  private pendingCapture: { promise: Promise<Project | null>; draft: ProjectDraftBase; epoch: number } | null = null;
  // Bumped whenever the copy on screen or the binding changes. A read that was
  // already in flight when it moves can no longer certify the draft: it describes
  // the row as it was BEFORE the change, so it must not commit afterwards.
  private epoch = 0;
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

  /** The content the member is actually looking at, as the draft reported it. */
  get heldDraft(): ProjectDraftBase | null { return this.draft; }

  /** True once this draft has a token that was published for its own base. */
  get isBound(): boolean { return this.revision !== null && this.base !== null; }

  /**
   * Records the copy this draft's local fields were seeded from.
   *
   * Called when the draft is created and again whenever it is genuinely re-based
   * (a different project, an explicit reload). The snapshot is the draft's own,
   * captured once from the row it was built on, and is deliberately NOT the
   * component's live `project` prop: that prop is replaced by every other writer's
   * answer, and a prop the member never typed against cannot certify their text.
   *
   * Reporting different content invalidates everything derived from the old copy -
   * the token, the verdict, any read still running - because none of it describes
   * what is on screen now.
   */
  noteDraft(snapshot: ProjectSnapshot | null): void {
    const next = snapshot ? draftBaseOf(snapshot) : null;
    const rowId = snapshot ? (snapshot as { id?: unknown }).id : undefined;
    if (typeof rowId === "string" && rowId !== this.projectId) {
      // A different row's content is not this draft's seed: forgetting the draft
      // makes the next save refuse instead of declaring a foreign revision.
      this.draft = null;
      this.epoch += 1;
      this.revision = null;
      this.base = null;
      this.diverged = null;
      return;
    }
    const unchanged = next === null
      ? this.draft === null
      : this.draft !== null && sameDraftBase(this.draft, next);
    if (unchanged) return;
    this.draft = next;
    this.epoch += 1;
    this.revision = null;
    this.base = null;
    this.diverged = null;
  }

  /**
   * Binds the revision of the row that still holds the content on screen.
   *
   * Called when the member begins editing, and again at save time if nothing was
   * bound, always against the draft's own seeded copy. The read is what makes the
   * binding honest: the server's content is compared with that copy, and only a
   * match lets this draft claim the published token. A mismatch raises the conflict
   * without touching the row, so an edit typed into a page that is already out of
   * date can never be stamped as current. Concurrent beginnings share one read.
   *
   * Once bound, the pair is kept until it is spent, refused as unusable, or
   * replaced by a write this session made, or dropped because the copy on screen
   * changed. The check belongs to the moment the token is TAKEN, not to every save
   * afterwards: re-comparing at save time would need a live prop, which is the
   * value this module refuses to trust, and a token the row has moved past is
   * caught by the server's own fence on the next write anyway.
   *
   * Takes no snapshot argument on purpose. The content it compares against is the
   * seed this draft reported when its fields were initialized, and accepting a
   * copy here would put the caller's newest `project` prop back in the driver's
   * seat, which is exactly how a stale draft gets certified.
   *
   * Resolves with the row that was read, or null when no read was needed.
   */
  begin(): Promise<Project | null> {
    if (this.diverged) return Promise.reject(this.diverged);
    if (this.isBound) return Promise.resolve(null);
    const draft = this.draft;
    if (!draft) return Promise.reject(new RevisionUnavailableError());
    if (this.pendingCapture) {
      // A capture started for another keystroke is only reusable while this draft
      // is still showing the same content it was compared against.
      if (sameDraftBase(this.pendingCapture.draft, draft)) return this.pendingCapture.promise;
      return this.pendingCapture.promise.then(() => this.begin(), () => this.begin());
    }
    const epoch = this.epoch;
    const promise = (async (): Promise<Project | null> => {
      const { project, revision } = await api.getProjectWithRevision(this.projectId);
      if (!revision) throw new RevisionUnavailableError();
      if (epoch !== this.epoch) {
        // The draft was re-based, or a write landed and was folded in, while this
        // read was in flight: the answer is about a superseded moment. Trust the
        // newer state if it certified the draft, and otherwise treat the row as
        // having moved rather than binding a token from a stale read.
        if (this.isBound) return project;
        this.diverged ??= new RevisionDivergedError(revision);
        throw this.diverged;
      }
      const serverBase = draftBaseOf(project);
      if (!sameDraftBase(serverBase, draft)) {
        // The row moved since this draft was built. The token just read certifies
        // the NEW content, not what the member is looking at, so it is not bound:
        // the caller shows the conflict and the member picks.
        this.diverged = new RevisionDivergedError(revision);
        throw this.diverged;
      }
      this.revision = revision;
      this.base = serverBase;
      return project;
    })().finally(() => { if (this.pendingCapture?.promise === promise) this.pendingCapture = null; });
    this.pendingCapture = { promise, draft, epoch };
    return promise;
  }

  /** Drops the binding; the next save re-captures before it declares anything. */
  clear(): void {
    this.revision = null;
    this.base = null;
    this.diverged = null;
    this.epoch += 1;
  }

  /** Forgets the binding AND the copy on screen: the draft is being torn down. */
  reset(): void {
    this.clear();
    this.draft = null;
    this.pendingCapture = null;
  }

  /**
   * Folds in the answer to a write made OUTSIDE this session - an archive or a
   * restore, which change the row's status and therefore its revision, or a save
   * whose result another draft on the same page learned about.
   *
   * Adoption is verified against the copy the member is looking at, which is the
   * whole point of the pair. A status write answers with the row it changed: its
   * stamp is safe to hold only while that row's editable content still matches
   * this draft's seeded base. Then the draft really does describe the row, only at
   * a newer revision, and adopting keeps it savable. When it does not match, the row
   * moved through content this draft never saw - so the token is dropped and the
   * divergence is latched at once instead of waiting for a keystroke, and the next
   * save refuses rather than blessing a stale draft with a token it never earned.
   * That holds for an UNBOUND session too: a draft whose content has never been
   * compared with the row has nothing for someone else's stamp to certify.
   *
   * A write this session made itself is NOT adopted this way: its answer carries
   * the content this draft just submitted, which differs from the base by design.
   * See bindOwnWrite below.
   */
  adopt(answer: RevisionSaveResult): void {
    if (!answer.revision) return;
    if (answer.project.id !== this.projectId) return;
    const published = draftBaseOf(answer.project);
    const draft = this.draft;
    if (!draft) {
      // Nothing has ever been reported as on screen for this draft, so there is no
      // token to refresh and no licence to invent one. Leave it unbound: the first
      // capture compares the row with the draft and conflicts if they differ.
      return;
    }
    this.epoch += 1;
    if (!sameDraftBase(published, draft)) {
      this.revision = null;
      this.base = null;
      this.diverged = new RevisionDivergedError(answer.revision);
      return;
    }
    this.diverged = null;
    this.revision = answer.revision;
    this.base = published;
  }

  /**
   * Binds the answer to this session's own fenced write. The row now holds the
   * content the draft submitted, so the published stamp certifies it directly and
   * editing continues without another read.
   *
   * The baseline moves on EVERY successful write, including the one case that must
   * stay fail-closed: an answer whose `ETag` was stripped in transit. There the
   * TOKEN is withheld - nothing is declared, and the next save has to re-capture
   * before it may write - but the content the member is looking at really is the
   * content the row took, because this session put it there. Keeping the older
   * baseline while the caller's form holds the saved copy would make the next
   * capture compare the row against a draft that no longer exists anywhere, and
   * report a divergence that never happened.
   */
  private bindOwnWrite(answer: RevisionSaveResult): void {
    const committed = draftBaseOf(answer.project);
    // Invalidation first: a read that was still running describes the row before
    // this write and must not commit over it.
    this.epoch += 1;
    this.draft = committed;
    this.diverged = null;
    if (!answer.revision) {
      // Fail closed on the declaration only: no token, so the next save re-captures
      // (and refuses outright if the row still publishes nothing).
      this.revision = null;
      this.base = null;
      return;
    }
    this.revision = answer.revision;
    this.base = committed;
  }

  /**
   * Saves the draft against the revision that certifies it. A draft that never
   * captured successfully binds at this point - against the content the draft was
   * built on, never a stamp taken from a row that has since moved.
   */
  async save(input: ProjectInput): Promise<RevisionSaveResult> {
    await this.begin();
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
    // The caller re-seeds its local fields from this answer, so the copy on screen
    // and the certified content become the same row.
    this.draft = this.base;
    this.epoch += 1;
    return project;
  }
}
