import { test, expect } from "./fixtures";

// These are ordinary route requests with synthetic test accounts only. The
// spec checks actual HTTP responses returned by Next.js; it does not warm,
// poison, or replay a shared cache and does not create or read private content.
const PRIVATE_CONVERSATION_PATH = "/chat/550e8400-e29b-41d4-a716-446655440000";
const MISSING_ROUTE_PATH = "/__e2e-private-cache-contract-not-found__";

function expectPrivateNoStoreResponse(
  headers: Record<string, string>,
  path: string,
): void {
  const cacheControl = (headers["cache-control"] ?? "").toLowerCase();
  const directives = cacheControl.split(",").map((directive) => directive.trim());

  expect(directives, `${path}: Cache-Control`).toContain("no-store");
  expect(
    directives.some((directive) => directive.split("=", 1)[0].trim() === "public"),
    `${path}: public directive`,
  ).toBe(false);
  expect(
    directives.some((directive) => directive.split("=", 1)[0].trim() === "s-maxage"),
    `${path}: s-maxage directive`,
  ).toBe(false);

  for (const directive of directives) {
    const separator = directive.indexOf("=");
    if (separator < 0) continue;
    const name = directive.slice(0, separator).trim();
    const value = directive.slice(separator + 1).trim();
    if (name === "max-age") {
      expect(value, `${path}: max-age`).toBe("0");
    }
  }

  expect(headers["x-robots-tag"] ?? "", `${path}: X-Robots-Tag`).toMatch(/\bnoindex\b/i);
}

test.describe("browser-observed private route cache headers", () => {
  test("an anonymous protected-route navigation returns a fenced server redirect", async ({ page }) => {
    const routeResponsePromise = page.waitForResponse((response) =>
      response.request().isNavigationRequest()
      && new URL(response.url()).pathname === PRIVATE_CONVERSATION_PATH,
    );

    // Playwright follows the redirect for the page, while the listener captures
    // the final HTTP response for the protected route itself.
    await page.goto(PRIVATE_CONVERSATION_PATH);
    const routeResponse = await routeResponsePromise;

    expect(routeResponse.status()).toBe(307);
    expect(routeResponse.headers().location).toBe(
      `/login?next=${encodeURIComponent(PRIVATE_CONVERSATION_PATH)}`,
    );
    expectPrivateNoStoreResponse(routeResponse.headers(), PRIVATE_CONVERSATION_PATH);
    await expect(page).toHaveURL(/\/login\?next=%2Fchat%2F550e8400-e29b-41d4-a716-446655440000$/);
  });

  test("an unknown route's actual 404 response remains no-store and unindexed", async ({ page }) => {
    const response = await page.goto(MISSING_ROUTE_PATH);

    expect(response).not.toBeNull();
    expect(response!.status()).toBe(404);
    expectPrivateNoStoreResponse(response!.headers(), MISSING_ROUTE_PATH);
  });

  test("a synthetic signed-in session receives a fenced response on an ordinary private route", async ({ authenticatedPage: page }) => {
    const response = await page.goto(PRIVATE_CONVERSATION_PATH);

    expect(response).not.toBeNull();
    expect(response!.status()).toBe(200);
    expectPrivateNoStoreResponse(response!.headers(), PRIVATE_CONVERSATION_PATH);
  });
});
