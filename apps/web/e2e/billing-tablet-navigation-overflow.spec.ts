import { test, expect } from "./fixtures";

/**
 * Real disposable authenticated browser session with local API/PostgreSQL.
 * Billing plan/credit UI may be seeded by the test API, but no paid provider,
 * production credential, charge, or remote account is involved.
 *
 * This catches the tablet overflow missed by checking only html.scrollWidth:
 * fullPage screenshots on the previous main grew from 768px to 781px (EN)
 * and 815px (Kurdish Sorani), with the desktop navbar clipping at the edge.
 */
const tabletLocales = [
  { key: "en", dir: "ltr" },
  { key: "ar", dir: "rtl" },
  { key: "ku", dir: "rtl" },
] as const;
const tabletWidths = [681, 768, 900] as const;

test.describe("tablet header must not overflow authenticated Billing or Account pages", () => {
  test.setTimeout(120_000);
  for (const locale of tabletLocales) {
    for (const width of tabletWidths) {
      test(`Billing ${locale.key} at ${width}px keeps header inside viewport`, async ({ authenticatedPage: page }, testInfo) => {
        await page.addInitScript((lang) => localStorage.setItem("taslim-locale", lang), locale.key);
        await page.setViewportSize({ width, height: 1024 });
        await page.goto("/account/billing");

        await expect(page.locator(".billing-page")).toBeVisible();
        await expect(page.locator("html")).toHaveAttribute("dir", locale.dir);
        const nav = page.locator(".desktop-nav");
        await expect(nav).toBeVisible();

        // The header may offer horizontal *internal* scrolling for translated
        // navigation links, but must not increase the page's scrollable width.
        const measure = () => page.evaluate(() => ({
          viewport: window.innerWidth,
          html: document.documentElement.scrollWidth,
          body: document.body.scrollWidth,
          nav: (document.querySelector(".desktop-nav") as HTMLElement)?.scrollWidth ?? 0,
          navClient: (document.querySelector(".desktop-nav") as HTMLElement)?.clientWidth ?? 0,
        }));
        const billing = await measure();
        const png = await page.screenshot({ fullPage: true });
        await testInfo.attach(`billing-${locale.key}-${width}px-tablet-header`, { body: png, contentType: "image/png" });
        expect(billing.viewport).toBe(width);
        expect(billing.html, "html scrollWidth").toBeLessThanOrEqual(width + 1);
        expect(billing.body, "body scrollWidth").toBeLessThanOrEqual(width + 1);
        expect(png.readUInt32BE(16), "full-page screenshot PNG width").toBeLessThanOrEqual(width + 1);
        expect(billing.navClient, "desktop navigation has a nonzero visible slot").toBeGreaterThan(0);

        // Route-specific billing styles must not hide a header regression on
        // the sibling Account page.
        await page.goto("/account");
        await expect(page.locator(".billing-page")).toHaveCount(0);
        const account = await measure();
        expect(account.html, "Account html scrollWidth").toBeLessThanOrEqual(width + 1);
        expect(account.body, "Account body scrollWidth").toBeLessThanOrEqual(width + 1);
      });
    }
  }
});
