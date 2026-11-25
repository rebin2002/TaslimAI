import { expect, test } from "./fixtures";

/**
 * Issue #1267 — exercise the real authenticated route with synthetic research data.
 * The accepted slice is provider-free: Plan preparation is deterministic and the
 * test confirms Discover remains explicitly unavailable rather than fabricated.
 * The shared E2E fixture must target a disposable local/CI database (never prod).
 */
test("a signed-in student can save and approve the Academic Plan from Standard Research", async ({ authenticatedPage: page }) => {
  const researchQuestion = "How do pharmaceutical market-access and regulation policies affect medicine availability in the Kurdistan Region of Iraq?";
  const keywords = "pharmaceutical market access, medicines regulation, Kurdistan Region of Iraq, medicine availability";

  await page.goto("/create/research");
  const academicEntry = page.getByRole("link", { name: /academic research/i });
  await expect(academicEntry).toBeVisible();
  await expect(page.getByLabel("Research question")).toBeVisible();
  await academicEntry.click();

  await expect(page).toHaveURL(/\/create\/research\/academic$/);
  await expect(page.getByRole("heading", { name: "Academic Research Studio" })).toBeVisible();
  await page.getByLabel("Discipline", { exact: true }).selectOption("law_and_policy");
  await page.getByRole("button", { name: "Use common practice for this discipline" }).click();

  // The plan is constrained to scholarly material; books and journal articles
  // are first-class source types for this synthetic regulation topic.
  await expect(page.getByRole("checkbox", { name: /book/i })).toBeChecked();
  await expect(page.getByRole("checkbox", { name: /journal article/i })).toBeChecked();

  await page.getByLabel("Research question", { exact: true }).fill(researchQuestion);
  await page.getByLabel("Geographic scope", { exact: true }).fill("Kurdistan Region of Iraq");
  await page.getByLabel("Population or context", { exact: true }).fill("Pharmaceutical market access, regulation, and medicine availability");
  await page.getByLabel("Time period", { exact: true }).fill("2015–2026");
  await page.getByLabel("Keywords", { exact: true }).fill(keywords);

  await page.getByRole("button", { name: "Save and prepare plan", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Research plan for review", exact: true })).toBeVisible();
  await expect(page.getByText(researchQuestion, { exact: false })).toBeVisible();
  await expect(page.getByText(keywords, { exact: false })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Suggested scholarly search queries", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Approve plan", exact: true })).toBeVisible();

  // The panel reloads the latest saved workspace project after navigation;
  // assert that the Plan and the entire keyword list came back from persistence.
  await page.reload();
  await expect(page).toHaveURL(/\/create\/research\/academic$/);
  await expect(page.getByRole("heading", { name: "Academic Research Studio" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Research plan for review", exact: true })).toBeVisible();
  await expect(page.getByText(keywords, { exact: false })).toBeVisible();
  await expect(page.getByRole("button", { name: "Approve plan", exact: true })).toBeVisible();

  await page.getByRole("button", { name: "Approve plan", exact: true }).click();
  const discoverNotice = page.getByRole("status").filter({ hasText: /scholarly discovery is not connected yet/i });
  await expect(discoverNotice).toBeVisible();
  const resumeResponse = page.waitForResponse((response) =>
    response.request().method() === "POST" && /\/api\/academic-projects\/[^/]+\/run(?:\?|$)/.test(response.url()),
  );
  await page.getByRole("button", { name: "Resume at the next available step", exact: true }).click();
  expect((await resumeResponse).status()).toBe(501);
  await expect(discoverNotice).toBeVisible();
  await expect(page.getByRole("button", { name: "Resume at the next available step", exact: true })).toBeEnabled();
});
