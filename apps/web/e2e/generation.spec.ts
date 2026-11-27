import { test, expect, apiJson } from "./fixtures";

/**
 * Provider-independent generation canary.
 *
 * The shipped version of this spec announced its terminal state with two page-wide
 * text matches:
 *
 *   await expect(page.getByText(/queued|running|succeeded|failed|cancelled/i).first()).toBeVisible();
 *   await expect(page.getByText(/job completed successfully|failed|cancelled/i).first()).toBeVisible();
 *
 * Neither one can fail for a broken generation foundation: `jobs.statusFailed`
 * renders the literal word "Failed" inside the job card, so the second match is
 * satisfied by the failure branch, and the first is satisfied by any status label at
 * any stage. The canary therefore reported green when the job never succeeded.
 *
 * The assertions below are bound to the job id this run created and to the
 * affordances that only a genuinely succeeded job renders; the failure branch is
 * asserted separately so the two outcomes can never be conflated again. Everything
 * runs against the app's own fixtures and a loopback API - no provider is reachable,
 * no real user data is used, and no approval is bypassed.
 */

const jobPath = /\/api\/generation\/jobs\/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

type GenerationJob = {
  id: string;
  title: string | null;
  status: string;
  progressPercent: number;
  resultJson: string | null;
  errorCode: string | null;
  errorMessage: string | null;
};

type AuthResponse = { personalWorkspace: { id: string } };

async function fetchJob(request: Parameters<typeof apiJson>[0], id: string) {
  return apiJson<GenerationJob>(request, "GET", `/api/generation/jobs/${id}`);
}

test.describe("generation safety foundation", () => {
  test("drives the system test job to the succeeded state the job card advertises", async ({ authenticatedPage: page }) => {
    await page.goto("/account/generation-jobs");
    await expect(page.getByRole("heading", { name: /generation jobs/i })).toBeVisible();

    // Scoped to the job created below, so the check always starts from a history this
    // principal has never written to.
    const recentRows = page.locator(".generation-job-list-item");
    await expect(page.getByText(/no test jobs yet/i)).toBeVisible();
    await expect(recentRows).toHaveCount(0);

    await page.getByRole("button", { name: /create test job/i }).click();

    const detail = page.locator(".generation-job-detail");
    await expect(detail).toBeVisible();
    const jobId = ((await detail.locator("code").textContent()) ?? "").trim();
    expect(jobId).toMatch(guid);

    // Terminal success, asserted only through affordances the component renders for a
    // succeeded job. A queued, running, failed or cancelled job cannot satisfy them.
    const success = detail.locator(".form-success");
    await expect(success).toBeVisible({ timeout: 30_000 });
    await expect(success).toContainText(/job completed successfully/i);
    await expect(detail.locator(".generation-status")).toHaveText(/succeeded/i);
    await expect(detail.getByRole("progressbar")).toHaveAttribute("aria-valuenow", "100");
    await expect(detail.getByRole("status")).toContainText(/succeeded.*100% complete/i);

    // The failure and cancellation branches must be visibly absent, not merely an
    // alternative spelling of "terminal".
    await expect(detail.locator("[role='alert']")).toHaveCount(0);
    await expect(detail.locator(".generation-running")).toHaveCount(0);
    await expect(detail.getByRole("button", { name: /cancel job/i })).toHaveCount(0);

    // The persisted job agrees with what the card rendered.
    const stored = await fetchJob(page.request, jobId);
    expect(stored.id).toBe(jobId);
    expect(stored.status).toBe("Succeeded");
    expect(stored.progressPercent).toBe(100);
    expect(stored.errorCode).toBeNull();
    expect(stored.errorMessage).toBeNull();
    expect(stored.resultJson).toContain("Generation job completed.");

    // The workspace history row for that job reports the same terminal state.
    await expect(recentRows).toHaveCount(1);
    await expect(recentRows.first().locator(".generation-status")).toHaveText(/succeeded/i);

    // The surface stays provider-neutral.
    await expect(page.getByText(/api key|provider secret|stack trace/i)).toHaveCount(0);
  });

  test("surfaces a failed system test job as a failure and never as a success", async ({ authenticatedPage: page }) => {
    const auth = await apiJson<AuthResponse>(page.request, "GET", "/api/auth/me");

    // Deterministic stand-in for a worker failure: the job's own detail read answers a
    // terminal failure. Nothing is generated, charged, or sent to a provider.
    await page.route(jobPath, async (route) => {
      const id = decodeURIComponent(new URL(route.request().url()).pathname.split("/").pop() ?? "");
      await route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify({
          id,
          workspaceId: auth.personalWorkspace.id,
          projectId: null,
          jobType: "system.test",
          status: "Failed",
          title: "System test job",
          progressPercent: 40,
          resultJson: null,
          errorCode: "GENERATION_FAILED",
          errorMessage: "The job could not be completed.",
          cancellationRequested: false,
          createdAt: "2026-11-16T00:00:00Z",
          queuedAt: "2026-11-16T00:00:01Z",
          startedAt: "2026-11-16T00:00:02Z",
          completedAt: null,
          failedAt: "2026-11-16T00:00:03Z",
          cancelledAt: null,
          outputs: [],
        }),
      });
    });

    await page.goto("/account/generation-jobs");
    await page.getByRole("button", { name: /create test job/i }).click();

    const detail = page.locator(".generation-job-detail");
    const recentRows = page.locator(".generation-job-list-item");
    const alert = detail.locator("[role='alert']");
    await expect(alert).toBeVisible({ timeout: 30_000 });
    await expect(alert).toContainText("The job could not be completed.");
    await expect(detail.locator(".generation-status")).toHaveText(/failed/i);

    // The two outcomes are mutually exclusive - exactly the pair the shipped regex
    // could not tell apart.
    await expect(detail.locator(".form-success")).toHaveCount(0);
    await expect(detail.getByRole("progressbar")).toHaveAttribute("aria-valuenow", "40");
    await expect(recentRows).toHaveCount(1);
    await expect(recentRows.first().locator(".generation-status")).toHaveText(/failed/i);
  });

  test("reuses a provider-independent job for the same idempotency key", async ({ authenticatedPage: page }) => {
    const auth = await apiJson<AuthResponse>(page.request, "GET", "/api/auth/me");
    const key = `e2e-idempotency-${Date.now().toString(36)}`;
    const body = {
      workspaceId: auth.personalWorkspace.id,
      jobType: "system.test",
      inputJson: JSON.stringify({ purpose: "e2e-idempotency" }),
      title: "E2E idempotency check",
    };
    const first = await apiJson<{ id: string }>(page.request, "POST", "/api/generation/jobs", body, { "Idempotency-Key": key });
    const second = await apiJson<{ id: string }>(page.request, "POST", "/api/generation/jobs", body, { "Idempotency-Key": key });
    expect(second.id).toBe(first.id);

    // Wait on the job's own observable state rather than assuming the worker has
    // already reached it.
    await expect.poll(async () => (await fetchJob(page.request, first.id)).status, {
      timeout: 30_000,
      intervals: [250, 500, 1_000],
    }).toBe("Succeeded");

    // Reusing the key must not have started a second run either: the workspace
    // history holds exactly one job for it.
    const history = await apiJson<{ items: GenerationJob[] }>(
      page.request,
      "GET",
      `/api/generation/jobs?workspaceId=${auth.personalWorkspace.id}&page=1&pageSize=50&jobType=system.test`,
    );
    const titled = history.items.filter((item) => item.title === "E2E idempotency check");
    expect(titled).toHaveLength(1);
    expect(titled[0]?.id).toBe(first.id);
  });
});
