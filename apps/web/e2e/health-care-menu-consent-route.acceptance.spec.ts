import { test, expect } from "./fixtures";

/**
 * Issue #714 — narrowly scoped synthetic integration verification.
 *
 * The one contract no existing lane pins end to end: the *visible* Health Care
 * entry in the authenticated Create chooser must deliver the privacy-safe
 * route, and that route must offer document selection ONLY after an explicit
 * consent grant and remove every selection affordance synchronously when the
 * consent is withdrawn — in English, Arabic and Kurdish Sorani, including the
 * RTL direction and the localized copy of the menu entry itself.
 *
 * Deliberately disjoint from the owning lanes (nothing here re-implements or
 * mutates their behaviour):
 *  - #715/#793 `e2e/health-care-tile*.spec.ts` and #1055
 *    `health-care-truth.acceptance.spec.ts`: menu entry plus the placeholder
 *    route, never the consent journey;
 *  - #804/#812 `e2e/health-care-consent-interaction.spec.ts`: the consent
 *    journey reached by a direct `/create/health` navigation;
 *  - #794 `healthCareConsentRevocation.test.ts` and #780
 *    `healthCareCreateMenuSmoke.test.ts`: unit-level controller/menu contracts.
 *
 * Boundaries honored: invented synthetic sample documents only, no real or
 * stored patient record, no upload, no attachment, no provider/AI/network
 * health call, no secrets, no consent-default change, no deploy, no merge.
 */
const HEALTH_ROUTE = "/create/health";

/** Literal cross-surface copy contract: menu entry text plus consent journey text.
 *  A silent drift between the tile lane (#715/#793) and the consent lane
 *  (#804/#812) must fail here, because that drift is what breaks the journey. */
const locales = [
  {
    code: "en",
    tag: "en",
    direction: "ltr",
    category: "Health & Wellness",
    title: "Health Care",
    status: "Coming soon",
    journeyHeading: "Consent preview (synthetic)",
    optInTitle: "Use your own health documents",
    grant: "Enable private documents",
    activeTitle: "Private documents",
    first: "Sample lab report (synthetic)",
    second: "Sample clinic note (synthetic)",
    preview: "Open preview",
    revoke: "Withdraw consent",
    revokedNotice: "Consent withdrawn. Selections were cleared.",
    previewNotice: "No preview is available yet. Nothing was uploaded or analyzed.",
  },
  {
    code: "ar",
    tag: "ar",
    direction: "rtl",
    category: "الصحة والعافية",
    title: "الرعاية الصحية",
    status: "قريبًا",
    journeyHeading: "معاينة الموافقة (بيانات تجريبية)",
    optInTitle: "استخدام مستنداتك الصحية الخاصة",
    grant: "تفعيل المستندات الخاصة",
    activeTitle: "المستندات الخاصة",
    first: "تقرير مختبر تجريبي (تخليقي)",
    second: "ملاحظة عيادة تجريبية (تخليقية)",
    preview: "فتح المعاينة",
    revoke: "سحب الموافقة",
    revokedNotice: "تم سحب الموافقة. تمت إزالة جميع الاختيارات.",
    previewNotice: "لا توجد معاينة حتى الآن. لم يُرفع أو يُحلَّل أي شيء.",
  },
  {
    code: "ku",
    tag: "ckb",
    direction: "rtl",
    category: "تەندروستی و خۆشگوزەرانی",
    title: "چاودێریی تەندروستی",
    status: "بەم زووانە",
    journeyHeading: "پێشاندانی ڕازی بوون (دراوستکراو)",
    optInTitle: "بەکارهێنانی بەڵگەنامە تەندروستییە تایبەتەکانت",
    grant: "چالاککردنی بەڵگەنامە تایبەتەکان",
    activeTitle: "بەڵگەنامە تایبەتەکان",
    first: "نموونەی ڕاپۆرتی تاقیگە (دروستکراو)",
    second: "نووسرای کلینیکی نموونەیی (دروستکراو)",
    preview: "کردنەوەی پێشاندان",
    revoke: "پاشگەزبوونەوە لە ڕازی بوون",
    revokedNotice: "ڕازی بوون کشێرایەوە. هەڵبژاردنەکان سڕایەوە.",
    previewNotice: "هێشتا پێشاندان بەردەست نییە. هیچ شتێک بار نەکراوە یان شیکاری نەکراوە.",
  },
] as const;

