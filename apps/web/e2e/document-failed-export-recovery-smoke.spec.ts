import { randomUUID } from "node:crypto";
import type { Route } from "@playwright/test";
import { E2E_API_URL, expect, registerInUi, test, type TestUser } from "./fixtures";
import type { GenerationJob } from "../src/lib/api";

const safeFailureMessage = "The PDF could not be prepared. Start a new document to try again.";

test("an accepted Document export failure is safe and the user can start a fresh job", async ({ page, testUser }) => {
  test.skip(process.env.GITHUB_ACTIONS !== "true" || process.env.CI !== "true", "Requires the disposable Taslim CI browser harness.");
  expect(process.env.GITHUB_WORKFLOW).toBe("Taslim CI");

  const loopbackHosts = new Set(["localhost", "127.0.0.1", "::1"]);
  const apiUrl = new URL(E2E_API_URL);
  const webUrl = new URL(process.env.E2E_WEB_URL ?? "http://localhost:3000");
  for (const url of [apiUrl, webUrl]) {
    expect(url.protocol).toBe("http:");
    expect(loopbackHosts.has(url.hostname)).toBe(true);
  }

  const blockedExternalRequests: string[] = [];
  const guardLoopbackTraffic = async (route: Route) => {
    const url = new URL(route.request().url());
    if (url.protocol === "http:" && loopbackHosts.has(url.hostname)) {
      await route.fallback();
      return;
    }
    blockedExternalRequests.push(`${route.request().method()} ${url.origin}`);
    await route.abort("blockedbyclient");
  };
  await page.route("**/*", guardLoopbackTraffic);

  const submissions: Array<{ payload: Record<string, unknown>; job: GenerationJob }> = [];
  const jobs = new Map<string, GenerationJob>();
  let firstJobId: string | null = null;
  const now = new Date().toISOString();

  await page.route("**/api/document-generation/jobs", async (route) => {
    const request = route.request();
    if (request.method() !== "POST") {
      await route.abort();
      return;
    }

    const payload = request.postDataJSON() as Record<string, unknown>;
    expect(payload.workspaceId).toBeTruthy();
    expect(payload.description).toBeTruthy();
    expect(request.headers()["x-csrf-token"]).toBeTruthy();

    const job: GenerationJob = {
      id: randomUUID(),
      workspaceId: String(payload.workspaceId),
      projectId: typeof payload.projectId === "string" ? payload.projectId : null,
      jobType: "document.generate",
      status: "Queued",
      title: typeof payload.title === "string" ? payload.title : null,
      progressPercent: 0,
      resultJson: null,
      errorCode: null,
      errorMessage: null,
      cancellationRequested: false,
      createdAt: now,
      queuedAt: now,
      startedAt: null,
      completedAt: null,
      failedAt: null,
      cancelledAt: null,
      outputs: [],
    };

    if (submissions.length === 0) firstJobId = job.id;
    submissions.push({ payload, job });
    jobs.set(job.id, job);
    await route.fulfill({
      status: 202,
      contentType: "application/json",
      body: JSON.stringify({ job }),
    });
  });

  await page.route("**/api/generation/jobs/*", async (route) => {
    if (route.request().method() !== "GET") {
      await route.abort();
      return;
    }

    const jobId = new URL(route.request().url()).pathname.split("/").at(-1) ?? "";
    const job = jobs.get(jobId);
    if (!job) {
      await route.abort();
      return;
    }

    const responseJob: GenerationJob = job.id === firstJobId
      ? {
        ...job,
        status: "Failed",
        progressPercent: 100,
        errorCode: "DOCUMENT_RENDER_FAILED",
        errorMessage: safeFailureMessage,
        completedAt: now,
        failedAt: now,
      }
      : job;
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify(responseJob),
    });
  });

  await registerInUi(page, testUser);
  await page.goto("/create/document");
  await expect(page.getByRole("heading", { name: /document studio/i })).toBeVisible();

  const brief = page.getByLabel(/what would you like taslim to create/i);
  await page.getByLabel(/output format/i).selectOption("pdf");
  await brief.fill("A deterministic brief whose export fails in the local test transport.");
  const firstCreateResponse = page.waitForResponse((response) => {
    const url = new URL(response.url());
    return response.request().method() === "POST"
      && url.origin === apiUrl.origin
      && url.pathname === "/api/document-generation/jobs";
  });
  await page.getByRole("button", { name: /generate document/i }).click();
  expect((await firstCreateResponse).status()).toBe(202);

  const terminalState = page.locator(".document-terminal-state");
  await expect(terminalState).toBeVisible();
  await expect(terminalState.getByRole("heading", { name: safeFailureMessage })).toBeVisible();
  await expect(terminalState).not.toContainText(/stack trace|exception|api.?key|secret|provider/i);
  await expect(terminalState.getByRole("button")).toHaveCount(1);
  expect(submissions).toHaveLength(1);
  expect(submissions[0].payload.description).toBe("A deterministic brief whose export fails in the local test transport.");

  await terminalState.getByRole("button").click();
  await expect(page.locator(".document-brief-card")).toBeVisible();
  await brief.fill("A fresh brief after the previous export failed.");
  const secondCreateResponse = page.waitForResponse((response) => {
    const url = new URL(response.url());
    return response.request().method() === "POST"
      && url.origin === apiUrl.origin
      && url.pathname === "/api/document-generation/jobs";
  });
  await page.getByRole("button", { name: /generate document/i }).click();
  expect((await secondCreateResponse).status()).toBe(202);

  await expect(page.locator(".document-terminal-state")).toHaveCount(0);
  await expect(page.locator(".document-state-card")).toBeVisible();
  expect(submissions).toHaveLength(2);
  expect(submissions[1].payload.description).toBe("A fresh brief after the previous export failed.");
  expect(submissions[1].payload.workspaceId).toBe(submissions[0].payload.workspaceId);
  expect(submissions[1].job.id).not.toBe(submissions[0].job.id);
  expect(blockedExternalRequests).toEqual([]);

  // The account and every browser/API endpoint in this test are disposable CI-local.
  // Both generation create and poll calls are fulfilled by the route mocks above.
  const _syntheticUser: TestUser = testUser;
  expect(_syntheticUser.email).toMatch(/@example\.test$/);
});
