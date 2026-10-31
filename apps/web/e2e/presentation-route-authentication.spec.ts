import { randomUUID } from "node:crypto";
import type { Page } from "@playwright/test";
import { test, expect, E2E_API_URL } from "./fixtures";

const webUrl = process.env.E2E_WEB_URL ?? "http://localhost:3000";
const routePath = "/create/presentation";
const loginLocation = "/login?next=%2Fcreate%2Fpresentation";

function watchGenerationProviderPosts(page: Page) {
  const posts: string[] = [];
  page.on("request", (request) => {
    const pathname = new URL(request.url()).pathname;
    if (request.method() === "POST" && /presentation|generation|provider|inference/i.test(pathname)) {
      posts.push(pathname);
    }
  });
  return posts;
}

test.describe("Presentation Studio server authentication", () => {
  test("redirects anonymous and expired API sessions before returning private Studio markup", async ({ page }) => {
    const generationProviderPosts = watchGenerationProviderPosts(page);

    const anonymousResponse = await page.request.get(new URL(routePath, webUrl).toString(), { maxRedirects: 0 });
    expect(anonymousResponse.status()).toBe(307);
    expect(anonymousResponse.headers().location).toBe(loginLocation);
    const anonymousMarkup = await anonymousResponse.text();
    expect(anonymousMarkup).not.toContain("presentation-studio-page");
    expect(anonymousMarkup).not.toContain("presentation-create-button");

    const expiredSession = `expired-${randomUUID()}`;
    await page.context().addCookies([{
      name: "taslim.auth",
      value: expiredSession,
      url: webUrl,
      httpOnly: true,
      sameSite: "Lax",
    }]);

    // Confirm the local E2E API rejects this synthetic expired/invalid session.
    const apiSession = await page.request.get(`${E2E_API_URL}/api/auth/me`, {
      headers: { Cookie: `taslim.auth=${expiredSession}` },
    });
    expect(apiSession.status()).toBe(401);

    const expiredResponse = await page.request.get(new URL(routePath, webUrl).toString(), { maxRedirects: 0 });
    expect(expiredResponse.status()).toBe(307);
    expect(expiredResponse.headers().location).toBe(loginLocation);
    const expiredMarkup = await expiredResponse.text();
    expect(expiredMarkup).not.toContain("presentation-studio-page");
    expect(expiredMarkup).not.toContain("presentation-create-button");

    // Following the redirect must settle on the sign-in page rather than loop.
    await page.goto(routePath);
    await expect(page).toHaveURL(/\/login\?next=%2Fcreate%2Fpresentation$/);
    await expect(page.getByRole("heading", { name: "Presentation Studio" })).toHaveCount(0);
    await expect(page.getByRole("main").getByRole("button", { name: /sign in/i })).toBeVisible();
    expect(generationProviderPosts).toEqual([]);
  });

  test("serves the Studio for an authenticated API session without starting generation", async ({ authenticatedPage: page }) => {
    const generationProviderPosts = watchGenerationProviderPosts(page);
    const apiSession = await page.request.get(`${E2E_API_URL}/api/auth/me`);
    expect(apiSession.status()).toBe(200);

    const response = await page.goto(routePath);
    expect(response?.status()).toBe(200);
    await expect(page).toHaveURL(/\/create\/presentation$/);
    await expect(page.getByRole("heading", { name: "Presentation Studio" })).toBeVisible();
    await expect(page.locator(".presentation-studio-page")).toBeVisible();
    await expect(page.locator(".presentation-create-button")).toBeVisible();
    await page.waitForLoadState("networkidle");
    expect(generationProviderPosts).toEqual([]);
  });
});