async function capture(
  page: import("@playwright/test").Page,
  testInfo: import("@playwright/test").TestInfo,
  name: string,
) {
  const path = testInfo.outputPath(`${name}.png`);
  await page.screenshot({ path, fullPage: true });
  await testInfo.attach(`${name}.png`, { path, contentType: "image/png" });
}

/** The synthetic identifiers and any consent/selection state must never reach
 *  browser storage: consent lives in memory for the mounted screen only. */
async function storageViolations(page: import("@playwright/test").Page) {
  return page.evaluate(() => {
    const keys = [...Object.keys(window.localStorage), ...Object.keys(window.sessionStorage)];
    const found: string[] = [];
    for (const key of keys) {
      if (/consent|health|document|selection|private/i.test(key)) found.push(`storage key: ${key}`);
      const value = `${window.localStorage.getItem(key) ?? ""}${window.sessionStorage.getItem(key) ?? ""}`;
      if (/synth-journey|sample lab|تجريبي|نموونە/i.test(value)) found.push(`storage value under ${key}`);
    }
    return found;
  });
}

test.describe("Health Care #714 Create-menu entry to consent-gated route — authenticated localized journey", () => {
  for (const locale of locales) {
    test(`${locale.code}: menu click delivers the route, selection only after consent, revoke clears everything`, async ({ authenticatedPage }, testInfo) => {
      const page = authenticatedPage;
      const unexpectedRequests: string[] = [];
      page.on("request", (request) => {
        if (/\/api\/(health|uploads|generation|providers)|sunoapi|runway/i.test(request.url())) {
          unexpectedRequests.push(request.url());
        }
      });

      // Open the authenticated chooser in the target locale before touching the menu.
      await page.setViewportSize({ width: 1440, height: 900 });
      await page.goto("/create");
      await page.evaluate((value) => localStorage.setItem("taslim-locale", value), locale.code);
      await page.reload();
      await expect(page.locator("html")).toHaveAttribute("lang", locale.tag);
      await expect(page.locator("html")).toHaveAttribute("dir", locale.direction);

      // Gate 1: the entry is a real, visible, localized card naming the route.
      const category = page.locator('section[data-health-care-category="true"]');
      await expect(category).toBeVisible();
      await expect(category.locator("h3")).toHaveText(locale.category);
      const tile = page.locator('a[data-health-care-tile="true"]');
      await expect(tile).toBeVisible();
      await expect(tile).toHaveAttribute("href", HEALTH_ROUTE);
      await expect(tile.locator("strong")).toHaveText(locale.title);
      await expect(tile.locator("small")).toHaveText(locale.status);
      await capture(page, testInfo, `01-create-menu-${locale.code}`);

      // Gate 2: a click inside the menu (never a direct goto) reaches the route.
      await tile.click();
      await expect(page).toHaveURL(new RegExp(`${HEALTH_ROUTE}$`));
      await expect(page.getByRole("heading", { name: locale.title, level: 1 })).toBeVisible();
      await expect(page.locator("#health-privacy-note")).toBeVisible();
      await expect(page.locator('meta[name="robots"]')).toHaveAttribute("content", /noindex/i);

      // Gate 3: consent is inert on arrival — nothing can be selected yet.
      const journey = page.locator(".health-consent-journey");
      await expect(journey).toBeVisible();
      await expect(journey).toHaveAttribute("dir", locale.direction);
      await expect(journey).toHaveAttribute("lang", locale.tag);
      await expect(journey.getByRole("heading", { name: locale.journeyHeading, level: 2 })).toBeVisible();
      const consent = journey.locator(".health-consent-selection");
      await expect(consent.getByRole("heading", { name: locale.optInTitle, level: 2 })).toBeVisible();
      const grant = consent.getByRole("button", { name: locale.grant });
      await expect(grant).toBeVisible();
      await expect(consent.getByRole("button", { name: locale.first })).toHaveCount(0);
      await expect(consent.getByRole("button", { name: locale.second })).toHaveCount(0);
      await expect(consent.getByRole("button", { name: locale.preview })).toHaveCount(0);
      await expect(consent.getByRole("button", { name: locale.revoke })).toHaveCount(0);
      await expect(journey.locator("input, textarea, [type='file'], form")).toHaveCount(0);
      expect(await storageViolations(page), "no consent or document state before the grant").toEqual([]);
      await capture(page, testInfo, `02-inert-opt-in-${locale.code}`);

      // Gate 4: explicit consent unlocks selection, and only for what is selected.
      await grant.click();
      const first = consent.getByRole("button", { name: locale.first });
      const second = consent.getByRole("button", { name: locale.second });
      await expect(consent.getByRole("heading", { name: locale.activeTitle, level: 2 })).toBeVisible();
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
      expect(await page.evaluate(() => document.body.innerText), "synthetic ids are never rendered")
        .not.toContain("synth-journey");
      await capture(page, testInfo, `03-granted-selected-${locale.code}`);

      // Gate 5: withdrawal clears rows, selection and previews in the same pass.
      await consent.getByRole("button", { name: locale.revoke }).click();
      await expect(consent.getByRole("heading", { name: locale.optInTitle, level: 2 })).toBeVisible();
      await expect(consent.getByText(locale.revokedNotice)).toBeVisible();
      await expect(consent.getByRole("button", { name: locale.first })).toHaveCount(0);
      await expect(consent.getByRole("button", { name: locale.second })).toHaveCount(0);
      await expect(previews).toHaveCount(0);
      await expect(consent.getByRole("button", { name: locale.revoke })).toHaveCount(0);
      await expect(journey.getByText(locale.previewNotice)).toHaveCount(0);
      await capture(page, testInfo, `04-revoked-${locale.code}`);

      // Gate 6: nothing persisted, so a fresh mount is opt-in again.
      await page.reload();
      await expect(consent.getByRole("button", { name: locale.grant })).toBeVisible();
      await expect(consent.getByRole("button", { name: locale.first })).toHaveCount(0);
      await expect(previews).toHaveCount(0);
      expect(await storageViolations(page), "consent and selection are never written to storage").toEqual([]);
      expect(unexpectedRequests, `${locale.code}: no health/document/provider request across the journey`).toEqual([]);
    });
  }

  test("the compact Home launcher entry reaches the same consent-gated route", async ({ authenticatedPage }, testInfo) => {
    const page = authenticatedPage;
    const unexpectedRequests: string[] = [];
    page.on("request", (request) => {
      if (/\/api\/(health|uploads|generation|providers)|sunoapi|runway/i.test(request.url())) {
        unexpectedRequests.push(request.url());
      }
    });

    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto("/");
    const homeTile = page.locator('section.home-studios a[data-health-care-tile="true"]');
    await expect(homeTile).toBeVisible();
    await expect(homeTile).toHaveAttribute("href", HEALTH_ROUTE);
    await capture(page, testInfo, "05-home-launcher");
    await homeTile.click();
    await expect(page).toHaveURL(new RegExp(`${HEALTH_ROUTE}$`));

    const journey = page.locator(".health-consent-journey");
    await expect(journey).toBeVisible();
    const consent = journey.locator(".health-consent-selection");
    await expect(consent.getByRole("button", { name: locales[0].first })).toHaveCount(0);
    await consent.getByRole("button", { name: locales[0].grant }).click();
    const first = consent.getByRole("button", { name: locales[0].first });
    await expect(first).toHaveCount(1);
    await consent.getByRole("button", { name: locales[0].revoke }).click();
    await expect(consent.getByText(locales[0].revokedNotice)).toBeVisible();
    await expect(consent.getByRole("button", { name: locales[0].first })).toHaveCount(0);
    expect(await storageViolations(page), "no consent state from the launcher path").toEqual([]);
    expect(unexpectedRequests, "no health/document/provider request from the launcher path").toEqual([]);
  });
});
