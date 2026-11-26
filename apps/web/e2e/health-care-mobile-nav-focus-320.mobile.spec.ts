import { test, expect } from "./fixtures";

/**
 * Issue #714 - narrowly scoped synthetic verification of the *mobile navigation*
 * half of the Health Care entry, which no existing lane pins:
 *
 *  - `health-care-tile.mobile.spec.ts` (#715/#793) drives the chooser at 320/375px
 *    in English with mouse clicks and a direct `/create` goto;
 *  - `health-care-tile.rtl.spec.ts` (#715/#793) pins the Arabic/Sorani copy but only
 *    on a 1440px desktop chooser;
 *  - #1151 pins the Create-menu entry into the consent journey at 1440px, by click;
 *  - `mobile.spec.ts` pins the five bottom-nav links in English with no Health entry
 *    and no keyboard traversal; `rtl.spec.ts` presses a single Tab and only asserts
 *    that *something* has focus.
 *
 * The contract asserted here is the one that combination leaves open: at the 320px
 * canvas, in the two RTL locales, the localized Health Care entry must be reachable
 * by keyboard through the *persistent mobile navigation* surface - in a stable order
 * that never strands focus off-canvas or behind the hidden desktop nav - and the
 * consent-gated route it points at must still refuse an unauthenticated request from
 * that same mobile RTL surface. A keyboard-locked or screen-reader mobile reader who
 * cannot reach the entry, or a mobile entry that leaks the gated route, is exactly
 * what this catches.
 *
 * Source files owned by #1151/#804/#812/#715 are not touched: this file only reads
 * the mounted product. Synthetic invented fixtures only: no medical data, no real or
 * stored patient record, no upload, no attachment, no provider/AI call, no spend, no
 * secrets, no migration, no consent-default change, no deploy, no merge.
 */
const MOBILE_VIEWPORT = { width: 320, height: 720 };
const HEALTH_ROUTE = "/create/health";

/** Literal RTL contract for the mobile navigation surface plus the Health entry.
 *  `nav` is the bottom-navigation label order documented in
 *  `apps/web/src/lib/navigation.ts` (primaryNavigation), which the compact mobile
 *  bar renders verbatim; a silent rename or reorder of a mobile nav label, or of the
 *  localized entry that must be reachable behind it, fails here. */
const rtlLocales = [
  {
    code: "ar",
    tag: "ar",
    skipToContent: "انتقل إلى المحتوى الرئيسي",
    mobileNavLabel: "التنقل الرئيسي للهاتف",
    nav: ["الرئيسية", "المشاريع", "إنشاء", "النشاط", "الحساب"],
    title: "الرعاية الصحية",
    status: "قريبًا",
  },
  {
    code: "ku",
    tag: "ckb",
    skipToContent: "بازدان بۆ ناوەڕۆکی سەرەکی",
    mobileNavLabel: "ڕێڕەوی سەرەکیی مۆبایل",
    nav: ["سەرەکی", "پڕۆژەکان", "دروستکردن", "چالاکی", "هەژمار"],
    title: "چاودێریی تەندروستی",
    status: "بەم زووانە",
  },
] as const;

/** The navigation hrefs the compact bar owns, in DOM order. */
const navHrefs = ["/", "/projects", "/create", "/notifications", "/account"] as const;

type FocusStop = {
  tag: string;
  href: string | null;
  className: string;
  region: string;
  name: string;
  left: number;
  right: number;
  healthEntry: boolean;
  ariaCurrent: string | null;
};

/** Records the element that actually received focus after each Tab press, together
 *  with the viewport-relative box needed to prove nothing lands off-canvas at 320px. */
async function nextFocusStop(page: import("@playwright/test").Page): Promise<FocusStop> {
  return page.evaluate(() => {
    const element = document.activeElement;
    if (!element || element === document.body) {
      return { tag: "BODY", href: null, className: "", region: "body", name: "", left: 0, right: 0, healthEntry: false, ariaCurrent: null };
    }
    const box = element.getBoundingClientRect();
    const anchor = element instanceof HTMLAnchorElement ? element : null;
    const inBottomNav = element.closest("nav.mobile-bottom-nav") !== null;
    const region = element.classList.contains("skip-link")
      ? "skip"
      : inBottomNav
        ? "bottomnav"
        : element.closest("main")
          ? "main"
          : element.closest("header")
            ? "header"
            : "other";
    const text = (element.textContent ?? "").replace(/\s+/g, " ").trim();
    return {
      tag: element.tagName,
      href: anchor?.getAttribute("href") ?? null,
      className: typeof element.className === "string" ? element.className : "",
      region,
      name: (element.getAttribute("aria-label") ?? text).replace(/\s+/g, " ").trim(),
      left: Math.round(box.left),
      right: Math.round(box.right),
      healthEntry: element.getAttribute("data-health-care-tile") === "true",
      ariaCurrent: element.getAttribute("aria-current"),
    };
  });
}

