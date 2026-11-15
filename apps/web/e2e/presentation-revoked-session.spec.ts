import { test, expect, apiJson } from "./fixtures";

// Provider-free authenticated acceptance. Intercept only a synthetic saved job:
// this test must never POST to a real presentation-generation endpoint.
test("revoked Presentation session clears private preview and stops protected polling", async ({ authenticatedPage: page }) => {
  test.setTimeout(60_000);
  const auth = await apiJson<{ user: { id: string }; personalWorkspace: { id: string } }>(page.request, "GET", "/api/auth/me");
  const jobId = "synthetic-revoked-presentation-job";
  const storageKey = `taslim:presentation-generation:${encodeURIComponent(auth.user.id)}:${encodeURIComponent(auth.personalWorkspace.id)}`;
  await page.evaluate(([key, value]) => sessionStorage.setItem(key, value), [storageKey, jobId]);

  let revoked = false;
  let deniedResponses = 0;
  let privateJobGets = 0;
  let generationPosts = 0;
  page.on("request", (request) => {
    if (request.method() === "POST" && /\/api\/(presentation-generation|generation)\/jobs(?:\?|$)/.test(request.url())) generationPosts++;
  });
  await page.route(`**/api/generation/jobs/${jobId}`, async (route) => {
    privateJobGets++;
    if (revoked) {
      deniedResponses++;
      await route.fulfill({ status: 401, contentType: "application/json", body: JSON.stringify({ error: { code: "AUTH_REQUIRED", message: "Session expired" } }) });
      return;
    }
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        id: jobId,
        workspaceId: auth.personalWorkspace.id,
        projectId: null,
        jobType: "presentation.generate",
        status: "Running",
        title: "SYNTHETIC_PRIVATE_DECK",
        progressPercent: 25,
        resultJson: null,
        errorCode: null,
        errorMessage: null,
        cancellationRequested: false,
        createdAt: "2026-11-15T00:00:00Z",
        queuedAt: "2026-11-15T00:00:00Z",
        startedAt: "2026-11-15T00:00:01Z",
        completedAt: null,
        failedAt: null,
        cancelledAt: null,
        outputs: [],
      }),
    });
  });

  await page.goto("/create/presentation");
  await expect(page.locator(".presentation-progress-workspace")).toBeVisible();
  revoked = true;
  await expect.poll(() => deniedResponses, { timeout: 15_000 }).toBeGreaterThan(0);
  await expect(page.locator(".presentation-progress-workspace")).toHaveCount(0);
  await expect(page.locator(".presentation-compose-workspace")).toHaveCount(0);
  await expect(page.getByRole("alert")).toBeVisible();
  await expect.poll(async () => page.evaluate((key) => sessionStorage.getItem(key), storageKey)).toBeNull();
  const requestsAtRejection = privateJobGets;
  await page.waitForTimeout(5_000);
  expect(privateJobGets).toBe(requestsAtRejection);
  expect(generationPosts).toBe(0);
  await expect(page.getByText("SYNTHETIC_PRIVATE_DECK")).toHaveCount(0);
});
