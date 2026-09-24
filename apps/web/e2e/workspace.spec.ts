import { test, expect, createProject, registerInUi } from "./fixtures";

test.describe("workspace and project journeys", () => {
  test("creates a project, opens detail, and hands off to a preselected Studio", async ({ authenticatedPage: page }) => {
    await page.goto("/projects");
    await page.getByRole("button", { name: /new project/i }).first().click();
    await page.getByLabel("Project name").fill("E2E Launch Plan");
    await page.getByLabel("Description").fill("A deterministic project used by browser coverage.");
    await page.getByLabel("Project type").selectOption("Marketing");
    await page.getByRole("button", { name: /create project/i }).click();

    await expect(page).toHaveURL(/\/projects\/[0-9a-f-]+$/i);
    await expect(page.getByRole("heading", { name: "E2E Launch Plan" })).toBeVisible();
    await expect(page.getByRole("heading", { name: /everything in one view/i })).toBeVisible();

    const imageStudio = page.getByRole("link", { name: /^image$/i });
    await expect(imageStudio).toHaveAttribute("href", /\/create\/image\?projectId=/);
    await imageStudio.click();
    await expect(page).toHaveURL(/\/create\/image\?projectId=[0-9a-f-]+$/i);
    await expect(page.getByRole("heading", { name: /image studio/i })).toBeVisible();
    await expect(page.getByLabel("Project")).not.toHaveValue("");
  });

  test("hands a project into Chat and preserves the project context", async ({ authenticatedPage: page }) => {
    const project = await createProject(page, "E2E Chat Context");
    await page.goto(`/projects/${project.id}`);
    await page.getByRole("button", { name: /new chat/i }).click();
    await expect(page).toHaveURL(new RegExp(`/chat\\?projectId=${project.id}`));
    await expect(page.getByLabel("Project context")).toHaveValue(project.id);
    await expect(page.getByText(new RegExp(`Active:.*${project.name}`, "i"))).toBeVisible();
  });

  test("keeps Assets scoped to the authenticated workspace", async ({ authenticatedPage: page }) => {
    const project = await createProject(page, "E2E Private Project");
    await page.goto("/assets");
    await expect(page.getByRole("heading", { name: /assets/i })).toBeVisible();

    const browser = page.context().browser();
    expect(browser).not.toBeNull();
    const otherContext = await browser!.newContext({ baseURL: process.env.E2E_WEB_URL ?? "http://127.0.0.1:3000" });
    const otherPage = await otherContext.newPage();
    const otherUser = {
      displayName: "E2E Other User",
      email: `e2e+other-${Date.now().toString(36)}@example.test`,
      password: process.env.E2E_TEST_PASSWORD ?? "E2eStrongPassword!123",
    };
    await registerInUi(otherPage, otherUser);
    await otherPage.goto(`/projects/${project.id}`);
    await expect(otherPage.getByText(/could not load your projects|project not found/i)).toBeVisible();
    await otherContext.close();
  });

  test("opens account settings and the charging-disabled billing view", async ({ authenticatedPage: page }) => {
    await page.goto("/account");
    await expect(page.getByRole("heading", { name: /^account$/i })).toBeVisible();
    await expect(page.getByRole("link", { name: /view billing/i })).toBeVisible();
    await page.getByRole("link", { name: /view billing/i }).click();
    await expect(page).toHaveURL(/\/account\/billing$/);
    await expect(page.getByRole("heading", { name: /billing/i })).toBeVisible();
    await expect(page.getByText(/billing is not collecting payment yet/i)).toBeVisible();
  });

  test("normal users are denied Admin Operations", async ({ authenticatedPage: page }) => {
    await page.goto("/account/admin/operations");
    await expect(page.getByRole("heading", { name: /administrator access required/i })).toBeVisible();
    await expect(page.getByText(/only to active taslim administrators/i)).toBeVisible();
  });
});
