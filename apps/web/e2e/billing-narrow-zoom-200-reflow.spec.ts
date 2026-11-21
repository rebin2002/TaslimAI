// Test-only browser regression for issue #700: Account -> Plan & credits on a narrow phone
// viewport rendered at 200% browser zoom, in English LTR plus Arabic and Kurdish Sorani RTL.
//
// WHY THIS FILE EXISTS AND WHY IT IS DISJOINT
// The #700 test lane is already partitioned and no existing claim exercises a narrow phone
// combined with 200% zoom:
//   - billing-visual-acceptance.spec.ts (merged in #732; being migrated to the `authenticatedPage`
//     fixture by #843): 320/375/768/1440 at 100% zoom, en/ar/ku, text-box collisions, page
//     overflow, disabled buttons, ledger inner scroll, cross-route CSS walk.
//   - billing-tablet-navigation-overflow.spec.ts (merged in #816): 681-900px header containment.
//   - billing-plan-change-disabled-actions.spec.ts (#782): disabled plan-change ACTION SEMANTICS
//     at 1/1.25/1.5 zoom, en/ar, inertness and hit-target separation.
//   - billing-plan-credits-zoom-legibility.spec.ts (#790): plan-label and credit-amount
//     LEGIBILITY at 200% zoom in ar/ckb RTL, measured at a 1280x900 desktop viewport.
//   - issue-700-onboarding-billing-layout.spec.ts (#846, owns the first-login onboarding surfaces
//     and onboarding-billing-ui.css): static onboarding/billing fixtures, 125% zoom LTR
//     containment, modal keyboard accessibility.
// The state measured here is what a phone actually shows when zoom is raised to 200%: the layout
// viewport becomes device width divided by the zoom factor, i.e. 320 / 2 = 160 CSS pixels. That is
// below WCAG 2.1 SC 1.4.10's 320-CSS-pixel reference viewport and below the product's own
// documented floor (`html { min-width: 320px }` in apps/web/src/app/globals.css), so this spec does
// NOT demand that the page reflow into 160px. It pins what is still wrong *inside* that state
// regardless of the floor -- run-on or clipped plan labels and credit figures, the keyboard reading
// order, and rendered target size (SC 2.5.8) -- so the narrow-zoom state stops being unmeasured.
// Zoom is expressed through the CSS viewport size rather than `style.zoom`, because the root zoom
// property leaves media queries and `documentElement.scrollWidth` unscaled and would assert
// vacuously. Containment is asserted against the documented minimum canvas and in box-width terms
// only, because RTL scroll-offset models differ between engines and coordinates would be fragile.
//
// SCOPE GUARD-RAILS (issue #700 acceptance gates)
// - Real product route `/account/billing`; no production source touched and no component edited.
//   The shared `globals.css` floor and the `.back-link` rule are only observed, never modified.
// - Every `/api/` request is fulfilled locally with synthetic data: no real user, customer,
//   database, migration, provider call, charge or checkout endpoint is reachable, and no
//   credential or secret is used.
// - Billing values are synthetic constants asserted only as literal round-trips; no calculation,
//   balance, price, upgrade-enablement, authentication or onboarded-user logic is derived, and
//   charging is never enabled.
// - Plan-action enablement semantics belong to #782, desktop 200% RTL legibility to #790, and the
//   first-login onboarding surfaces to #846; this file does not re-assert them.
import { test, expect, type Page, type Route } from "@playwright/test";
import { appendFileSync, mkdirSync } from "node:fs";
import { resolve } from "node:path";

const WORKSPACE_ID = "6f2b7a41-3c5d-4e11-9b8a-0d7f2c1e4a33";
const OUT_DIR = resolve(process.cwd(), "test-results/narrow-zoom-700");

const ZOOM = 2;
const DEVICE_CSS_WIDTH = 320;
/** A phone at 200% zoom presents device width divided by the zoom factor. */
const ZOOMED_VIEWPORT = DEVICE_CSS_WIDTH / ZOOM;
/** The product's documented minimum canvas: `html { min-width: 320px }` in globals.css. */
const MINIMUM_CANVAS_CSS = 320;
/** WCAG 2.5.8 minimum target size, in CSS pixels. */
const MIN_TARGET_CSS = 24;

