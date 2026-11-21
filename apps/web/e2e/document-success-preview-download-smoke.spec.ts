import { readFile } from "node:fs/promises";
import type { Route } from "@playwright/test";
import type { GenerationJob } from "../src/lib/api";
import { apiJson, E2E_API_URL, expect, registerInUi, test } from "./fixtures";

const documentJobId = "00000000-0000-4000-8000-000000000127";
const assetId = "00000000-0000-4000-8000-000000000128";
const representationId = "00000000-0000-4000-8000-000000000129";
const documentTitle = "Authenticated Launch QA Document";
const brief = "A deterministic document for authenticated preview and PDF download regression coverage.";
const summary = "A private launch checklist rendered from a completed synthetic job.";
const sectionText = "The preview is scoped to the signed-in workspace.";
const privateProviderMarker = "DOCUMENT_PROVIDER_PRIVATE_MARKER_SHOULD_NOT_RENDER";
const representationFileName = "taslim-private-qa.pdf";
const fixtureTimestamp = "2026-10-30T00:00:00.000Z";

function createPdfFixture() {
  const stream = "BT /F1 12 Tf 20 200 Td (Taslim QA PDF fixture) Tj ET\n";
  const objects = [
    "<< /Type /Catalog /Pages 2 0 R >>",
    "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
    "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 320 240] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
    `<< /Length ${Buffer.byteLength(stream, "ascii")} >>\nstream\n${stream}endstream`,
    "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
  ];

  let pdf = "%PDF-1.4\n";
  const offsets = [0];
  objects.forEach((object, index) => {
    offsets.push(Buffer.byteLength(pdf, "ascii"));
    pdf += `${index + 1} 0 obj\n${object}\nendobj\n`;
  });
  const xrefOffset = Buffer.byteLength(pdf, "ascii");
  pdf += "xref\n0 6\n0000000000 65535 f \n";
  for (const offset of offsets.slice(1)) pdf += `${String(offset).padStart(10, "0")} 00000 n \n`;
  pdf += `trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n${xrefOffset}\n%%EOF\n`;
  return Buffer.from(pdf, "ascii");
}

const pdfFixture = createPdfFixture();

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

function documentJob(workspaceId: string, status: GenerationJob["status"], resultJson: string | null = null): GenerationJob {
  const succeeded = status === "Succeeded";
  return {
    id: documentJobId,
    workspaceId,
    projectId: null,
    jobType: "document.generate",
    status,
    title: documentTitle,
    progressPercent: succeeded ? 100 : 25,
    resultJson,
    errorCode: null,
    errorMessage: null,
    cancellationRequested: false,
    createdAt: fixtureTimestamp,
    queuedAt: fixtureTimestamp,
    startedAt: succeeded ? fixtureTimestamp : null,
    completedAt: succeeded ? fixtureTimestamp : null,
    failedAt: null,
    cancelledAt: null,
    outputs: [],
  };
}

async function fulfillCorsOptions(route: Route, corsHeaders: Record<string, string>) {
  await route.fulfill({ status: 204, headers: corsHeaders });
}

