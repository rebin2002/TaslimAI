import { test, expect } from "./fixtures";

test.describe("Movie Studio V2 browser smoke", () => {
  test("creates a Full Movie project and navigates every production room without fake output", async ({ authenticatedPage: page }) => {
    test.setTimeout(120_000);
    await page.goto("/create/movie");
    await expect(page.getByRole("heading", { name: /movie studio/i })).toBeVisible();

    await page.getByRole("button", { name: /full movie/i }).first().click();
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
    await expect(page.getByRole("button", { name: /quick movie/i }).first()).toBeVisible();
    await expect(page.getByText("One brief, one output")).toBeVisible();
    await expect(page.getByText(/Quick Movie stays intentionally small/i)).toBeVisible();
    await expect(page).not.toHaveURL(/\/create\/movie\/[0-9a-f-]+\/overview$/i);
  });
});
