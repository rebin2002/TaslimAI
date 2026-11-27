import type { Project } from "./api";

// The projects endpoint is the canonical paginated task-list surface: it
// returns bare pages ordered by UpdatedAt descending with an Id tie-break,
// clamps pageSize to the server maximum, and never reports a total. A single
// default page therefore silently hides every project past the server's
// default page size, so callers that render the whole list must keep walking
// pages until one comes back short.
export const ProjectListPageSize = 100;
export const MaxProjectListPages = 50;

// Render-time isolation prevents an old response from exposing a previous
// workspace or tab's cards before effect cancellation has run.
export function scopedProjectList(projects: readonly Project[], workspaceId: string | undefined, status: "Active" | "Archived"): Project[] {
  if (!workspaceId) return [];
  return projects.filter(project => project.workspaceId === workspaceId && project.status === status);
}

export type ProjectListPageFetcher = (page: number, pageSize: number) => Promise<Project[]>;

/**
 * Concatenates every page of a workspace's project list. Iteration stops at
 * the first short page. Overlapping ids are dropped: the keyset can shift
 * between requests when a project is edited or archived mid-pagination, and a
 * duplicate row would render two cards for one project. The page count is
 * capped so a server that ignores its own page-size clamp cannot drive an
 * unbounded request loop. Reaching the cap without a short page is an error,
 * not proof that all projects were retrieved: never publish partial counts.
 */
export async function collectAllProjectPages(fetchPage: ProjectListPageFetcher): Promise<Project[]> {
  const pageSize = ProjectListPageSize;
  const collected: Project[] = [];
  const seen = new Set<string>();
  for (let page = 1; page <= MaxProjectListPages; page += 1) {
    const items = await fetchPage(page, pageSize);
    for (const project of items) {
      if (seen.has(project.id)) continue;
      seen.add(project.id);
      collected.push(project);
    }
    if (items.length < pageSize) return collected;
  }
  // Every bounded page was full. The server may contain more projects;
  // silently returning the prefix would advertise an inaccurate total.
  throw new Error("Project list pagination limit reached before the final page.");
}
