import { randomUUID } from "node:crypto";
import { E2E_PASSWORD, logoutInUi, registerInUi, loginInUi, test, expect } from "./fixtures";

/**
 * Actual authenticated, disposable-API browser acceptance for the synthetic-only
 * Health Care consent journey. No patient file, upload, provider or charge is used.
 * The same grant / select / preview / revoke / regrant contract must work in
 * English, Arabic and Kurdish Sorani, including RTL and a fresh page mount.
 */
const locales = [
  {
    code: "en", tag: "en", direction: "ltr",
    first: "Sample lab report (synthetic)",
    second: "Sample clinic note (synthetic)",
    grant: "Enable private documents",
    revoke: "Withdraw consent",
    preview: "Open preview",
    previewNotice: "No preview is available yet. Nothing was uploaded or analyzed.",
    revokedNotice: "Consent withdrawn. Selections were cleared.",
  },
  {
    code: "ar", tag: "ar", direction: "rtl",
    first: "تقرير مختبر تجريبي (تخليقي)",
    second: "ملاحظة عيادة تجريبية (تخليقية)",
    grant: "تفعيل المستندات الخاصة",
    revoke: "سحب الموافقة",
    preview: "فتح المعاينة",
    previewNotice: "لا توجد معاينة حتى الآن. لم يُرفع أو يُحلَّل أي شيء.",
    revokedNotice: "تم سحب الموافقة. تمت إزالة جميع الاختيارات.",
  },
  {
    code: "ku", tag: "ckb", direction: "rtl",
    first: "نموونەی ڕاپۆرتی تاقیگە (دروستکراو)",
    second: "نووسرای کلینیکی نموونەیی (دروستکراو)",
    grant: "چالاککردنی بەڵگەنامە تایبەتەکان",
    revoke: "پاشگەزبوونەوە لە ڕازی بوون",
    preview: "کردنەوەی پێشاندان",
    previewNotice: "هێشتا پێشاندان بەردەست نییە. هیچ شتێک بار نەکراوە یان شیکاری نەکراوە.",
    revokedNotice: "ڕازی بوون کشێرایەوە. هەڵبژاردنەکان سڕایەوە.",
  },
] as const;

