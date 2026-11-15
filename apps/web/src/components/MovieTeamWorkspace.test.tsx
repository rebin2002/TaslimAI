import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const workspaceSource = readFileSync(new URL("./MovieTeamWorkspace.tsx", import.meta.url), "utf8");

describe("Movie Team review workflow", () => {
  it("fails closed and remounts review permissions across identity/project changes", () => {
    expect(workspaceSource).toContain("project.workspaceId || !project.id");
    expect(workspaceSource).toContain("if (loading || !user?.id || !project.workspaceId");
    expect(workspaceSource).toContain("key={movieTeamIdentityKey(user.id, project.workspaceId, project.id)}");
    expect(workspaceSource).toContain("function IdentityScopedMovieTeamWorkspace");
    // Invited collaborators may review projects outside their personal workspace.
    expect(workspaceSource).not.toContain("project.workspaceId !== workspace.id");
  });

  it("invalidates stale permission snapshots across review writes and remounts", () => {
    expect(workspaceSource).toContain("const loadRevision = useRef(0)");
    expect(workspaceSource).toContain("const mounted = useRef(false)");
    expect(workspaceSource).toContain("const revision = ++loadRevision.current");
    expect(workspaceSource).toContain("mounted.current && revision === loadRevision.current");
    expect(workspaceSource).toContain("return () => {\n      mounted.current = false;");
    // Four writes (comment, resolve, request, decide) plus unmount all
    // invalidate earlier authorization reads before a late response can render.
    expect(workspaceSource.split("loadRevision.current += 1").length - 1).toBe(5);
    expect(workspaceSource).not.toContain("setCollaboration(await api.getMovieCollaboration(project.id))");
  });

  it("requests project sign-off from a different current team member", () => {
    expect(workspaceSource).toContain("api.requestMovieReview(project.id");
    expect(workspaceSource).toContain('targetType: "project"');
    expect(workspaceSource).toContain("reviewerOptions");
    expect(workspaceSource).toContain("member.userId !== user.id");
    expect(workspaceSource).toContain("reviewIsFinal");
    // A revoked FinalApproval capability must clear a previously selected final gate
    // from both the UI and the API payload, even before the server reauthorizes it.
    expect(workspaceSource).toContain("isFinal: reviewIsFinal && canRequestFinalReview");
    expect(workspaceSource).toContain("checked={reviewIsFinal && canRequestFinalReview}");
    expect(workspaceSource).toContain('if (!next.currentUserPermissions.includes("FinalApproval")) setReviewIsFinal(false);');
    expect(workspaceSource).toContain("setCollaboration(null);\\n        setReviewIsFinal(false);");
  });

  it("exposes decisions only to the assigned authenticated reviewer", () => {
    expect(workspaceSource).toContain("review.reviewerUserId === user.id");
    expect(workspaceSource).toContain("api.decideMovieReview(project.id, reviewId");
    expect(workspaceSource).toContain('"Approved" | "ChangesRequested" | "Rejected"');
    expect(workspaceSource).toContain('decideReview(review.id, "ChangesRequested")');
    expect(workspaceSource).toContain('decideReview(review.id, "Rejected")');
  });

  it("captures a bounded per-review decision note and sends it to the existing authorized API", () => {
    const apiSource = readFileSync(new URL("../lib/api.ts", import.meta.url), "utf8");
    expect(workspaceSource).toContain('const [decisionNotes, setDecisionNotes] = useState<Record<string, string>>({});');
    expect(workspaceSource).toContain('value={decisionNotes[review.id] ?? ""}');
    expect(workspaceSource).toContain("maxLength={4000}");
    expect(workspaceSource).toContain("onChange={(event) => setDecisionNotes");
    expect(workspaceSource).toContain("decisionNote: decisionNotes[reviewId]?.trim() || null");
    expect(workspaceSource).toContain("delete next[reviewId]");
    expect(workspaceSource).toContain("review.decisionNote");
    expect(apiSource).toContain("decisionNote?: string | null");
    expect(apiSource).toContain("/decision");
  });

  it("keeps final-gate authority and server re-authorization visible", () => {
    expect(workspaceSource).toContain('includes("FinalApproval")');
    expect(workspaceSource).toContain("Final gate requests require FinalApproval authority.");
    expect(workspaceSource).toContain("Every action on this page is re-authorized by the API");
  });
});