const sessionFixture = {
  user: {
    id: "narrow-zoom-700-user", email: "narrow-zoom-700@example.invalid", displayName: "Narrow Zoom 700 Fixture",
    preferredLanguage: "en", personalWorkspaceId: WORKSPACE_ID, createdAt: "2026-01-01T00:00:00Z",
    defaultGenerationLanguage: "en", timeZone: "UTC", outputPreference: "balanced", includeSourceLinks: false,
    onboardingCompletedAt: "2026-01-01T00:00:00Z", onboardingIntent: "project", isAdmin: false,
  },
  personalWorkspace: { id: WORKSPACE_ID, name: "Narrow Zoom 700 Workspace", slug: "narrow-zoom-700", type: "Personal", role: "Owner" },
};

/** Synthetic plan catalog; values are round-tripped as literals only, never recomputed. */
const plans = [
  { code: "free", name: "Free", monthlyPriceUsd: 0, monthlyCreditAllowance: 1_000, currency: "USD", isCurrent: true },
  { code: "pro", name: "Pro", monthlyPriceUsd: 9, monthlyCreditAllowance: 10_000, currency: "USD", isCurrent: false },
];

const billingFixture = {
  currentPlan: { code: "free", name: "Free", monthlyPriceUsd: 0, monthlyCreditAllowance: 1_000, currency: "USD" },
  subscription: { status: "active", currentPeriodStart: "2026-11-01T00:00:00Z", currentPeriodEnd: "2026-12-01T00:00:00Z", nextRenewalAt: "2026-12-01T00:00:00Z", cancelAtPeriodEnd: false },
  billingPeriod: { id: "narrow-zoom-period", status: "active", startsAt: "2026-11-01T00:00:00Z", endsAt: "2026-12-01T00:00:00Z", includedCredits: 1_000 },
  credits: { includedGranted: 1_000, includedRemaining: 640, purchasedRemaining: 0, adjustmentBalance: 0, totalRemaining: 1_505 },
  transactions: [
    { id: "nz-tx-grant", type: "Grant", amount: 1_000, reason: "Monthly plan allowance", createdAt: "2026-11-01T08:00:00Z" },
    { id: "nz-tx-debit", type: "Debit", amount: -120, reason: "Research generation", createdAt: "2026-11-03T10:30:00Z" },
  ],
  availablePlans: plans,
  paymentStatus: { status: "disabled", provider: null, lastFailureReason: null, lastPaymentAt: null },
  actions: { checkoutAvailable: false, upgradeAvailable: false, downgradeAvailable: false, cancelAvailable: false, disabledReason: "Customer charging is disabled on this account." },
};

/**
 * Copy read from apps/web/src/lib/i18n.ts at authoring time; #732's spec pins the same strings the
 * same way, so update the two together if a translation changes. Credit figures are formatted
 * inside the page (browser ICU) because ckb digit shapes differ between Node's and Chromium's ICU.
 */
const localized = {
  en: { dir: "ltr", tag: "en", noCharge: "No charge", currentBadge: "Current", currentPlan: "Current plan", upgrade: "Upgrade unavailable", includedCredits: "{count} included credits", includedRemaining: "{count} included credits remaining" },
  ar: { dir: "rtl", tag: "ar", noCharge: "بلا رسوم", currentBadge: "الحالية", currentPlan: "الخطة الحالية", upgrade: "الترقية غير متاحة", includedCredits: "{count} من الأرصدة المشمولة", includedRemaining: "{count} من الأرصدة المشمولة متبقية" },
  ku: { dir: "rtl", tag: "ckb", noCharge: "بێ تێچوو", currentBadge: "ئێستا", currentPlan: "پلانی ئێستا", upgrade: "بەرزکردنەوە بەردەست نییە", includedCredits: "{count} خاڵی ناوخۆیی", includedRemaining: "{count} خاڵی ناوخۆیی ماوە" },
} as const;

type CaseLocale = keyof typeof localized;

