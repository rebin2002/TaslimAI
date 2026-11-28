import { beforeEach, describe, expect, it, vi } from "vitest";
import { makeProject, stampFrom, PROJECT_TOKEN } from "./projectRevisionFenceFixtures";

// Transport-level contract for the fenced project calls. The session suite
// (projectRevision.test.ts) proves the fence is *used*; this one proves the
// header itself survives the round trip:
//
//  * the published token is handed back byte for byte. `Date` only keeps three
//    fractional digits while the store writes seven, so any rebuild of the
//    value would silently produce a token the server can never match.
//  * a read that carries no `ETag` yields `null` rather than an invented token,
//    so the caller can see the fence is unavailable instead of believing it holds.
//  * the revision a mutation committed comes back on the same answer, so an
//    editing session advances without racing a follow-up read.
//  * a refusal carries the revision that actually holds the row on the error,
//    which is what the conflict prompt reads.

function json(body: unknown, status = 200, extra: Record<string, string> = {}) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json", ...extra },
  });
}

async function loadClient(fetchMock: ReturnType<typeof vi.fn>) {
  vi.stubGlobal("fetch", fetchMock);
  const { api, ApiError } = await import("./api");
  return { api, ApiError };
}

beforeEach(() => {
  vi.resetModules();
  vi.unstubAllGlobals();
});

describe("api project revision transport", () => {
  it("hands back the published ETag exactly as the server wrote it", async () => {
    const fetchMock = vi.fn(async () => json(makeProject("project-1", "I", "N"), 200, { ETag: PROJECT_TOKEN }));
    const { api } = await loadClient(fetchMock);

    const answer = await api.getProjectWithRevision("project-1");

    expect(answer.revision).toBe(PROJECT_TOKEN);
    expect(answer.project.id).toBe("project-1");
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit | undefined];
    expect(url).toContain("/api/projects/project-1");
    expect(init?.method ?? "GET").toBe("GET");
  });

  it("reports no revision when the answer carries no ETag instead of inventing one", async () => {
    const fetchMock = vi.fn(async () => json(makeProject("project-1", "I", "N")));
    const { api } = await loadClient(fetchMock);

    const answer = await api.getProjectWithRevision("project-1");

    expect(answer.revision).toBeNull();
  });

  it("declares the held token verbatim and takes the revision the write committed", async () => {
    const committed = stampFrom(7);
    const fetchMock = vi.fn(async (_input: string, init?: RequestInit) => {
      if ((init?.method ?? "GET") === "GET") return json({ token: "csrf-token" });
      return json(makeProject("project-1", "Edited", "N"), 200, { ETag: committed });
    });
    const { api } = await loadClient(fetchMock);

    const answer = await api.updateProjectWithRevision("project-1", { name: "Project 1", instructions: "Edited" }, PROJECT_TOKEN);

    expect(answer.revision).toBe(committed);
    const patch = fetchMock.mock.calls.find((call) => (call[1] as RequestInit | undefined)?.method === "PATCH") as unknown as [string, RequestInit];
    const headers = new Headers(patch[1].headers);
    expect(headers.get("If-Match")).toBe(PROJECT_TOKEN);
    // A mutation still needs the CSRF token the endpoint requires.
    expect(headers.get("X-CSRF-TOKEN")).toBe("csrf-token");
    expect(JSON.parse(String(patch[1].body))).toMatchObject({ instructions: "Edited" });
  });

  it("sends the wildcard a member chose, so the API applies over the live row", async () => {
    const fetchMock = vi.fn(async (_input: string, init?: RequestInit) => {
      if ((init?.method ?? "GET") === "GET") return json({ token: "csrf-token" });
      return json(makeProject("project-1", "Kept mine", "N"), 200, { ETag: stampFrom(9) });
    });
    const { api } = await loadClient(fetchMock);

    await api.updateProjectWithRevision("project-1", { name: "Project 1", instructions: "Kept mine" }, "*");

    const patch = fetchMock.mock.calls.find((call) => (call[1] as RequestInit | undefined)?.method === "PATCH") as [string, RequestInit];
    expect(new Headers(patch[1].headers).get("If-Match")).toBe("*");
  });

  it("carries the live revision on a refusal, which is what the conflict prompt needs", async () => {
    const live = stampFrom(4);
    const fetchMock = vi.fn(async (_input: string, init?: RequestInit) => {
      if ((init?.method ?? "GET") === "GET") return json({ token: "csrf-token" });
      return json({ error: { code: "PROJECT_REVISION_CONFLICT", message: "This project was changed by someone else." } }, 409, { ETag: live });
    });
    const { api } = await loadClient(fetchMock);

    const caught = await api
      .updateProjectWithRevision("project-1", { name: "Project 1", instructions: "Stale draft" }, PROJECT_TOKEN)
      .then(() => null)
      .catch((error: unknown) => error);

    expect(caught).toBeInstanceOf(Error);
    const failure = caught as { status: number; code?: string; revision?: string | null };
    expect(failure.status).toBe(409);
    expect(failure.code).toBe("PROJECT_REVISION_CONFLICT");
    expect(failure.revision).toBe(live);
  });

  it("publishes archive and restore revisions without declaring one", async () => {
    const archived = stampFrom(3);
    const fetchMock = vi.fn(async (_input: string, init?: RequestInit) => {
      if ((init?.method ?? "GET") === "GET") return json({ token: "csrf-token" });
      return json(makeProject("project-1", "I", "N", { status: "Archived", archivedAt: archived.slice(1, -1) }), 200, { ETag: archived });
    });
    const { api } = await loadClient(fetchMock);

    const answer = await api.archiveProjectWithRevision("project-1");

    expect(answer.revision).toBe(archived);
    const post = fetchMock.mock.calls.find((call) => (call[1] as RequestInit | undefined)?.method === "POST") as [string, RequestInit];
    expect(post[0]).toContain("/api/projects/project-1/archive");
    // The endpoint is documented as last-write-wins: sending a token here would
    // turn a status click into a conflict the member cannot act on.
    expect(new Headers(post[1].headers).get("If-Match")).toBeNull();
  });

  it("keeps the revision from the answer that actually finished after a CSRF retry", async () => {
    const committed = stampFrom(12);
    let patches = 0;
    const fetchMock = vi.fn(async (_input: string, init?: RequestInit) => {
      const method = (init?.method ?? "GET").toUpperCase();
      if (method === "GET") return json({ token: "csrf-token" });
      patches += 1;
      if (patches === 1) return json({ error: { code: "CSRF_VALIDATION_FAILED", message: "Invalid CSRF token." } }, 400);
      return json(makeProject("project-1", "I", "N"), 200, { ETag: committed });
    });
    const { api } = await loadClient(fetchMock);

    const answer = await api.updateProjectWithRevision("project-1", { name: "Project 1" }, PROJECT_TOKEN);

    expect(patches).toBe(2);
    expect(answer.revision).toBe(committed);
    // The retry re-declares the same token; it never quietly becomes a wildcard.
    const second = fetchMock.mock.calls.filter((call) => (call[1] as RequestInit | undefined)?.method === "PATCH")[1] as unknown as [string, RequestInit];
    expect(new Headers(second[1].headers).get("If-Match")).toBe(PROJECT_TOKEN);
  });
});
