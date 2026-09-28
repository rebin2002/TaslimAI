import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const workspaceSource = readFileSync(new URL("./FullMovieWorkspaceView.tsx", import.meta.url), "utf8");
const shotDesignerSource = readFileSync(new URL("./ShotDesigner.tsx", import.meta.url), "utf8");
const createSource = readFileSync(new URL("./MovieStudioView.tsx", import.meta.url), "utf8");
const directorSource = readFileSync(new URL("./MovieDirectorPanel.tsx", import.meta.url), "utf8");

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

  it("treats Cast as an operational room with durable reads, shared assets, states, and visible locks", () => {
    expect(workspaceSource).toContain("getMovieCast(projectId)");
    expect(workspaceSource).toContain("getMovieCharacterDetail");
    expect(workspaceSource).toContain("Shared Asset library");
    expect(workspaceSource).toContain("Character states");
    expect(workspaceSource).toContain("approved and protected");
    expect(workspaceSource).toContain("No reference asset");
  });

  it("keeps Story focused on revisions, provenance, and typed screenplay elements", () => {
    expect(workspaceSource).toContain('const storySections: StorySection[] = ["Premise", "Logline", "Synopsis", "Treatment", "Screenplay"]');
    expect(workspaceSource).toContain("api.getMovieStory(projectId)");
    expect(workspaceSource).toContain("MovieStoryRevisionInput");
    expect(workspaceSource).toContain("AiSuggested");
    expect(workspaceSource).toContain("Approved screenplay");
    expect(workspaceSource).toContain("Structured pages");
    expect(workspaceSource).toContain("projectShell");
    expect(workspaceSource).toContain("movie-workspace-layout ${activeModule === \"story\" ? \"is-story-layout\" : \"\"}");
    expect(workspaceSource).toContain("Current manuscript section");
    expect(workspaceSource).toContain("storyAuthorshipLabel");
    expect(workspaceSource).toContain("storyAuthorshipDescription");
  });

  it("keeps the active Story section spacious, accessible, and visible after Director application", () => {
    expect(workspaceSource).toContain("sectionForDirectorApply");
    expect(workspaceSource).toContain("setFocusAfterDirectorApply(changedSection)");
    expect(workspaceSource).toContain("story-editor-${focusAfterDirectorApply.toLowerCase()}");
    expect(workspaceSource).toContain('aria-label="Story sections"');
    expect(workspaceSource).toContain('aria-controls={`story-editor-${item.toLowerCase()}`}');
    expect(workspaceSource).toContain('id={`story-editor-${key}`}');
    expect(workspaceSource).not.toContain('if (!auto) setSection("Screenplay")');
  });

  it("makes Scenes the explicit screenplay-to-production bridge", () => {
    expect(workspaceSource).toContain("api.getMovieScenesWorkspace(projectId)");
    expect(workspaceSource).toContain("api.breakDownMovieScreenplay(projectId)");
    expect(workspaceSource).toContain("Screenplay → production scenes → shots");
    expect(workspaceSource).toContain("Acts, sequences, scenes");
    expect(workspaceSource).toContain("Existing scenes and shots are never silently replaced");
    expect(workspaceSource).toContain("api.reorderMovieEntity(\"scenes\"");
  });

  it("keeps shot planning scene-scoped, explainable, and non-generating", () => {
    expect(workspaceSource).toContain("api.getMovieSceneShotPlan(scene.id)");
    expect(workspaceSource).toContain("api.addMovieShot(scene.id, input)");
    expect(workspaceSource).toContain("api.reorderMovieShot(shotId, sequence)");
    expect(workspaceSource).toContain("api.archiveMovieShot(shotId)");
    expect(workspaceSource).toContain("creating a shot never starts generation");
    expect(workspaceSource).toContain("shot.readiness.summary");
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
  it("keeps Story Director proposals on the canonical MovieProject and explains the guide prerequisite", () => {
    expect(workspaceSource).toContain("createMovieDirectorProposal(projectId");
    expect(workspaceSource).toContain("guideLocked={projectShell?.lockedGuideRevisionNumber != null}");
    expect(workspaceSource).toContain("Lock the Movie Guide before creating a Story Director proposal.");
    expect(workspaceSource).toContain("Open Cast to lock it");
    expect(workspaceSource).toContain("disabled={storyBusy || !guideLocked}");
  });
  it("mounts Shot Designer from the scene inspector without mutating the guide", () => {
    expect(workspaceSource).toContain("<ShotDesigner scene={scene} guide={guide} presets={presets}");
    expect(workspaceSource).toContain("api.addMovieShot(sceneId");
    expect(shotDesignerSource).toContain("A shot override never rewrites the Movie Guide");
    expect(shotDesignerSource).toContain("Guide stays locked; this saves as a shot-level override");
    expect(shotDesignerSource).toContain("Native");
    expect(shotDesignerSource).toContain("Translated");
    expect(shotDesignerSource).toContain("Simulated/Post");
    expect(shotDesignerSource).toContain("Unsupported");
  });

  it("organizes storyboard work by scene and shot without inventing artwork", () => {
    expect(workspaceSource).toContain("api.getMovieStoryboard(projectId)");
    expect(workspaceSource).toContain("Storyboard candidates");
    expect(workspaceSource).toContain("Create candidate from Shot Plan");
    expect(workspaceSource).toContain("No storyboard has been generated for this shot.");
    expect(workspaceSource).toContain("No Asset attached");
    expect(workspaceSource).toContain("Request revision");
    expect(workspaceSource).toContain("Storyboard composition approved.");
  });

  it("keeps production actions explicit and preserves the candidate/take distinction", () => {
    expect(workspaceSource).toContain("Approve keyframe");
    expect(workspaceSource).toContain("Create motion preview");
    expect(workspaceSource).toContain("Start production render");
    expect(workspaceSource).toContain("Create MovieTake");
    expect(workspaceSource).toContain("artifact candidates stay separate from rendered takes");
    expect(workspaceSource).toContain("A MovieTake appears only after a real render has produced a private Asset.");
  });
  it("surfaces existing generation, resilience, and QC signals instead of inventing footage", () => {
    expect(workspaceSource).toContain("retryCount");
    expect(workspaceSource).toContain("qualityControlStatus");
    expect(workspaceSource).toContain("Failure / resilience");
    expect(workspaceSource).toContain("No generated output");
  });

  it("keeps one Director contextual across rooms and targets real shots", () => {
    expect(workspaceSource).toContain("<MovieDirectorPanel");
    expect(workspaceSource).toContain("sceneShotCount");
    expect(workspaceSource).toContain("selectedShot={selectedShot}");
    expect(workspaceSource).toContain("api.addMovieShot");
    expect(directorSource).toContain("Review → explicit approval → execute");
  });

  it("keeps loading and failure states useful without fabricating project content", () => {
    expect(workspaceSource).toContain("WorkspaceSkeleton");
    expect(workspaceSource).toContain('aria-busy="true"');
    expect(workspaceSource).toContain("Try again");
    expect(workspaceSource).toContain('role="alert"');
    expect(workspaceSource).toContain('aria-label={item.label}');
    expect(workspaceSource).toContain('data-module-state={isFuture ? "foundation" : "operational"}');
  });

});
