import { beforeEach, describe, expect, it, vi } from "vitest";
import { fence, makeProject, stampFrom, type Row } from "./projectRevisionFenceFixtures";

// Client side of the project revision fence: the session that binds a row's
// published `ETag` to the exact content it was published for, and declares that
// token on every save.
//
// The API has always offered this - it stamps a revision, advertises it as an
// `ETag`, and refuses a `PATCH` whose `If-Match` names a revision that no longer
// holds the row - but the browser never sent `If-Match`, so every save took the
// last-write-wins path: two open tabs, or a member and a collaborator, quietly
// replaced each other and the loser's stale edit landed on top of the newer one.
//
// The subtler failure, and the reason the session holds a PAIR rather than a
// token: a stamp read at the moment of the first keystroke describes the row as
// it is THEN, while the form is showing a copy rendered earlier. Certifying that
// older copy with the newer token re-opens exactly the lost update the fence
// closes. So every binding here compares the server's content with the content on
// screen, and refuses when they differ.
//
// These cases run the real `@/lib/api` transport against the shared contract stub
// in projectRevisionFenceFixtures.ts, which is the same stub the component suite
// drives, so the two can never describe different servers.

function server(rows: Row[], publishEtag = true) {
  const base = fence(rows, 0, publishEtag);
  return { ...base, fetchMock: vi.fn(base.handler) };
}

function row(id = "project-1"): Row {
  return { project: makeProject(id, "Original instructions", "Original notes"), revision: stampFrom(0) };
}

async function loadClient(fetchMock: ReturnType<typeof vi.fn>) {
  vi.stubGlobal("fetch", fetchMock);
  const { api } = await import("./api");
  const { ProjectRevisionSession, isRevisionConflict, isRevisionDiverged, isRevisionUnavailable } = await import("./projectRevision");
  return { api, ProjectRevisionSession, isRevisionConflict, isRevisionDiverged, isRevisionUnavailable };
}

function ifMatchOf(call: [string, RequestInit | undefined] | undefined): string | null {
  if (!call) return null;
  return new Headers(call[1]?.headers).get("If-Match");
}

function patches(store: ReturnType<typeof server>) {
  return store.calls.filter((call) => call.method === "PATCH");
}

/** The copy a page is holding: a snapshot of the row as it was when it rendered. */
function displayed(store: ReturnType<typeof server>, id = "project-1") {
  return { ...store.row(id).project };
}

beforeEach(() => {
  vi.resetModules();
  vi.unstubAllGlobals();
});

