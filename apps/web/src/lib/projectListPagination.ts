import type { Project } from "./api";

// The projects endpoint is the canonical paginated task-list surface: it
// returns bare pages ordered by UpdatedAt descending with an Id tie-break,
// clamps pageSize to the server maximum, and never reports a total. A single
// default page therefore silently hides every project past the server's
// default page size, so callers that render the whole list must keep walking
// pages until one comes back short.
export const ProjectListPageSize = 100;
export const MaxProjectListPages = 50;

export type ProjectListPageFetcher = (page: number, pageSize: number) => Promise<Project[]>;

/**
 * Concatenates every page of a workspace's project list. Iteration stops at
 * the first short page. Overlapping ids are dropped: the keyset can shift
 * between requests when a project is edited or archived mid-pagination, and a
 * duplicate row would render two cards for one project. The page count is
 * capped so a server that ignores its own page-size clamp cannot drive an
 * unbounded request loop.
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
  return collected;
}