async function fulfillJson(route: Route, body: unknown) {
  await route.fulfill({ contentType: "application/json", body: JSON.stringify(body) });
}

/** Provider-free mount of the real protected route: read-only traffic only, no mutation reachable. */
async function mockBillingSurface(page: Page) {
  await page.route("**/api/**", async (route) => {
    if (route.request().method() === "GET") return fulfillJson(route, {});
    return route.fulfill({ status: 204 });
  });
  await page.route("**/api/auth/csrf", (route) => fulfillJson(route, { token: "narrow-zoom-700-fixture" }));
  await page.route("**/api/auth/me", (route) => fulfillJson(route, sessionFixture));
  await page.route(`**/api/workspaces/${WORKSPACE_ID}/billing`, (route) => fulfillJson(route, billingFixture));
}

function record(row: Record<string, unknown>) {
  mkdirSync(OUT_DIR, { recursive: true });
  appendFileSync(resolve(OUT_DIR, "metrics.jsonl"), `${JSON.stringify(row)}\n`);
}

async function capture(page: Page, testInfo: import("@playwright/test").TestInfo, name: string) {
  mkdirSync(OUT_DIR, { recursive: true });
  const path = resolve(OUT_DIR, `${name}.png`);
  await page.screenshot({ path });
  await testInfo.attach(name, { path, contentType: "image/png" });
}

interface ZoomMetrics {
  dir: string;
  lang: string;
  viewport: number;
  docScrollWidth: number;
  htmlScrollWidth: number;
  /** Boxes wider than the documented minimum canvas, i.e. genuinely overflowing at any width. */
  beyondCanvas: string[];
  contentWidths: string[];
  widestBoxes: string[];
  /** Text inside its own box that is clipped away with no scroll affordance. */
  clippedText: string[];
  collisions: { a: string; b: string; overlapX: number; overlapY: number }[];
  planTexts: { name: string; badge: string; price: string; credits: string; action: string }[];
  creditTotals: { remaining: string; included: string };
  tabStops: { tag: string; cls: string; text: string; disabled: boolean; tabbable: boolean; boxWidth: number; boxHeight: number }[];
  ledger: { clientWidth: number; scrollWidth: number; boxWidth: number; tableWidth: number };
}