describe("project revision fence - two tabs on one account", () => {
  it("declares the revision captured when editing began, not one read at save time", async () => {
    const store = server([row()]);
    const { ProjectRevisionSession } = await loadClient(store.fetchMock);
    // Both pages rendered from the same copy of the row.
    const viewA = displayed(store);
    const viewB = displayed(store);
    const tabA = new ProjectRevisionSession("project-1", "context");
    await tabA.begin(viewA);
    expect(tabA.heldRevision).toBe(stampFrom(0));

    // The other tab commits first, moving the stamp on the row.
    const tabB = new ProjectRevisionSession("project-1", "context");
    await tabB.begin(viewB);
    await tabB.save({ name: "Renamed by tab B" }, viewB);
    expect(store.row("project-1").project.name).toBe("Renamed by tab B");

    // Tab A's page still shows the copy it edited from.
    await tabA.save({ name: "Edited by tab A" }, viewA).catch(() => undefined);
    // The refused write still declared the revision tab A's page was built on.
    const staleDeclaration = ifMatchOf(store.fetchMock.mock.calls.at(-1) as [string, RequestInit]);
    expect(staleDeclaration).toBe(stampFrom(0));
  });

  it("rejects the stale tab, writes none of its draft, and keeps the expired token", async () => {
    const store = server([row()]);
    const { ProjectRevisionSession, isRevisionConflict } = await loadClient(store.fetchMock);
    const viewA = displayed(store);
    const tabA = new ProjectRevisionSession("project-1", "context");
    await tabA.begin(viewA);

    const tabB = new ProjectRevisionSession("project-1", "context");
    await tabB.begin(displayed(store));
    await tabB.save({ name: "Renamed by tab B" }, displayed(store));

    let caught: unknown;
    await tabA.save({ name: "Stale edit from tab A" }, viewA).catch((error) => { caught = error; });
    // A server refusal, not a client-side divergence: tab A's token was honest,
    // the row simply moved while it held.
    expect(isRevisionConflict(caught)).toBe(true);
    expect(store.row("project-1").project.name).toBe("Renamed by tab B");
    // An expired token is never quietly replaced by the live one: adopting a
    // newer revision has to be the member's decision.
    expect(tabA.heldRevision).toBe(stampFrom(0));
    // Exactly two attempts on the row - the winner's and the refused one - and
    // the refused one declared the revision the draft was built on.
    expect(patches(store)).toHaveLength(2);
    expect(patches(store).slice(-1)[0]!.ifMatch).toBe(stampFrom(0));
  });

  it("carries the live revision on the conflict answer, so no extra read is needed", async () => {
    const store = server([row()]);
    const { api, ProjectRevisionSession } = await loadClient(store.fetchMock);
    const viewA = displayed(store);
    const tabA = new ProjectRevisionSession("project-1", "context");
    await tabA.begin(viewA);
    const tabB = new ProjectRevisionSession("project-1", "context");
    await tabB.begin(displayed(store));
    await tabB.save({ name: "Renamed by tab B" }, displayed(store));

    await expect(api.updateProjectWithRevision("project-1", { name: "x" }, stampFrom(0))).rejects.toMatchObject({
      status: 409,
      code: "PROJECT_REVISION_CONFLICT",
      revision: stampFrom(1),
    });
  });

  it("lets the member keep their draft over the newer revision by explicit choice", async () => {
    const store = server([row()]);
    const { ProjectRevisionSession } = await loadClient(store.fetchMock);
    const viewA = displayed(store);
    const tabA = new ProjectRevisionSession("project-1", "context");
    await tabA.begin(viewA);
    const tabB = new ProjectRevisionSession("project-1", "context");
    await tabB.begin(displayed(store));
    await tabB.save({ name: "Renamed by tab B" }, displayed(store));

    await tabA.save({ name: "Stale edit from tab A" }, viewA).catch(() => undefined);
    const kept = await tabA.saveOverLatest({ name: "Stale edit from tab A" });

    expect(store.row("project-1").project.name).toBe("Stale edit from tab A");
    // The override declared the wildcard, not somebody else's token.
    expect(patches(store).slice(-1)[0]!.ifMatch).toBe("*");
    // The session now holds what that write committed, so a follow-up is honest.
    expect(tabA.heldRevision).toBe(kept.revision);
    await tabA.save({ name: "Follow-up edit" }, kept.project);
    expect(store.row("project-1").project.name).toBe("Follow-up edit");
  });

  it("replaces the draft with the newer copy when the member reloads instead", async () => {
    const store = server([row()]);
    const { ProjectRevisionSession } = await loadClient(store.fetchMock);
    const viewA = displayed(store);
    const tabA = new ProjectRevisionSession("project-1", "context");
    await tabA.begin(viewA);
    const tabB = new ProjectRevisionSession("project-1", "context");
    await tabB.begin(displayed(store));
    await tabB.save({ name: "Renamed by tab B", instructions: "New instructions" }, displayed(store));

    await tabA.save({ name: "Stale edit from tab A" }, viewA).catch(() => undefined);
    const latest = await tabA.reloadLatest();

    expect(latest.name).toBe("Renamed by tab B");
    expect(tabA.heldRevision).toBe(stampFrom(1));
    // Rebased cleanly: the token and the re-read content are bound together, so
    // the next save is accepted because it describes what is now on screen.
    await tabA.save({ name: "Edit after reload" }, latest);
    expect(store.row("project-1").project.name).toBe("Edit after reload");
  });
});

