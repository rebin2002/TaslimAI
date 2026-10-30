import type { GenerationJob } from "../src/lib/api";
import { test, expect, apiJson, E2E_API_URL } from "./fixtures";

const jobId = "e2e-presentation-failure-recovery";
const safeFailure = "This test presentation could not be created. Please try again.";

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

function presentationJob(workspaceId: string, status: GenerationJob["status"], errorMessage: string | null): GenerationJob {
  const failed = status === "Failed";
  const now = "2026-10-30T00:00:00.000Z";
  return {
    id: jobId,
    workspaceId,
    projectId: null,
    jobType: "presentation.generate",
    status,
    title: "QA Presentation Failure Journey",
    progressPercent: failed ? 100 : 0,
    resultJson: null,
    errorCode: failed ? "PRESENTATION_GENERATION_FAILED" : null,
    errorMessage,
    cancellationRequested: false,
    createdAt: now,
    queuedAt: status === "Queued" ? now : null,
    startedAt: failed ? now : null,
    completedAt: null,
    failedAt: failed ? now : null,
    cancelledAt: null,
    outputs: [],
  };
}

test.describe("Presentation Studio failed-job recovery", () => {
  test.skip(!safeTargetsOnly, "Runs only against loopback web/API targets; it must never register against a remote service.");

  test("restores a safe failed-job state after reload and lets the authenticated user recover", async ({ authenticatedPage: page }) => {
    const auth = await apiJson<{ personalWorkspace: { id: string } }>(page.request, "GET", "/api/auth/me");
    const workspaceId = auth.personalWorkspace.id;
    const storageKey = `taslim:presentation-generation:${workspaceId}`;
    const blockedExternalRequests: string[] = [];
    let submittedPayload: Record<string, unknown> | undefined;
    let csrfToken: string | undefined;
    let jobReads = 0;

    await page.route("**/*", async (route) => {
      if (!isLoopbackHttpUrl(route.request().url())) {
        blockedExternalRequests.push(route.request().url());
        await route.abort("blockedbyclient");
        return;
      }
      await route.fallback();
    });

    await page.route("**/api/presentation-generation/jobs", async (route) => {
      const request = route.request();
      submittedPayload = request.postDataJSON() as Record<string, unknown>;
      csrfToken = request.headers()["x-csrf-token"];
      await route.fulfill({
        status: 202,
        contentType: "application/json",
        body: JSON.stringify({ job: presentationJob(workspaceId, "Queued", null) }),
      });
    });

    await page.route(`**/api/generation/jobs/${jobId}`, async (route) => {
      jobReads += 1;
      await route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify(presentationJob(workspaceId, "Failed", safeFailure)),
      });
    });

    await page.goto("/create/presentation");
    await expect(page.getByRole("heading", { name: /presentation studio/i })).toBeVisible();
    await page.getByLabel("Deck title").fill("QA presentation failure recovery");
    await page.getByLabel("Tell us what to present").fill("A synthetic release QA journey.");
    await page.getByRole("button", { name: "Create presentation" }).click();

    const failureHeading = page.getByRole("heading", { name: safeFailure, exact: true });
    await expect(failureHeading).toBeVisible();
    expect(submittedPayload).toMatchObject({
      workspaceId,
      projectId: null,
      title: "QA presentation failure recovery",
      description: "A synthetic release QA journey.",
      attachmentIds: [],
    });
    expect(csrfToken).toBeTruthy();
    expect(submittedPayload).not.toHaveProperty("provider");
    expect(submittedPayload).not.toHaveProperty("model");
    expect(submittedPayload).not.toHaveProperty("apiKey");
    expect(submittedPayload).not.toHaveProperty("secret");
    expect(jobReads).toBeGreaterThan(0);
    expect(await page.evaluate((key) => window.sessionStorage.getItem(key), storageKey)).toBe(jobId);

    const readsBeforeReload = jobReads;
    await page.reload();
    await expect(page.getByRole("heading", { name: safeFailure, exact: true })).toBeVisible();
    await expect.poll(() => jobReads).toBeGreaterThan(readsBeforeReload);
    expect(await page.evaluate((key) => window.sessionStorage.getItem(key), storageKey)).toBe(jobId);

    await page.getByRole("button", { name: /create another/i }).click();
    const titleInput = page.getByLabel("Deck title");
    const descriptionInput = page.getByLabel("Tell us what to present");
    await expect(titleInput).toBeVisible();
    await titleInput.fill("QA presentation retry");
    await descriptionInput.fill("A clean retry brief for the local-only test.");
    await expect(page.getByRole("button", { name: "Create presentation" })).toBeEnabled();
    await expect.poll(() => page.evaluate((key) => window.sessionStorage.getItem(key), storageKey)).toBeNull();
    expect(blockedExternalRequests).toEqual([]);
  });
});