// `page.evaluate` serializes this function, so the documented canvas is passed in as an argument
// instead of being closed over.
const MEASURE = (minimumCanvas: number): ZoomMetrics => {
  const root = document.querySelector<HTMLElement>(".billing-page");
  if (!root) throw new Error("billing route did not render");
  const viewport = window.innerWidth;
  const canvas = Math.max(viewport, minimumCanvas);
  const box = (el: Element) => el.getBoundingClientRect();
  const label = (el: Element) => `${el.tagName.toLowerCase()}${typeof el.className === "string" && el.className ? `.${el.className.split(" ").filter(Boolean).slice(0, 2).join(".")}` : ""}`;
  const describe = (el: Element) => `${label(el)} "${(el.textContent ?? "").trim().slice(0, 26)}" w=${Math.round(box(el).width)}`;
  const leafText = Array.from(root.querySelectorAll<HTMLElement>("strong, b, span, p, small, dt, dd, h1, h2, h3, th, td, a, button")).filter((el) => {
    for (const child of el.childNodes) if (child.nodeType === Node.TEXT_NODE && (child.textContent ?? "").trim()) return true;
    return false;
  });
  const collisions: ZoomMetrics["collisions"] = [];
  for (let i = 0; i < leafText.length; i += 1) {
    for (let j = i + 1; j < leafText.length; j += 1) {
      const a = leafText[i];
      const b = leafText[j];
      if (a.contains(b) || b.contains(a)) continue;
      const ra = box(a);
      const rb = box(b);
      if (ra.width <= 1 || ra.height <= 1 || rb.width <= 1 || rb.height <= 1) continue;
      const overlapX = Math.min(ra.right, rb.right) - Math.max(ra.left, rb.left);
      const overlapY = Math.min(ra.bottom, rb.bottom) - Math.max(ra.top, rb.top);
      if (overlapX > 1 && overlapY > 1) collisions.push({ a: describe(a), b: describe(b), overlapX: Math.round(overlapX * 10) / 10, overlapY: Math.round(overlapY * 10) / 10 });
    }
  }
  const clippedText = leafText
    .filter((el) => {
      const style = getComputedStyle(el);
      const ellipsis = el.scrollWidth > el.clientWidth + 1 && style.textOverflow === "ellipsis";
      const hidden = el.scrollWidth > el.clientWidth + 1 && (style.overflow === "hidden" || style.overflowX === "hidden");
      return ellipsis || hidden;
    })
    .map((el) => `${describe(el)} scrollW=${el.scrollWidth} clientW=${el.clientWidth}`);
  // Width terms only: RTL scroll-offset models differ between engines, so nothing here depends on
  // document coordinates. `widestBoxes` is recorded as evidence; it is never asserted on.
  const all = Array.from(document.querySelectorAll<HTMLElement>("body *")).filter((el) => box(el).width > 0);
  const widestBoxes = [...all].sort((a, b) => box(b).width - box(a).width).slice(0, 4).map(describe);
  // Billing content is measured by its own scroll box: the ledger table is expected to be wider
  // than the canvas inside its own horizontal scroll region, so only the region is checked here.
  const content = Array.from(root.querySelectorAll<HTMLElement>(".account-card, .billing-plan-option, .usage-table-wrap, .back-link, h1, .billing-plan-option button"));
  const beyondCanvas = content.filter((el) => box(el).width > canvas + 1).map(describe);
  const contentWidths = content.map(describe);
  const options = Array.from(root.querySelectorAll<HTMLElement>(".billing-plan-option"));
  const planTexts = options.map((option) => ({
    name: (option.querySelector("strong")?.textContent ?? "").trim(),
    badge: (option.querySelector(".billing-current-badge")?.textContent ?? "").trim(),
    price: (option.querySelector("b")?.textContent ?? "").trim(),
    credits: (option.querySelector("span:not(.billing-current-badge)")?.textContent ?? "").trim(),
    action: (option.querySelector("button")?.textContent ?? "").trim(),
  }));
  const creditCard = root.querySelector<HTMLElement>(".billing-credit-card");
  const creditSpans = creditCard ? Array.from(creditCard.querySelectorAll("span")) : [];
  const tabStops = Array.from(root.querySelectorAll<HTMLElement>('a[href],button,input,select,textarea,[tabindex]:not([tabindex="-1"]),.usage-table-wrap')).map((el) => {
    const r = box(el);
    const disabled = (el as HTMLButtonElement).disabled === true;
    return { tag: el.tagName.toLowerCase(), cls: label(el).replace(/^[a-z]+\./, ""), text: (el.textContent ?? "").trim().slice(0, 26), disabled, tabbable: !disabled && el.tabIndex >= 0, boxWidth: Math.round(r.width), boxHeight: Math.round(r.height) };
  });
  const wrap = root.querySelector<HTMLElement>(".usage-table-wrap");
  const table = wrap?.querySelector("table");
  return {
    dir: document.documentElement.dir,
    lang: document.documentElement.lang,
    viewport,
    docScrollWidth: document.documentElement.scrollWidth,
    htmlScrollWidth: Math.round(box(document.documentElement).width),
    beyondCanvas,
    contentWidths,
    widestBoxes,
    clippedText: clippedText.slice(0, 6),
    collisions,
    planTexts,
    creditTotals: { remaining: (creditCard?.querySelector("strong")?.textContent ?? "").trim(), included: (creditSpans[creditSpans.length - 1]?.textContent ?? "").trim() },
    tabStops,
    ledger: wrap && table ? { clientWidth: wrap.clientWidth, scrollWidth: wrap.scrollWidth, boxWidth: Math.round(box(wrap).width), tableWidth: Math.round(box(table).width) } : { clientWidth: 0, scrollWidth: 0, boxWidth: 0, tableWidth: 0 },
  };
};

