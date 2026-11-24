import { test, expect, apiJson, E2E_API_URL } from "./fixtures";

const webUrl = process.env.E2E_WEB_URL ?? "http://localhost:3000";

// `viewMarker` is the client-component reference that the server-rendered
// payload carries while a private route is gated only in the browser: it is
// present in the anonymous response on an ungated route and must never appear
// once `requireAuthenticatedPage` short-circuits the request with a redirect.
const serverGatedRoutes = [
  { path: "/activity", heading: /^activity center$/i, viewMarker: "ActivityCenterView" },
  { path: "/notifications", heading: /^notifications$/i, viewMarker: "NotificationCenterView" },
] as const;

test.describe("activity and notifications server authentication", () => {
  for (const route of serverGatedRoutes) {
    const loginNext = `/login?next=${encodeURIComponent(route.path)}`;
    const loginUrl = new RegExp(`/login\\?next=${encodeURIComponent(route.path)}$`);

    test(`redirects an anonymous HTTP request before ${route.path} content is rendered`, async ({ page }) => {
      const response = await page.request.get(new URL(route.path, webUrl).toString(), { maxRedirects: 0 });

      expect(response.status(), `anonymous request at ${route.path}`).toBe(307);
      expect(response.headers().location).toBe(loginNext);
      expect(await response.text()).not.toContain(route.viewMarker);

      await page.goto(route.path);
      await expect(page).toHaveURL(loginUrl);
      await expect(page.getByRole("heading", { name: route.heading })).toHaveCount(0);
      await expect(page.getByRole("main").getByRole("button", { name: /sign in/i })).toBeVisible();
    });

    test(`rejects a forged ${route.path} session with a single-hop redirect`, async ({ page, context }) => {
      await page.goto("/login");
      await context.addCookies([
        { name: "taslim.auth", value: "forged-cookie-not-a-session", url: new URL(page.url()).origin },
      ]);

      const response = await page.request.get(new URL(route.path, webUrl).toString(), {
        maxRedirects: 0,
        failOnStatusCode: false,
      });

      expect(response.status(), `forged session at ${route.path}`).toBe(307);
      expect(response.headers().location).toBe(loginNext);
      expect(await response.text()).not.toContain(route.viewMarker);
    });

    test(`rejects a previously valid ${route.path} session after revocation`, async ({ authenticatedPage: page }) => {
      const priorAuthCookie = (await page.context().cookies(webUrl)).find((cookie) => cookie.name === "taslim.auth");
      expect(priorAuthCookie).toBeDefined();
      if (!priorAuthCookie) throw new Error("The authenticated fixture did not issue a session cookie.");

      await apiJson(page.request, "POST", "/api/auth/sessions/revoke");
      const refreshedAuthCookie = (await page.context().cookies(webUrl)).find((cookie) => cookie.name === "taslim.auth");
      expect(refreshedAuthCookie).toBeDefined();
      if (!refreshedAuthCookie) throw new Error("Session revocation did not refresh the current session cookie.");
      expect((await page.request.get(`${E2E_API_URL}/api/auth/me`)).status()).toBe(200);

      // Replay the pre-revocation cookie: the API must reject it, and the
      // server-rendered route must redirect before emitting private markup.
      await page.context().addCookies([priorAuthCookie]);
      expect((await page.request.get(`${E2E_API_URL}/api/auth/me`)).status()).toBe(401);
      const response = await page.request.get(new URL(route.path, webUrl).toString(), { maxRedirects: 0 });
      expect(response.status(), `revoked session at ${route.path}`).toBe(307);
      expect(response.headers().location).toBe(loginNext);
      expect(await response.text()).not.toContain(route.viewMarker);

      // Restore the fresh test-only session so the fixture's teardown can log it out.
      await page.context().addCookies([refreshedAuthCookie]);
    });

    test(`serves ${route.path} to a genuine session without an HTTP bounce`, async ({ authenticatedPage: page }) => {
      const response = await page.request.get(new URL(route.path, webUrl).toString(), {
        maxRedirects: 0,
        failOnStatusCode: false,
      });

      expect(response.status(), `authenticated session at ${route.path}`).toBe(200);
      expect(response.headers().location, `unexpected redirect at ${route.path}`).toBeUndefined();

      await page.goto(route.path);
      await expect(page).toHaveURL(new RegExp(`${route.path}$`));
      await expect(page.getByRole("heading", { name: route.heading })).toBeVisible();
    });
  }
});