test.describe("Health Care synthetic consent journey — authenticated localized browser", () => {
  for (const locale of locales) {
    test("grant, select, preview, revoke and fresh-mount privacy in " + locale.code, async ({ authenticatedPage: page }, testInfo) => {
      const unexpectedRequests: string[] = [];
      page.on("request", (request) => {
        if (/\/api\/(health|uploads|generation|providers)|sunoapi|runway/i.test(request.url())) {
          unexpectedRequests.push(request.url());
        }
      });

      await page.setViewportSize({ width: 375, height: 900 });
      await page.goto("/create/health");
      await page.evaluate((value) => localStorage.setItem("taslim-locale", value), locale.code);
      await page.reload();

      const journey = page.locator(".health-consent-journey");
      const consent = journey.locator(".health-consent-selection");
      await expect(page.locator("html")).toHaveAttribute("lang", locale.tag);
      await expect(page.locator("html")).toHaveAttribute("dir", locale.direction);
      await expect(journey).toHaveAttribute("dir", locale.direction);
      await expect(journey).toHaveAttribute("lang", locale.tag);
      await expect(journey.locator("input, textarea, [type='file'], form")).toHaveCount(0);

      const grant = consent.getByRole("button", { name: locale.grant });
      await expect(grant).toBeVisible();
      await expect(consent.getByText(locale.first)).toHaveCount(0);
      await expect(journey.getByText(locale.previewNotice)).toHaveCount(0);
      await grant.click();

      const first = consent.getByRole("button", { name: locale.first });
      const second = consent.getByRole("button", { name: locale.second });
      await expect(first).toHaveAttribute("aria-pressed", "false");
      await expect(second).toHaveAttribute("aria-pressed", "false");
      const previews = consent.getByRole("button", { name: locale.preview });
      await expect(previews).toHaveCount(2);
      await expect(previews.nth(0)).toBeDisabled();
      await expect(previews.nth(1)).toBeDisabled();

      await first.click();
      await expect(first).toHaveAttribute("aria-pressed", "true");
      await expect(previews.nth(0)).toBeEnabled();
      await expect(previews.nth(1)).toBeDisabled();
      await previews.nth(0).click();
      await expect(journey.getByText(locale.previewNotice)).toBeVisible();

      const overflow = await page.evaluate(() =>
        Math.max(document.documentElement.scrollWidth, document.body.scrollWidth) -
        document.documentElement.clientWidth
      );
      expect(overflow, "375px localized consent journey should not overflow").toBeLessThanOrEqual(1);
      const shot = testInfo.outputPath("health-consent-" + locale.code + "-375.png");
      await page.screenshot({ path: shot, fullPage: true });
      await testInfo.attach("health-consent-" + locale.code + "-375.png", { path: shot, contentType: "image/png" });

      await consent.getByRole("button", { name: locale.revoke }).click();
      await expect(consent.getByText(locale.first)).toHaveCount(0);
      await expect(consent.getByText(locale.second)).toHaveCount(0);
      await expect(previews).toHaveCount(0);
      await expect(journey.getByText(locale.previewNotice)).toHaveCount(0);
      await expect(consent.getByText(locale.revokedNotice)).toBeVisible();
      await consent.getByRole("button", { name: locale.grant }).click();
      await expect(consent.getByRole("button", { name: locale.first })).toHaveAttribute("aria-pressed", "false");
      await expect(previews.nth(0)).toBeDisabled();

      // Client-only consent is never persisted; a new mount is opt-in again.
      await page.reload();
      await expect(consent.getByRole("button", { name: locale.grant })).toBeVisible();
      await expect(consent.getByText(locale.first)).toHaveCount(0);
      await expect(previews).toHaveCount(0);
      expect(unexpectedRequests).toEqual([]);
    });
  }
});

test("Health consent remains opt-in across a same-tab account switch", async ({ authenticatedPage: page }) => {
  const unexpectedHealthRequests: string[] = [];
  page.on("request", (request) => {
    if (/\/api\/(health|uploads|generation|providers)|sunoapi|runway/i.test(request.url())) {
      unexpectedHealthRequests.push(request.url());
    }
  });
  await page.goto("/create/health");
  const journey = page.locator(".health-consent-journey");
  const consent = journey.locator(".health-consent-selection");
  await consent.getByRole("button", { name: "Enable private documents" }).click();
  const originalSample = consent.getByRole("button", { name: "Sample lab report (synthetic)" });
  await originalSample.click();
  await expect(originalSample).toHaveAttribute("aria-pressed", "true");
  const previews = consent.getByRole("button", { name: "Open preview" });
  await previews.first().click();
  await expect(journey.getByText("No preview is available yet. Nothing was uploaded or analyzed.")).toBeVisible();

  const originalAccount = page.testUser;
  await logoutInUi(page);
  await registerInUi(page, {
    displayName: "Health Consent Account B",
    email: `e2e+health-consent-${randomUUID()}@example.test`,
    password: E2E_PASSWORD,
  });
  await page.goto("/create/health");
  await expect(consent.getByRole("button", { name: "Enable private documents" })).toBeVisible();
  await expect(consent.getByText("Sample lab report (synthetic)")).toHaveCount(0);
  await expect(previews).toHaveCount(0);

  await logoutInUi(page);
  await loginInUi(page, originalAccount);
  await page.goto("/create/health");
  await expect(consent.getByRole("button", { name: "Enable private documents" })).toBeVisible();
  await expect(consent.getByText("Sample lab report (synthetic)")).toHaveCount(0);
  await expect(previews).toHaveCount(0);
  expect(unexpectedHealthRequests).toEqual([]);
});