/** Walks the tab sequence once from a neutralized document and returns every stop
 *  until the mobile bottom navigation has been fully traversed. Bounded so a focus
 *  trap fails the traversal assertions instead of hanging the lane. */
async function captureTabStops(page: import("@playwright/test").Page, limit = 40): Promise<FocusStop[]> {
  await page.evaluate(() => {
    if (document.activeElement instanceof HTMLElement) document.activeElement.blur();
  });
  const stops: FocusStop[] = [];
  for (let attempt = 0; attempt < limit; attempt += 1) {
    await page.keyboard.press("Tab");
    const stop = await nextFocusStop(page);
    stops.push(stop);
    if (stop.region === "bottomnav" && stop.href === "/account") break;
  }
  return stops;
}

async function capture(
  page: import("@playwright/test").Page,
  testInfo: import("@playwright/test").TestInfo,
  name: string,
) {
  const path = testInfo.outputPath(`${name}.png`);
  await page.screenshot({ path, fullPage: false });
  await testInfo.attach(`${name}.png`, { path, contentType: "image/png" });
}

/** Applies the device-level RTL choice before the chooser mounts, the same
 *  persistence contract the Arabic/Sorani chooser lanes use, then lands on the
 *  mobile Create surface through the bottom navigation rather than a goto.
 *
 *  The final reload is not decoration: a click leaves the browser's sequential
 *  focus navigation origin on the clicked nav link, so the next Tab would resume
 *  from the middle of the bar instead of the top of the document. Reloading the
 *  already-localized route restores the true keyboard entry point - which is the
 *  state an actual mobile reader lands in when they open the Create surface. */
async function openMobileCreateInLocale(
  page: import("@playwright/test").Page,
  locale: (typeof rtlLocales)[number],
) {
  await page.setViewportSize(MOBILE_VIEWPORT);
  await page.goto("/");
  await page.evaluate((value) => localStorage.setItem("taslim-locale", value), locale.code);
  await page.goto("/projects");
  await expect(page.locator("html")).toHaveAttribute("lang", locale.tag);
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  const nav = page.locator("nav.mobile-bottom-nav");
  await expect(nav).toBeVisible();
  await expect(nav.locator(`a[href="/create"]`)).toHaveText(new RegExp(locale.nav[2]));
  await nav.locator(`a[href="/create"]`).click();
  await expect(page).toHaveURL(/\/create$/);
  await expect(page.locator("html")).toHaveAttribute("lang", locale.tag);
  await page.reload();
  await expect(page.locator('a[data-health-care-tile="true"]')).toBeVisible();
}

