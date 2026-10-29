import { test, expect } from "./fixtures";

test.describe("Movie Studio V2 browser smoke", () => {
  test("creates a Full Movie project and navigates every production room without fake output", async ({ authenticatedPage: page }) => {
    test.setTimeout(120_000);
    await page.goto("/create/movie");
    await expect(page.getByRole("heading", { name: /movie studio/i })).toBeVisible();

    await page.getByRole("tab", { name: /full movie/i }).first().click();
    await page.getByLabel("Movie title").fill("E2E Movie V2 Workspace");
    await page.getByLabel("Describe your movie").fill("A deterministic Full Movie workspace smoke project.");
    await page.getByRole("button", { name: /create full project/i }).click();

    await expect(page).toHaveURL(/\/create\/movie\/[0-9a-f-]+\/overview$/i);
    const projectUrl = page.url();
    const projectId = projectUrl.match(/\/create\/movie\/([0-9a-f-]+)\/overview$/i)?.[1];
    expect(projectId).toBeTruthy();
    await expect(page.getByRole("heading", { name: "E2E Movie V2 Workspace", level: 1 })).toBeVisible({ timeout: 20_000 });
    await expect(page.getByText("No output yet")).toBeVisible();
    await expect(page.getByText("No generated footage yet")).toBeVisible();

    await page.getByRole("link", { name: "Scenes", exact: true }).click();
    await page.getByPlaceholder("Scene title").fill("Morning street");
    await page.getByPlaceholder("One-line scene intent").fill("Establish the street before Mara arrives.");
    await page.getByRole("button", { name: "Add scene", exact: true }).click();
    await expect(page.getByText("Add shot to scene")).toBeVisible();
    await page.getByLabel("Shot purpose").fill("Establish the quiet morning mood.");
    await page.getByLabel("Shot description").fill("Mara crosses into the morning light.");
    await page.getByLabel("Subjects / characters").fill("Mara");
    await page.getByLabel("Location / set").fill("Old city street set");
    await page.getByLabel("Expected duration").fill("6");
    await page.getByLabel("Production requirements").fill("Canvas bag and restrained crossing performance.");
    await page.getByLabel("Camera / framing").fill("Medium-wide, eye level");
    await page.getByLabel("Continuity references").fill("Cool dawn palette; follows the empty street.");
    await page.getByRole("button", { name: "Add planned shot", exact: true }).click();
    await expect(page.getByText("1/1 ready for Storyboard")).toBeVisible();
    await expect(page.getByText("creating a shot never starts generation")).toBeVisible();

    for (const room of ["Story", "Cast", "World", "Scenes", "Storyboard", "Production", "Team"]) {
      await page.getByRole("link", { name: room, exact: true }).click();
      await expect(page).toHaveURL(new RegExp(`/create/movie/${projectId}/${room.toLowerCase()}$`));
      await expect(page.getByRole("heading", { name: "E2E Movie V2 Workspace", level: 1 })).toBeVisible({ timeout: 20_000 });
      await expect(page.locator("main.movie-workspace-main")).toBeVisible();
    }

    await page.getByRole("link", { name: "Overview", exact: true }).click();
    await expect(page).toHaveURL(new RegExp(`/create/movie/${projectId}/overview$`));
    await expect(page.getByText("No output yet")).toBeVisible();
    await expect(page.getByText("No generated footage yet")).toBeVisible();

    await page.goto("/create/movie");
    await expect(page.getByRole("tab", { name: /quick movie/i }).first()).toBeVisible();
    await expect(page.getByText("One brief, one output")).toBeVisible();
    await expect(page.getByText(/Quick Movie stays intentionally small/i)).toBeVisible();
    await expect(page).not.toHaveURL(/\/create\/movie\/[0-9a-f-]+\/overview$/i);
  });

  test("keeps Quick and Full draft durations separate when switching tabs", async ({ authenticatedPage: page }) => {
    await page.goto("/create/movie");
    const quickTab = page.getByRole("tab", { name: /quick movie/i }).first();
    const fullTab = page.getByRole("tab", { name: /full movie/i }).first();
    const durationField = page.locator(".movie-brief-card input[type=number]").first();

    await expect(durationField).toHaveAttribute("max", "3600");
    await durationField.fill("90");
    await fullTab.click();
    await expect(durationField).toHaveValue("30");
    await expect(durationField).toHaveAttribute("max", "7200");
    await durationField.fill("7200");

    // Switching back cannot quietly turn a feature-length film into a
    // 3,600-second Quick clip or leave the Quick form invalid.
    await quickTab.click();
    await expect(durationField).toHaveAttribute("max", "3600");
    await expect(durationField).toHaveValue("90");
    await fullTab.click();
    await expect(durationField).toHaveValue("7200");

    // Keyboard tab navigation must preserve the same independent drafts.
    await fullTab.press("ArrowLeft");
    await expect(durationField).toHaveValue("90");
    await quickTab.press("ArrowRight");
    await expect(durationField).toHaveValue("7200");
    // No generation or project creation is invoked by this regression.
  });

  test("persists a two-hour full movie timeline without queueing paid work", async ({ authenticatedPage: page }) => {
    test.setTimeout(120_000);
    await page.goto("/create/movie");
    const quickDurationField = page.locator(".movie-brief-card input[type=number]").first();
    await expect(quickDurationField).toHaveAttribute("max", "3600");
    await page.getByRole("tab", { name: /full movie/i }).first().click();

    // The project duration is a timeline goal; it is not an instruction to
    // generate a single 7,200-second paid provider clip.
    const durationField = page.locator(".movie-brief-card input[type=number]").first();
    await expect(durationField).toHaveAttribute("max", "7200");
    await durationField.fill("7200");
    await page.getByLabel("Movie title").fill("E2E Two Hour Movie Timeline");
    await page.getByLabel("Describe your movie").fill("A two-hour production plan that will be assembled from separately admitted shots.");
    await page.getByRole("button", { name: /create full project/i }).click();

    await expect(page).toHaveURL(/\/create\/movie\/[0-9a-f-]+\/overview$/i);
    const projectId = page.url().match(/\/create\/movie\/([0-9a-f-]+)\/overview$/i)?.[1];
    expect(projectId).toBeTruthy();

    const response = await page.request.get(`/api/movie-studio/projects/${projectId}`);
    expect(response.ok()).toBe(true);
    const project = (await response.json()) as {
      id: string;
      mode: string;
      durationSeconds: number;
      clips: unknown[];
    };
    expect(project.id).toBe(projectId);
    expect(project.mode).toBe("Full");
    expect(project.durationSeconds).toBe(7200);
    expect(project.clips).toEqual([]);

    await page.reload();
    await expect(page.getByRole("heading", { name: "E2E Two Hour Movie Timeline", level: 1 })).toBeVisible();
    await expect(page.getByText("No generated footage yet")).toBeVisible();
  });

  test("creates an empty Story Director proposal for the same MovieProject after the guide is locked", async ({ authenticatedPage: page }) => {
    test.setTimeout(120_000);
    await page.goto("/create/movie");
    await page.getByRole("tab", { name: /full movie/i }).first().click();
    await page.getByLabel("Movie title").fill("E2E Empty Story Director");
    await page.getByLabel("Describe your movie").fill("A deterministic premise about a choice and its consequence.");
    await page.getByRole("button", { name: /create full project/i }).click();
    await expect(page).toHaveURL(/\/create\/movie\/([0-9a-f-]+)\/overview$/i);
    const projectId = page.url().match(/\/create\/movie\/([0-9a-f-]+)\/overview$/i)?.[1];
    expect(projectId).toBeTruthy();

    await page.getByRole("link", { name: "Story", exact: true }).click();
    await expect(page.getByText("Lock the Movie Guide before creating a Story Director proposal.")).toBeVisible({ timeout: 20_000 });
    await expect(page.getByRole("button", { name: "Create proposal", exact: true })).toBeDisabled();

    await page.getByRole("link", { name: "Cast", exact: true }).click();
    await expect(page.getByRole("button", { name: /lock current guide/i })).toBeVisible({ timeout: 20_000 });
    await page.getByRole("button", { name: /lock current guide/i }).click();
    await expect(page.getByRole("button", { name: /lock current guide/i })).toHaveCount(0, { timeout: 20_000 });

    await page.getByRole("link", { name: "Story", exact: true }).click();
    await expect(page).toHaveURL(new RegExp(`/create/movie/${projectId}/story$`));
    await expect(page.getByRole("button", { name: "Create proposal", exact: true })).toBeEnabled();
    await page.getByRole("button", { name: "Create proposal", exact: true }).click();
    await expect(page.getByText("Existing content")).toBeVisible({ timeout: 20_000 });
    await expect(page.getByText(/movie project not found/i)).toHaveCount(0);
    await expect(page).toHaveURL(new RegExp(`/create/movie/${projectId}/story$`));
  });
});
