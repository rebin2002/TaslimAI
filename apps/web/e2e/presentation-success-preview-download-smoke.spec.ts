import { readFile } from "node:fs/promises";
import type { Route } from "@playwright/test";
import type { GenerationJob } from "../src/lib/api";
import { apiJson, E2E_API_URL, expect, registerInUi, test } from "./fixtures";

const jobId = "00000000-0000-4000-8000-000000000131";
const assetId = "00000000-0000-4000-8000-000000000132";
const representationId = "00000000-0000-4000-8000-000000000133";
const title = "Taslim Launch QA Presentation";
const description = "A synthetic presentation preview and download journey.";
const secondSlide = "Workspace access";
const privateProviderMarker = "PRESENTATION_PROVIDER_DIAGNOSTIC_MUST_NOT_RENDER";
const fileName = "taslim-private-qa.pptx";
const timestamp = "2026-10-30T00:00:00.000Z";

const pptxFixture = Buffer.from("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", "base64");

function isLoopbackHttpUrl(value: string | undefined) {
  try {
    const url = new URL(value ?? "");
    return url.protocol === "http:" && ["localhost", "127.0.0.1", "::1", "[::1]"].includes(url.hostname);
  } catch {
    return false;
  }
}

const safeTargetsOnly =
  isLoopbackHttpUrl(E2E_API_URL) &&
  isLoopbackHttpUrl(process.env.E2E_WEB_URL ?? "http://localhost:3000") &&
  (!process.env.NEXT_PUBLIC_API_URL || isLoopbackHttpUrl(process.env.NEXT_PUBLIC_API_URL));

function presentationJob(workspaceId: string, status: GenerationJob["status"], resultJson: string | null = null): GenerationJob {
  const succeeded = status === "Succeeded";
  return {
    id: jobId,
    workspaceId,
    projectId: null,
    jobType: "presentation.generate",
    status,
    title,
    progressPercent: succeeded ? 100 : 25,
    resultJson,
    errorCode: null,
    errorMessage: null,
    cancellationRequested: false,
    createdAt: timestamp,
    queuedAt: timestamp,
    startedAt: succeeded ? timestamp : null,
    completedAt: succeeded ? timestamp : null,
    failedAt: null,
    cancelledAt: null,
    outputs: [],
  };
}

async function respondToOptions(route: Route, corsHeaders: Record<string, string>) {
  await route.fulfill({ status: 204, headers: corsHeaders });
}

