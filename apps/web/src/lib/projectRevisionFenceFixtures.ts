import type { Project } from "./api";

/**
 * Fixtures shared by the client-side revision fence suites: the shape of a
 * project row, the token format the API publishes, and a stub of the fenced
 * endpoint. Kept as a non-test module so the component suite and the lib suite
 * describe the same contract rather than two approximations of it.
 */

/** A token as the production store writes it: seven fractional digits. */
export const PROJECT_TOKEN = '"2026-11-16T09:00:00.0000001Z"';

export function makeProject(id: string, instructions: string, contextNotes: string, overrides: Partial<Project> = {}): Project {
  return {
    id,
    workspaceId: "workspace-1",
    name: id,
    description: null,
    instructions,
    contextNotes,
    type: "General",
    status: "Active",
    createdAt: "2026-11-16T00:00:00.0000000Z",
    updatedAt: "2026-11-16T00:00:00.0000000Z",
    archivedAt: null,
    ...overrides,
  };
}

/** Advances on the same 100-nanosecond grid the production store publishes. */
export function stampFrom(ticks: number): string {
  const base = Date.UTC(2026, 10, 16, 9, 0, 0);
  const millis = Math.floor(ticks / 10_000);
  const clock = new Date(base + millis).toISOString().replace("Z", "").slice(0, 19);
  return `"${clock}.${String(millis % 1000).padStart(3, "0")}${String(ticks % 10_000).padStart(4, "0")}Z"`;
}

export type Row = { project: Project; revision: string };

export type FenceCall = { method: string; path: string; ifMatch: string | null; body: unknown };

/** Reads back the 100-nanosecond tick encoded in a published token. */
export function tickOf(token: string): number {
  const match = /^"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.(\d{3})(\d{4})Z"$/.exec(token);
  if (!match) throw new Error(`not a published revision token: ${token}`);
  return Number(match[1]) * 10_000 + Number(match[2]);
}

/**
 * @param publishEtag set false to model an intermediate that strips the header: the
 * row still carries a revision, but no client can learn it.
 */
export function fence(rows: Row[], startTicks = 0, publishEtag = true) {
  const byId = new Map(rows.map((row) => [row.project.id, { ...row }]));
  // Writes must move forward from whatever revision the rows already carry, the
  // way a store's clock does, so a seeded token can never certify its own row.
  let ticks = Math.max(startTicks, ...rows.map((row) => tickOf(row.revision)));
  const calls: FenceCall[] = [];

  const handler = async (input: string, init?: RequestInit): Promise<Response> => {
    const url = new URL(input);
    const method = (init?.method ?? "GET").toUpperCase();
    const headers = new Headers(init?.headers);
    const json = (body: unknown, status = 200, extra: Record<string, string> = {}) =>
      new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json", ...extra } });

    if (url.pathname === "/api/auth/csrf") return json({ token: "synthetic-csrf" });

    const statusMatch = url.pathname.match(/^\/api\/projects\/([^/]+)\/(archive|restore)$/);
    const match = url.pathname.match(/^\/api\/projects\/([^/]+)$/);
    if (!match && !statusMatch) return json({ error: { code: "NOT_FOUND", message: "No such route." } }, 404);
    const id = (match ?? statusMatch)![1];
    const row = byId.get(id);
    if (!row) return json({ error: { code: "PROJECT_NOT_FOUND", message: "Project not found." } }, 404);

    if (method === "GET") return json(row.project, 200, publishEtag ? { ETag: row.revision } : {});

    if (statusMatch && method === "POST") {
      // Documented scope: archive and restore run against the live row. They do
      // not inspect If-Match, but they still advance the revision monotonically,
      // so a client that adopts the published stamp is never stranded.
      const archive = statusMatch[2] === "archive";
      const next = stampFrom(++ticks);
      const updated: Row = {
        revision: next,
        project: {
          ...row.project,
          status: archive ? "Archived" : "Active",
          archivedAt: archive ? next.slice(1, -1) : null,
          updatedAt: next.slice(1, -1),
        },
      };
      byId.set(id, updated);
      calls.push({ method, path: url.pathname, ifMatch: headers.get("If-Match"), body: undefined });
      return json(updated.project, 200, { ETag: next });
    }

    const ifMatch = headers.get("If-Match");
    calls.push({ method, path: url.pathname, ifMatch, body: init?.body ? JSON.parse(String(init.body)) : undefined });

    if (ifMatch !== null && ifMatch !== "*" && !/^"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z"$/.test(ifMatch)) {
      // A value that is not a revision this endpoint could have published is a
      // client defect: the API answers it with 400 INVALID_IF_MATCH, not 409.
      return json(
        { error: { code: "INVALID_IF_MATCH", message: "If-Match must be a revision token from this project's ETag header, or *." } },
        400,
        { ETag: row.revision },
      );
    }

    if (ifMatch !== null && ifMatch !== "*" && ifMatch !== row.revision) {
      // The fence refuses: nothing about the draft is applied, and the answer
      // names the revision that actually holds the row.
      return json(
        { error: { code: "PROJECT_REVISION_CONFLICT", message: "This project was changed by someone else." } },
        409,
        { ETag: row.revision },
      );
    }

    const next = stampFrom(++ticks);
    const body = JSON.parse(String(init?.body ?? "{}")) as Partial<Project>;
    const updated: Row = {
      revision: next,
      project: {
        ...row.project,
        ...body,
        description: body.description ?? null,
        instructions: body.instructions ?? null,
        contextNotes: body.contextNotes ?? null,
        updatedAt: next.slice(1, -1),
      },
    };
    byId.set(row.project.id, updated);
    return json(updated.project, 200, { ETag: next });
  };

  return {
    handler,
    calls,
    row: (id: string) => byId.get(id)!,
    /** Another writer commits out of band: exactly what a second tab does. */
    commitFromElsewhere: (id: string, changes: Partial<Project>) => {
      const row = byId.get(id)!;
      const next = stampFrom(++ticks);
      byId.set(id, { revision: next, project: { ...row.project, ...changes, updatedAt: next.slice(1, -1) } });
      return next;
    },
  };
}