async function openBilling(page: Page, locale: CaseLocale, viewportWidth: number): Promise<ZoomMetrics> {
  await page.addInitScript((value) => window.localStorage.setItem("taslim-locale", value), locale);
  await mockBillingSurface(page);
  await page.setViewportSize({ width: viewportWidth, height: viewportWidth * 2 });
  await page.goto("/account/billing");
  await expect(page.locator(".billing-page")).toBeVisible();
  await expect(page.locator(".billing-plan-option")).toHaveCount(plans.length);
  return page.evaluate(MEASURE, MINIMUM_CANVAS_CSS);
}

const formatInPage = (page: Page, tag: string, value: number) =>
  page.evaluate(({ localeTag, num }) => new Intl.NumberFormat(localeTag).format(num), { localeTag: tag, num: value });

test.describe("Plan & credits inside the documented canvas at 320 device px / 200% zoom (#700 B)", () => {
  test.setTimeout(90_000);

  for (const locale of ["en", "ar", "ku"] as const) {
    const strings = localized[locale];

    test(`keeps the zoomed narrow layout inside the ${MINIMUM_CANVAS_CSS}px minimum canvas in ${strings.dir} (${locale})`, async ({ page }, testInfo) => {
      const m = await openBilling(page, locale, ZOOMED_VIEWPORT);
      record({ case: "canvas", locale, ...m });
      expect.soft(m.lang, "document lang").toBe(strings.tag);
      expect.soft(m.dir, "document direction").toBe(strings.dir);
      expect.soft(m.viewport, "a phone at 200% zoom presents device width divided by the zoom factor").toBe(ZOOMED_VIEWPORT);
      expect.soft(m.docScrollWidth, `the document must not spill past the documented ${MINIMUM_CANVAS_CSS}px canvas at 200% zoom on a ${DEVICE_CSS_WIDTH}px phone: widest=${JSON.stringify(m.widestBoxes)}`).toBeLessThanOrEqual(MINIMUM_CANVAS_CSS + 1);
      expect(m.beyondCanvas, `no billing box is wider than the minimum canvas (content sizes: ${JSON.stringify(m.contentWidths)})`).toEqual([]);
      await capture(page, testInfo, `700-canvas-${locale}`);
    });

    test(`renders plan labels, credit amounts and CTA copy as distinct unclipped text at 200% zoom in ${strings.dir} (${locale})`, async ({ page }, testInfo) => {
      const m = await openBilling(page, locale, ZOOMED_VIEWPORT);
      record({ case: "labels", locale, planTexts: m.planTexts, creditTotals: m.creditTotals, collisions: m.collisions.length, clippedText: m.clippedText });
      expect.soft(m.collisions, `run-on/colliding text boxes at 200% zoom: ${JSON.stringify(m.collisions.slice(0, 8))}`).toHaveLength(0);
      expect.soft(m.clippedText, "no ellipsis-clipped or overflow-hidden text runs").toHaveLength(0);
      const [current, other] = m.planTexts;
      expect.soft([current.name, current.badge, current.price], "the current plan row renders name, badge and price as separate text").toEqual([plans[0].name, strings.currentBadge, strings.noCharge]);
      expect.soft(current.action, "the current-plan CTA label is localized").toBe(strings.currentPlan);
      expect.soft(other.action, "the upgrade CTA label is localized and present").toBe(strings.upgrade);
      expect.soft(other.credits, "the plan credit allowance is localized text").toBe(strings.includedCredits.replace("{count}", await formatInPage(page, strings.tag, plans[1].monthlyCreditAllowance)));
      expect.soft(other.price, "the price text is rendered").not.toBe("");
      expect.soft(m.creditTotals.remaining, "the remaining-credit figure is rendered").not.toBe("");
      expect(m.creditTotals.included, "the included-remaining figure is rendered").toBe(strings.includedRemaining.replace("{count}", await formatInPage(page, strings.tag, billingFixture.credits.includedRemaining)));
      await capture(page, testInfo, `700-labels-${locale}`);
    });

    test(`keeps the keyboard reading order usable at 200% zoom in ${strings.dir} (${locale})`, async ({ page }, testInfo) => {
      const m = await openBilling(page, locale, ZOOMED_VIEWPORT);
      const tabbable = m.tabStops.filter((stop) => stop.tabbable);
      record({ case: "keyboard", locale, tabStops: m.tabStops });
      // While every plan action is disabled, the only *declared* billing tab stop is the account
      // back-link; the ledger scroll region is reachable too (Chromium gives focusable scroll
      // containers sequential-focus navigation with a reported tabIndex of -1), which is asserted
      // from the real Tab walk below. Enablement semantics belong to #782.
      expect.soft(tabbable.map((stop) => `${stop.tag}.${stop.cls}`), "the account back-link is the only declared billing tab stop while every plan action is disabled").toEqual(["a.back-link"]);
      const visited: { seq: string; insideBilling: boolean; left: number; right: number; disabled: boolean }[] = [];
      for (let i = 0; i < 12; i += 1) {
        await page.keyboard.press("Tab");
        const step = await page.evaluate(() => {
          const el = document.activeElement as HTMLElement | null;
          if (!el) return null;
          const r = el.getBoundingClientRect();
          const cls = typeof el.className === "string" && el.className ? `.${el.className.split(" ").filter(Boolean)[0]}` : "";
          return {
            seq: `${el.tagName.toLowerCase()}${cls}:"${(el.textContent ?? "").trim().slice(0, 18)}"`,
            insideBilling: Boolean(el.closest(".billing-page")),
            left: Math.round(r.left),
            right: Math.round(r.right),
            disabled: (el as HTMLButtonElement).disabled === true,
          };
        });
        if (!step) break;
        visited.push(step);
      }
      record({ case: "keyboard-walk", locale, visited });
      const billingStops = visited.filter((step) => step.insideBilling);
      expect.soft(billingStops.length, `Tab reached the billing content: walk=${JSON.stringify(visited.map((s) => s.seq))}`).toBeGreaterThan(0);
      expect.soft(billingStops[0]?.seq, "the account back-link is the first billing tab stop, ahead of any plan action").toContain("back-link");
      expect.soft(billingStops.map((step) => step.seq.split(":")[0]), "in Tab order the billing walk is the back-link then the ledger scroll region, with no plan action between them").toEqual(["a.back-link", "div.usage-table-wrap"]);
      expect.soft(visited.some((step) => step.disabled), "focus never lands on a disabled plan action").toBe(false);
      const invisible = billingStops.filter((step) => step.right <= 0 || step.left >= m.viewport);
      expect(invisible, `every focused billing element is at least partly on screen so Tab cannot strand the user (2.4.3 / 2.4.7): ${JSON.stringify(invisible)}`).toHaveLength(0);
      await capture(page, testInfo, `700-keyboard-${locale}`);
    });
  }
});

