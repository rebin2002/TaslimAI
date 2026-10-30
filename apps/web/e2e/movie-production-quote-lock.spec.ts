import { test, expect } from "./fixtures";
import { apiJson } from "./fixtures";
import { seedMovieOperationalRecords } from "./movie-v2-operational-fixtures";
import type { MovieProductionVersion, MovieProject } from "../src/lib/api";

test("keeps the quoted production profile fixed while confirmation is pending", async ({ authenticatedPage: page }) => {
  await page.goto("/create/movie");
  await page.getByRole("tab", { name: /full movie/i }).first().click();
  await page.getByLabel("Movie title").fill("E2E Quote Lock Movie");
  await page.getByLabel("Describe your movie").fill("Provider-free profile confirmation test.");
  await page.getByRole("button", { name: /create full project/i }).click();
  await expect(page).toHaveURL(/\/create\/movie\/[0-9a-f-]+\/overview$/i);

  const projectId = page.url().match(/\/create\/movie\/([0-9a-f-]+)\/overview$/i)?.[1];
  expect(projectId).toBeTruthy();
  const fixture = await seedMovieOperationalRecords(page, projectId!);
  const project = await apiJson<MovieProject>(page.request, "GET", `/api/movie-studio/projects/${projectId}`);
  const shot = project.scenes.flatMap((scene) => scene.shots).find((candidate) => candidate.id === fixture.shotId);
  expect(shot).toBeDefined();
  const keyframe = shot?.productionVersions.find((version) => version.id === fixture.keyframeId);
  expect(keyframe).toBeDefined();
  const now = new Date().toISOString();
  const motion: MovieProductionVersion = {
    ...keyframe!,
    id: "a0000000-0000-0000-0000-000000000001",
    versionNumber: Math.max(...(shot?.productionVersions.map((version) => version.versionNumber) ?? [0])) + 1,
    stage: "MotionPreview",
    status: "Approved",
    label: "Provider-free approved motion fixture",
    sourceVersionId: keyframe!.id,
    generationJobId: null,
    assetId: null,
    createdAt: now,
    updatedAt: now,
    execution: null,
  };
  shot!.productionVersions.push(motion);

  await page.route(`**/api/movie-studio/projects/${projectId}`, async (route) => {
    if (route.request().method() !== "GET") return route.continue();
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(project) });
  });
  await page.route(`**/api/movie-studio/shots/${fixture.shotId}/production/preflight**`, async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        canProceed: true,
        decision: "ready",
        summary: "Provider-free test quote",
        priceIntent: { currency: "USD", expectedMaximumAmountUsd: 0.15, quoteFingerprint: "provider-free-e2e-quote" },
      }),
    });
  });

  let queuedInput: Record<string, unknown> | null = null;
  await page.route(`**/api/movie-studio/shots/${fixture.shotId}/production/render`, async (route) => {
    queuedInput = route.request().postDataJSON() as Record<string, unknown>;
    await route.fulfill({ status: 202, contentType: "application/json", body: JSON.stringify({ accepted: true }) });
  });

  await page.goto(`/create/movie/${projectId}/production`);
  const tierGroup = page.getByRole("group", { name: "Choose finish level" });
  const upgradeTier = tierGroup.getByRole("button", { name: /Upgrade/ });
  await upgradeTier.click();
  await expect(upgradeTier).toHaveAttribute("aria-pressed", "true");
  await page.getByRole("button", { name: "Create master pass" }).click();

  const confirmation = page.getByRole("dialog", { name: "Confirm this production pass" });
  await expect(confirmation).toBeVisible();
  await expect(confirmation).toContainText("Quoted profile: 1080p · Cinematic");
  await expect(tierGroup.getByRole("button", { name: /Master/ })).toBeDisabled();
  await expect(page.getByRole("button", { name: "Create master pass" })).toBeDisabled();
  await expect(confirmation.getByRole("button", { name: "Confirm and queue" })).toBeEnabled();

  await confirmation.getByRole("button", { name: "Confirm and queue" }).click();
  await expect.poll(() => queuedInput).not.toBeNull();
  expect(queuedInput).toMatchObject({
    targetResolution: "1080p",
    qualityTier: "Cinematic",
    confirmationAccepted: true,
    expectedMaximumAmountUsd: 0.15,
    expectedQuoteFingerprint: "provider-free-e2e-quote",
  });
});
