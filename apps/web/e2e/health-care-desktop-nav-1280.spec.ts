import { test, expect } from "./fixtures";

/**
 * Issue #714 — desktop navigation lane.
 *
 * Owns exactly one surface that no other #714 lane pins: the HEALTH CARE ENTRY AS A
 * DESKTOP KEYBOARD USER SEES IT at a 1280px laptop viewport, for an authenticated
 * account in English, Arabic and Kurdish Sorani. For each locale it proves the
 * entry is (1) present and localized in the desktop navigation, (2) reachable with
 * the keyboard alone and visibly focused, (3) routed to the Health Care entry
 * `/create/health`, and (4) gated — arriving through navigation must not surface
 * clinical/document capability before any consent is granted.
 *
 * Why 1280px is not redundant with the existing lanes. The desktop chooser grid is
 * `repeat(4, minmax(0, 1fr))` above its 900px breakpoint, so 1280px exercises the
 * full desktop header navigation plus the four-up card grid at the most common
 * laptop width, while every existing desktop assertion is made at 1440px:
 *  - `health-care-tile.spec.ts` (#793): 1440px, mouse click only, English only.
 *  - `health-care-tile.rtl.spec.ts` (#793): 1440px, mouse click only, AR/CKB.
 *  - `health-care-tile.mobile.spec.ts` (#793) and
 *    `health-care-mobile-nav-focus-320.mobile.spec.ts` (#1173): 320px/375px mobile
 *    bottom navigation — the mobile keyboard-focus contract is theirs, not this one.
 *  - `health-care.spec.ts` (#715): keyboard tab order *inside* `/create/health`,
 *    never through the chooser, and locale changes via the Account form.
 *  - `health-care-truth.acceptance.spec.ts` (#1055) and
 *    `health-care-preauth*` (#1070/#1093/#1106): category truthfulness and the
 *    data-plane boundary, at 320px/1440px, no keyboard traversal.
 *  - `health-care-menu-consent-route.acceptance.spec.ts` (#1151): the consent
 *    journey itself. This lane deliberately asserts only the ABSENCE of
 *    pre-consent capability, so it stays disjoint from and unblocked by #812.
 *
 * Locale changes go through the real authenticated header language selector rather
 * than a blind storage write, so the copy under test is the copy a desktop user
 * would actually see after choosing a language.
 *
 * Boundaries honored: synthetic E2E accounts created and logged out by the existing
 * fixture only. No real or stored patient record, no upload, no document access, no
 * provider/AI/medical call, no spend, no secrets, no production file touched, no
 * merge, no deploy.
 */

const HEALTH_ROUTE = "/create/health";
/** The distinct desktop width owned by this lane. */
const DESKTOP_VIEWPORT = { width: 1280, height: 900 } as const;
/** The navigation labels from `lib/i18n.ts`, so a silent translation change fails here. */
const locales = [
  {
    code: "en",
    tag: "en",
    direction: "ltr",
    createLabel: "Create",
    category: "Health & Wellness",
    title: "Health Care",
    status: "Coming soon",
    backToCreate: "Back to Create",
  },
  {
    code: "ar",
    tag: "ar",
    direction: "rtl",
    createLabel: "إنشاء",
    category: "الصحة والعافية",
    title: "الرعاية الصحية",
    status: "قريبًا",
    backToCreate: "العودة إلى إنشاء",
  },
  {
    code: "ku",
    tag: "ckb",
    direction: "rtl",
    createLabel: "دروستکردن",
    category: "تەندروستی و خۆشگوزەرانی",
    title: "چاودێریی تەندروستی",
    status: "بەم زووانە",
    backToCreate: "گەڕانەوە بۆ دروستکردن",
  },
] as const;

/** Capability that must NOT be reachable from navigation before an explicit consent grant. */
const prematureCapability = /upload|analyze|analyse|extract|diagnos|summar/i;

type FocusStop = {
  tag: string;
  href: string | null;
  region: "skip" | "header" | "main" | "other" | "body";
  name: string;
  healthTile: boolean;
  left: number;
  right: number;
  outlineStyle: string;
  boxShadow: string;
};

async function capture(
  page: import("@playwright/test").Page,
  testInfo: import("@playwright/test").TestInfo,
  name: string,
) {
  const path = testInfo.outputPath(`${name}.png`);
  await page.screenshot({ path, fullPage: false });
  await testInfo.attach(`${name}.png`, { path, contentType: "image/png" });
}

/** Reads the element holding focus, with the computed properties that constitute its
 *  visible focus indicator and the viewport-relative box used to prove it is on-screen. */
