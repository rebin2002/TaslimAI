import { test, expect, apiJson } from "./fixtures";

test.describe("generation safety foundation", () => {
  test("creates a provider-independent system test job and observes a terminal state", async ({ authenticatedPage: page }) => {
    await page.goto("/account/generation-jobs");
    await expect(page.getByRole("heading", { name: /generation jobs/i })).toBeVisible();
    await page.getByRole("button", { name: /create test job/i }).click();
    await expect(page.getByText(/queued|running|succeeded|failed|cancelled/i).first()).toBeVisible({ timeout: 15_000 });
    await expect(page.getByText(/job completed successfully|failed|cancelled/i).first()).toBeVisible({ timeout: 30_000 });
    await expect(page.getByText(/api key|provider secret|stack trace/i)).toHaveCount(0);
  });

  test("reuses a provider-independent job for the same idempotency key", async ({ authenticatedPage: page }) => {
    const auth = await apiJson<{ personalWorkspace: { id: string } }>(page.request, "GET", "/api/auth/me");
    const key = `e2e-idempotency-${Date.now().toString(36)}`;
    const first = await apiJson<{ id: string }>(page.request, "POST", "/api/generation/jobs", {
      workspaceId: auth.personalWorkspace.id,
      jobType: "system.test",
      inputJson: JSON.stringify({ purpose: "e2e-idempotency" }),
      title: "E2E idempotency check",
    }, { "Idempotency-Key": key });
    const second = await apiJson<{ id: string }>(page.request, "POST", "/api/generation/jobs", {
      workspaceId: auth.personalWorkspace.id,
      jobType: "system.test",
      inputJson: JSON.stringify({ purpose: "e2e-idempotency" }),
      title: "E2E idempotency check",
    }, { "Idempotency-Key": key });
    expect(second.id).toBe(first.id);
  });
});