describe("project revision fence - a token only ever certifies the copy it was published for", () => {
  it("refuses a page that went stale before the first keystroke", async () => {
    // The exact reported race: the row was read for the overview, the page sat
    // there, another tab saved, and only then did the member start typing. The
    // token available at that moment belongs to the NEW content.
    const store = server([row()]);
    const { ProjectRevisionSession, isRevisionConflict, isRevisionDiverged } = await loadClient(store.fetchMock);
    const view = displayed(store);
    store.commitFromElsewhere("project-1", { name: "Saved by tab B", instructions: "Rewritten by tab B" });

    const tabA = new ProjectRevisionSession("project-1", "context");
    let caught: unknown;
    await tabA.begin(view).catch((error) => { caught = error; });

    expect(isRevisionConflict(caught)).toBe(true);
    expect(isRevisionDiverged(caught)).toBe(true);
    // Nothing was bound, so nothing can be declared, so nothing can overwrite.
    expect(tabA.heldRevision).toBeNull();
    expect(tabA.heldBase).toBeNull();
    expect(patches(store)).toHaveLength(0);
    expect(store.row("project-1").project.name).toBe("Saved by tab B");
  });

  it("refuses a save with no typing at all on a stale page", async () => {
    // Clicking Save without touching a field used to send the displayed copy
    // against a freshly read token, reverting the other tab's edit with no
    // keystroke and no conflict at all.
    const store = server([row()]);
    const { ProjectRevisionSession, isRevisionDiverged } = await loadClient(store.fetchMock);
    const view = displayed(store);
    store.commitFromElsewhere("project-1", { description: "Tab B's synopsis" });

    const tabA = new ProjectRevisionSession("project-1", "details");
    let caught: unknown;
    await tabA.save({ name: view.name, description: view.description ?? undefined, instructions: view.instructions ?? undefined, contextNotes: view.contextNotes ?? undefined, type: view.type }, view)
      .catch((error) => { caught = error; });

    expect(isRevisionDiverged(caught)).toBe(true);
    expect(patches(store)).toHaveLength(0);
    expect(store.row("project-1").project.description).toBe("Tab B's synopsis");
  });

  it("latches the divergence instead of re-reading on every keystroke or retrying the refused write", async () => {
    const store = server([row()]);
    const { ProjectRevisionSession, isRevisionDiverged } = await loadClient(store.fetchMock);
    const view = displayed(store);
    store.commitFromElsewhere("project-1", { instructions: "Rewritten by tab B" });
    const tabA = new ProjectRevisionSession("project-1", "context");

    await tabA.begin(view).catch(() => undefined);
    const readsAfter = store.fetchMock.mock.calls.length;
    // Continued typing must not keep hammering the row looking for a token it
    // cannot honestly use, and a later save must not quietly go through.
    await tabA.begin(view).catch(() => undefined);
    let caught: unknown;
    await tabA.save({ name: "Typed after the divergence" }, view).catch((error) => { caught = error; });

    expect(store.fetchMock.mock.calls).toHaveLength(readsAfter);
    expect(isRevisionDiverged(caught)).toBe(true);
    expect(patches(store)).toHaveLength(0);
  });

  it("re-captures after a failed read, and still refuses if the row moved meanwhile", async () => {
    // A capture that never landed is not a binding: the retry has to compare
    // again, or the first failure would leave a hole through which the same
    // stale page could be stamped as current on the second attempt.
    let failed = false;
    const store = server([row()]);
    const flaky = vi.fn(async (input: string, init?: RequestInit) => {
      if (!failed) { failed = true; throw new TypeError("offline"); }
      return store.handler(input, init);
    });
    const { ProjectRevisionSession, isRevisionDiverged } = await loadClient(flaky);
    const view = displayed(store);
    const tabA = new ProjectRevisionSession("project-1", "context");

    let first: unknown;
    await tabA.begin(view).catch((error) => { first = error; });
    expect(first).toBeInstanceOf(TypeError);
    expect(tabA.heldRevision).toBeNull();

    // The other tab commits while this page still shows the older copy.
    store.commitFromElsewhere("project-1", { name: "Renamed while offline" });

    let caught: unknown;
    await tabA.save({ name: "Saved after recapture" }, view).catch((error) => { caught = error; });
    expect(isRevisionDiverged(caught)).toBe(true);
    expect(patches(store)).toHaveLength(0);
    expect(store.row("project-1").project.name).toBe("Renamed while offline");
  });

  it("re-captures after a failed read and binds when the row has not moved", async () => {
    let failed = false;
    const store = server([row()]);
    const flaky = vi.fn(async (input: string, init?: RequestInit) => {
      if (!failed) { failed = true; throw new TypeError("offline"); }
      return store.handler(input, init);
    });
    const { ProjectRevisionSession } = await loadClient(flaky);
    const view = displayed(store);
    const tabA = new ProjectRevisionSession("project-1", "context");
    await tabA.begin(view).catch(() => undefined);

    const saved = await tabA.save({ name: "Saved after recapture" }, view);
    expect(saved.project.name).toBe("Saved after recapture");
    expect(patches(store)[0]!.ifMatch).toBe(stampFrom(0));
  });

  it("shares one read between concurrent beginnings of the same draft", async () => {
    const store = server([row()]);
    const { ProjectRevisionSession } = await loadClient(store.fetchMock);
    const view = displayed(store);
    const session = new ProjectRevisionSession("project-1", "context");

    await Promise.all([session.begin(view), session.begin(view), session.begin(view)]);
    const reads = store.fetchMock.mock.calls.filter((call) => String(call[0]).includes("/api/projects/"));
    expect(reads).toHaveLength(1);
    expect(session.heldRevision).toBe(stampFrom(0));
  });

  it("treats server-trimmed whitespace as the same content, since that is what the row holds", async () => {
    const store = server([row()]);
    const { ProjectRevisionSession } = await loadClient(store.fetchMock);
    // The page shows what the member typed before saving, which the API stored
    // trimmed; an untrimmed local copy is not a divergence.
    const view = { ...displayed(store), instructions: "  Original instructions  ", contextNotes: "Original notes\n" };
    const session = new ProjectRevisionSession("project-1", "context");

    await session.begin(view);
    expect(session.heldRevision).toBe(stampFrom(0));
  });

  it("keeps each draft's binding to itself, so one form's write cannot certify another's", async () => {
    // ProjectDetailView shows two editable drafts of one row. Each submits the
    // whole row, so a token earned by the details draft must not vouch for the
    // context draft's independently populated fields.
    const store = server([row()]);
    const { ProjectRevisionSession, isRevisionConflict, isRevisionDiverged } = await loadClient(store.fetchMock);
    const view = displayed(store);
    const details = new ProjectRevisionSession("project-1", "details");
    const context = new ProjectRevisionSession("project-1", "context");
    await details.begin(view);
    await context.begin(view);

    const written = await details.save(
      { name: "Renamed in the details form", description: view.description ?? undefined, instructions: view.instructions ?? undefined, contextNotes: view.contextNotes ?? undefined, type: view.type },
      view,
    );
    // The details answer describes a row the context draft has never seen, so
    // folding it in would leave that draft bound to content it does not hold.
    context.adopt(written);
    expect(context.heldRevision).toBeNull();
    expect(context.heldBase).toBeNull();

    // The context draft still holds v1 content. It cannot use the newer token,
    // and it is refused before anything is declared.
    let caught: unknown;
    await context.save(
      { name: view.name, description: view.description ?? undefined, instructions: "Typed into the stale context draft", contextNotes: view.contextNotes ?? undefined, type: view.type },
      view,
    ).catch((error) => { caught = error; });
    expect(isRevisionDiverged(caught)).toBe(true);
    expect(isRevisionConflict(caught)).toBe(true);
    expect(store.row("project-1").project.instructions).toBe("Original instructions");
    expect(store.row("project-1").project.name).toBe("Renamed in the details form");
  });

  it("keeps both drafts savable when they were bound to the same content", async () => {
    const store = server([row()]);
    const { ProjectRevisionSession } = await loadClient(store.fetchMock);
    const view = displayed(store);
    const details = new ProjectRevisionSession("project-1", "details");
    const context = new ProjectRevisionSession("project-1", "context");
    await details.begin(view);
    await context.begin(view);

    await details.save({ name: "Renamed in the details form" }, view);
    // The context draft's own token is now expired by that write, so its save is
    // a conflict the member decides on - not a silent overwrite of the rename.
    const caught = await context.save({ name: view.name, instructions: "Typed in the context draft" }, view).catch((error) => error);
    expect(isStatus(caught, 409)).toBe(true);
    expect(store.row("project-1").project.name).toBe("Renamed in the details form");
    // Recovering through the wildcard applies the context edit on top of it, keeping
    // the other tab's rename because the member chose to merge it into their draft.
    const kept = await context.saveOverLatest({ name: "Renamed in the details form", instructions: "Typed in the context draft" });
    expect(kept.project.name).toBe("Renamed in the details form");
    expect(kept.project.instructions).toBe("Typed in the context draft");
  });
});

