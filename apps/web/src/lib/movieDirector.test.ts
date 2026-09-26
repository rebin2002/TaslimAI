import { describe, expect, it } from "vitest";
import { directorActionTypeLabel, directorHistoryLabel, directorProposalStatusLabel, directorRoomForModule } from "./movieDirector";

describe("movie Director surface contracts", () => {
  it("maps Full Movie rooms to one contextual Director", () => {
    expect(directorRoomForModule("story")).toBe("Story");
    expect(directorRoomForModule("cast")).toBe("Cast");
    expect(directorRoomForModule("world")).toBe("World");
    expect(directorRoomForModule("scenes")).toBe("Scene");
    expect(directorRoomForModule("storyboard")).toBe("Storyboard");
    expect(directorRoomForModule("production")).toBe("Production");
  });

  it("keeps proposal and action language typed for review", () => {
    expect(directorActionTypeLabel("generate_shot")).toBe("Generate shot");
    expect(directorProposalStatusLabel("PendingApproval")).toBe("Review needed");
    expect(directorProposalStatusLabel("Approved")).toBe("Approved · ready to run");
  });

  it("uses safe, user-facing history labels", () => {
    expect(directorHistoryLabel("proposal_created")).toBe("Proposal created");
    expect(directorHistoryLabel("action_succeeded")).toBe("Action completed");
    expect(directorHistoryLabel("action_failed")).toBe("Action needs attention");
  });
});
