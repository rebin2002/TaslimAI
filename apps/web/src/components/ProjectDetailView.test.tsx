import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

/**
 * Pins the fail-closed boundary of the workspace member role badge on the
 * project detail page. The badge, the project copy and the membership lists
 * are all rendered from one cached overview snapshot. A definitive access
 * answer (401 session ended, 403 membership revoked, 404 the server no
 * longer resolves the project for this principal) must invalidate that
 * snapshot, or a downgraded or removed member keeps a live role claim for a
 * workspace they can no longer open. A transient failure (5xx, 429, network)
 * must keep the snapshot so the badge does not flicker away - the same
 * contract the notification unread badge follows.
 *
 * The decision itself is exercised by src/lib/projectDetailAccessState.test.ts;
 * these checks pin that every project-detail failure path delegates to it.
 */
const detailViewSource = readFileSync(new URL("./ProjectDetailView.tsx", import.meta.url), "utf8");

describe("project detail workspace role badge revocation boundary", () => {
  it("routes every failure path through the snapshot disposition", () => {
    expect(detailViewSource).toContain('resolveProjectDetailSnapshotAfterFailure(caught) === "invalidate"');
    // The overview load plus the two membership-scoped mutations the page
    // owns: archive and restore. Anything less leaves one path pinning the
    // stale role after a revocation.
    const callCount = detailViewSource.match(/resolveProjectDetailSnapshotAfterFailure\(caught\)/g)?.length ?? 0;
    expect(callCount).toBe(3);
  });

  it("drops the membership snapshot, role badge included, on a definitive answer", () => {
    // The badge renders only from `overview.workspace`; clearing the
    // snapshot is what takes it off screen and exposes the retry alert.
    expect(detailViewSource).toMatch(
      /resolveProjectDetailSnapshotAfterFailure\(caught\) === "invalidate"\) setOverview\(null\);/,
    );
    expect(detailViewSource).toContain('if (error && !overview) return <div className="inline-error" role="alert">');
  });

  it("never clears the snapshot on a transient failure from the error path", () => {
    // Transient catches must only set the error banner; the disposition
    // guard is what separates them from the definitive ones.
    const errorSets = detailViewSource.match(/setOverview\(null\);/g)?.length ?? 0;
    expect(errorSets).toBe(3);
    expect(detailViewSource).not.toMatch(/catch[^\n]*\{[^\n]*setOverview\(null\)[^"\n]*invalidate/);
  });

  it("renders the role badge from the live overview snapshot only", () => {
    expect(detailViewSource).toContain("const { project, workspace, counts, conversations, recentActivity } = overview;");
    expect(detailViewSource).toContain('const roleKey = `projects.role.${workspace.role.toLowerCase()}`;');
    // The badge must not fall back to a client-cached role once the
    // snapshot is gone.
    expect(detailViewSource).not.toMatch(/className="workspace-role"[^>]*\{[^}]*sessionRole/);
  });
});
