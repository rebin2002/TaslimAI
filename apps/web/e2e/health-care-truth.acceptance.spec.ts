import { test, expect } from "./fixtures";
/**
 * Issue #714 release acceptance: prove Health Care is a REAL category with a
 * live private route, not a decorative tile.
 *
 * This lane is disjoint by design so it adds no owner conflict:
 *  - `health-care.spec.ts`             route copy, responsive bounds, RTL, tab order
 *  - `health-care-tile.spec.ts`        desktop tile visibility and click-through
 *  - `health-care-tile.mobile.spec.ts` 320/375px tile geometry and tap target
 *  - `health-care-tile.rtl.spec.ts`    AR/CKB tile and landing copy
 * Pinned here instead:
 *  1. the localized Health & Wellness CATEGORY renders on the Create menu at
 *     320px and on desktop, and its tile points at a route that actually serves
 *     the authenticated user (no broken or purely decorative tile);
 *  2. route truthfulness on `/create/health`: every planned capability is a
 *     disabled, explicitly-unavailable action, no capture control exists while
 *     the consent/document lane (#804/#812) is unmerged, and health-family
 *     destinations referenced from the page are not dead ends;
 *  3. no unauthorized health document exposure: the private route leaks nothing
 *     to an anonymous visitor in any supported locale, and the health surface
 *     itself enumerates no user document, asset or health API endpoint.
 *
 * Provider-free and read-only: no generation, no upload, no provider call, no
 * real medical record, no production data.
 */
async function capture(
  page: import("@playwright/test").Page,
  testInfo: import("@playwright/test").TestInfo,
  name: string,
) {
  const path = testInfo.outputPath(`${name}.png`);
  await page.screenshot({ path, fullPage: true });
  await testInfo.attach(`${name}.png`, { path, contentType: "image/png" });
}
const healthCareTile = (page: import("@playwright/test").Page) =>
  page.locator('a[data-health-care-tile="true"]');
const healthCareCategory = (page: import("@playwright/test").Page) =>
  page.locator('section[data-health-care-category="true"]');
/** The informational Health Care panel only, excluding the shared app navigation. */
const healthCarePanel = (page: import("@playwright/test").Page) =>
  page.locator("[aria-labelledby='health-care-title']");
const HEALTH_ROUTE = "/create/health";
/** English is the chooser's default for a freshly registered E2E account. */
const en = {
  category: "Health & Wellness",
  title: "Health Care",
  status: "Coming soon",
  plannedHeading: "Planned, not available yet",
  actionUnavailable: "Unavailable — coming soon",
  backToCreate: "Back to Create",
  privacyClaim: "does not collect, upload, store, or analyze",
};
/** The Health Care h1 in every shipped locale; none may reach an anonymous visitor. */
const localizedTitles = ["Health Care", "الرعاية الصحية", "چاودێریی تەندروستی"];
/**
 * The informational page legitimately NAMES planned capabilities such as
 * "Health documents and lab reports", so a violation is a live data surface: a
 * health/document/asset endpoint or clinical-record identifier.
 */
const healthDataSurface =
  /\/api\/health|\/api\/documents|\/api\/assets|healthDocumentId|patient record|prescription/i;
