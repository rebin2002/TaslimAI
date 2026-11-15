import { test, expect, apiJson } from "./fixtures";

// Provider-free browser acceptance: only a synthetic generation-job GET is intercepted.
// Never create a chargeable Image generation job in this test.
test("revoked Image job polling stops further private GETs and clears saved state", async ({ authenticatedPage: page }) => {
  test.setTimeout(60_000);
  const auth = await apiJson<{ user: { id: string }; personalWorkspace: { id: string } }>(page.request, "GET", "/api/auth/me");
  const jobId = "synthetic-revoked-image-job";
  const storageKey = `taslim:image-generation:${encodeURIComponent(auth.user.id)}:${encodeURIComponent(auth.personalWorkspace.id)}`;

  // sessionStorage survives same-origin navigation, but is never shared between principals.
  await page.evaluate(([key, value]) => sessionStorage.setItem(key, value), [storageKey, jobId]);

  let revoked = false;
  let deniedResponses = 0;
  let privateJobGets = 0;
  let generationPosts = 0;
  page.on("request", (request) => {
    if (request.method() === "POST" && /\/api\/(image-generation|generation)\/jobs(?:\?|$)/.test(request.url())) generationPosts++;
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
        jobType: "image.generate",
        status: "Running",
        title: "Synthetic image",
        progressPercent: 12,
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

  await page.goto("/create/image");
  await expect(page.locator(".image-generating-canvas")).toBeVisible();
  revoked = true;
  await expect.poll(() => deniedResponses, { timeout: 15_000 }).toBeGreaterThan(0);
  await expect(page.locator(".image-generating-canvas")).toHaveCount(0);
  await expect.poll(async () => page.evaluate((key) => sessionStorage.getItem(key), storageKey)).toBeNull();
  const countAtRejection = privateJobGets;
  // Old behavior retried a denied poll after 4–8 seconds and kept its private pointer.
  await page.waitForTimeout(9_000);
  expect(privateJobGets).toBe(countAtRejection);
  expect(generationPosts).toBe(0);
  await expect(page.locator(".image-recent-item")).toHaveCount(0);
});


// Provider-free mounted acceptance of explicit 403/404 orphan recovery. The
// synthetic job has no provider/output/ledger side effects; denial is injected
// only after a real mounted "Running" render.
for (const deniedStatus of [403, 404] as const) {
  test("Image Studio drops a revoked/orphaned active job on HTTP " + deniedStatus, async ({ authenticatedPage: page }) => {
    test.setTimeout(60_000);
    const auth = await apiJson<{ user: { id: string }; personalWorkspace: { id: string } }>(
      page.request, "GET", "/api/auth/me",
    );
    const jobId = "synthetic-orphan-" + deniedStatus;
    const storageKey = "taslim:image-generation:"
      + encodeURIComponent(auth.user.id) + ":" + encodeURIComponent(auth.personalWorkspace.id);
    await page.evaluate(([key, value]) => sessionStorage.setItem(key, value), [storageKey, jobId]);

    let deny = false;
    let deniedResponses = 0;
    let jobGets = 0;
    let chargedOrGenerated = 0;
    page.on("request", (request) => {
      if (request.method() === "POST" && /\/api\/(?:image-generation|generation)\/jobs(?:\?|$)/.test(request.url()))
        chargedOrGenerated++;
    });
    await page.route("**/api/generation/jobs/" + jobId, async (route) => {
      jobGets++;
      if (deny) {
        deniedResponses++;
        await route.fulfill({ status: deniedStatus, contentType: "application/json",
          body: JSON.stringify({ error: { code: "NOT_ACCESSIBLE", message: "Unavailable" } }) });
        return;
      }
      await route.fulfill({ status: 200, contentType: "application/json",
        body: JSON.stringify({
          id: jobId, workspaceId: auth.personalWorkspace.id, projectId: null,
          jobType: "image.generate", status: "Running", title: "Synthetic orphan",
          progressPercent: 5, resultJson: null, errorCode: null, errorMessage: null,
          cancellationRequested: false, createdAt: "2026-11-15T00:00:00Z",
          queuedAt: "2026-11-15T00:00:00Z", startedAt: "2026-11-15T00:00:01Z",
          completedAt: null, failedAt: null, cancelledAt: null, outputs: [],
        }) });
    });

    await page.goto("/create/image");
    await expect(page.locator(".image-generating-canvas")).toBeVisible();
    deny = true;
    await expect.poll(() => deniedResponses, { timeout: 15_000 }).toBeGreaterThan(0);
    await expect(page.locator(".image-generating-canvas")).toHaveCount(0);
    await expect.poll(async () => page.evaluate((key) => sessionStorage.getItem(key), storageKey)).toBeNull();
    const lastGetCount = jobGets;
    // A broken recovery path retries denied private GETs after 4–8 seconds.
    await page.waitForTimeout(9_000);
    expect(jobGets).toBe(lastGetCount);
    expect(chargedOrGenerated).toBe(0);
  });
}

// A transport/server failure must not be mistaken for an authorization loss:
// the existing bounded retry policy should keep an active job restorable.
test("Image Studio retains a running job across synthetic HTTP 500 polling retries", async ({ authenticatedPage: page }) => {
  test.setTimeout(60_000);
  const auth = await apiJson<{ user: { id: string }; personalWorkspace: { id: string } }>(
    page.request, "GET", "/api/auth/me",
  );
  const jobId = "synthetic-transient-image-500";
  const storageKey = "taslim:image-generation:"
    + encodeURIComponent(auth.user.id) + ":" + encodeURIComponent(auth.personalWorkspace.id);
  await page.evaluate(([key, value]) => sessionStorage.setItem(key, value), [storageKey, jobId]);

  let returnServerError = false;
  let serverErrors = 0;
  let chargedOrGenerated = 0;
  page.on("request", (request) => {
    if (request.method() === "POST" && /\/api\/(?:image-generation|generation)\/jobs(?:\?|$)/.test(request.url()))
      chargedOrGenerated++;
  });
  await page.route("**/api/generation/jobs/" + jobId, async (route) => {
    if (returnServerError) {
      serverErrors++;
      await route.fulfill({ status: 500, contentType: "application/json",
        body: JSON.stringify({ error: { code: "TEMPORARY_FAILURE" } }) });
      return;
    }
    await route.fulfill({ status: 200, contentType: "application/json",
      body: JSON.stringify({
        id: jobId, workspaceId: auth.personalWorkspace.id, projectId: null,
        jobType: "image.generate", status: "Running", title: "Synthetic retry",
        progressPercent: 9, resultJson: null, errorCode: null, errorMessage: null,
        cancellationRequested: false, createdAt: "2026-11-15T00:00:00Z",
        queuedAt: "2026-11-15T00:00:00Z", startedAt: "2026-11-15T00:00:01Z",
        completedAt: null, failedAt: null, cancelledAt: null, outputs: [],
      }) });
  });

  await page.goto("/create/image");
  await expect(page.locator(".image-generating-canvas")).toBeVisible();
  returnServerError = true;
  await expect.poll(() => serverErrors, { timeout: 20_000 }).toBeGreaterThanOrEqual(2);
  await expect(page.locator(".image-generating-canvas")).toBeVisible();
  expect(await page.evaluate((key) => sessionStorage.getItem(key), storageKey)).toBe(jobId);
  expect(chargedOrGenerated).toBe(0);
});
