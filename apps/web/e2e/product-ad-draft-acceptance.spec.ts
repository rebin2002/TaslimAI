import { randomUUID } from "node:crypto";
import { E2E_PASSWORD, loginInUi, logoutInUi, registerInUi, test, expect } from "./fixtures";

const onePixelPng = Buffer.from(
  "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/aGQAAAAASUVORK5CYII=",
  "base64",
);

test.describe("Product Ad preview — real authenticated session, local-only draft", () => {
  for (const profile of [
    { width: 375, locale: "en", dir: "ltr" },
    { width: 375, locale: "ar", dir: "rtl" },
    { width: 768, locale: "ku", dir: "rtl" },
  ] as const) {
    test(`restores a private product draft at ${profile.width}px (${profile.locale}) without restoring file bytes`, async ({ authenticatedPage: page }, testInfo) => {
      const unexpectedProviderRequests: string[] = [];
      page.on("request", (request) => {
        if (/sunoapi|runway|\/api\/(generation|payments|checkout|provider)/i.test(request.url())) {
          unexpectedProviderRequests.push(request.url());
        }
      });

      await page.setViewportSize({ width: profile.width, height: 850 });
      await page.addInitScript((locale) => {
        window.localStorage.setItem("taslim-locale", locale);
      }, profile.locale);
      await page.goto("/create/product-ad");

      const name = page.locator('input[id$="-product-name"]');
      const benefit = page.locator('textarea[id$="-benefit"]');
      const photo = page.locator('input[id$="-product-photo"]');
      const logo = page.locator('input[id$="-brand-logo"]');
      const channel = page.locator('select[id$="-channel"]');
      const format = page.locator('select[id$="-format"]');
      const submit = page.locator('form button[type="submit"]');

      await expect(name).toBeVisible();
      await expect(photo).toHaveAttribute("required", "");
      await expect(logo).not.toHaveAttribute("required", "");
      await name.fill("Cedar & Silk");
      await benefit.fill("Keeps your skin soft");
      await channel.selectOption("tiktok");
      await format.selectOption("story");
      await photo.setInputFiles({ name: "product.png", mimeType: "image/png", buffer: onePixelPng });
      await logo.setInputFiles({ name: "brand-logo.png", mimeType: "image/png", buffer: onePixelPng });
      await submit.click();
      await expect(page.locator("aside h3")).toHaveText("Cedar & Silk");
      await expect(page.locator('aside img[data-preview-asset="product"]')).toHaveAttribute("src", /^blob:/);
      await expect(page.locator('aside img[data-preview-asset="brand-logo"]')).toHaveAttribute("src", /^blob:/);

      await page.reload();
      await expect(page.locator('aside img[data-preview-asset]')).toHaveCount(0);
      await expect(name).toHaveValue("Cedar & Silk");
      await expect(benefit).toHaveValue("Keeps your skin soft");
      await expect(channel).toHaveValue("tiktok");
      await expect(format).toHaveValue("story");
      await expect(page.locator('section[aria-labelledby] p[role="status"]')).toBeVisible();
      expect(await photo.evaluate((input) => (input as HTMLInputElement).files?.length ?? 0)).toBe(0);
      expect(await logo.evaluate((input) => (input as HTMLInputElement).files?.length ?? 0)).toBe(0);
      await photo.setInputFiles({ name: "product.png", mimeType: "image/png", buffer: onePixelPng });
      await logo.setInputFiles({ name: "brand-logo.png", mimeType: "image/png", buffer: onePixelPng });
      await submit.click();
      await expect(page.locator("aside h3")).toHaveText("Cedar & Silk");
      await expect(page.locator('aside img[data-preview-asset="product"]')).toHaveAttribute("src", /^blob:/);
      await expect(page.locator('aside img[data-preview-asset="brand-logo"]')).toHaveAttribute("src", /^blob:/);

      const overflow = await page.evaluate(() =>
        document.documentElement.scrollWidth - document.documentElement.clientWidth
      );
      expect(overflow, "No horizontal overflow").toBeLessThanOrEqual(1);
      expect(unexpectedProviderRequests).toEqual([]);

      const screenshot = testInfo.outputPath(`product-ad-${profile.locale}-${profile.width}.png`);
      await page.screenshot({ path: screenshot, fullPage: true });
      await testInfo.attach(`product-ad-${profile.locale}-${profile.width}`, {
        path: screenshot, contentType: "image/png",
      });
    });
  }
});


