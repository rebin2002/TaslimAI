import { readFile } from "node:fs/promises";
import type { GenerationJob } from "../src/lib/api";
import { apiJson, E2E_API_URL, expect, registerInUi, test } from "./fixtures";

const failedJobId = "e2e-image-provider-failure";
const successJobId = "e2e-image-provider-free-success";
const assetId = "a694f298-5b2d-4c6a-98bc-6271ee5b5b4d";
const privateProviderMarker = "PRIVATE_PROVIDER_DIAGNOSTIC_MUST_NOT_RENDER";
const generatedPng = Buffer.from(
  "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=",
  "base64",
);

function isLoopbackHttpUrl(value: string | undefined) {
  try {
    const url = new URL(value ?? "");
    return url.protocol === "http:" && ["localhost", "127.0.0.1", "::1", "[::1]"].includes(url.hostname);
  } catch {
    return false;
  }
}

const safeTestTargetsOnly =
  isLoopbackHttpUrl(E2E_API_URL) &&
  isLoopbackHttpUrl(process.env.E2E_WEB_URL ?? "http://localhost:3000") &&
  (!process.env.NEXT_PUBLIC_API_URL || isLoopbackHttpUrl(process.env.NEXT_PUBLIC_API_URL));

function imageJob(
  workspaceId: string,
  id: string,
  status: GenerationJob["status"],
  resultJson: string | null = null,
  errorCode: string | null = null,
  errorMessage: string | null = null,
): GenerationJob {
  const now = "2026-10-30T00:00:00.000Z";
  const failed = status === "Failed";
  const succeeded = status === "Succeeded";
  return {
    id,
    workspaceId,
    projectId: null,
    jobType: "image.generate",
    status,
    title: "QA Image Failure Recovery",
    progressPercent: failed || succeeded ? 100 : 0,
    resultJson,
    errorCode,
    errorMessage,
    cancellationRequested: false,
    createdAt: now,
    queuedAt: now,
    startedAt: failed || succeeded ? now : null,
    completedAt: succeeded ? now : null,
    failedAt: failed ? now : null,
    cancelledAt: null,
    outputs: [],
  };
}