test.describe("Health Care #714 mobile navigation at 320px in RTL - keyboard entry and route gate", () => {
  for (const locale of rtlLocales) {
    test(`${locale.code}: the localized Health Care entry is keyboard-reachable through the mobile navigation at 320px`, async ({ authenticatedPage }, testInfo) => {
      const page = authenticatedPage;
      await openMobileCreateInLocale(page, locale);

      // The persistent mobile navigation, not the desktop header, owns this canvas:
      // the desktop nav must be out of the traversal entirely.
      await expect(page.locator("header .desktop-nav")).toBeHidden();
      const tile = page.locator('a[data-health-care-tile="true"]');
      await expect(tile).toBeVisible();
      await expect(tile).toHaveAttribute("href", HEALTH_ROUTE);

      const stops = await captureTabStops(page);
      await capture(page, testInfo, `01-tab-traversal-${locale.code}-320`);

      // 1. The localized skip link is the first keyboard stop on the RTL canvas.
      expect(stops[0]?.region, `${locale.code}: first stop is the skip link`).toBe("skip");
      expect(stops[0]?.name, `${locale.code}: skip link keeps its localized label`).toBe(locale.skipToContent);

      // 2. The localized Health Care entry is reachable by Tab alone, exactly once.
      const entryStops = stops.filter((stop) => stop.healthEntry);
      expect(entryStops.length, `${locale.code}: the Health Care entry is a single keyboard stop`).toBe(1);
      const entryIndex = stops.findIndex((stop) => stop.healthEntry);
      expect(entryIndex, `${locale.code}: the Health Care entry is keyboard-reachable`).toBeGreaterThanOrEqual(0);
      const entry = stops[entryIndex];
      expect(entry.region, `${locale.code}: the entry lives in the page body, not the hidden header`).toBe("main");
      expect(entry.href).toBe(HEALTH_ROUTE);
      // The honest availability label travels with the entry: a mobile reader must
      // hear both what it is and that it is not live yet, in the target language.
      expect(entry.name, `${locale.code}: the entry names itself in ${locale.tag}`).toContain(locale.title);
      expect(entry.name, `${locale.code}: the entry states its localized availability`).toContain(locale.status);

      // 3. Keyboard order is complete and stable: the gated entry is reachable before
      //    the traversal leaves the chooser for the persistent bottom navigation, and
      //    that bar is traversed in its documented order.
      const bottomNavStart = stops.findIndex((stop) => stop.region === "bottomnav");
      expect(bottomNavStart, `${locale.code}: the mobile bottom navigation is reached`).toBeGreaterThan(-1);
      expect(entryIndex, `${locale.code}: the entry is not stranded behind the mobile navigation`).toBeLessThan(bottomNavStart);
      const bottomNav = stops.filter((stop) => stop.region === "bottomnav");
      expect(bottomNav.map((stop) => stop.href), `${locale.code}: mobile nav order`).toEqual([...navHrefs]);
      expect(bottomNav.map((stop) => stop.name), `${locale.code}: mobile nav labels are localized`).toEqual([...locale.nav]);
      expect(bottomNav[2].ariaCurrent, `${locale.code}: Create is the current page`).toBe("page");
      expect(stops.some((stop) => stop.className.includes("top-link")), `${locale.code}: no phantom tab stop behind the hidden desktop nav`).toBe(false);

      // 4. RTL narrow-canvas focus containment: every stop is inside the 320px box.
      for (const [index, stop] of stops.entries()) {
        expect(stop.left, `${locale.code}: stop ${index} (${stop.name}) is not off-canvas left`).toBeGreaterThanOrEqual(-1);
        expect(stop.right, `${locale.code}: stop ${index} (${stop.name}) is not off-canvas right`).toBeLessThanOrEqual(MOBILE_VIEWPORT.width + 1);
      }

      // 5. Activate the focused entry from the keyboard only.
      await expect(page.locator(`nav.mobile-bottom-nav`)).toHaveAccessibleName(locale.mobileNavLabel);
      await tile.focus();
      await page.keyboard.press("Enter");
      await expect(page).toHaveURL(new RegExp(`${HEALTH_ROUTE}$`));
      await expect(page.getByRole("heading", { name: locale.title, level: 1 })).toBeVisible();
      await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
      expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth),
        `${locale.code}: the gated route does not overflow 320px`).toBeLessThanOrEqual(1);
      await capture(page, testInfo, `02-activated-${locale.code}-320`);
    });

    test(`${locale.code}: the consent-gated route still refuses an unauthenticated mobile request at 320px`, async ({ page }, testInfo) => {
      const unexpectedRequests: string[] = [];
      page.on("request", (request) => {
        if (/\/api\/(health|uploads|generation|providers)|sunoapi|runway/i.test(request.url())) {
          unexpectedRequests.push(request.url());
        }
      });

      await page.setViewportSize(MOBILE_VIEWPORT);
      await page.goto("/login");
      await page.evaluate((value) => localStorage.setItem("taslim-locale", value), locale.code);
      await page.goto("/");
      await expect(page.locator("html")).toHaveAttribute("lang", locale.tag);
      await expect(page.locator("html")).toHaveAttribute("dir", "rtl");

      // The refusal must come from the server boundary, not only from the client
      // guard that hydrates afterwards: a document request that never follows the
      // redirect has to be refused outright. Without this, removing
      // `requireAuthenticatedPage` still passes because `ProtectedPage` redirects
      // in the browser, and the gate would silently degrade to client-side only.
      const gated = await page.request.get(HEALTH_ROUTE, { maxRedirects: 0 });
      expect([301, 302, 303, 307, 308], `${locale.code}: the gated mobile document request is refused before render`)
        .toContain(gated.status());
      expect(gated.headers()["location"], `${locale.code}: and it is sent to the login route with the return path`)
        .toBe(`/login?next=%2Fcreate%2Fhealth`);

      await page.goto(HEALTH_ROUTE);
      await expect(page).toHaveURL(/\/login\?next=%2Fcreate%2Fhealth$/);
      await expect(page.getByRole("heading", { level: 1, name: locale.title })).toHaveCount(0);
      await expect(page.locator('a[data-health-care-tile="true"]')).toHaveCount(0);
      await expect(page.locator("body")).not.toContainText(/lab report|medication list|diagnosis|تقرير مختبر|قائمة الأدوية|ڕاپۆرتی تاقیگە|نموونەی ڕاپۆرت/i);
      await capture(page, testInfo, `03-anonymous-${locale.code}-320`);

      const chooserGated = await page.request.get("/create", { maxRedirects: 0 });
      expect([301, 302, 303, 307, 308], `${locale.code}: the mobile chooser refuses the anonymous document request too`)
        .toContain(chooserGated.status());
      expect(chooserGated.headers()["location"]).toBe("/login?next=%2Fcreate");
      expect(unexpectedRequests, `${locale.code}: the anonymous mobile path never reaches a data plane`).toEqual([]);
    });
  }
});