async function focusedStop(page: import("@playwright/test").Page): Promise<FocusStop> {
  return page.evaluate(() => {
    const element = document.activeElement;
    if (!element || element === document.body) {
      return {
        tag: "BODY", href: null, region: "body" as const, name: "", healthTile: false,
        left: 0, right: 0, outlineStyle: "", boxShadow: "",
      };
    }
    const style = window.getComputedStyle(element);
    const box = element.getBoundingClientRect();
    const text = (element.textContent ?? "").replace(/\s+/g, " ").trim();
    const region = element.classList.contains("skip-link")
      ? "skip"
      : element.closest("header")
        ? "header"
        : element.closest("main")
          ? "main"
          : "other";
    return {
      tag: element.tagName,
      href: element instanceof HTMLAnchorElement ? element.getAttribute("href") : null,
      region: region as FocusStop["region"],
      name: (element.getAttribute("aria-label") ?? text).replace(/\s+/g, " ").trim(),
      healthTile: element.getAttribute("data-health-care-tile") === "true",
      left: Math.round(box.left),
      right: Math.round(box.right),
      outlineStyle: style.outlineStyle,
      boxShadow: style.boxShadow,
    };
  });
}

/** Blurs the document and walks Tab until `done` accepts a stop, bounded so that a
 *  focus trap fails an assertion instead of hanging the lane. */
async function tabUntil(
  page: import("@playwright/test").Page,
  done: (stop: FocusStop) => boolean,
  limit = 60,
): Promise<{ stops: FocusStop[]; hit: FocusStop | null }> {
  await page.evaluate(() => {
    if (document.activeElement instanceof HTMLElement) document.activeElement.blur();
  });
  const stops: FocusStop[] = [];
  for (let attempt = 0; attempt < limit; attempt += 1) {
    await page.keyboard.press("Tab");
    const stop = await focusedStop(page);
    stops.push(stop);
    if (done(stop)) return { stops, hit: stop };
  }
  return { stops, hit: null };
}

/** Chooses an interface language through the authenticated desktop header selector,
 *  then reloads so the durable (post-hydration restore) path is what gets asserted. */
async function useHeaderLanguage(
  page: import("@playwright/test").Page,
  locale: (typeof locales)[number],
) {
  // Scoped by the header's own class: the control's accessible name is localized,
  // so matching it by label would stop working once the page is not in English.
  await page.locator(".language-select select").selectOption(locale.code);
  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("lang", locale.tag);
  await expect(page.locator("html")).toHaveAttribute("dir", locale.direction);
}

/** The navigation-owned half of the contract: the entry is on screen, translated,
 *  single, and inside the 1280px canvas. */
async function expectLocalizedDesktopEntry(
  page: import("@playwright/test").Page,
  locale: (typeof locales)[number],
) {
  const tile = page.locator('main a[data-health-care-tile="true"]');
  // A real Health & Wellness category in `navigation.ts` must replace the hardcoded
  // section rather than render beside it; a duplicate card is a navigation defect.
  await expect(tile).toHaveCount(1);
  await expect(tile).toBeVisible();
  await expect(page.locator('section[data-health-care-category="true"] h3')).toHaveText(locale.category);
  await expect(tile.locator("strong")).toHaveText(locale.title);
  await expect(tile.locator("small")).toHaveText(locale.status);
  const box = await tile.boundingBox();
  expect(box, `the Health Care entry is measurable at ${DESKTOP_VIEWPORT}px`).not.toBeNull();
  if (!box) throw new Error("the Health Care entry is missing");
  expect(box.x).toBeGreaterThanOrEqual(0);
  expect(box.x + box.width).toBeLessThanOrEqual(DESKTOP_VIEWPORT.width + 1);
  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
  );
  expect(overflow, `${locale.code} desktop chooser does not overflow horizontally`).toBeLessThanOrEqual(1);
  return tile;
}

/** The route-gate half of the contract: navigation lands on the honest preview and
 *  exposes no clinical or document capability before consent. */
async function expectGatedLanding(
  page: import("@playwright/test").Page,
  locale: (typeof locales)[number],
) {
  await expect(page).toHaveURL(new RegExp(`${HEALTH_ROUTE}$`));
  await expect(page.getByRole("heading", { name: locale.title, level: 1 })).toBeVisible();
  await expect(page.getByRole("status")).toHaveText(locale.status);
  // Nothing capturable, nothing pre-selected, nothing already parsed.
  await expect(page.locator('input[type="file"]')).toHaveCount(0);
  await expect(page.locator("input:checked")).toHaveCount(0);
  await expect(page.locator("textarea")).toHaveCount(0);
  await expect(page.locator("iframe")).toHaveCount(0);
  const enabled = page.locator("main button:not([disabled]), main a").filter({ hasText: prematureCapability });
  await expect(enabled, `${locale.code} landing offers no capability before consent`).toHaveCount(0);
  await expect(page.locator('meta[name="robots"]')).toHaveAttribute("content", /noindex/);
}