test("authenticated Document success renders a private preview and downloads exact PDF bytes", async ({ page, testUser }) => {
  test.skip(process.env.GITHUB_ACTIONS !== "true" || process.env.CI !== "true", "Requires the disposable Taslim CI browser harness.");
  expect(process.env.GITHUB_WORKFLOW).toBe("Taslim CI");
  expect(safeTestTargetsOnly).toBe(true);

  const loopbackHosts = new Set(["localhost", "127.0.0.1", "::1", "[::1]"]);
  const apiUrl = new URL(E2E_API_URL);
  const webUrl = new URL(process.env.E2E_WEB_URL ?? "http://localhost:3000");
  const apiOrigin = apiUrl.origin;
  const webOrigin = webUrl.origin;
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
    const url = new URL(route.request().url());
    if (url.protocol === "http:" && loopbackHosts.has(url.hostname)) {
      await route.fallback();
      return;
    }
    blockedExternalRequests.push(`${route.request().method()} ${url.origin}`);
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
    let createHadSessionCookie = false;
    let pollHadSessionCookie = false;
    let downloadHadSessionCookie = false;
    let submittedPayload: Record<string, unknown> | undefined;

    await page.route((url) => url.origin === apiOrigin && url.pathname === "/api/document-generation/jobs", async (route) => {
      const method = route.request().method();
      if (method === "OPTIONS") {
        await fulfillCorsOptions(route, corsHeaders);
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
      createHadSessionCookie = true;
      createRequests += 1;
      submittedPayload = route.request().postDataJSON() as Record<string, unknown>;
      expect(submittedPayload).toMatchObject({
        workspaceId,
        projectId: null,
        description: brief,
        outputFormat: "pdf",
        attachmentIds: [],
      });
      expect(submittedPayload).not.toHaveProperty("provider");
      expect(submittedPayload).not.toHaveProperty("model");
      expect(submittedPayload).not.toHaveProperty("apiKey");
      expect(submittedPayload).not.toHaveProperty("secret");
      await route.fulfill({
        status: 202,
        headers: { ...corsHeaders, "Content-Type": "application/json" },
        body: JSON.stringify({ job: documentJob(workspaceId, "Queued") }),
      });
    });

    await page.route((url) => url.origin === apiOrigin && url.pathname === `/api/generation/jobs/${documentJobId}`, async (route) => {
      const method = route.request().method();
      if (method === "OPTIONS") {
        await fulfillCorsOptions(route, corsHeaders);
        return;
      }
      if (method !== "GET") {
        await route.abort();
        return;
      }

      const headers = await route.request().allHeaders();
      expect(headers.cookie).toBeTruthy();
      expect(headers.origin).toBe(webOrigin);
      pollHadSessionCookie = true;
      pollRequests += 1;
      const resultJson = JSON.stringify({
        assetId,
        documentType: "document",
        title: documentTitle,
        language: "en",
        summary,
        sections: [{ heading: "Workspace access", blocks: [{ type: "paragraph", text: sectionText }] }],
        representations: [{
          id: representationId,
          type: "pdf",
          fileName: representationFileName,
          contentType: "application/pdf",
        }],
        provider: privateProviderMarker,
        providerMetadata: { diagnostic: privateProviderMarker },
      });
      await route.fulfill({
        status: 200,
        headers: { ...corsHeaders, "Content-Type": "application/json" },
        body: JSON.stringify(documentJob(workspaceId, "Succeeded", resultJson)),
      });
    });

    // The route is a deterministic browser fixture and requires the authenticated cookie.
    // Real owner-vs-other-user asset authorization and PDF signatures are covered by
    // LaunchArtifactDownloadSmokeTests.cs in this same PR.
    await page.route((url) => url.origin === apiOrigin && url.pathname === `/api/assets/${assetId}/representations/${representationId}/download`, async (route) => {
      const method = route.request().method();
      if (method === "OPTIONS") {
        await fulfillCorsOptions(route, corsHeaders);
        return;
      }
      if (method !== "GET") {
        await route.abort();
        return;
      }

      const headers = await route.request().allHeaders();
      expect(headers.cookie).toBeTruthy();
      expect(headers.origin).toBe(webOrigin);
      downloadHadSessionCookie = true;
      downloadRequests += 1;
      await route.fulfill({
        status: 200,
        headers: {
          ...corsHeaders,
          "Content-Type": "application/pdf",
          "Content-Disposition": `attachment; filename="${representationFileName}"`,
          "Cache-Control": "no-store",
        },
        body: pdfFixture,
      });
    });

    await page.goto("/create/document");
    await expect(page.getByRole("heading", { name: /document studio/i })).toBeVisible();
    await expect(page.locator("#document-project")).toBeEnabled({ timeout: 15_000 });
    const prompt = page.getByLabel(/what would you like taslim to create/i);
    await prompt.fill(brief);
    await page.getByLabel(/output format/i).selectOption("pdf");

    const createResponse = page.waitForResponse((response) => {
      const url = new URL(response.url());
      return response.request().method() === "POST" && url.origin === apiOrigin && url.pathname === "/api/document-generation/jobs";
    });
    const pollResponse = page.waitForResponse((response) => {
      const url = new URL(response.url());
      return response.request().method() === "GET" && url.origin === apiOrigin && url.pathname === `/api/generation/jobs/${documentJobId}`;
    });
    await page.getByRole("button", { name: /generate document/i }).click();
    expect((await createResponse).status()).toBe(202);
    expect((await pollResponse).status()).toBe(200);

    const workspace = page.locator(".document-workspace");
    await expect(workspace).toBeVisible({ timeout: 20_000 });
    const reader = workspace.locator(".document-reader");
    await expect(reader.getByRole("heading", { name: documentTitle, exact: true })).toBeVisible();
    await expect(reader.locator(".document-reader-summary")).toHaveText(summary);
    await expect(reader.getByRole("heading", { name: /Workspace access/ })).toBeVisible();
    await expect(reader.getByText(sectionText, { exact: true })).toBeVisible();
    await expect(page.locator("body")).not.toContainText(privateProviderMarker);
    expect(submittedPayload).toBeDefined();
    expect(createHadSessionCookie).toBe(true);
    expect(pollHadSessionCookie).toBe(true);

    const downloadButton = workspace.locator(".document-action-stack").getByRole("button", { name: /PDF$/i });
    await expect(downloadButton).toBeEnabled();
    const downloadPromise = page.waitForEvent("download");
    await downloadButton.click();
    const download = await downloadPromise;
    expect(download.suggestedFilename()).toBe(representationFileName);
    const downloadPath = await download.path();
    expect(downloadPath).toBeTruthy();
    expect(await readFile(downloadPath!)).toEqual(pdfFixture);
    expect(pdfFixture.subarray(0, 5).toString("ascii")).toBe("%PDF-");
    expect(downloadRequests).toBe(1);
    expect(downloadHadSessionCookie).toBe(true);
    expect(pollRequests).toBeGreaterThan(0);
    expect(createRequests).toBe(1);
    expect(blockedExternalRequests).toEqual([]);
  } finally {
    if (registrationAttempted) {
      await apiJson(page.request, "POST", "/api/auth/logout").catch(() => undefined);
    }
  }
});
