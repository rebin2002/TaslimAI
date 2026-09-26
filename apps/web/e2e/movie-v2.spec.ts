import { test, expect } from "./fixtures";

test.describe("Movie Studio V2 browser smoke", () => {
  test("creates a Full Movie project and navigates every production room without fake output", async ({ authenticatedPage: page }) => {
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

    for (const room of ["Overview", "Story", "Cast", "World", "Scenes", "Storyboard", "Production", "Team"]) {
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