test.describe("Health Care desktop navigation at 1280px - keyboard entry, localization and route gate", () => {
  test("walks the English desktop header navigation into the chooser and keyboard-activates the Health Care entry", async ({ authenticatedPage }, testInfo) => {
    const page = authenticatedPage;
    await page.setViewportSize(DESKTOP_VIEWPORT);
    await page.goto("/");

    // Step 1 — the desktop header navigation reaches Create without a mouse.
    const nav = await tabUntil(page, (stop) => stop.region === "header" && stop.href === "/create");
    expect(nav.hit, "the Create link in the desktop header is keyboard-reachable").not.toBeNull();
    if (!nav.hit) throw new Error("Create link was not reachable by keyboard");
    expect(nav.hit.name).toContain(locales[0].createLabel);
    await page.keyboard.press("Enter");
    await expect(page).toHaveURL(/\/create$/);

    // Step 2 — the entry is present, localized and single on the desktop chooser.
    const tile = await expectLocalizedDesktopEntry(page, locales[0]);
    await capture(page, testInfo, "01-create-chooser-en-desktop-1280");

    // Step 3 — Tab reaches the entry and focus is visibly indicated, not silent.
    const baseline = await tile.evaluate((element) => window.getComputedStyle(element).boxShadow);
    const walk = await tabUntil(page, (stop) => stop.healthTile);
    expect(walk.hit, "the Health Care entry is keyboard-reachable in the desktop chooser").not.toBeNull();
    if (!walk.hit) throw new Error("the Health Care entry is not in the desktop tab order");
    expect(walk.hit.region, "the entry is reached inside the main landmark").toBe("main");
    expect(walk.hit.href).toBe(HEALTH_ROUTE);
    expect(walk.hit.left).toBeGreaterThanOrEqual(0);
    expect(walk.hit.right).toBeLessThanOrEqual(DESKTOP_VIEWPORT.width + 1);
    const indicated = walk.hit.outlineStyle !== "none" || walk.hit.boxShadow !== baseline;
    expect(indicated, "keyboard focus on the entry is visually indicated").toBe(true);
    await capture(page, testInfo, "02-health-entry-keyboard-focus-en-1280");

    // Step 4 — Enter alone routes to the Health Care entry, gated.
    await page.keyboard.press("Enter");
    await expectGatedLanding(page, locales[0]);
    await capture(page, testInfo, "03-create-health-landed-keyboard-en-1280");

    // Step 5 — the desktop route returns to the chooser it came from.
    const back = page.getByRole("link", { name: locales[0].backToCreate });
    await expect(back).toBeVisible();
    await back.click();
    await expect(page).toHaveURL(/\/create$/);
    await expect(page.locator('a[data-health-care-tile="true"]')).toHaveCount(1);
  });

  for (const locale of [locales[1], locales[2]] as const) {
    test(`keyboard-activates the localized ${locale.tag} Health Care entry in the desktop chooser and lands on the gated route`, async ({ authenticatedPage }, testInfo) => {
      const page = authenticatedPage;
      await page.setViewportSize(DESKTOP_VIEWPORT);
      await page.goto("/create");
      await useHeaderLanguage(page, locale);

      const tile = await expectLocalizedDesktopEntry(page, locale);

      const baseline = await tile.evaluate((element) => window.getComputedStyle(element).boxShadow);
      const walk = await tabUntil(page, (stop) => stop.healthTile);
      expect(walk.hit, `the ${locale.tag} Health Care entry is keyboard-reachable`).not.toBeNull();
      if (!walk.hit) throw new Error(`the ${locale.tag} Health Care entry is not in the desktop tab order`);
      expect(walk.hit.region).toBe("main");
      expect(walk.hit.href).toBe(HEALTH_ROUTE);
      expect(walk.hit.outlineStyle !== "none" || walk.hit.boxShadow !== baseline,
        `keyboard focus on the ${locale.tag} entry is visually indicated`).toBe(true);
      await capture(page, testInfo, `04-create-chooser-${locale.code}-keyboard-focus-1280`);

      await page.keyboard.press("Enter");
      await expectGatedLanding(page, locale);
      await capture(page, testInfo, `05-create-health-${locale.code}-gated-1280`);
    });
  }

  test("keeps one keyboard-reachable Health Care entry in the desktop home launcher routed to the same gated route", async ({ authenticatedPage }, testInfo) => {
    const page = authenticatedPage;
    const locale = locales[0];
    await page.setViewportSize(DESKTOP_VIEWPORT);
    await page.goto("/");

    const launcher = page.locator('section.home-studios a[data-health-care-tile="true"]');
    await expect(launcher).toHaveCount(1);
    await expect(launcher).toBeVisible();
    await expect(launcher).toHaveAttribute("href", HEALTH_ROUTE);
    await capture(page, testInfo, "06-home-compact-launcher-en-1280");

    const baseline = await launcher.evaluate((element) => window.getComputedStyle(element).boxShadow);
    const walk = await tabUntil(page, (stop) => stop.healthTile);
    expect(walk.hit, "the home launcher entry is keyboard-reachable").not.toBeNull();
    if (!walk.hit) throw new Error("the home launcher entry is not in the desktop tab order");
    expect(walk.hit.outlineStyle !== "none" || walk.hit.boxShadow !== baseline).toBe(true);

    await page.keyboard.press("Enter");
    await expectGatedLanding(page, locale);
    await capture(page, testInfo, "07-home-launcher-gated-route-1280");
  });
});
