import { test, expect } from "./fixtures";

test.describe("Document Studio provider boundary", () => {
  test("submits a document brief and renders a safe unavailable state without provider details", async ({
    authenticatedPage: page,
  }) => {
    let submittedPayload: Record<string, unknown> | undefined;
    await page.route("**/api/document-generation/jobs", async (route) => {
      submittedPayload = route.request().postDataJSON() as Record<string, unknown>;
      await route.fulfill({
        status: 503,
        contentType: "application/json",
        body: JSON.stringify({
          error: {
            code: "DOCUMENT_STUDIO_UNAVAILABLE",
            message: "Document generation is not available right now.",
          },
        }),
      });
    });

    await page.goto("/create/document");
    await expect(page.getByRole("heading", { name: /document studio/i })).toBeVisible();
    await page
      .getByLabel(/what would you like taslim to create/i)
      .fill("A concise launch brief for browser regression coverage.");
    await page.getByRole("button", { name: /generate document/i }).click();

    const error = page.locator(".document-brief-card").getByRole("alert");
    await expect(error).toBeVisible();
    await expect(error).toContainText(/could not be started|try again/i);
    await expect(page.locator(".document-brief-card")).toBeVisible();
    await expect(page.getByLabel(/what would you like taslim to create/i)).toHaveValue("A concise launch brief for browser regression coverage.");
    await expect(page.getByRole("button", { name: /generate document/i })).toBeEnabled();
    expect(submittedPayload).toMatchObject({
      description: "A concise launch brief for browser regression coverage.",
      projectId: null,
      attachmentIds: [],
    });
    expect(JSON.stringify(submittedPayload)).not.toMatch(/provider|model|api.?key|secret/i);
    await expect(page.locator("body")).not.toContainText(/stack trace|exception|api.?key|secret/i);
  });

  test("keeps the brief validation local and does not submit an empty request", async ({
    authenticatedPage: page,
  }) => {
    let requestCount = 0;
    await page.route("**/api/document-generation/jobs", async (route) => {
      requestCount += 1;
      await route.continue();
    });

    await page.goto("/create/document");
    const brief = page.getByLabel(/what would you like taslim to create/i);
    const submit = page.getByRole("button", { name: /generate document/i });
    await expect(submit).toBeDisabled();
    await brief.fill("No");
    await expect(submit).toBeDisabled();
    await expect(page.locator(".document-brief-card").getByRole("alert")).toHaveCount(0);
    expect(requestCount).toBe(0);
  });
});
