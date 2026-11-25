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

const tile = (page: import("@playwright/test").Page) =>
  page.locator('a[data-health-care-tile="true"]');

test.describe("Health Care chooser tile", () => {
  test("is visible and clickable on the Create chooser", async ({ authenticatedPage }, testInfo) => {
    const page = authenticatedPage;
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto("/create");

    const healthTile = tile(page);
    await expect(healthTile).toBeVisible();
    await expect(healthTile).toHaveAttribute("href", "/create/health");
    await expect(healthTile.getByRole("strong", { level: undefined })).toContainText("Health Care");
    await expect(healthTile.locator("small")).toContainText("Coming soon");
    await expect(page.locator('section[data-health-care-category="true"]'))
      .toContainText("Health & Wellness");

    const box = await healthTile.boundingBox();
    expect(box, "the tile is measurable").not.toBeNull();
    await capture(page, testInfo, "01-create-chooser-en-desktop-1440");

    await healthTile.click();
    await expect(page).toHaveURL(/\/create\/health$/);
    await expect(page.getByRole("heading", { name: "Health Care", level: 1 })).toBeVisible();
    await expect(page.getByRole("status")).toContainText("Coming soon");
    await capture(page, testInfo, "02-create-health-landed-from-tile");
  });

  test("is visible on the compact home launcher and links to the same route", async ({ authenticatedPage }, testInfo) => {
    const page = authenticatedPage;
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto("/");

    const homeTile = page.locator('section.home-studios a[data-health-care-tile="true"]');
    await expect(homeTile).toBeVisible();
    await expect(homeTile).toHaveAttribute("href", "/create/health");
    await capture(page, testInfo, "03-home-compact-launcher-en");

    await homeTile.click();
    await expect(page).toHaveURL(/\/create\/health$/);
  });
});
