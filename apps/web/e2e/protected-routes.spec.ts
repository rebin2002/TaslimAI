import { test, expect } from "./fixtures";

const protectedEntryPoints = [
  { path: "/projects", label: "Projects" },
  { path: "/account", label: "Account" },
  { path: "/create/document", label: "Document Studio" },
  { path: "/search", label: "Global Search" },
  { path: "/account/admin/operations", label: "Admin Operations" },
  { path: "/create", label: "Create" },
  { path: "/create/social", label: "Social Studio" },
  { path: "/chat/e2e-anonymous-boundary", label: "Chat conversation" },
] as const;

test.describe("anonymous protected-route boundary", () => {
  for (const entryPoint of protectedEntryPoints) {
    test(`${entryPoint.label} redirects before private content is exposed`, async ({ page }) => {
      await page.goto(entryPoint.path);

      await expect(page).toHaveURL(new RegExp(`/login\\?next=%2F${entryPoint.path.slice(1).replaceAll("/", "%2F")}$`));
      await expect(page.getByRole("main").getByRole("button", { name: /sign in/i })).toBeVisible();
      await expect(page.getByRole("heading", { name: new RegExp(entryPoint.label, "i") })).toHaveCount(0);
      await expect(page.locator("body")).not.toContainText(/provider details|workspace projects|administrator access required/i);
    });
  }
});


const newlyServerGatedRoutes = [
  "/account",
  "/create",
  "/create/document",
  "/create/social",
  "/chat/e2e-auth-boundary",
] as const;

test.describe("server-gated private routes: session verification", () => {
  test("rejects a forged auth cookie on every newly gated route", async ({ page, context }) => {
    await page.goto("/login");
    await context.addCookies([{
      name: "taslim.auth",
      value: "forged-cookie-not-a-session",
      url: new URL(page.url()).origin,
    }]);

    for (const path of newlyServerGatedRoutes) {
      const response = await page.request.get(path, {
        maxRedirects: 0,
        failOnStatusCode: false,
      });
      expect(response.status(), `forged session at ${path}`).toBe(307);
      expect(response.headers()["location"]).toBe(`/login?next=${encodeURIComponent(path)}`);
    }
  });

  test("accepts a genuine API-authenticated session without a login bounce", async ({ authenticatedPage }) => {
    for (const path of newlyServerGatedRoutes) {
      const response = await authenticatedPage.request.get(path, {
        maxRedirects: 0,
        failOnStatusCode: false,
      });
      expect(response.status(), `authenticated session at ${path}`).toBe(200);
      expect(response.headers()["location"], `unexpected redirect at ${path}`).toBeUndefined();
    }
  });
});
