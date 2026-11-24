import { beforeEach, describe, expect, it, vi } from "vitest";
import { fence, makeProject, stampFrom, type Row } from "./projectRevisionFenceFixtures";

// Client side of the project revision fence: the session that captures a row's
// published `ETag` when an edit begins and declares it on every save.
//
// The API has always offered this - it stamps a revision, advertises it as an
// `ETag`, and refuses a `PATCH` whose `If-Match` names a revision that no longer
// holds the row - but the browser never sent `If-Match`, so every save took the
// last-write-wins path: two open tabs, or a member and a collaborator, quietly
// replaced each other and the loser's stale edit landed on top of the newer one.
//
// These cases run the real `@/lib/api` transport against the shared contract
// stub in projectRevisionFenceFixtures.ts, which is the same stub the component
// suite drives, so the two can never describe different servers.

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
  const { ProjectRevisionSession, isRevisionConflict } = await import("./projectRevision");
  return { api, ProjectRevisionSession, isRevisionConflict };
}

function ifMatchOf(call: [string, RequestInit | undefined] | undefined): string | null {
  if (!call) return null;
  return new Headers(call[1]?.headers).get("If-Match");
}

function patches(store: ReturnType<typeof server>) {
  return store.calls.filter((call) => call.method === "PATCH");
}

beforeEach(() => {
  vi.resetModules();
  vi.unstubAllGlobals();
});

describe("project revision fence - two tabs on one account", () => {
  it("declares the revision captured when editing began, not one read at save time", async () => {
    const store = server([row()]);
    const { ProjectRevisionSession } = await loadClient(store.fetchMock);
    const tabA = new ProjectRevisionSession("project-1");

    await tabA.begin();
    expect(tabA.heldRevision).toBe(stampFrom(0));

    // The other tab commits first, moving the stamp on the row.
    const tabB = new ProjectRevisionSession("project-1");
    await tabB.begin();
    await tabB.save({ name: "Renamed by tab B" });
    expect(store.row("project-1").project.name).toBe("Renamed by tab B");

    await tabA.save({ name: "Edited by tab A" }).catch(() => undefined);
    expect(ifMatchOf(store.fetchMock.mock.calls.find((call) => (call[1] as RequestInit | undefined)?.method === "PATCH") as [string, RequestInit])).toBe(stampFrom(0));
  });

  it("rejects the stale tab, writes none of its draft, and keeps the expired token", async () => {
    const store = server([row()]);
    const { ProjectRevisionSession, isRevisionConflict } = await loadClient(store.fetchMock);
    const tabA = new ProjectRevisionSession("project-1");
    await tabA.begin();

    const tabB = new ProjectRevisionSession("project-1");
    await tabB.begin();
    await tabB.save({ name: "Renamed by tab B" });

    let caught: unknown;
    await tabA.save({ name: "Stale edit from tab A" }).catch((error) => { caught = error; });
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
    const tabA = new ProjectRevisionSession("project-1");
    await tabA.begin();
    const tabB = new ProjectRevisionSession("project-1");
    await tabB.begin();
    await tabB.save({ name: "Renamed by tab B" });

    await expect(api.updateProjectWithRevision("project-1", { name: "x" }, stampFrom(0))).rejects.toMatchObject({
      status: 409,
      code: "PROJECT_REVISION_CONFLICT",
      revision: stampFrom(1),
    });
  });

  it("lets the member keep their draft over the newer revision by explicit choice", async () => {
    const store = server([row()]);
    const { ProjectRevisionSession } = await loadClient(store.fetchMock);
    const tabA = new ProjectRevisionSession("project-1");
    await tabA.begin();
    const tabB = new ProjectRevisionSession("project-1");
    await tabB.begin();
    await tabB.save({ name: "Renamed by tab B" });

    await tabA.save({ name: "Stale edit from tab A" }).catch(() => undefined);
    const kept = await tabA.saveOverLatest({ name: "Stale edit from tab A" });

    expect(store.row("project-1").project.name).toBe("Stale edit from tab A");
    // The override declared the wildcard, not somebody else's token.
    expect(patches(store).slice(-1)[0]!.ifMatch).toBe("*");
    // The session now holds what that write committed, so a follow-up is honest.
    expect(tabA.heldRevision).toBe(kept.revision);
    await tabA.save({ name: "Follow-up edit" });
    expect(store.row("project-1").project.name).toBe("Follow-up edit");
  });

  it("replaces the draft with the newer copy when the member reloads instead", async () => {
    const store = server([row()]);
    const { ProjectRevisionSession } = await loadClient(store.fetchMock);
    const tabA = new ProjectRevisionSession("project-1");
    await tabA.begin();
    const tabB = new ProjectRevisionSession("project-1");
    await tabB.begin();
    await tabB.save({ name: "Renamed by tab B", instructions: "New instructions" });

    await tabA.save({ name: "Stale edit from tab A" }).catch(() => undefined);
    const latest = await tabA.reloadLatest();

    expect(latest.name).toBe("Renamed by tab B");
    expect(tabA.heldRevision).toBe(stampFrom(1));
    // Rebased cleanly: the next save is accepted because the token is current.
    await tabA.save({ name: "Edit after reload" });
    expect(store.row("project-1").project.name).toBe("Edit after reload");
  });
});

