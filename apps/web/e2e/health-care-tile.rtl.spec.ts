import { test, expect } from "./fixtures";

async function capture(
  page: import("@playwright/test").Page,
  testInfo: import("@playwright/test").TestInfo,
  name: string,
) {
  const path = testInfo.outputPath(`${name}.png`);
  await page.screenshot({ path, fullPage: true });
  await testInfo.attach(`${name}.png`, { path, contentType: "image/png" });
}

/** Literal EN/AR/CKB acceptance contract for the chooser tile, mirroring the
 *  Health route lane's feature-local copy so a silent string change fails here. */
const rtlLocales = [
  {
    code: "ar",
    tag: "ar",
    category: "الصحة والعافية",
    title: "الرعاية الصحية",
    status: "قريبًا",
  },
  {
    code: "ku",
    tag: "ckb",
    category: "تەندروستی و خۆشگوزەرانی",
    title: "چاودێریی تەندروستی",
    status: "بەم زووانە",
  },
] as const;

test.describe("Health Care chooser tile in RTL locales", () => {
  for (const locale of rtlLocales) {
    test(`renders the localized ${locale.code} tile and lands on the RTL Health Care preview`, async ({ authenticatedPage }, testInfo) => {
      const page = authenticatedPage;
      await page.setViewportSize({ width: 1440, height: 900 });
      await page.goto("/create");
      await page.evaluate((value) => localStorage.setItem("taslim-locale", value), locale.code);
      await page.reload();

      await expect(page.locator("html")).toHaveAttribute("lang", locale.tag);
      await expect(page.locator("html")).toHaveAttribute("dir", "rtl");

      const healthTile = page.locator('a[data-health-care-tile="true"]');
      await expect(healthTile).toBeVisible();
      await expect(healthTile.locator("strong")).toHaveText(locale.title);
      await expect(healthTile.locator("small")).toHaveText(locale.status);
      await expect(page.locator('section[data-health-care-category="true"] h3')).toHaveText(locale.category);

      const overflow = await page.evaluate(
        () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
      );
      expect(overflow, `${locale.code} chooser does not overflow horizontally`).toBeLessThanOrEqual(1);

      await capture(page, testInfo, `create-chooser-${locale.code}-rtl`);

      await healthTile.click();
      await expect(page).toHaveURL(/\/create\/health$/);
      await expect(page.getByRole("heading", { name: locale.title, level: 1 })).toBeVisible();
      await expect(page.getByRole("status")).toHaveText(locale.status);
      await expect(page.locator("form, input, textarea, [type='file']")).toHaveCount(0);
      await capture(page, testInfo, `create-health-${locale.code}-rtl`);
    });
  }
});