test("Image Studio safely recovers from a failed job and downloads the successful local fixture", async ({ page, testUser }) => {
  test.skip(!safeTestTargetsOnly, "Runs only against loopback HTTP web/API targets; it must never register against a remote service.");

  const blockedExternalRequests: string[] = [];
  await page.route("**/*", async (route) => {
    if (!isLoopbackHttpUrl(route.request().url())) {
      blockedExternalRequests.push(`${route.request().method()} ${route.request().url()}`);
      await route.abort("blockedbyclient");
      return;
    }
    await route.fallback();
  });

  await registerInUi(page, testUser);
  const auth = await apiJson<{ personalWorkspace: { id: string } }>(page.request, "GET", "/api/auth/me");
  const workspaceId = auth.personalWorkspace.id;
  const description = "A clean, minimal product image for a synthetic launch QA journey.";
  const webOrigin = new URL(process.env.E2E_WEB_URL ?? "http://localhost:3000").origin;
  const apiOrigin = new URL(E2E_API_URL).origin;
  const createRequests: Array<{
    body: Record<string, unknown>;
    csrfToken: string | undefined;
    idempotencyKey: string | undefined;
  }> = [];
  let failedPollReads = 0;
  let successPollReads = 0;
  let previewReads = 0;
  let downloadReads = 0;

  await page.route("**/api/image-generation/jobs", async (route) => {
    const request = route.request();
    if (request.method() !== "POST") {
      await route.fallback();
      return;
    }

    const url = new URL(request.url());
    expect(url.pathname).toBe("/api/image-generation/jobs");
    expect(isLoopbackHttpUrl(request.url())).toBe(true);
    const body = request.postDataJSON() as Record<string, unknown>;
    createRequests.push({
      body,
      csrfToken: request.headers()["x-csrf-token"],
      idempotencyKey: request.headers()["idempotency-key"],
    });

    if (createRequests.length === 1) {
      await route.fulfill({
        status: 202,
        contentType: "application/json",
        body: JSON.stringify({ job: imageJob(workspaceId, failedJobId, "Queued") }),
      });
      return;
    }
    if (createRequests.length === 2) {
      await route.fulfill({
        status: 202,
        contentType: "application/json",
        body: JSON.stringify({ job: imageJob(workspaceId, successJobId, "Queued") }),
      });
      return;
    }

    await route.fulfill({ status: 500, contentType: "application/json", body: "Unexpected extra image submission" });
  });

  await page.route(`**/api/generation/jobs/${failedJobId}`, async (route) => {
    if (route.request().method() !== "GET") {
      await route.fallback();
      return;
    }
    failedPollReads += 1;
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify(imageJob(workspaceId, failedJobId, "Failed", null, "IMAGE_PROVIDER_UNAVAILABLE", privateProviderMarker)),
    });
  });

  await page.route(`**/api/generation/jobs/${successJobId}`, async (route) => {
    if (route.request().method() !== "GET") {
      await route.fallback();
      return;
    }
    successPollReads += 1;
    const resultJson = JSON.stringify({
      assetId,
      assetType: "image",
      format: "png",
      width: 1,
      height: 1,
      aspectRatio: "square",
      quality: "standard",
      provider: privateProviderMarker,
      providerModel: privateProviderMarker,
    });
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify(imageJob(workspaceId, successJobId, "Succeeded", resultJson)),
    });
  });

  await page.route(`**/api/assets/${assetId}/download**`, async (route) => {
    const url = new URL(route.request().url());
    if (url.pathname !== `/api/assets/${assetId}/download` || route.request().method() !== "GET") {
      await route.fallback();
      return;
    }

    if (url.searchParams.get("inline") === "true") {
      previewReads += 1;
      await route.fulfill({
        status: 200,
        contentType: "image/png",
        headers: {
          "access-control-allow-origin": webOrigin,
          "access-control-allow-credentials": "true",
        },
        body: generatedPng,
      });
      return;
    }

    downloadReads += 1;
    await route.fulfill({
      status: 200,
      contentType: "image/png",
      headers: { "content-disposition": 'attachment; filename="taslim-e2e-image.png"' },
      body: generatedPng,
    });
  });

  let localCspAdjusted = false;
  // CI serves its API over HTTP loopback; adapt only this test page response, never app CSP.
  await page.route("**/create/image", async (route) => {
    // Preserve the synthetic browser session when fetching the page response.
    const requestHeaders = await route.request().allHeaders();
    const sessionCookie = requestHeaders.cookie;
    if (!sessionCookie) {
      await route.abort();
      throw new Error("Expected the synthetic auth cookie on the loopback page request.");
    }
    const response = await route.fetch({ headers: { cookie: sessionCookie } });
    const headers = response.headers();
    const imagePolicy = "img-src 'self' data: blob: https:";
    const csp = headers["content-security-policy"];
    expect(csp).toContain(imagePolicy);
    headers["content-security-policy"] = csp.replace(imagePolicy, `${imagePolicy} ${apiOrigin}`);
    await route.fulfill({ response, headers });
    localCspAdjusted = true;
  });

  await page.goto("/create/image");
  expect(localCspAdjusted).toBe(true);
  const prompt = page.getByRole("textbox", { name: "What would you like to create?" });
  await expect(prompt).toBeVisible();
  await prompt.fill(description);
  await page.getByRole("button", { name: "Generate image" }).click();

  const failureCanvas = page.locator(".image-failure-canvas");
  await expect(failureCanvas).toBeVisible();
  await expect(failureCanvas).toContainText("We couldn't finish that image.");
  await expect(failureCanvas).toContainText("The image service is temporarily unavailable.");
  await expect(page.locator("body")).not.toContainText(privateProviderMarker);
  expect(createRequests).toHaveLength(1);
  expect(failedPollReads).toBeGreaterThan(0);
  expect(createRequests[0]).toMatchObject({
    body: {
      workspaceId,
      projectId: null,
      description,
      style: "product",
      aspectRatio: "square",
      quality: "standard",
    },
  });
  expect(createRequests[0].csrfToken).toBeTruthy();
  expect(createRequests[0].idempotencyKey).toBeTruthy();
  expect(createRequests[0].body).not.toHaveProperty("provider");
  expect(createRequests[0].body).not.toHaveProperty("model");
  expect(createRequests[0].body).not.toHaveProperty("apiKey");
  expect(createRequests[0].body).not.toHaveProperty("secret");

  await failureCanvas.getByRole("button", { name: "Try again" }).click();
  await expect(page.locator(".image-success-canvas")).toBeVisible({ timeout: 30_000 });
  await expect(page.getByRole("heading", { name: "Ready to use" })).toBeVisible();
  await expect.poll(() => createRequests.length).toBe(2);
  expect(createRequests[1]).toMatchObject({
    body: { workspaceId, projectId: null, description },
  });
  expect(createRequests[1].csrfToken).toBeTruthy();
  expect(createRequests[1].idempotencyKey).toBeTruthy();
  expect(successPollReads).toBeGreaterThan(0);

  const resultImage = page.locator(".image-result-frame img");
  await expect(resultImage).toHaveAttribute("alt", description);
  await expect.poll(() => resultImage.evaluate((image) => (image as HTMLImageElement).naturalWidth)).toBe(1);
  expect(previewReads).toBeGreaterThan(0);
  await expect(page.locator("body")).not.toContainText(privateProviderMarker);

  const downloadLink = page.getByRole("link", { name: "Download", exact: true });
  await expect(downloadLink).toHaveAttribute("href", new RegExp(`/api/assets/${assetId}/download$`));
  const downloadPromise = page.waitForEvent("download");
  await downloadLink.click();
  const download = await downloadPromise;
  expect(download.suggestedFilename()).toBe("taslim-e2e-image.png");
  const downloadPath = await download.path();
  expect(downloadPath).toBeTruthy();
  expect(await readFile(downloadPath!)).toEqual(generatedPng);
  expect(downloadReads).toBe(1);
  expect(blockedExternalRequests).toEqual([]);
  expect(createRequests).toHaveLength(2);
});
