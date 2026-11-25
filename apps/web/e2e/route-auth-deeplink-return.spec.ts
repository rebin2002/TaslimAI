import { randomUUID } from "node:crypto";
import type { Page } from "@playwright/test";
import { test, expect, E2E_API_URL, logoutInUi } from "./fixtures";

const webUrl = process.env.E2E_WEB_URL ?? "http://localhost:3000";

// Deep links as the application actually builds them (assetDestination in
// src/lib/searchNavigation.ts): the identifier keeps an encoded slash, and the
// library restores its filters from these parameters.
const assetDeepLink = "/assets?assetId=asset%2Farchived&status=Archived";
const assetDeepLinkNext = "%2Fassets%3FassetId%3Dasset%252Farchived%26status%3DArchived";
const presentationDeepLink = "/create/presentation?projectId=project-1";
const presentationDeepLinkNext = "%2Fcreate%2Fpresentation%3FprojectId%3Dproject-1";

function url(path: string) {
  return new URL(path, webUrl).toString();
}

async function expectRedirectTo(page: Page, path: string, location: string, privateMarkers: string[]) {
  const response = await page.request.get(url(path), { maxRedirects: 0 });
  expect(response.status()).toBe(307);
  expect(response.headers().location).toBe(location);
  const markup = await response.text();
  for (const marker of privateMarkers) expect(markup).not.toContain(marker);
}

async function forgeExpiredSession(page: Page): Promise<string> {
  const expiredSession = `expired-${randomUUID()}`;
  await page.context().addCookies([{
    name: "taslim.auth",
    value: expiredSession,
    url: webUrl,
    httpOnly: true,
    sameSite: "Lax",
  }]);
  return expiredSession;
}

test.describe("Protected route deep-link sign-in returns", () => {
  test("keeps the asset filters in the anonymous sign-in destination", async ({ page }) => {
    await expectRedirectTo(page, assetDeepLink, `/login?next=${assetDeepLinkNext}`, [
      "asset-search",
      "assets-toolbar-top",
    ]);
    // The bare route keeps promising exactly the bare route, so the batch-2
    // census rows stay valid alongside the deep-link behaviour.
    await expectRedirectTo(page, "/assets", "/login?next=%2Fassets", ["asset-search"]);
  });

  test("keeps the studio selection for a forged expired session", async ({ page }) => {
    const expiredSession = await forgeExpiredSession(page);
    const session = await page.request.get(`${E2E_API_URL}/api/auth/me`, {
      headers: { Cookie: `taslim.auth=${expiredSession}` },
    });
    expect(session.status()).toBe(401);

    await expectRedirectTo(page, presentationDeepLink, `/login?next=${presentationDeepLinkNext}`, [
      "presentation-studio-page",
      "presentation-create-button",
    ]);
    await expectRedirectTo(page, "/account/memory?draftId=memory-1", "/login?next=%2Faccount%2Fmemory%3FdraftId%3Dmemory-1", ["memory-boundary-card"]);
    await expectRedirectTo(page, "/account/generation-jobs?page=2", "/login?next=%2Faccount%2Fgeneration-jobs%3Fpage%3D2", ["generation-jobs-grid"]);
  });

  test("serves the filtered library to a signed-in deep link", async ({ authenticatedPage: page }) => {
    const listResponse = page.waitForResponse((response) => {
      const pathname = new URL(response.url()).pathname;
      return pathname.endsWith("/api/assets") && response.request().method() === "GET";
    });

    const response = await page.goto(assetDeepLink);
    expect(response?.status()).toBe(200);
    await expect(page).toHaveURL(/\/assets\?assetId=asset%2Farchived&status=Archived$/);

    const list = await listResponse;
    const requestParams = new URL(list.request().url()).searchParams;
    expect(requestParams.get("status")).toBe("Archived");
  });

  test("returns to the deep link after a real sign-in", async ({ authenticatedPage: page }) => {
    const { email, password } = page.testUser;
    await logoutInUi(page);

    await page.goto(assetDeepLink);
    await expect(page).toHaveURL(new RegExp(`/login\\?next=${assetDeepLinkNext}$`));
    await page.getByLabel(/email/i).fill(email);
    await page.getByRole("textbox", { name: /^password/i }).fill(password);
    await page.getByRole("button", { name: /sign in/i }).click();

    // The promised destination is the filtered library, not a bare /assets.
    await expect(page).toHaveURL(/\/assets\?assetId=asset%2Farchived&status=Archived$/);
  });
});
