import { describe, expect, it } from "vitest";
import type { Project } from "./api";
import { collectAllProjectPages, MaxProjectListPages, ProjectListPageSize } from "./projectListPagination";

// Synthetic regression harness for the projects task-list pagination lane.
// The endpoint returns bare pages ordered by UpdatedAt (server side), so a
// surface that renders the complete list has to keep walking pages until one
// comes back short; a single default page silently hides every project past
// the server's page clamp. These cases pin that contract without a live API.

const project = (index: number, overrides: Partial<Project> = {}): Project => ({
  id: `project-${index}`,
  workspaceId: "workspace-1",
  name: `Project ${index}`,
  description: null,
  instructions: null,
  contextNotes: null,
  type: "General",
  status: "Active",
  createdAt: "2026-11-16T00:00:00Z",
  updatedAt: `2026-11-16T00:00:${String(index % 60).padStart(2, "0")}Z`,
  archivedAt: null,
  ...overrides,
});

const pageOf = (start: number, count: number): Project[] =>
  Array.from({ length: count }, (_, offset) => project(start + offset));

describe("collectAllProjectPages", () => {
  it("walks every page instead of silently truncating at one server page", async () => {
    // 145 projects: a full first page, a full second page, a short third.
    const requests: Array<{ page: number; pageSize: number }> = [];
    const pages = new Map<number, Project[]>([
      [1, pageOf(1, ProjectListPageSize)],
      [2, pageOf(1 + ProjectListPageSize, ProjectListPageSize)],
      [3, pageOf(1 + 2 * ProjectListPageSize, 45)],
    ]);
    const collected = await collectAllProjectPages(async (page, pageSize) => {
      requests.push({ page, pageSize });
      return pages.get(page) ?? [];
    });

    expect(requests).toEqual([
      { page: 1, pageSize: ProjectListPageSize },
      { page: 2, pageSize: ProjectListPageSize },
      { page: 3, pageSize: ProjectListPageSize },
    ]);
    expect(collected).toHaveLength(245);
    expect(new Set(collected.map((item) => item.id)).size).toBe(245);
    // The server owns the ordering; the walk must not re-sort.
    expect(collected.map((item) => item.id)).toEqual([
      ...pages.get(1)!.map((item) => item.id),
      ...pages.get(2)!.map((item) => item.id),
      ...pages.get(3)!.map((item) => item.id),
    ]);
  });

  it("stops at a short page even when it is the first page", async () => {
    const requests: Array<{ page: number; pageSize: number }> = [];
    const collected = await collectAllProjectPages(async (page, pageSize) => {
      requests.push({ page, pageSize });
      return pageOf(1, 3);
    });
    expect(requests).toHaveLength(1);
    expect(collected).toHaveLength(3);
  });

  it("returns an empty list for an empty workspace without extra requests", async () => {
    const requests: Array<{ page: number; pageSize: number }> = [];
    const collected = await collectAllProjectPages(async (page, pageSize) => {
      requests.push({ page, pageSize });
      return [];
    });
    expect(requests).toHaveLength(1);
    expect(collected).toEqual([]);
  });

  it("drops duplicates when the keyset shifts under a mid-pagination edit", async () => {
    // Page boundaries are ordered by UpdatedAt with an Id tie-break. Editing
    // a project between two requests can move it across the boundary, so the
    // second page can replay a row the first page already showed.
    const first = pageOf(1, ProjectListPageSize);
    const replayed = first[first.length - 1];
    // A full page whose first row is the replayed project and which therefore
    // carries only 99 new rows, then a short third page that stops the walk.
    const second = [replayed, ...pageOf(1 + ProjectListPageSize, ProjectListPageSize - 1)];
    const third = pageOf(1 + 2 * ProjectListPageSize, 5);
    const requests: number[] = [];
    const collected = await collectAllProjectPages(async (page) => {
      requests.push(page);
      return page === 1 ? first : page === 2 ? second : third;
    });
    // 100 unique from page one + 99 new from page two + the short third
    // page. The replayed row is never published twice.
    expect(collected).toHaveLength(ProjectListPageSize + (ProjectListPageSize - 1) + third.length);
    expect(requests).toEqual([1, 2, 3]);
    expect(collected.map((item) => item.id).filter((id) => id === replayed.id)).toHaveLength(1);
    // The first occurrence keeps its position; the replay is dropped.
    expect(collected[ProjectListPageSize - 1].id).toBe(replayed.id);
  });

  it("caps the walk when a server ignores its own page-size clamp", async () => {
    let calls = 0;
    const collected = await collectAllProjectPages(async () => {
      calls += 1;
      // A misbehaving page that is always "full" must not loop forever.
      return pageOf(calls * 1000, ProjectListPageSize);
    });
    expect(calls).toBe(MaxProjectListPages);
    expect(collected).toHaveLength(MaxProjectListPages * ProjectListPageSize);
  });

  it("propagates transport failures to the caller instead of publishing partial pages", async () => {
    const boom = new Error("network down");
    try {
      await collectAllProjectPages(async () => {
        throw boom;
      });
      expect.unreachable("collect should have rethrown");
    } catch (caught) {
      expect(caught).toBe(boom);
    }
  });
});