test.describe("Plan & credits target size at 320px and under 200% zoom (#700 B, SC 2.5.8)", () => {
  test.setTimeout(90_000);

  for (const locale of ["en", "ar", "ku"] as const) {
    const strings = localized[locale];

    test(`gives every keyboard-operable billing control a ${MIN_TARGET_CSS}x${MIN_TARGET_CSS} target at 100% and 200% zoom in ${strings.dir} (${locale})`, async ({ page }, testInfo) => {
      const states = [
        { name: "320px-at-100", viewport: DEVICE_CSS_WIDTH },
        { name: "320px-at-200", viewport: ZOOMED_VIEWPORT },
      ];
      for (const state of states) {
        const m = await openBilling(page, locale, state.viewport);
        const small = m.tabStops.filter((stop) => stop.tabbable && (stop.boxWidth < MIN_TARGET_CSS || stop.boxHeight < MIN_TARGET_CSS));
        record({ case: "targets", locale, state: state.name, tabStops: m.tabStops, small });
        await capture(page, testInfo, `700-targets-${locale}-${state.name}`);
        expect.soft(small, `${state.name}: keyboard-operable billing controls need a ${MIN_TARGET_CSS}x${MIN_TARGET_CSS} CSS px target (WCAG 2.5.8); offenders: ${JSON.stringify(small)}`).toEqual([]);
      }
    });
  }
});
