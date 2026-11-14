import { test, expect } from "./fixtures";

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

      await page.reload();
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
