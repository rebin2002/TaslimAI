import { test, expect, apiJson, E2E_API_URL } from "./fixtures";
import type { APIRequestContext, Page } from "@playwright/test";
import { seedLastSeedWave2Plan } from "./movie-wave2-last-seed-fixtures";

type MovieProject = {
  id: string;
  workspaceId: string;
  title: string;
  description: string;
  durationSeconds: number;
  aspectRatio: string;
  style: string;
  mode: string;
  guide: { lockedRevisionNumber?: number | null };
  scenes: unknown[];
  characters: unknown[];
  locations: unknown[];
  clips: unknown[];
  assemblies: unknown[];
  world: { locations: unknown[]; sets: unknown[]; props: unknown[]; references: unknown[]; usages: unknown[] };
};

type DirectorResponse = {
  proposal: {
    id: string;
    status: string;
    actions: Array<{ id: string; status: string }>;
    storyReview: {
      action: string;
      baseRevisionId: string | null;
      changes: Array<{ field: string; existingContent: string; proposedContent: string }>;
      findings: Array<unknown>;
      appliesToStory: boolean;
      groundedFindings?: Array<{ evidence?: unknown[] }> | null;
      synopsisDevelopment?: {
        status: string;
        scope: string;
        durationSeconds: number;
        synopsis: string;
        beatCount: number;
        complications: string[];
        proposedElements: string[];
      };
    } | null;
  };
  storyContext: { movieBrief: string; durationSeconds: number; currentRevision: unknown | null } | null;
};

type Story = {
  currentRevisionId: string | null;
  approvedRevisionId: string | null;
  currentRevision: {
    id: string;
    status: string;
    authorship: string;
    premise: string;
    logline: string;
    synopsis: string;
    treatment: string;
    scenes: Array<{ id: string; sceneIdentifier: string; elements: Array<{ id: string; elementType: string; content: string; characterName: string | null }> }>;
  } | null;
  approvedRevision: { id: string; status: string } | null;
};

type CastSuggestions = {
  storyRevisionStatus: string;
  suggestions: Array<{ name: string; isEstablished: boolean; isProposed: boolean; sourceType: string }>;
};

const TITLE = "The Last Seed";
const BRIEF = "In the near future, a young farmer lives in a dry village where almost nothing can grow. His grandfather gives him the last seed from an old tree that once covered the valley. The farmer decides to plant it despite everyone telling him it will never survive. After days of protecting it from heat and wind, rain finally arrives and the seed begins to grow. Emotional, hopeful, cinematic.";

async function csrf(request: APIRequestContext) {
  const response = await request.get(`${E2E_API_URL}/api/auth/csrf`);
  expect(response.ok()).toBeTruthy();
  return (await response.json() as { token: string }).token;
}

async function rawPost(request: APIRequestContext, path: string, data: unknown) {
  return request.fetch(`${E2E_API_URL}${path}`, {
    method: "POST",
    data,
    headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": await csrf(request) },
  });
}

async function waitForStory(page: Page, projectId: string): Promise<Story> {
  for (let attempt = 0; attempt < 20; attempt += 1) {
    const response = await page.request.get(`${E2E_API_URL}/api/movie-studio/projects/${projectId}/story`);
    if (response.ok()) return await response.json() as Story;
    await new Promise((resolve) => setTimeout(resolve, 250));
  }
  throw new Error("Story revision did not persist before the acceptance timeout.");
}

async function createAndApplyStoryProposal(page: Page, projectId: string, payload: Record<string, unknown>) {
  const proposal = await apiJson<DirectorResponse>(page.request, "POST", `/api/movie-director/projects/${projectId}/proposals`, payload);
  expect(proposal.proposal.status).toBe("PendingApproval");
  const approved = await apiJson<{ actions: Array<{ id: string; status: string }> }>(page.request, "POST", `/api/movie-director/proposals/${proposal.proposal.id}/approve`, {});
  expect(approved.actions[0].status).toBe("Ready");
  return apiJson<{ action: { status: string }; result: { status: string } }>(page.request, "POST", `/api/movie-director/actions/${approved.actions[0].id}/execute`, {});
}

