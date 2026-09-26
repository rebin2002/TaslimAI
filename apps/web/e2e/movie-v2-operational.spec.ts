import { test, expect } from "./fixtures";
import {
  getMovieProject,
  seedMovieOperationalRecords,
  startMovieDiagnostics,
} from "./movie-v2-operational-fixtures";

test.describe("Movie Studio V2 operational browser contracts", () => {
  test("keeps the real Full Movie journey persisted without manufacturing output", async ({
    authenticatedPage: page,
  }, testInfo) => {
    test.setTimeout(120_000);
    const diagnostics = startMovieDiagnostics(page);

    await page.goto("/create/movie");
    await expect(
      page.getByRole("heading", { name: /movie studio/i }),
    ).toBeVisible();
    await page
      .getByRole("button", { name: /full movie/i })
      .first()
      .click();
    await page.getByLabel("Movie title").fill("E2E Operational Full Movie");
    await page
      .getByLabel("Describe your movie")
      .fill(
        "A deterministic Full Movie operational journey with real persisted records.",
      );
    await page.getByRole("button", { name: /create full project/i }).click();
    await expect(page).toHaveURL(/\/create\/movie\/[0-9a-f-]+\/overview$/i);

    const projectId = page
      .url()
      .match(/\/create\/movie\/([0-9a-f-]+)\/overview$/i)?.[1];
    expect(projectId).toBeTruthy();
    const records = await seedMovieOperationalRecords(page, projectId!);
    const persisted = await getMovieProject(page, projectId!);
    expect(persisted).toMatchObject({ id: projectId, mode: "Full" });
    expect(persisted.characters).toContainEqual(
      expect.objectContaining({ id: records.characterId, name: "Mara" }),
    );
    expect(persisted.locations).toContainEqual(
      expect.objectContaining({ id: records.locationId, name: "Old Harbor" }),
    );
    expect(persisted.scenes).toContainEqual(
      expect.objectContaining({
        id: records.sceneId,
        title: "Blue Hour Harbor",
      }),
    );
    const persistedShot = persisted.scenes
      .flatMap((scene) => scene.shots)
      .find((shot) => shot.id === records.shotId);
    expect(persistedShot).toBeDefined();
    expect(persistedShot?.productionVersions).toEqual(
      expect.arrayContaining([
        expect.objectContaining({
          id: records.storyboardCandidateId,
          stage: "ApprovedStoryboard",
          assetId: null,
          generationJobId: null,
        }),
        expect.objectContaining({
          id: records.keyframeId,
          stage: "ApprovedKeyframe",
          assetId: null,
          generationJobId: null,
        }),
      ]),
    );

    await expect(page.getByText("No output yet")).toBeVisible();
    await expect(page.getByText("No generated footage yet")).toBeVisible();
    await expect(page.locator("video")).toHaveCount(0);

    for (const room of [
      "Story",
      "Cast",
      "World",
      "Scenes",
      "Storyboard",
      "Production",
      "Team",
    ]) {
      await page.getByRole("link", { name: room, exact: true }).click();
      await expect(page).toHaveURL(
        new RegExp(`/create/movie/${projectId}/${room.toLowerCase()}$`),
      );
      await expect(
        page.getByRole("heading", {
          name: "E2E Operational Full Movie",
          level: 1,
        }),
      ).toBeVisible({ timeout: 20_000 });
      await expect(page.locator("main.movie-workspace-main")).toBeVisible();
    }

    await page.getByRole("link", { name: "Cast", exact: true }).click();
    await expect(
      page.getByRole("heading", { name: "Mara", level: 3 }),
    ).toBeVisible();
    await page.getByRole("link", { name: "World", exact: true }).click();
    await expect(
      page.getByRole("heading", { name: "Old Harbor", level: 3 }),
    ).toBeVisible();
    await page.getByRole("link", { name: "Scenes", exact: true }).click();
    await expect(page.getByText("Blue Hour Harbor")).toBeVisible();
    await page.reload();
    await expect(page.getByText("Blue Hour Harbor")).toBeVisible();

    await page.getByRole("link", { name: "Overview", exact: true }).click();
    await expect(page).toHaveURL(
      new RegExp(`/create/movie/${projectId}/overview$`),
    );
    await expect(page.getByText("No output yet")).toBeVisible();
    await expect(page.getByText("No generated footage yet")).toBeVisible();
    await expect(page.locator("video")).toHaveCount(0);
    await expect(page.locator("body")).not.toContainText(
      /generated video|latest project output|ready to review/i,
    );

    await page.goto("/create/movie");
    await expect(
      page.getByRole("button", { name: /quick movie/i }).first(),
    ).toBeVisible();
    await expect(page.getByText("One brief, one output")).toBeVisible();
    await expect(
      page.getByText(/Quick Movie stays intentionally small/i),
    ).toBeVisible();
    await expect(page).not.toHaveURL(/\/create\/movie\/[0-9a-f-]+\/overview$/i);

    const observed = diagnostics.stop();
    await testInfo.attach("movie-operational-network-diagnostics.json", {
      body: JSON.stringify(observed, null, 2),
      contentType: "application/json",
    });
    expect(
      observed.some(
        (item) =>
          item.url.includes(`/api/movie-studio/projects/${projectId}`) &&
          item.status >= 200 &&
          item.status < 300,
      ),
    ).toBe(true);
  });
});