async function horizontalOverflow(page: import("@playwright/test").Page) {
  return page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
  );
}
async function internalHealthDestinations(page: import("@playwright/test").Page) {
  const hrefs = await page.locator("a[href]").evaluateAll((anchors) =>
    anchors
      .map((anchor) => anchor.getAttribute("href") ?? "")
      .filter((href) => href.startsWith("/create") || href.startsWith("/personal")),
  );
  return [...new Set(hrefs.map((href) => href.split("#")[0] || "/"))];
}
test.describe("Health Care category and route truthfulness", () => {
  for (const width of [320, 1440]) {
    test(`renders a real ${en.category} category at ${width}px whose tile serves the route`, async ({ authenticatedPage }, testInfo) => {
      const page = authenticatedPage;
      await page.setViewportSize({ width, height: 900 });
      await page.goto("/create");
      const category = healthCareCategory(page);
      await expect(category, `the category section is rendered at ${width}px`).toBeVisible();
      await expect(category.locator("h3")).toHaveText(en.category);
      const tile = healthCareTile(page);
      await expect(tile).toBeVisible();
      // Truthfulness gate 1: the tile names the real route, never a placeholder.
      expect(await tile.getAttribute("href"), "the tile links to the Health Care route")
        .toBe(HEALTH_ROUTE);
      // Truthfulness gate 2: that route serves the authenticated user instead of 404.
      const routeResponse = await page.request.get(HEALTH_ROUTE);
      expect(routeResponse.status(), `${HEALTH_ROUTE} is not a dead end`).not.toBe(404);
      expect(routeResponse.ok(), `${HEALTH_ROUTE} serves the authenticated user`).toBe(true);
      const box = await category.boundingBox();
      expect(box, `the category is measurable at ${width}px`).not.toBeNull();
      if (box) {
        expect(box.x).toBeGreaterThanOrEqual(0);
        expect(box.x + box.width).toBeLessThanOrEqual(width + 1);
      }
      expect(await horizontalOverflow(page), `no horizontal overflow at ${width}px`)
        .toBeLessThanOrEqual(1);
      await capture(page, testInfo, `create-menu-${width}px`);
      await tile.click();
      await expect(page).toHaveURL(new RegExp(`${HEALTH_ROUTE}$`));
      await expect(page.getByRole("heading", { name: en.title, level: 1 })).toBeVisible();
      await capture(page, testInfo, `health-route-${width}px`);
    });
  }
  test("keeps the route honest: planned actions are disabled and health destinations resolve", async ({ authenticatedPage }, testInfo) => {
    const page = authenticatedPage;
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto(HEALTH_ROUTE);
    await expect(page.getByRole("heading", { name: en.title, level: 1 })).toBeVisible();
    await expect(page.getByRole("status")).toHaveText(en.status);
    const planned = page.locator('section[aria-labelledby="health-care-planned-title"]');
    await expect(planned.getByRole("heading", { level: 2 })).toHaveText(en.plannedHeading);
    const actions = planned.locator("button");
    const actionCount = await actions.count();
    expect(actionCount, "the planned capability list is rendered").toBeGreaterThan(0);
    for (let index = 0; index < actionCount; index += 1) {
      const action = actions.nth(index);
      await expect(action, `planned action ${index} is disabled`).toBeDisabled();
      await expect(action.locator("small")).toHaveText(en.actionUnavailable);
    }
    expect(await planned.locator("button:not([disabled])").count(), "no action is silently enabled")
      .toBe(0);
    // Nothing on this surface may capture data yet: the consent/document lane
    // (#804/#812) is unmerged, so an enabled form or file control would be a lie.
    await expect(page.locator("form, input, textarea, [type='file']")).toHaveCount(0);
    // Route truthfulness: health-family destinations must not dead-end.
    const destinations = await internalHealthDestinations(page);
    expect(destinations.length, "the route exposes health-family navigation").toBeGreaterThan(0);
    expect(destinations, "the surface returns to the Create chooser").toContain("/create");
    for (const href of destinations) {
      const response = await page.request.get(href);
      expect(response.status(), `${href} is not a broken link`).not.toBe(404);
    }
    await expect(page.getByRole("link", { name: en.backToCreate })).toBeVisible();
    await expect(page.locator('meta[name="robots"]')).toHaveAttribute("content", /noindex/);
    const html = await page.content();
    expect(html, "no health document API surface is advertised").not.toMatch(healthDataSurface);
    await capture(page, testInfo, "health-route-honest-capabilities");
  });
  test("exposes no health document surface to an anonymous visitor", async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 320, height: 720 });
    await page.goto(HEALTH_ROUTE);
    await expect(page).toHaveURL(new RegExp("/login\\?next=%2Fcreate%2Fhealth$"));
    const body = await page.locator("body").innerText();
    for (const title of localizedTitles) {
      expect(body, `the ${title} surface never reaches an anonymous visitor`).not.toContain(title);
      await expect(page.getByRole("heading", { name: title, level: 1 })).toHaveCount(0);
    }
    expect(body, "no localized capability or privacy copy leaks pre-authentication")
      .not.toMatch(/health information|lab report|medication|coming soon/i);
    await expect(healthCarePanel(page)).toHaveCount(0);
    expect(await page.content(), "no health data endpoint is exposed").not.toMatch(healthDataSurface);
    await capture(page, testInfo, "anonymous-health-route-boundary");
  });
  test("mounts no document or asset surface inside the authenticated health panel", async ({ authenticatedPage }, testInfo) => {
    const page = authenticatedPage;
    await page.setViewportSize({ width: 320, height: 720 });
    await page.goto(HEALTH_ROUTE);
    await expect(page.getByRole("heading", { name: en.title, level: 1 })).toBeVisible();
    // The shared app navigation legitimately links to Projects and Assets, so the
    // gate is scoped to the health surface itself: it must stay a listing of nothing.
    const panel = healthCarePanel(page);
    await expect(panel).toBeVisible();
    expect(await panel.locator("img").count(), "no document or asset preview is rendered").toBe(0);
    expect(await panel.locator('a[href^="/assets"], a[href^="/projects"]').count(),
      "no link into the user's documents or projects").toBe(0);
    expect(await panel.locator("form, input, textarea, [type='file']").count(),
      "no capture control inside the health surface").toBe(0);
    await expect(panel).toContainText(en.privacyClaim);
    expect(await panel.innerText(), "no record-shaped text in the health surface")
      .not.toMatch(healthDataSurface);
    expect(await horizontalOverflow(page), "the 320px route does not overflow").toBeLessThanOrEqual(1);
    await capture(page, testInfo, "authenticated-health-surface-no-documents");
  });
});
