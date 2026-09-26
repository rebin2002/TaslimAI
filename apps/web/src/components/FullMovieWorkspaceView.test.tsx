import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const workspaceSource = readFileSync(new URL("./FullMovieWorkspaceView.tsx", import.meta.url), "utf8");
const createSource = readFileSync(new URL("./MovieStudioView.tsx", import.meta.url), "utf8");

describe("Full Movie workspace foundation", () => {
  it("keeps the requested restrained production map in order", () => {
    const labels = ["Overview", "Story", "Cast", "World", "Scenes", "Storyboard", "Production", "Edit", "Audio", "QC", "Exports", "Team"];
    const positions = labels.map((label) => workspaceSource.indexOf(`label: "${label}"`));

    expect(positions.every((position) => position >= 0)).toBe(true);
    expect(positions).toEqual([...positions].sort((a, b) => a - b));
  });

  it("uses durable project routes and marks future surfaces honestly", () => {
    expect(workspaceSource).toContain("/create/movie/${workspace.id}/${item.slug}");
    expect(workspaceSource).toContain("Foundation surface");
    expect(workspaceSource).toContain("No generated footage yet");
    expect(workspaceSource).toContain("Team controls are not connected yet");
  });

  it("uses the bounded overview read model instead of loading the full graph", () => {
    expect(workspaceSource).toContain("api.getMovieOverview(projectId)");
    expect(workspaceSource).toContain("Production path");
    expect(workspaceSource).toContain("Kept separate by source");
    expect(workspaceSource).toContain("No inferred completion");
  });

  it("keeps Story focused on revisions, provenance, and typed screenplay elements", () => {
    expect(workspaceSource).toContain('const storySections: StorySection[] = ["Premise", "Logline", "Synopsis", "Treatment", "Screenplay"]');
    expect(workspaceSource).toContain("api.getMovieStory(projectId)");
    expect(workspaceSource).toContain("MovieStoryRevisionInput");
    expect(workspaceSource).toContain("AiSuggested");
    expect(workspaceSource).toContain("Approved screenplay");
    expect(workspaceSource).toContain("Structured pages");
    expect(workspaceSource).toContain("projectShell");
  });

  it("keeps Quick Movie on a separate compact result path", () => {
    expect(createSource).toContain('mode === "Full"');
    expect(createSource).toContain('router.push(`/create/movie/${result.project.id}/overview`)');
    expect(createSource).toContain("Quick Movie stays intentionally small");
    expect(createSource).toContain("function QuickMovieResult");
  });

  it("keeps Story assistance behind a visible proposal review and apply boundary", () => {
    expect(workspaceSource).toContain("Director assistance");
    expect(workspaceSource).toContain("Create proposal");
    expect(workspaceSource).toContain("Existing content");
    expect(workspaceSource).toContain("Proposed content");
    expect(workspaceSource).toContain("Accept proposal");
    expect(workspaceSource).toContain("Reject");
    expect(workspaceSource).toContain("Apply to editable Story revision");
    expect(workspaceSource).toContain("It never silently rewrites an approved revision.");
  });
});
