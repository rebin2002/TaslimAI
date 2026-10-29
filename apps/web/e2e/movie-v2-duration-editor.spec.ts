import { test, expect, apiJson } from "./fixtures";

// This journey edits only project metadata and adds a planning record.
// It must never submit a provider job, reserve paid generation, or invent footage.
test.describe("Full Movie draft runtime browser acceptance", () => {
  test("recovers a failed duration-only preview without submitting provider work", async ({
    authenticatedPage: page,
  }) => {
    test.setTimeout(120_000);
    let acceptPreviewRequests = false;
    let failedPreviewRequests = 0;
    let retriedPreviewRequests = 0;
    const providerSubmissions: string[] = [];

    page.on("request", (request) => {
      const path = new URL(request.url()).pathname;
      if (
        request.method() === "POST" &&
        /\/api\/(?:generation-jobs|movie-studio\/.*\/(?:generate|render|queue))(?:\/|$)/i.test(path)
      ) providerSubmissions.push(path);
    });
    await page.route("**/api/movie-studio/projects/*/clip-segmentation-preview", async (route) => {
      if (!acceptPreviewRequests) {
        failedPreviewRequests += 1;
        await route.fulfill({
          status: 503,
          contentType: "application/json",
          body: JSON.stringify({ error: "Synthetic preview outage" }),
        });
        return;
      }
      retriedPreviewRequests += 1;
      await route.continue();
    });

    await page.goto("/create/movie");
    await page.getByRole("tab", { name: /full movie/i }).first().click();
    await page.getByLabel("Movie title").fill("E2E Read-only Preview Retry");
    await page.getByLabel("Describe your movie").fill(
      "Synthetic, provider-free transport recovery in the long-film planning view.",
    );
    await page.getByRole("button", { name: /create full project/i }).click();
    await expect(page).toHaveURL(/\/create\/movie\/[0-9a-f-]+\/overview$/i);

    const retry = page.getByRole("button", { name: "Retry preview" });
    await expect(retry).toBeVisible({ timeout: 20_000 });
    expect(failedPreviewRequests).toBeGreaterThanOrEqual(1);

    acceptPreviewRequests = true;
    await retry.click();
    await expect.poll(() => retriedPreviewRequests).toBeGreaterThanOrEqual(1);
    await expect(retry).toHaveCount(0, { timeout: 20_000 });
    expect(providerSubmissions).toEqual([]);
    await expect(page.locator("video")).toHaveCount(0);
  });

  test("persists a two-hour draft edit and locks duration after planning begins", async ({
    authenticatedPage: page,
  }) => {
    test.setTimeout(120_000);
    const providerSubmissions: string[] = [];
    page.on("request", (request) => {
      const path = new URL(request.url()).pathname;
      if (
        request.method() === "POST" &&
        /\/api\/(?:generation-jobs|movie-studio\/.*\/(?:generate|render|queue))(?:\/|$)/i.test(path)
      ) providerSubmissions.push(path);
    });

    await page.goto("/create/movie");
    await page.getByRole("tab", { name: /full movie/i }).first().click();
    await page.getByLabel("Movie title").fill("E2E Draft Runtime Lock");
    await page.getByLabel("Describe your movie").fill(
      "Provider-free acceptance test for a long-form project runtime.",
    );
    await page.getByRole("button", { name: /create full project/i }).click();
    await expect(page).toHaveURL(/\/create\/movie\/[0-9a-f-]+\/overview$/i);

    const projectId = page.url().match(/\/create\/movie\/([0-9a-f-]+)\/overview$/i)?.[1];
    expect(projectId).toBeTruthy();
    const input = page.getByLabel("Duration (seconds)", { exact: true });
    await expect(input).toBeVisible({ timeout: 20_000 });
    await expect(input).toHaveAttribute("max", "7200");

    await input.fill("7200");
    const saved = page.waitForResponse(
      (response) =>
        response.request().method() === "PATCH" &&
        new URL(response.url()).pathname.endsWith(
          `/api/movie-studio/projects/${projectId}/duration`,
        ),
    );
    await page.getByRole("button", { name: "Save duration" }).click();
    expect((await saved).status()).toBe(200);
    await expect(page.getByLabel("Duration (seconds)", { exact: true }))
      .toHaveValue("7200");

    await page.reload();
    await expect(page.getByLabel("Duration (seconds)", { exact: true }))
      .toHaveValue("7200");
    await expect(page.getByRole("heading", { name: "E2E Draft Runtime Lock", level: 1 }))
      .toBeVisible();

    // Creating a scene locks the duration in the UI. The API independently
    // enforces the same lock even if a client omits the disabled editor.
    await page.getByRole("link", { name: "Scenes", exact: true }).click();
    await page.getByPlaceholder("Scene title").fill("First scene");
    await page.getByPlaceholder("One-line scene intent").fill(
      "Begin the feature-length story without generating footage.",
    );
    await page.getByRole("button", { name: "Add scene", exact: true }).click();
    await expect(page.getByText("Add shot to scene")).toBeVisible();
    await page.getByRole("link", { name: "Overview", exact: true }).click();
    await expect(page.getByText(/The runtime is locked after scene planning/i))
      .toBeVisible();
    await expect(page.getByLabel("Duration (seconds)", { exact: true }))
      .toHaveCount(0);

    const persisted = await apiJson<{
      id: string;
      mode: string;
      durationSeconds: number;
      scenes: unknown[];
      clips: unknown[];
    }>(page.request, "GET", `/api/movie-studio/projects/${projectId}`);
    expect(persisted).toMatchObject({
      id: projectId,
      mode: "Full",
      durationSeconds: 7200,
    });
    expect(persisted.scenes).toHaveLength(1);
    expect(persisted.clips).toEqual([]);
    expect(providerSubmissions).toEqual([]);
    await expect(page.locator("video")).toHaveCount(0);
  });
});