describe("project revision fence - status writes", () => {
  it("adopts the revision an archive committed, so a status click is not a conflict", async () => {
    const store = server([row()]);
    const { api, ProjectRevisionSession } = await loadClient(store.fetchMock);
    const view = displayed(store);
    const session = new ProjectRevisionSession("project-1", "context");
    await session.begin(view);

    // The real endpoint publishes the stamp it committed on success, which is
    // what makes this adoption possible; pinned against the backend by
    // StatusWrite_PublishesTheCommittedRevision_AndThatTokenCarriesAnOpenDraftForward.
    const archived = await api.archiveProjectWithRevision("project-1");
    expect(archived.revision).toBe(stampFrom(1));
    session.adopt(archived);
    expect(session.heldRevision).toBe(stampFrom(1));

    // Status changes declare nothing: the API runs them on its LiveRow path,
    // so they can neither raise a conflict nor be preconditions to one.
    const archiveCall = store.fetchMock.mock.calls.find((call) => String(call[0]).endsWith("/archive")) as [string, RequestInit];
    expect(new Headers(archiveCall[1].headers).get("If-Match")).toBeNull();

    // The draft the member was writing stays savable against the moved row.
    const saved = await session.save({ name: "Edited after archiving" }, view);
    expect(saved.project.name).toBe("Edited after archiving");
    expect(saved.project.status).toBe("Archived");

    const restored = await api.restoreProjectWithRevision("project-1");
    session.adopt(restored);
    // The page carries the copy the member last saved, which is what it shows them.
    await session.save({ name: "Edited after restoring" }, saved.project);
    expect(store.row("project-1").project.status).toBe("Active");
  });

  it("refuses to let a status write bless a draft whose row moved out of band", async () => {
    // The unsafe shape the reviewer pointed at: the row changed through a path
    // this draft never saw, and the archive's brand-new stamp would otherwise
    // certify the draft's stale fields as if they were current.
    const store = server([row()]);
    const { api, ProjectRevisionSession, isRevisionDiverged } = await loadClient(store.fetchMock);
    const view = displayed(store);
    const session = new ProjectRevisionSession("project-1", "context");
    await session.begin(view);

    store.commitFromElsewhere("project-1", { instructions: "Rewritten by another tab" });
    const archived = await api.archiveProjectWithRevision("project-1");
    session.adopt(archived);

    // The binding was dropped rather than refreshed onto content this page does
    // not show, so the next save is refused instead of silently overwriting.
    expect(session.heldRevision).toBeNull();
    const caught = await session.save({ name: "Stale draft after archive" }, view).catch((error) => error);
    expect(isRevisionDiverged(caught)).toBe(true);
    expect(patches(store)).toHaveLength(0);
    expect(store.row("project-1").project.instructions).toBe("Rewritten by another tab");
  });

  it("leaves an unbound draft free to adopt a status write, since it certified nothing", async () => {
    const store = server([row()]);
    const { api, ProjectRevisionSession } = await loadClient(store.fetchMock);
    const session = new ProjectRevisionSession("project-1", "context");
    const archived = await api.archiveProjectWithRevision("project-1");
    session.adopt(archived);
    expect(session.heldRevision).toBe(stampFrom(1));
    expect(session.heldBase?.name).toBe("project-1");
  });
});