test("authenticated Presentation success previews slides and downloads exact PPTX bytes", async ({ page, testUser }) => {
  test.skip(process.env.GITHUB_ACTIONS !== "true" || process.env.CI !== "true", "Requires disposable Taslim CI browser infrastructure.");
  expect(process.env.GITHUB_WORKFLOW).toBe("Taslim CI");
  expect(safeTargetsOnly).toBe(true);
  expect(pptxFixture.subarray(0, 2).toString("ascii")).toBe("PK");

  const apiOrigin = new URL(E2E_API_URL).origin;
  const webOrigin = new URL(process.env.E2E_WEB_URL ?? "http://localhost:3000").origin;
  const corsHeaders = {
    "Access-Control-Allow-Origin": webOrigin,
    "Access-Control-Allow-Credentials": "true",
    "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
    "Access-Control-Allow-Headers": "content-type,x-csrf-token,idempotency-key",
    "Access-Control-Expose-Headers": "Content-Disposition",
    Vary: "Origin",
  };
  const blockedExternalRequests: string[] = [];
  await page.route("**/*", async (route) => {
    if (isLoopbackHttpUrl(route.request().url())) {
      await route.fallback();
      return;
    }
    blockedExternalRequests.push(route.request().method() + " " + new URL(route.request().url()).origin);
    await route.abort("blockedbyclient");
  });

  let registrationAttempted = false;
  try {
    registrationAttempted = true;
    await registerInUi(page, testUser);
    const auth = await apiJson<{ personalWorkspace: { id: string } }>(page.request, "GET", "/api/auth/me");
    const workspaceId = auth.personalWorkspace.id;
    expect(workspaceId).toBeTruthy();

    let createRequests = 0;
    let pollRequests = 0;
    let downloadRequests = 0;
    let submittedPayload: Record<string, unknown> | undefined;
    let createHasCookie = false;
    let pollHasCookie = false;
    let downloadHasCookie = false;

    await page.route((url) => url.origin === apiOrigin && url.pathname === "/api/presentation-generation/jobs", async (route) => {
      const method = route.request().method();
      if (method === "OPTIONS") {
        await respondToOptions(route, corsHeaders);
        return;
      }
      if (method !== "POST") {
        await route.abort();
        return;
      }
      const headers = await route.request().allHeaders();
      expect(headers.cookie).toBeTruthy();
      expect(headers.origin).toBe(webOrigin);
      expect(headers["x-csrf-token"]).toBeTruthy();
      createHasCookie = true;
      createRequests += 1;
      submittedPayload = route.request().postDataJSON() as Record<string, unknown>;
      expect(submittedPayload).toMatchObject({
        workspaceId,
        projectId: null,
        title,
        description,
        attachmentIds: [],
      });
      expect(submittedPayload).not.toHaveProperty("provider");
      expect(submittedPayload).not.toHaveProperty("model");
      expect(submittedPayload).not.toHaveProperty("apiKey");
      await route.fulfill({
        status: 202,
        headers: { ...corsHeaders, "Content-Type": "application/json" },
        body: JSON.stringify({ job: presentationJob(workspaceId, "Queued") }),
      });
    });

    await page.route((url) => url.origin === apiOrigin && url.pathname === "/api/generation/jobs/" + jobId, async (route) => {
      const method = route.request().method();
      if (method === "OPTIONS") {
        await respondToOptions(route, corsHeaders);
        return;
      }
      if (method !== "GET") {
        await route.abort();
        return;
      }
      const headers = await route.request().allHeaders();
      expect(headers.cookie).toBeTruthy();
      expect(headers.origin).toBe(webOrigin);
      pollHasCookie = true;
      pollRequests += 1;
      const resultJson = JSON.stringify({
        assetId,
        title,
        presentationType: "business",
        language: "en",
        slideCount: 2,
        previewSlides: [
          { order: 1, type: "title", title, subtitle: "Synthetic fixture only", blocks: [] },
          { order: 2, type: "content", title: secondSlide, subtitle: "Private workspace", blocks: [
            { type: "paragraph", text: "Private slide content for release QA." },
          ] },
        ],
        representations: [{
          id: representationId,
          type: "pptx",
          fileName,
          contentType: "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        }],
        provider: privateProviderMarker,
        providerMetadata: { diagnostic: privateProviderMarker },
      });
      await route.fulfill({
        status: 200,
        headers: { ...corsHeaders, "Content-Type": "application/json" },
        body: JSON.stringify(presentationJob(workspaceId, "Succeeded", resultJson)),
      });
    });

    // This synthetic route checks the browser contract. Real owner/other-user authorization
    // and PPTX representation signatures are independently covered by LaunchArtifactDownloadSmokeTests.cs.
    await page.route((url) => url.origin === apiOrigin && url.pathname === "/api/assets/" + assetId + "/representations/" + representationId + "/download", async (route) => {
      const method = route.request().method();
      if (method === "OPTIONS") {
        await respondToOptions(route, corsHeaders);
        return;
      }
      if (method !== "GET") {
        await route.abort();
        return;
      }
      const headers = await route.request().allHeaders();
      expect(headers.cookie).toBeTruthy();
      expect(headers.origin).toBe(webOrigin);
      downloadHasCookie = true;
      downloadRequests += 1;
      await route.fulfill({
        status: 200,
        headers: {
          ...corsHeaders,
          "Content-Type": "application/vnd.openxmlformats-officedocument.presentationml.presentation",
          "Content-Disposition": 'attachment; filename="' + fileName + '"',
          "Cache-Control": "no-store",
        },
        body: pptxFixture,
      });
    });

    await page.goto("/create/presentation");
    await expect(page.getByRole("heading", { name: /presentation studio/i })).toBeVisible();
    await page.getByLabel("Deck title").fill(title);
    await page.getByLabel("Tell us what to present").fill(description);

    const createResponse = page.waitForResponse((response) =>
      response.request().method() === "POST" && response.url().startsWith(apiOrigin + "/api/presentation-generation/jobs")
    );
    const pollResponse = page.waitForResponse((response) =>
      response.request().method() === "GET" && response.url().startsWith(apiOrigin + "/api/generation/jobs/" + jobId)
    );
    await page.getByRole("button", { name: "Create presentation" }).click();
    expect((await createResponse).status()).toBe(202);
    expect((await pollResponse).status()).toBe(200);

    const workspace = page.locator(".presentation-output-workspace");
    await expect(workspace).toBeVisible({ timeout: 20_000 });
    await expect(workspace.locator(".presentation-output-toolbar h2")).toHaveText(title);
    await expect(workspace.locator(".presentation-active-slide h3")).toHaveText(title);
    await expect(workspace.locator(".slide-thumbnail")).toHaveCount(2);
    await workspace.locator(".slide-thumbnail").nth(1).click();
    await expect(workspace.locator(".presentation-active-slide h3")).toHaveText(secondSlide);
    await expect(workspace.locator(".presentation-active-slide")).toContainText("Private slide content for release QA.");
    await expect(page.locator("body")).not.toContainText(privateProviderMarker);

    const downloadButton = workspace.getByRole("button", { name: "PPTX", exact: true });
    await expect(downloadButton).toBeEnabled();
    const downloadPromise = page.waitForEvent("download");
    await downloadButton.click();
    const download = await downloadPromise;
    expect(download.suggestedFilename()).toBe(fileName);
    const downloadPath = await download.path();
    expect(downloadPath).toBeTruthy();
    expect(await readFile(downloadPath!)).toEqual(pptxFixture);

    expect(submittedPayload).toBeDefined();
    expect(createHasCookie).toBe(true);
    expect(pollHasCookie).toBe(true);
    expect(downloadHasCookie).toBe(true);
    expect(createRequests).toBe(1);
    expect(pollRequests).toBeGreaterThan(0);
    expect(downloadRequests).toBe(1);
    expect(blockedExternalRequests).toEqual([]);
  } finally {
    if (registrationAttempted) {
      await apiJson(page.request, "POST", "/api/auth/logout").catch(() => undefined);
    }
  }
});