test("product-ad draft stays in its original account after an authenticated switch in the same tab", async ({ authenticatedPage: page }) => {
  await page.goto("/create/product-ad");
  const name = page.locator('input[id$="-product-name"]');
  const oldAccountDraft = "Account A private ad draft";
  await expect(name).toBeVisible();
  await name.fill(oldAccountDraft);
  // Assert the actual storage write completed before navigating away; this also
  // makes the test independent of React's effect scheduling.
  await page.waitForFunction((expected) => {
    for (let index = 0; index < window.sessionStorage.length; index++) {
      const key = window.sessionStorage.key(index);
      if (key?.startsWith("taslim:draft:product-ad:")
        && window.sessionStorage.getItem(key)?.includes(expected)) return true;
    }
    return false;
  }, oldAccountDraft);

  const originalUser = page.testUser;
  await logoutInUi(page);
  const secondUser = {
    displayName: "E2E Alternate Account",
    email: `e2e+ad-switch-${randomUUID()}@example.test`,
    password: E2E_PASSWORD,
  };
  await registerInUi(page, secondUser);
  await page.goto("/create/product-ad");
  await expect(name).toBeVisible();
  await expect(name).toHaveValue("");
  await expect(page.locator("main")).not.toContainText(oldAccountDraft);

  await name.fill("Account B distinct ad draft");
  await logoutInUi(page);
  await loginInUi(page, originalUser);
  await page.goto("/create/product-ad");
  await expect(name).toHaveValue(oldAccountDraft);
  await expect(page.locator("main")).not.toContainText("Account B distinct ad draft");
  // The first account's text settings survive; browser file-input bytes never do.
  expect(await page.locator('input[id$="-product-photo"]').evaluate((el) => (el as HTMLInputElement).files?.length ?? 0)).toBe(0);
});


// Integration acceptance: the preview is discoverable from both customer entry
// points, with the correct locale/RTL and no provider, checkout or publish call.
for (const entry of ["/create", "/"] as const) {
  for (const profile of [
    { locale: "en", heading: "Start with your product.", title: "Product Ad", dir: "ltr", width: 375 },
    { locale: "ar", heading: "ابدأ بمنتجك.", title: "إعلان منتج", dir: "rtl", width: 375 },
    { locale: "ku", heading: "لە بەرهەمەکەتەوە دەست پێ بکە.", title: "ڕیکلامی بەرهەم", dir: "rtl", width: 768 },
  ] as const) {
    test(`Product Ad appears once on ${entry} and opens its local-only preview (${profile.locale})`, async ({ authenticatedPage: page }) => {
      const unexpectedWrites: string[] = [];
      page.on("request", (request) => {
        if (/\/api\/(generation|payments|checkout|provider|assets\/upload)|sunoapi|runway/i.test(request.url())) {
          unexpectedWrites.push(request.url());
        }
      });
      await page.setViewportSize({ width: profile.width, height: 850 });
      await page.addInitScript((locale) => window.localStorage.setItem("taslim-locale", locale), profile.locale);
      await page.goto(entry);
      const tile = page.locator('a[data-product-ad-tile="true"]');
      await expect(tile).toHaveCount(1);
      await expect(tile).toBeVisible();
      await expect(tile.locator("strong")).toHaveText(profile.title);
      await expect(tile).toHaveAttribute("href", "/create/product-ad");
      await tile.click();
      await expect(page).toHaveURL(/\/create\/product-ad$/);
      const wizard = page.locator('section[aria-labelledby][lang]');
      await expect(wizard).toHaveAttribute("dir", profile.dir);
      await expect(wizard.locator("h1")).toHaveText(profile.heading);
      await expect(wizard.locator('input[id$="-product-name"]')).toBeVisible();
      await expect(page.getByText(/local-only preview|معاينة محلية فقط|تەنها پێشبینینی ناوخۆییە/)).toBeVisible();
      const geometry = await page.evaluate(() => ({
        html: document.documentElement.scrollWidth - document.documentElement.clientWidth,
        body: document.body.scrollWidth - document.documentElement.clientWidth,
      }));
      expect(geometry.html).toBeLessThanOrEqual(1);
      expect(geometry.body).toBeLessThanOrEqual(1);
      expect(unexpectedWrites).toEqual([]);
    });
  }
}
