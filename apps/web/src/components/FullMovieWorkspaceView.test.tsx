import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const workspaceSource = readFileSync(new URL("./FullMovieWorkspaceView.tsx", import.meta.url), "utf8");
const shotDesignerSource = readFileSync(new URL("./ShotDesigner.tsx", import.meta.url), "utf8");
const shotDesignerI18nSource = readFileSync(new URL("../lib/shotDesignerI18n.ts", import.meta.url), "utf8");
const createSource = readFileSync(new URL("./MovieStudioView.tsx", import.meta.url), "utf8");
const directorSource = readFileSync(new URL("./MovieDirectorPanel.tsx", import.meta.url), "utf8");
const selectsSource = readFileSync(new URL("./MovieSelectsWorkspace.tsx", import.meta.url), "utf8");
const salvageSource = readFileSync(new URL("../lib/movieSalvage.ts", import.meta.url), "utf8");
const i18nSource = readFileSync(new URL("../lib/i18n.ts", import.meta.url), "utf8");

describe("Full Movie workspace foundation", () => {
  it("keeps the requested restrained production map in order", () => {
    // Visible labels are resolved from i18n, so the module map is asserted on
    // the stable slugs and the copy is asserted where it now lives.
    const slugs = ["overview", "story", "cast", "world", "scenes", "storyboard", "production", "edit", "audio", "qc", "exports", "team"];
    const positions = slugs.map((slug) => workspaceSource.indexOf(`slug: "${slug}"`));

    expect(positions.every((position) => position >= 0)).toBe(true);
    expect(positions).toEqual([...positions].sort((a, b) => a - b));
  });

  it("makes Production Kit a durable reference-first workspace route", () => {
    expect(workspaceSource).toContain('slug: "production-kit"');
    expect(workspaceSource).toContain("<ProductionKitWorkspace projectId={fullProject.id} />");
    expect(workspaceSource).toContain("movieModule.${item.slug}.label");
    expect(i18nSource).toContain('"movieModule.production-kit.eyebrow": "Reference control"');
  });

  it("uses durable project routes and exposes operational delivery surfaces", () => {
    expect(workspaceSource).toContain("/create/movie/${workspace.id}/${item.slug}");
    expect(workspaceSource).toContain("No generated footage yet");
    expect(workspaceSource).toContain("<MovieAudioWorkspace project={fullProject} />");
    expect(workspaceSource).toContain("<MovieExportsWorkspace project={fullProject} />");
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
    expect(workspaceSource).toContain('movieDeep.storySections');
    expect(workspaceSource).toContain('aria-controls={`story-editor-${item.toLowerCase()}`}');
    expect(workspaceSource).toContain('id={`story-editor-${key}`}');
    expect(workspaceSource).not.toContain('if (!auto) setSection("Screenplay")');
  });

  it("makes Scenes the explicit screenplay-to-production bridge", () => {
    expect(workspaceSource).toContain("api.getMovieScenesWorkspace(projectId)");
    expect(workspaceSource).toContain("api.breakDownMovieScreenplay(projectId)");
    expect(workspaceSource).toContain('movieDeep.sceneReview');
    expect(workspaceSource).toContain("Acts, sequences, scenes");
    expect(workspaceSource).toContain("Existing scenes and shots are never silently replaced");
    expect(workspaceSource).toContain("api.reorderMovieEntity(\"scenes\"");
  });

  it("keeps the Scenes empty state action-led and honest about provenance", () => {
    expect(workspaceSource).toContain("Plan my scenes");
    expect(workspaceSource).toContain("Create manually");
    expect(workspaceSource).toContain("movie-scenes-empty-state");
    expect(workspaceSource).toContain("Applied / current");
    expect(workspaceSource).toContain("Manually edited");
    expect(workspaceSource).toContain("AI-suggested");
    expect(workspaceSource).toContain("sceneMatchesFilter");
    expect(workspaceSource).toContain("api.updateMovieScene(sceneId, input)");
  });

  it("keeps scene decisions behind explicit Director review boundaries", () => {
    expect(workspaceSource).toContain("Review it, approve or reject it, then execute explicitly");
    expect(workspaceSource).toContain("Generate / regenerate");
    expect(workspaceSource).toContain("No local-only scene record is created");
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
    expect(createSource).toContain("movieBody.quick.quickNote");
    expect(i18nSource).toContain("movieBody.quick.quickNote");
    expect(createSource).toContain("function QuickMovieResult");
  });

  it("keeps Story assistance behind a visible proposal review and apply boundary", () => {
    expect(workspaceSource).toContain("movieDeep.directorAssistance");
    expect(workspaceSource).toContain("Create proposal");
    expect(workspaceSource).toContain("Existing content");
    expect(workspaceSource).toContain("Proposed content");
    expect(workspaceSource).toContain("Accept proposal");
    expect(workspaceSource).toContain("Reject");
    expect(workspaceSource).toContain("Apply to editable Story revision");
    expect(workspaceSource).toContain("It never silently rewrites an approved revision.");
  });
  it("renders Story consistency findings as typed, grounded review evidence", () => {
    expect(workspaceSource).toContain("Grounded Director findings");
    expect(workspaceSource).toContain("finding.findingType.replaceAll");
    expect(workspaceSource).toContain("finding.suggestedCorrection");
    expect(workspaceSource).toContain("finding.evidence.map");
    expect(workspaceSource).toContain("No grounded inconsistencies found");
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
    expect(shotDesignerSource).toContain('text("shotOverrideHint")');
    expect(shotDesignerSource).toContain('text("guideLockedNote")');
    expect(shotDesignerI18nSource).toContain("A shot override never rewrites the Movie Guide");
    expect(shotDesignerI18nSource).toContain("يمكن لهذه اللقطة أن تحدد اتجاهها الخاص");
    expect(shotDesignerI18nSource).toContain("ئەم شۆتە دەتوانێت ئاراستەی خۆی دابنێت");
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
    expect(workspaceSource).toContain("Approve source frame");
    expect(workspaceSource).toContain("Create motion check");
    expect(workspaceSource).toContain("Create master pass");
    expect(workspaceSource).toContain("Save as take");
    expect(workspaceSource).toContain("Choose one take, then carry it into the master.");
    expect(workspaceSource).toContain("A take appears only after a real pass has produced a private output.");
    expect(workspaceSource).toContain("MovieProductionResolutionPanel");
    expect(workspaceSource).toContain("selectedTier");
  });
  it("surfaces existing generation, resilience, and QC signals instead of inventing footage", () => {
    expect(workspaceSource).toContain("qualityControlStatus");
    expect(workspaceSource).toContain("attemptCount");
    expect(workspaceSource).toContain("Review this pass before continuing");
    expect(workspaceSource).toContain("No output yet");
    expect(workspaceSource).not.toContain("Generation Jobs, cost guardrails, provider attempts");
  });

  it("surfaces a durable project checkpoint with blocked dependencies and safe recovery", () => {
    expect(workspaceSource).toContain("getMovieProductionCheckpoint");
    expect(workspaceSource).toContain("recoverMovieProduction");
    expect(workspaceSource).toContain("Production can resume safely");
    expect(workspaceSource).toContain("Blocked dependencies");
    expect(workspaceSource).toContain("Resume safely");
    expect(workspaceSource).toContain("Progress is derived from persisted shot, version, take, and job states.");
  });

  it("keeps one Director contextual across rooms and targets real shots", () => {
    expect(workspaceSource).toContain("<MovieDirectorPanel");
    expect(workspaceSource).toContain("sceneShotCount");
    expect(workspaceSource).toContain("selectedShot={selectedShot}");
    expect(workspaceSource).toContain("api.addMovieShot");
    expect(directorSource).toContain('t("boundary")');
  });

  it("keeps loading and failure states useful without fabricating project content", () => {
    expect(workspaceSource).toContain("WorkspaceSkeleton");
    expect(workspaceSource).toContain('aria-busy="true"');
    expect(workspaceSource).toContain("movieShell.tryAgain");
    expect(workspaceSource).toContain('role="alert"');
    expect(workspaceSource).toContain('aria-label={t(`movieModule.${item.slug}.label`)}');
    expect(workspaceSource).toContain('data-module-state="operational"');
  });

  it("delivers the audio, QC, exports and team rooms as operational modules", () => {
    // The four delivery rooms replaced their previous "Soon" placeholders.
    expect(workspaceSource).toContain("<MovieAudioWorkspace project={fullProject} />");
    expect(workspaceSource).toContain("<MovieQualityWorkspace project={fullProject} />");
    expect(workspaceSource).toContain("<MovieExportsWorkspace project={fullProject} />");
    expect(workspaceSource).toContain("<MovieTeamWorkspace project={fullProject} />");
    expect(workspaceSource).not.toContain("futureModules");
    expect(workspaceSource).not.toContain("FutureModule");
  });

  it("appends the persisted bounded select to the exported master timeline", () => {
    expect(salvageSource).toContain("movieTakeSelectId");
    expect(selectsSource).toContain("api.createMovieTakeSelect");
    expect(selectsSource).toContain("api.reviewMovieTakeSelect");
    expect(selectsSource).not.toContain("Accept recommendation");
  });

  it("makes Edit a stateful, validated, keyboard-accessible timeline", () => {
    expect(workspaceSource).toContain("buildMovieEditModel(project)");
    expect(workspaceSource).toContain('role="listbox" aria-label="Movie scene timeline"');
    expect(workspaceSource).toContain('role="option" aria-selected={selected}');
    expect(workspaceSource).toContain("moveMovieEditSelection(model, sceneId, event.key)");
    expect(workspaceSource).toContain("Refresh timeline");
    expect(workspaceSource).toContain("Validation blockers");
    expect(workspaceSource).toContain("no provider call or media mutation");
  });

  it("keeps Edit review grounded in the persisted scene output", () => {
    expect(workspaceSource).toContain("selectedScene.clipAssetId");
    expect(workspaceSource).toContain("assetFileUrl(selectedScene.clipAssetId, true)");
    expect(workspaceSource).toContain('controls preload="metadata"');
    expect(workspaceSource).toContain("Download scene output");
    expect(workspaceSource).toContain("No private scene output recorded yet.");
  });

});
