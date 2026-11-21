import { randomUUID } from "node:crypto";
import { test, expect, E2E_API_URL, createProject } from "./fixtures";
const webUrl = process.env.E2E_WEB_URL ?? "http://localhost:3000";
// The deep movie workspace is the private project room: it renders persisted
// production state for one project, so it must never be reachable before the
// server has verified the session. Client-side ProtectedPage hydration alone
// still returns the shell in the first response.
const anonymousRoute = "/create/movie/e2e-anonymous-boundary/story";
const anonymousLoginLocation = `/login?next=${encodeURIComponent(anonymousRoute)}`;
const privateMarkers = ["movie-full-workspace", "movie-workspace-nav", "movie-director-panel"];
function privateMarkup(markup: string) {
  return privateMarkers.filter((marker) => markup.includes(marker));
}
test.describe("Full movie module server authentication", () => {
  test("redirects an anonymous request before returning private workspace markup", async ({ page }) => {
    const response = await page.request.get(new URL(anonymousRoute, webUrl).toString(), { maxRedirects: 0 });
    expect(response.status()).toBe(307);
    expect(response.headers().location).toBe(anonymousLoginLocation);
    expect(privateMarkup(await response.text())).toEqual([]);
    // Following the redirect must settle on sign-in rather than loop.
    await page.goto(anonymousRoute);
    await expect(page).toHaveURL(/\/login\?next=/);
    await expect(page.getByRole("main").getByRole("button", { name: /sign in/i })).toBeVisible();
    await expect(page.locator(".movie-full-workspace")).toHaveCount(0);
  });
  test("rejects forged and expired sessions with a one-hop redirect", async ({ page, context }) => {
    await page.goto("/login");
    for (const value of ["forged-cookie-not-a-session", `expired-${randomUUID()}`]) {
      await context.addCookies([{ name: "taslim.auth", value, url: webUrl, httpOnly: true, sameSite: "Lax" }]);
      // Confirm the API itself refuses the synthetic session before asserting
      // that the server-rendered gate agrees with it.
      const apiSession = await page.request.get(`${E2E_API_URL}/api/auth/me`, {
        headers: { Cookie: `taslim.auth=${value}` },
      });
      expect(apiSession.status(), `API session check for ${value}`).toBe(401);
      const response = await page.request.get(new URL(anonymousRoute, webUrl).toString(), { maxRedirects: 0 });
      expect(response.status(), `gate for ${value}`).toBe(307);
      expect(response.headers().location, `redirect for ${value}`).toBe(anonymousLoginLocation);
      expect(privateMarkup(await response.text()), `markup for ${value}`).toEqual([]);
    }
  });
  test("serves the workspace to a genuine API-authenticated session without a login bounce", async ({ authenticatedPage }) => {
    const project = await createProject(authenticatedPage, "E2E Movie Route Auth Project");
    const route = `/create/movie/${project.id}/story`;
    const session = await authenticatedPage.request.get(`${E2E_API_URL}/api/auth/me`);
    expect(session.status()).toBe(200);
    const response = await authenticatedPage.request.get(new URL(route, webUrl).toString(), {
      maxRedirects: 0,
      failOnStatusCode: false,
    });
    expect(response.status()).toBe(200);
    expect(response.headers().location).toBeUndefined();
    await authenticatedPage.goto(route);
    await expect(authenticatedPage).toHaveURL(new RegExp(`/create/movie/${project.id}/story$`));
  });
});