test.describe("Final integrated Last Seed acceptance", () => {
  test("creates, reviews, applies, and reloads The Last Seed without fake production output", async ({ authenticatedPage: page }) => {
    test.setTimeout(180_000);

    await page.goto("/create/movie");
    await page.getByRole("button", { name: /full movie/i }).first().click();
    await page.getByLabel("Movie title").fill(TITLE);
    await page.getByLabel("Describe your movie").fill(BRIEF);
    await page.getByLabel("Duration").fill("30");
    await page.getByLabel("Aspect ratio").selectOption("16:9");
    await page.getByLabel("Style").selectOption("cinematic");
    await page.getByLabel("Visual language").fill("Emotional, hopeful, cinematic naturalism.");
    await page.getByLabel("Camera language").fill("Slow, intimate push-ins; wide valley frames after rain.");
    await page.getByLabel("Color & lighting").fill("Dry ochre heat resolving into soft rain greens.");
    await page.getByLabel("Sound & narration").fill("Wind and dry silence resolving into rain and a hopeful breath.");
    await page.getByLabel("Continuity rules").fill("The last seed, the grandfather's gift, and the valley's drought remain consistent.");
    await page.getByRole("button", { name: /create full project/i }).click();
    await expect(page).toHaveURL(/\/create\/movie\/[0-9a-f-]+\/overview$/i);
    const projectId = page.url().match(/\/create\/movie\/([0-9a-f-]+)\/overview$/i)?.[1];
    expect(projectId).toBeTruthy();
    if (!projectId) throw new Error("Full Movie project ID was not present in the URL.");

    const project = await apiJson<MovieProject>(page.request, "GET", `/api/movie-studio/projects/${projectId}`);
    expect(project.title).toBe(TITLE);
    expect(project.description).toBe(BRIEF);
    expect(project.mode).toBe("Full");
    expect(project.durationSeconds).toBe(30);
    expect(project.aspectRatio).toBe("16:9");
    expect(project.style.toLowerCase()).toContain("cinematic");
    expect(project.scenes).toHaveLength(0);
    expect(project.characters).toHaveLength(0);
    expect(project.locations).toHaveLength(0);
    expect(project.clips).toHaveLength(0);
    expect(project.assemblies).toHaveLength(0);
    expect(project.world.locations).toHaveLength(0);
    expect(project.world.sets).toHaveLength(0);
    expect(project.world.props).toHaveLength(0);
    expect(project.world.references).toHaveLength(0);
    expect(project.world.usages).toHaveLength(0);

    await page.getByRole("link", { name: "Story", exact: true }).click();
    await expect(page.getByRole("heading", { name: /shape the story/i })).toBeVisible({ timeout: 20_000 });
    await expect(page.getByLabel("Action")).toHaveValue("develop_premise");
    await expect(page.getByLabel("Action").locator("option:checked")).toHaveText("Develop my story");
    await expect(page.getByLabel("Action").locator('option[value="propose_screenplay_scene"]')).toHaveCount(1);
    await expect(page.getByRole("button", { name: "Create proposal", exact: true })).toBeDisabled();

    await page.getByRole("link", { name: "Cast", exact: true }).click();
    await expect(page.getByRole("button", { name: /lock current guide/i })).toBeVisible({ timeout: 20_000 });
    await page.getByRole("button", { name: /lock current guide/i }).click();
    await expect(page.getByRole("button", { name: /lock current guide/i })).toHaveCount(0, { timeout: 20_000 });
    const lockedProject = await apiJson<MovieProject>(page.request, "GET", `/api/movie-studio/projects/${projectId}`);
    expect(lockedProject.guide.lockedRevisionNumber).toBeTruthy();
    await expect(page.getByText(/needs a real shot target/i)).toHaveCount(0);
    const castDirectorButton = page.getByRole("button", { name: "Create typed proposal", exact: true });
    await expect(castDirectorButton).toBeEnabled();
    await castDirectorButton.click();
    await expect(page.getByRole("button", { name: "Approve proposal", exact: true })).toBeVisible({ timeout: 20_000 });
    await page.getByRole("button", { name: "Reject", exact: true }).click();

    await page.getByRole("link", { name: "Story", exact: true }).click();
    const proposalResponse = page.waitForResponse((response) => response.url().includes(`/api/movie-director/projects/${projectId}/proposals`) && response.request().method() === "POST");
    await page.getByRole("button", { name: "Create proposal", exact: true }).click();
    const proposal = await (await proposalResponse).json() as DirectorResponse;
    expect(proposal.proposal.storyReview?.action).toBe("develop_premise");
    expect(proposal.proposal.storyReview?.changes[0].field).toBe("premise");
    expect(proposal.storyContext?.movieBrief).toBe(BRIEF);
    expect(proposal.storyContext?.durationSeconds).toBe(30);
    await expect(page.getByText("Existing content")).toBeVisible({ timeout: 20_000 });
    await expect(page.getByRole("button", { name: "Accept proposal" })).toBeVisible();

    const storyBeforeApply = await page.request.get(`${E2E_API_URL}/api/movie-studio/projects/${projectId}/story`);
    expect(storyBeforeApply.status()).toBe(404);
    const premature = await rawPost(page.request, `/api/movie-director/actions/${proposal.proposal.actions[0].id}/execute`, {});
    expect(premature.status()).toBe(409);
    expect(await premature.text()).toContain("DIRECTOR_APPROVAL_REQUIRED");

    await page.getByRole("button", { name: "Accept proposal" }).click();
    await expect(page.getByRole("button", { name: "Apply to editable Story revision" })).toBeVisible({ timeout: 20_000 });
    const stillUnapplied = await page.request.get(`${E2E_API_URL}/api/movie-studio/projects/${projectId}/story`);
    expect(stillUnapplied.status()).toBe(404);
    await page.getByRole("button", { name: "Apply to editable Story revision" }).click();
    let story = await waitForStory(page, projectId);
    expect(story.currentRevision?.status).toBe("Draft");
    expect(story.currentRevision?.authorship).toBe("AiSuggested");
    expect(story.approvedRevisionId).toBeNull();

    const current = story.currentRevision!;
    for (const anchor of ["young farmer", "dry village", "grandfather", "last seed", "protect", "rain", "hope"]) {
      expect(`${current.premise} ${current.synopsis} ${current.treatment}`.toLowerCase()).toContain(anchor);
    }
    expect(current.logline.toLowerCase()).toContain("last seed");
    expect(current.logline.toLowerCase()).not.toContain("a protagonist faces a defining choice");
    expect(current.treatment).toContain("30-second");

    const synopsisProposal = await apiJson<DirectorResponse>(page.request, "POST", `/api/movie-director/projects/${projectId}/proposals`, { storyAction: "expand_synopsis" });
    const synopsis = synopsisProposal.proposal.storyReview?.synopsisDevelopment;
    expect(synopsis?.status).toBe("proposed");
    expect(synopsis?.scope).toBe("short");
    expect(synopsis?.durationSeconds).toBe(30);
    expect(synopsis?.beatCount).toBeLessThanOrEqual(4);
    expect(synopsis?.complications.length).toBeLessThanOrEqual(1);
    expect(synopsis?.proposedElements.length).toBeGreaterThan(0);
    expect(synopsis?.synopsis.toLowerCase()).toContain("last seed");
    await createAndApplyStoryProposal(page, projectId, { storyAction: "expand_synopsis" });
    story = await waitForStory(page, projectId);

    const screenplayProposal = await apiJson<DirectorResponse>(page.request, "POST", `/api/movie-director/projects/${projectId}/proposals`, { storyAction: "propose_screenplay_scene", goal: "Stage the seed's turning point." });
    expect(screenplayProposal.proposal.storyReview?.changes[0].field).toBe("screenplay_scene");
    expect(screenplayProposal.proposal.storyReview?.changes[0].proposedContent.toLowerCase()).toContain("seed");
    await createAndApplyStoryProposal(page, projectId, { storyAction: "propose_screenplay_scene", goal: "Stage the seed's turning point." });
    story = await waitForStory(page, projectId);
    const scene = story.currentRevision?.scenes[0];
    expect(scene).toBeTruthy();
    expect(scene?.elements.map((element) => element.elementType)).toEqual(expect.arrayContaining(["Action", "Dialogue"]));
    expect(scene?.elements.some((element) => element.characterName === "YOUNG FARMER")).toBeTruthy();
    expect(scene?.elements.some((element) => element.characterName === "GRANDFATHER")).toBeTruthy();

    const castFromStory = await apiJson<CastSuggestions>(page.request, "GET", `/api/movie-studio/projects/${projectId}/cast/from-story`);
    expect(castFromStory.storyRevisionStatus).toBe("Draft");
    expect(castFromStory.suggestions.map((item) => item.name)).toEqual(expect.arrayContaining(["YOUNG FARMER", "GRANDFATHER"]));
    expect(castFromStory.suggestions.every((item) => item.isProposed && !item.isEstablished && item.sourceType === "story_screenplay_character")).toBeTruthy();
    const cast = await apiJson<{ characters: unknown[] }>(page.request, "GET", `/api/movie-studio/projects/${projectId}/cast`);
    expect(cast.characters).toHaveLength(0);

    const castRoomDirector = await apiJson<DirectorResponse>(page.request, "POST", `/api/movie-director/projects/${projectId}/proposals`, { contextTargetType: "cast", storyAction: "improve_logline", goal: "Review Cast continuity without a shot." });
    expect(castRoomDirector.proposal.status).toBe("PendingApproval");
    await apiJson(page.request, "POST", `/api/movie-director/proposals/${castRoomDirector.proposal.id}/reject`, {});

    const elementsBeforeRewrite = scene!.elements.map((element) => ({ id: element.id, content: element.content }));
    const target = scene!.elements[0];
    const rewriteProposal = await apiJson<DirectorResponse>(page.request, "POST", `/api/movie-director/projects/${projectId}/proposals`, { storyAction: "rewrite_selected_passage", targetSceneId: scene!.id, targetElementId: target.id, selectedPassage: target.content });
    expect(rewriteProposal.proposal.storyReview?.changes[0].field).toBe("screenplay_passage");
    await createAndApplyStoryProposal(page, projectId, { storyAction: "rewrite_selected_passage", targetSceneId: scene!.id, targetElementId: target.id, selectedPassage: target.content });
    story = await waitForStory(page, projectId);
    const rewritten = story.currentRevision!.scenes[0].elements;
    expect(rewritten.filter((element, index) => element.content !== elementsBeforeRewrite[index].content)).toHaveLength(1);
    expect(rewritten.find((element) => element.id === target.id)?.content).not.toBe(target.content);

    const consistency = await apiJson<DirectorResponse>(page.request, "POST", `/api/movie-director/projects/${projectId}/proposals`, { storyAction: "identify_story_inconsistencies" });
    expect(consistency.proposal.storyReview?.appliesToStory).toBe(false);
    expect(consistency.proposal.storyReview?.findings.length).toBeGreaterThan(0);
    expect(consistency.proposal.storyReview?.groundedFindings?.length).toBeGreaterThan(0);
    expect(consistency.proposal.storyReview?.groundedFindings?.every((finding) => (finding.evidence?.length ?? 0) > 0)).toBeTruthy();
    const revisionBeforeConsistency = story.currentRevisionId;
    const consistencyResult = await createAndApplyStoryProposal(page, projectId, { storyAction: "identify_story_inconsistencies" });
    expect(consistencyResult.result.status).toBe("Succeeded");
    story = await waitForStory(page, projectId);
    expect(story.currentRevisionId).toBe(revisionBeforeConsistency);

    const continuityLocation = await apiJson<{ id: string }>(page.request, "POST", `/api/movie-studio/projects/${projectId}/locations`, {
      name: "Dry Village",
      description: "A near-future village whose fields are cracked by drought.",
      visualContinuityNotes: "Ochre dust, dry wind, and the old tree remain visible references until rain arrives.",
    });
    const continuitySet = await apiJson<{ id: string }>(page.request, "POST", `/api/movie-studio/projects/${projectId}/sets`, {
      name: "Old Tree Field",
      description: "The field where the farmer plants the last seed.",
      environmentType: "natural",
      movieLocationId: continuityLocation.id,
      visualDescription: "Cracked soil beneath the old tree, with a clear view of the valley.",
      timeOfDay: "late afternoon",
      weather: "dry wind",
      continuityNotes: "The field changes from dust and heat to rain and the first green shoot.",
    });
    const continuityProp = await apiJson<{ id: string }>(page.request, "POST", `/api/movie-studio/projects/${projectId}/props`, {
      name: "Last Seed",
      description: "The single seed from the grandfather's old tree.",
      category: "hero prop",
      continuityNotes: "The same seed is handed over, planted, protected, and represented by the first shoot.",
    });

    const wave2Plan = await seedLastSeedWave2Plan(page, projectId, async (sceneId, selectedShotId) => {
      await apiJson(page.request, "POST", `/api/movie-studio/scenes/${sceneId}/world-usage`, {
        entityType: "set",
        entityId: continuitySet.id,
        role: "primary planting environment",
      });
      await apiJson(page.request, "POST", `/api/movie-studio/scenes/${sceneId}/world-usage`, {
        entityType: "prop",
        entityId: continuityProp.id,
        movieShotId: selectedShotId,
        role: "hero seed",
      });
    });
    expect(wave2Plan.scene.title.toLowerCase()).toContain("last seed");
    expect(wave2Plan.scene.summary.toLowerCase()).toContain("rain");
    expect(wave2Plan.scene.durationSeconds).toBe(30);
    expect(wave2Plan.shots.map((shot) => shot.durationSeconds)).toEqual([8, 10, 12]);
    expect(wave2Plan.shots.every((shot) => shot.planState === "ReadyForStoryboard")).toBeTruthy();
    expect(wave2Plan.shots.every((shot) => shot.readiness.ready)).toBeTruthy();
    expect(wave2Plan.shots.flatMap((shot) => `${shot.description} ${shot.purpose} ${shot.continuityReferences}`).join(" ").toLowerCase()).toEqual(expect.stringContaining("last seed"));
    expect(wave2Plan.selectedShot.purpose?.toLowerCase()).toContain("turning point");
    expect(wave2Plan.selectedShot.productionVersions).toEqual(expect.arrayContaining([
      expect.objectContaining({
        id: wave2Plan.approvedStoryboardId,
        stage: "ApprovedStoryboard",
        status: "Approved",
        assetId: null,
        generationJobId: null,
      }),
    ]));

    const shotContinuity = await apiJson<{
      movieProjectId: string;
      sceneId: string;
      shotId: string;
      sets: Array<{ id: string; name: string }>;
      props: Array<{ id: string; name: string }>;
      warnings: Array<{ severity: string; source: { entityId: string | null; value: string } }>;
    }>(page.request, "GET", `/api/movie-studio/shots/${wave2Plan.selectedShot.id}/world-continuity`);
    expect(shotContinuity.movieProjectId).toBe(projectId);
    expect(shotContinuity.sceneId).toBe(wave2Plan.scene.id);
    expect(shotContinuity.shotId).toBe(wave2Plan.selectedShot.id);
    expect(shotContinuity.sets).toEqual(expect.arrayContaining([expect.objectContaining({ id: continuitySet.id, name: "Old Tree Field" })]));
    expect(shotContinuity.props).toEqual(expect.arrayContaining([expect.objectContaining({ id: continuityProp.id, name: "Last Seed" })]));
    expect(shotContinuity.warnings.every((warning) => warning.source.entityId || warning.source.value)).toBeTruthy();

    for (const requirement of wave2Plan.integrationRequirements) test.info().annotations.push({ type: "integration-required", description: requirement });

    await page.reload();
    await expect(page.getByRole("navigation", { name: "Story sections" }).getByText("AI suggested", { exact: false }).first()).toBeVisible({ timeout: 20_000 });
    const persisted = await waitForStory(page, projectId);
    expect(persisted.currentRevision?.scenes.length).toBe(1);
    expect(persisted.currentRevision?.authorship).toBe("AiSuggested");
    const persistedPlan = await apiJson<{ scenes: Array<{ id: string; title: string; durationSeconds: number | null; shots: Array<{ id: string; durationSeconds: number | null; productionVersions: Array<{ id: string; stage: string; status: string }> }> }> }>(page.request, "GET", `/api/movie-studio/projects/${projectId}`);
    const persistedScene = persistedPlan.scenes.find((item) => item.id === wave2Plan.scene.id);
    expect(persistedScene).toMatchObject({ id: wave2Plan.scene.id, title: wave2Plan.scene.title, durationSeconds: 30 });
    expect(persistedScene?.shots.map((shot) => shot.durationSeconds)).toEqual([8, 10, 12]);
    expect(persistedScene?.shots.flatMap((shot) => shot.productionVersions).some((version) => version.id === wave2Plan.approvedStoryboardId && version.stage === "ApprovedStoryboard" && version.status === "Approved")).toBeTruthy();

    const provider = await apiJson<{ provider: { ready: boolean } }>(page.request, "GET", "/api/movie-studio/provider");
    expect(provider.provider.ready).toBe(false);
    const me = await apiJson<{ personalWorkspace: { id: string } }>(page.request, "GET", "/api/auth/me");
    const usage = await apiJson<{ totalRequests: number; customerChargedAmount: number }>(page.request, "GET", `/api/workspaces/${me.personalWorkspace.id}/usage/summary`);
    expect(usage.totalRequests).toBeGreaterThan(0);
    expect(usage.customerChargedAmount).toBe(0);

    await page.goto("/create/movie");
    await expect(page.getByRole("button", { name: /quick movie/i }).first()).toBeVisible();
    await expect(page.getByText("One brief, one output")).toBeVisible();
  });
});
