import { test, expect } from "./fixtures";

function expectBillingLoginRedirect(status: number, location: string | undefined) {
  expect([307, 308]).toContain(status);
  expect(location).toBeTruthy();
  const target = new URL(location!, "http://taslim.invalid");
  expect(target.pathname).toBe("/login");
  expect(target.searchParams.get("next")).toBe("/account/billing");
}

test.describe("Billing page server authorization", () => {
  test("anonymous requests redirect before private billing markup can render", async ({ request }) => {
    const response = await request.get("/account/billing", { maxRedirects: 0 });
    expectBillingLoginRedirect(response.status(), response.headers()["location"]);
    expect(await response.text()).not.toContain("billing-credit-card");
  });

  test("a valid authenticated session reaches the protected Billing route", async ({ authenticatedPage: page }) => {
    const response = await page.request.get("/account/billing", { maxRedirects: 0 });
    expect(response.status()).toBe(200);
    await page.goto("/account/billing");
    await expect(page).toHaveURL(/\/account\/billing$/);
    await expect(page.locator(".billing-page")).toBeVisible();
  });

  test("a forged session cookie does not authorize Billing", async ({ request }) => {
    const response = await request.get("/account/billing", {
      maxRedirects: 0,
      headers: { cookie: "taslim.auth=invalid-e2e-session" },
    });
    expectBillingLoginRedirect(response.status(), response.headers()["location"]);
    expect(await response.text()).not.toContain("billing-credit-card");
  });
});