describe("project revision fence - successful saves and session hygiene", () => {
  it("advances to the revision each successful save committed without a re-read", async () => {
    const store = server([row()]);
    const { ProjectRevisionSession } = await loadClient(store.fetchMock);
    const session = new ProjectRevisionSession("project-1");
    await session.begin();

    const first = await session.save({ name: "First save" });
    expect(first.revision).toBe(stampFrom(1));
    const second = await session.save({ name: "Second save" });
    expect(second.revision).toBe(stampFrom(2));
    expect(session.heldRevision).toBe(stampFrom(2));

    // Only the initial capture read the row; each write carried its own answer.
    const reads = store.fetchMock.mock.calls.filter((call) => ((call[1] as RequestInit | undefined)?.method ?? "GET") === "GET" && String(call[0]).includes("/api/projects/"));
    expect(reads).toHaveLength(1);
  });

  it("never issues an unfenced write, even when the capture was skipped", async () => {
    const store = server([row()]);
    const { ProjectRevisionSession } = await loadClient(store.fetchMock);
    const session = new ProjectRevisionSession("project-1");

    await session.save({ name: "Saved without an explicit begin" });
    expect(patches(store)[0]!.ifMatch).toBe(stampFrom(0));
  });

  it("drops a token the server refused outright and re-captures on the next save", async () => {
    const store = server([row()]);
    const { api, ProjectRevisionSession, isRevisionConflict } = await loadClient(store.fetchMock);

    await expect(api.updateProjectWithRevision("project-1", { name: "x" }, "not-a-revision")).rejects.toMatchObject({ status: 400, code: "INVALID_IF_MATCH" });
    expect(isRevisionConflict(new Error("plain"))).toBe(false);
    expect(isRevisionConflict({ status: 409, code: "PROJECT_REVISION_CONFLICT" })).toBe(true);

    const session = new ProjectRevisionSession("project-1");
    session.adopt("not-a-revision");
    await session.save({ name: "Rejected first" }).catch(() => undefined);
    // The unusable token was dropped, so the retry captured a real one and won.
    const saved = await session.save({ name: "Recovered" });
    expect(saved.project.name).toBe("Recovered");
  });

  it("keeps a revision scoped to the project it was captured for", async () => {
    const store = server([
      row("project-1"),
      { project: makeProject("project-2", "Other instructions", "Other notes"), revision: stampFrom(50_000) },
    ]);
    const { ProjectRevisionSession, isRevisionConflict } = await loadClient(store.fetchMock);

    const sessionOne = new ProjectRevisionSession("project-1");
    await sessionOne.begin();
    const sessionTwo = new ProjectRevisionSession("project-2");
    await sessionTwo.begin();
    expect(sessionTwo.heldRevision).not.toBe(sessionOne.heldRevision);

    // A token borrowed across projects names a different row, and the fence
    // treats it as the conflict it is rather than as a save.
    const hijacked = new ProjectRevisionSession("project-2");
    hijacked.adopt(sessionOne.heldRevision);
    let caught: unknown;
    await hijacked.save({ name: "wrong row" }).catch((error) => { caught = error; });
    expect(isRevisionConflict(caught)).toBe(true);
    expect(store.row("project-2").project.name).toBe("project-2");
  });

  it("adopts the revision an archive or restore committed, so a status click is not a conflict", async () => {
    const store = server([row()]);
    const { api, ProjectRevisionSession } = await loadClient(store.fetchMock);
    const session = new ProjectRevisionSession("project-1");
    await session.begin();

    const archived = await api.archiveProjectWithRevision("project-1");
    session.adopt(archived.revision);
    expect(session.heldRevision).toBe(stampFrom(1));

    // Status changes declare nothing: the API runs them on its LiveRow path,
    // so they can neither raise a conflict nor be preconditions to one.
    const archiveCall = store.fetchMock.mock.calls.find((call) => String(call[0]).endsWith("/archive")) as [string, RequestInit];
    expect(new Headers(archiveCall[1].headers).get("If-Match")).toBeNull();

    // The draft the member was writing stays savable against the moved row.
    const saved = await session.save({ name: "Edited after archiving" });
    expect(saved.project.name).toBe("Edited after archiving");
    expect(saved.project.status).toBe("Archived");

    const restored = await api.restoreProjectWithRevision("project-1");
    session.adopt(restored.revision);
    await session.save({ name: "Edited after restoring" });
    expect(store.row("project-1").project.status).toBe("Active");
  });

  it("refuses to write when the read publishes no revision at all", async () => {
    // An intermediate that strips the header must not turn the fence back into a
    // silent last-write-wins path. The save fails with a condition the view can
    // name and localise, and no PATCH is issued - the draft waits.
    const store = server([row()], false);
    const { ProjectRevisionSession, isRevisionConflict } = await loadClient(store.fetchMock);
    const { isRevisionUnavailable } = await import("./projectRevision");
    const session = new ProjectRevisionSession("project-1");

    const caught = await session.save({ name: "Edited" }).then(() => null).catch((error: unknown) => error);

    expect(isRevisionUnavailable(caught)).toBe(true);
    expect(isRevisionConflict(caught)).toBe(false);
    expect(patches(store)).toHaveLength(0);
    expect(store.row("project-1").project.name).toBe("project-1");
    expect(session.heldRevision).toBeNull();
  });

  it("carries the published token verbatim rather than a Date-rounded copy", async () => {
    const token = '"2026-11-16T09:00:00.1234567Z"';
    const store = server([{ project: makeProject("project-1", "Original instructions", "Original notes"), revision: token }]);
    const { ProjectRevisionSession } = await loadClient(store.fetchMock);
    const session = new ProjectRevisionSession("project-1");
    await session.begin();

    // A reconstruction through a JavaScript Date stops at three fractional
    // digits and would be refused outright by the production store.
    expect(session.heldRevision).toBe(token);
    expect(new Date(token.slice(1, -1)).toISOString()).not.toBe(token.slice(1, -1));

    await session.save({ name: "Precision preserved" });
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
