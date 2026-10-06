import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const workspaceSource = readFileSync(new URL("./MovieTeamWorkspace.tsx", import.meta.url), "utf8");

describe("Movie Team review workflow", () => {
  it("requests project sign-off from a different current team member", () => {
    expect(workspaceSource).toContain("api.requestMovieReview(project.id");
    expect(workspaceSource).toContain('targetType: "project"');
    expect(workspaceSource).toContain("reviewerOptions");
    expect(workspaceSource).toContain("member.userId !== user.id");
    expect(workspaceSource).toContain("reviewIsFinal");
  });

  it("exposes decisions only to the assigned authenticated reviewer", () => {
    expect(workspaceSource).toContain("review.reviewerUserId === user.id");
    expect(workspaceSource).toContain("api.decideMovieReview(project.id, reviewId");
    expect(workspaceSource).toContain('"Approved" | "ChangesRequested" | "Rejected"');
    expect(workspaceSource).toContain('decideReview(review.id, "ChangesRequested")');
    expect(workspaceSource).toContain('decideReview(review.id, "Rejected")');
  });

  it("keeps final-gate authority and server re-authorization visible", () => {
    expect(workspaceSource).toContain('includes("FinalApproval")');
    expect(workspaceSource).toContain("Final gate requests require FinalApproval authority.");
    expect(workspaceSource).toContain("Every action on this page is re-authorized by the API");
  });
});
