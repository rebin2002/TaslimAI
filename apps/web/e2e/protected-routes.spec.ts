import { test, expect } from "./fixtures";

// `next` records the login return path the server gate promises when it differs
// from the requested path itself (the private project-detail gate redirects to
// the Projects list, matching apps/web/src/app/projects/[projectId]/page.tsx).
const protectedEntryPoints = [
  { path: "/projects", label: "Projects" },
  { path: "/account", label: "Account" },
  { path: "/create/document", label: "Document Studio" },
  { path: "/search", label: "Global Search" },
  { path: "/account/admin/operations", label: "Admin Operations" },
  { path: "/create", label: "Create" },
  { path: "/create/social", label: "Social Studio" },
  { path: "/chat/e2e-anonymous-boundary", label: "Chat conversation" },
  { path: "/activity", label: "Activity" },
  { path: "/notifications", label: "Notifications" },
  { path: "/assets", label: "Assets" },
  { path: "/account/usage", label: "Usage" },
  { path: "/account/memory", label: "Memory" },
  { path: "/account/generation-jobs", label: "Generation Jobs" },
  { path: "/account/admin/usage", label: "Admin Usage" },
  { path: "/create/image", label: "Image Studio" },
  { path: "/create/music", label: "Music Studio" },
  { path: "/create/voice", label: "Voice Studio" },
  { path: "/create/movie", label: "Movie Studio" },
  { path: "/create/presentation", label: "Presentation Studio" },
  { path: "/create/research", label: "Research Studio" },
  { path: "/projects/e2e-anonymous-boundary", label: "Project detail", next: "/projects" },
  { path: "/media/images", label: "Feature placeholder" },
] as const;

test.describe("anonymous protected-route boundary", () => {
  for (const entryPoint of protectedEntryPoints) {
    test(`${entryPoint.label} redirects before private content is exposed`, async ({ page }) => {
      await page.goto(entryPoint.path);

      const promisedPath = "next" in entryPoint ? entryPoint.next : undefined;
      const expectedNext = encodeURIComponent(promisedPath ?? entryPoint.path);
      await expect(page).toHaveURL(new RegExp(`/login\\?next=${expectedNext}$`));
      await expect(page.getByRole("main").getByRole("button", { name: /sign in/i })).toBeVisible();
      await expect(page.getByRole("heading", { name: new RegExp(entryPoint.label, "i") })).toHaveCount(0);
      await expect(page.locator("body")).not.toContainText(/provider details|workspace projects|administrator access required/i);
    });
  }
});

// Every route that gains a server-side session gate must reject a forged
// `taslim.auth` cookie with a one-hop 307 to /login and serve a genuine
// API-authenticated session without an HTTP bounce.
const newlyServerGatedRoutes = [
  { path: "/create" },
  { path: "/create/document" },
  { path: "/create/social" },
  { path: "/chat/e2e-auth-boundary" },
  { path: "/activity" },
  { path: "/notifications" },
  { path: "/assets" },
  { path: "/account" },
  { path: "/account/usage" },
  { path: "/account/memory" },
  { path: "/account/generation-jobs" },
  { path: "/account/admin/operations" },
  { path: "/account/admin/usage" },
  { path: "/projects" },
  { path: "/projects/e2e-auth-boundary", next: "/projects" },
  { path: "/search" },
  { path: "/create/image" },
  { path: "/create/music" },
  { path: "/create/voice" },
  { path: "/create/movie" },
  { path: "/create/presentation" },
  { path: "/create/research" },
  { path: "/media/images" },
] as const;

test.describe("server-gated private routes: session verification", () => {
  test("rejects a forged auth cookie on every newly gated route", async ({ page, context }) => {
    await page.goto("/login");
    await context.addCookies([{
      name: "taslim.auth",
      value: "forged-cookie-not-a-session",
      url: new URL(page.url()).origin,
    }]);

    for (const route of newlyServerGatedRoutes) {
      const response = await page.request.get(route.path, {
        maxRedirects: 0,
        failOnStatusCode: false,
      });
      const promisedPath = "next" in route ? route.next : undefined;
      expect(response.status(), `forged session at ${route.path}`).toBe(307);
      expect(response.headers()["location"], `forged session redirect at ${route.path}`).toBe(`/login?next=${encodeURIComponent(promisedPath ?? route.path)}`);
    }
  });

  test("accepts a genuine API-authenticated session without a login bounce", async ({ authenticatedPage }) => {
    for (const route of newlyServerGatedRoutes) {
      const response = await authenticatedPage.request.get(route.path, {
        maxRedirects: 0,
        failOnStatusCode: false,
      });
      expect(response.status(), `authenticated session at ${route.path}`).toBe(200);
      expect(response.headers()["location"], `unexpected redirect at ${route.path}`).toBeUndefined();
    }
  });
});