describe("project revision fence - successful saves and session hygiene", () => {
  it("advances to the revision each successful save committed without a re-read", async () => {
    const store = server([row()]);
    const { ProjectRevisionSession } = await loadClient(store.fetchMock);
    const session = new ProjectRevisionSession("project-1", "context");
    const view = displayed(store);
    await session.begin(view);

    const first = await session.save({ name: "First save" }, view);
    expect(first.revision).toBe(stampFrom(1));
    const second = await session.save({ name: "Second save" }, first.project);
    expect(second.revision).toBe(stampFrom(2));
    expect(session.heldRevision).toBe(stampFrom(2));
    expect(session.heldBase?.name).toBe("Second save");

    // Only the initial capture read the row; each write carried its own answer.
    const reads = store.fetchMock.mock.calls.filter((call) => ((call[1] as RequestInit | undefined)?.method ?? "GET") === "GET" && String(call[0]).includes("/api/projects/"));
    expect(reads).toHaveLength(1);
  });

  it("never issues an unfenced write, even when the capture was skipped", async () => {
    const store = server([row()]);
    const { ProjectRevisionSession } = await loadClient(store.fetchMock);
    const session = new ProjectRevisionSession("project-1", "context");

    await session.save({ name: "Saved without an explicit begin" }, displayed(store));
    expect(patches(store)[0]!.ifMatch).toBe(stampFrom(0));
  });

  it("drops a token the server refused outright and re-captures on the next save", async () => {
    const store = server([row()]);
    const { api, ProjectRevisionSession, isRevisionConflict } = await loadClient(store.fetchMock);

    await expect(api.updateProjectWithRevision("project-1", { name: "x" }, "not-a-revision")).rejects.toMatchObject({ status: 400, code: "INVALID_IF_MATCH" });
    expect(isRevisionConflict(new Error("plain"))).toBe(false);
    expect(isRevisionConflict({ status: 409, code: "PROJECT_REVISION_CONFLICT" })).toBe(true);
    expect(isRevisionConflict({ status: 409, code: "REVISION_DIVERGED" })).toBe(true);

    const session = new ProjectRevisionSession("project-1", "context");
    session.adopt({ project: displayed(store), revision: "not-a-revision" });
    await session.save({ name: "Rejected first" }, displayed(store)).catch(() => undefined);
    // The unusable token was dropped, so the retry captured a real one and won.
    const saved = await session.save({ name: "Recovered" }, displayed(store));
    expect(saved.project.name).toBe("Recovered");
  });

  it("keeps a revision scoped to the project it was captured for", async () => {
    const store = server([
      row("project-1"),
      { project: makeProject("project-2", "Other instructions", "Other notes"), revision: stampFrom(50_000) },
    ]);
    const { ProjectRevisionSession, isRevisionConflict } = await loadClient(store.fetchMock);

    const sessionOne = new ProjectRevisionSession("project-1", "context");
    await sessionOne.begin(displayed(store, "project-1"));
    const sessionTwo = new ProjectRevisionSession("project-2", "context");
    await sessionTwo.begin(displayed(store, "project-2"));
    expect(sessionTwo.heldRevision).not.toBe(sessionOne.heldRevision);

    // A token borrowed across projects names a different row, and the fence
    // treats it as the conflict it is rather than as a save.
    const hijacked = new ProjectRevisionSession("project-2", "context");
    hijacked.adopt({ project: displayed(store, "project-2"), revision: sessionOne.heldRevision });
    let caught: unknown;
    await hijacked.save({ name: "wrong row" }, displayed(store, "project-2")).catch((error) => { caught = error; });
    expect(isRevisionConflict(caught)).toBe(true);
    expect(store.row("project-2").project.name).toBe("project-2");
  });

  it("refuses to write when the read publishes no revision at all", async () => {
    // An intermediate that strips the header must not turn the fence back into a
    // silent last-write-wins path. The save fails with a condition the view can
    // name and localise, and no PATCH is issued - the draft waits.
    const store = server([row()], false);
    const { ProjectRevisionSession, isRevisionConflict, isRevisionUnavailable } = await loadClient(store.fetchMock);
    const view = displayed(store);
    const session = new ProjectRevisionSession("project-1", "context");

    const caught = await session.save({ name: "Edited" }, view).then(() => null).catch((error: unknown) => error);

    expect(isRevisionUnavailable(caught)).toBe(true);
    expect(isRevisionConflict(caught)).toBe(false);
    expect(patches(store)).toHaveLength(0);
    expect(store.row("project-1").project.name).toBe("project-1");
    expect(session.heldRevision).toBeNull();
  });

  it("carries the published token verbatim rather than a Date-rounded copy", async () => {
    const token = '"2026-11-16T09:00:00.1234567Z"';
    const project = { ...makeProject("project-1", "Original instructions", "Original notes"), updatedAt: "2026-11-16T09:00:00.1234567Z" };
    const store = server([{ project, revision: token }]);
    const { ProjectRevisionSession } = await loadClient(store.fetchMock);
    const session = new ProjectRevisionSession("project-1", "context");
    await session.begin(displayed(store));

    // A reconstruction through a JavaScript Date stops at three fractional
    // digits and would be refused outright by the production store.
    expect(session.heldRevision).toBe(token);
    expect(new Date(token.slice(1, -1)).toISOString()).not.toBe(token.slice(1, -1));

    await session.save({ name: "Precision preserved" }, displayed(store));
    expect(patches(store)[0]!.ifMatch).toBe(token);
  });

  it("sends no If-Match from the legacy call, which is why the fence was missing", async () => {
    const store = server([row()]);
    const { api } = await loadClient(store.fetchMock);

    // The unfenced entry point is kept only for callers that have not been
    // migrated; the edit paths in this task never use it, and this assertion
    // documents exactly what the browser used to do on every save.
    await api.updateProject("project-1", { name: "Legacy write" });
    expect(patches(store)[0]!.ifMatch).toBeNull();
  });
});

/** Read a failure structurally, the way the predicates in the module do. */
function isStatus(error: unknown, status: number): boolean {
  return typeof error === "object" && error !== null && (error as { status?: unknown }).status === status;
}
