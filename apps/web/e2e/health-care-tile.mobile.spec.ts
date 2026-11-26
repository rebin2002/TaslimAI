import { test, expect } from "./fixtures";

async function capture(
  page: import("@playwright/test").Page,
  testInfo: import("@playwright/test").TestInfo,
  name: string,
) {
  const path = testInfo.outputPath(`${name}.png`);
  await page.screenshot({ path, fullPage: false });
  await testInfo.attach(`${name}.png`, { path, contentType: "image/png" });
}

test.describe("Health Care chooser tile on small screens", () => {
  for (const width of [320, 375]) {
    test(`stays visible, inside the viewport, and tappable at ${width}px`, async ({ authenticatedPage }, testInfo) => {
      const page = authenticatedPage;
      await page.setViewportSize({ width, height: 720 });
      await page.goto("/create");

      const healthTile = page.locator('a[data-health-care-tile="true"]');
      await expect(healthTile).toBeVisible();

      const box = await healthTile.boundingBox();
      expect(box, `tile is measurable at ${width}px`).not.toBeNull();
      if (!box) throw new Error(`tile is missing at ${width}px`);
      expect(box.x).toBeGreaterThanOrEqual(0);
      expect(box.x + box.width).toBeLessThanOrEqual(width + 1);

      const overflow = await page.evaluate(
        () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
      );
      expect(overflow, `no horizontal page overflow at ${width}px`).toBeLessThanOrEqual(1);

      await capture(page, testInfo, `create-chooser-${width}px`);

      await healthTile.click();
      await expect(page).toHaveURL(/\/create\/health$/);
      await expect(page.getByRole("heading", { name: "Health Care", level: 1 })).toBeVisible();
    });
  }
});
