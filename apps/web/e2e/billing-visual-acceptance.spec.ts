import { test, expect, type Page, type Route } from "@playwright/test";
import { appendFileSync, copyFileSync, mkdirSync } from "node:fs";

/**
 * PR #732 visual acceptance gate for authenticated /account/billing.
 *
 * Runs the real protected billing route against deterministic in-browser
 * mocks only: session and billing payloads are fulfilled via `page.route`,
 * so the spec touches no real user, provider, charge, database or migration
 * and requires no API backend.
 *
 * Head mode (default) asserts the fixed layout: zero text-box collisions,
 * zero page-level horizontal overflow, viewport containment, disabled plan
 * change/checkout controls, and localized plan/price/credit/status labels at
 * narrow (320/375), tablet (768) and desktop (1440) widths in English (LTR)
 * plus Arabic and Sorani Kurdish (RTL, ckb).
 *
 * Negative control: `BILLING_BASELINE=1` serves the billing route stylesheet
 * chunk as an empty stylesheet — reproducing the pre-PR November 9 "run
 * together" render with one changed variable — and the suite then requires
 * the collision probe to actually detect the historical defects (aggregate
 * gate), proving the head-mode assertions are not vacuously green.
 *
 * Optional local artifacts: `BILLING_METRICS_OUT=<file.jsonl>` records the
 * measured geometry per case; `BILLING_SHOTS_DIR=<dir>` copies the per-case
 * screenshots there in addition to the Playwright HTML report attachment.
 */

const WORKSPACE_ID = "9a57d1f4-0b0e-4c2c-9d1a-72f5c6b3e111";

const sessionFixture = {
  user: {
    id: "billing-fixture-user",
    email: "billing-visual-fixture@example.invalid",
    displayName: "Billing Visual Fixture",
    preferredLanguage: "en",
    personalWorkspaceId: WORKSPACE_ID,
    createdAt: "2026-01-01T00:00:00Z",
    defaultGenerationLanguage: "en",
    timeZone: "UTC",
    outputPreference: "balanced",
    includeSourceLinks: true,
    onboardingCompletedAt: "2026-01-01T00:00:00Z",
    onboardingIntent: "project",
    isAdmin: false,
  },
  personalWorkspace: {
    id: WORKSPACE_ID,
    name: "Fixture Personal Workspace",
    slug: "fixture-personal-workspace",
    type: "Personal",
    role: "Owner",
  },
};

/** Mirrors the active plan catalog in apps/api/Domain/BillingEntities.cs; balances are simulated. */
const plans = [
  { code: "free", name: "Free", monthlyPriceUsd: 0, monthlyCreditAllowance: 1_000, currency: "USD", isCurrent: true },
  { code: "pro", name: "Pro", monthlyPriceUsd: 9, monthlyCreditAllowance: 10_000, currency: "USD", isCurrent: false },
  { code: "ultra", name: "Ultra", monthlyPriceUsd: 19, monthlyCreditAllowance: 30_000, currency: "USD", isCurrent: false },
  { code: "mega", name: "Mega", monthlyPriceUsd: 29, monthlyCreditAllowance: 60_000, currency: "USD", isCurrent: false },
  { code: "business", name: "Business", monthlyPriceUsd: 59, monthlyCreditAllowance: 150_000, currency: "USD", isCurrent: false },
];

const billingFixture = {
  currentPlan: { code: "free", name: "Free", monthlyPriceUsd: 0, monthlyCreditAllowance: 1_000, currency: "USD" },
  subscription: {
    status: "active",
    currentPeriodStart: "2026-11-01T00:00:00Z",
    currentPeriodEnd: "2026-12-01T00:00:00Z",
    nextRenewalAt: "2026-12-01T00:00:00Z",
    cancelAtPeriodEnd: false,
  },
  billingPeriod: {
    id: "fixture-period",
    status: "active",
    startsAt: "2026-11-01T00:00:00Z",
    endsAt: "2026-12-01T00:00:00Z",
    includedCredits: 1_000,
  },
  credits: { includedGranted: 1_000, includedRemaining: 640, purchasedRemaining: 865, adjustmentBalance: 0, totalRemaining: 1_505 },
  transactions: [
    { id: "tx-grant", type: "Grant", amount: 1_000, reason: "Monthly plan allowance", createdAt: "2026-11-01T08:00:00Z" },
    { id: "tx-debit", type: "Debit", amount: -120, reason: "Research generation", createdAt: "2026-11-03T10:30:00Z" },
    { id: "tx-correction", type: "AdministrativeCorrection", amount: 5, reason: "Rounding correction", createdAt: "2026-11-05T12:00:00Z" },
  ],
  availablePlans: plans,
  paymentStatus: { status: "disabled", provider: null, lastFailureReason: null, lastPaymentAt: null },
  actions: {
    checkoutAvailable: false,
    upgradeAvailable: false,
    downgradeAvailable: false,
    cancelAvailable: false,
    disabledReason: "Billing is not collecting payment yet. Charges and plan changes stay disabled until the release owner authorizes charging.",
  },
};

/** Exact values from src/lib/i18n.ts; update together with any translation change. */
const localized = {
  en: { dir: "ltr", tag: "en", title: "Plan & credits", status: "Charging disabled", month: "month", noCharge: "No charge", currentBadge: "Current", currentPlan: "Current plan", upgrade: "Upgrade unavailable", includedCredits: "{count} included credits", includedRemaining: "{count} included credits remaining" },
  ar: { dir: "rtl", tag: "ar", title: "الخطة والأرصدة", status: "التحصيل معطل", month: "شهر", noCharge: "بلا رسوم", currentBadge: "الحالية", currentPlan: "الخطة الحالية", upgrade: "الترقية غير متاحة", includedCredits: "{count} من الأرصدة المشمولة", includedRemaining: "{count} من الأرصدة المشمولة متبقية" },
  ku: { dir: "rtl", tag: "ckb", title: "پلانی بەکارهێنان و خاڵەکان", status: "پارەدان ناچالاکە", month: "مانگ", noCharge: "بێ تێچوو", currentBadge: "ئێستا", currentPlan: "پلانی ئێستا", upgrade: "بەرزکردنەوە بەردەست نییە", includedCredits: "{count} خاڵی ناوخۆیی", includedRemaining: "{count} خاڵی ناوخۆیی ماوە" },
} as const;

type CaseLocale = keyof typeof localized;
const cases: { locale: CaseLocale; width: number; height: number }[] = [];
for (const locale of ["en", "ar", "ku"] as const) {
  for (const [width, height] of [[320, 640], [375, 667], [768, 1024], [1440, 900]] as const) {
    cases.push({ locale, width, height });
  }
}

const BASELINE = process.env.BILLING_BASELINE === "1";
const detectedCollisionsByCase = new Map<string, number>();

async function fulfillJson(route: Route, body: unknown) {
  await route.fulfill({ contentType: "application/json", body: JSON.stringify(body) });
}

async function mockBillingSurface(page: Page) {
  // Registered first so the specific routes below take precedence (Playwright matches newest route first).
  await page.route("**/api/**", async (route) => {
    if (route.request().method() === "GET") return fulfillJson(route, {});
    return route.fulfill({ status: 204 });
  });
  await page.route("**/api/auth/csrf", async (route) => fulfillJson(route, { token: "billing-visual-acceptance-fixture" }));
  await page.route("**/api/auth/password-policy", async (route) =>
    fulfillJson(route, { requiredLength: 10, requireUppercase: true, requireLowercase: true, requireDigit: true, requireNonAlphanumeric: true }),
  );
  await page.route("**/api/auth/me", async (route) => fulfillJson(route, sessionFixture));
  await page.route(`**/api/workspaces/${WORKSPACE_ID}/billing`, async (route) => fulfillJson(route, billingFixture));
  if (BASELINE) {
    // Negative control: serve the billing route stylesheet chunk empty so the
    // page renders like the pre-PR build, changing one variable only.
    await page.route("**/_next/static/**/*.css", async (route) => {
      const response = await route.fetch();
      const body = await response.text();
      if (body.includes(".billing-plan-option")) {
        return route.fulfill({ contentType: "text/css", body: "/* billing route css stripped (baseline control) */" });
      }
      return route.fulfill({ status: response.status(), headers: response.headers(), body });
    });
  }
}

interface PageMetrics {
  collisions: { a: string; b: string; overlapX: number; overlapY: number }[];
  pageOverflow: boolean;
  viewportWidth: number;
  dir: string;
  lang: string;
  cardsOutsideViewport: number;
  enabledButtons: number;
  totalButtons: number;
  planOptionCount: number;
  planCards: { x: number; y: number; w: number; h: number }[];
  ledgerWrapClientWidth: number;
  ledgerWrapScrollWidth: number;
  remainingCreditsText: string;
  priceTexts: string[];
}

async function measureBillingPage(page: Page): Promise<PageMetrics> {
  return page.evaluate(() => {
    const root = document.querySelector<HTMLElement>(".billing-page");
    if (!root) throw new Error("billing route did not render");
    const viewportWidth = window.innerWidth;
    // Element union bounding rect, matching the November 10 geometry-proof
    // criterion: pre-PR inline-flow text sharing wrapped anonymous blocks
    // collides in union space even when adjacent client rects do not overlap.
    const rect = (el: Element) => el.getBoundingClientRect();
    const leafTextSelector = "strong, b, span, p, small, dt, dd, h1, h2, h3, th, td, a, button";
    const nodes = Array.from(root.querySelectorAll<HTMLElement>(leafTextSelector)).filter((el) => {
      for (const child of el.childNodes) if (child.nodeType === Node.TEXT_NODE && (child.textContent ?? "").trim()) return true;
      return false;
    });
    const label = (el: HTMLElement) => `${el.tagName.toLowerCase()} "${(el.textContent ?? "").trim().slice(0, 40)}"`;
    const collisions: PageMetrics["collisions"] = [];
    for (let i = 0; i < nodes.length; i += 1) {
      for (let j = i + 1; j < nodes.length; j += 1) {
        const a = nodes[i];
        const b = nodes[j];
        // Skip only true ancestor/descendant pairs (the same text stack);
        // siblings and cross-card overlaps are exactly what must be detected.
        if (a.contains(b) || b.contains(a)) continue;
        const ra = rect(a);
        const rb = rect(b);
        if (ra.width <= 1 || ra.height <= 1 || rb.width <= 1 || rb.height <= 1) continue;
        const overlapX = Math.min(ra.right, rb.right) - Math.max(ra.left, rb.left);
        const overlapY = Math.min(ra.bottom, rb.bottom) - Math.max(ra.top, rb.top);
        if (overlapX > 1 && overlapY > 1) {
          collisions.push({ a: label(a), b: label(b), overlapX: Math.round(overlapX * 100) / 100, overlapY: Math.round(overlapY * 100) / 100 });
        }
      }
    }
    const cards = Array.from(root.querySelectorAll<HTMLElement>(".account-card, .billing-plan-option"));
    const cardsOutsideViewport = cards.filter((card) => {
      const r = card.getBoundingClientRect();
      return r.left < -1 || r.right > viewportWidth + 1;
    }).length;
    const buttons = Array.from(root.querySelectorAll<HTMLButtonElement>("button"));
    const options = Array.from(root.querySelectorAll<HTMLElement>(".billing-plan-option"));
    const wrap = root.querySelector<HTMLElement>(".usage-table-wrap");
    const creditCard = root.querySelector<HTMLElement>(".billing-credit-card");
    return {
      collisions,
      pageOverflow: document.documentElement.scrollWidth > viewportWidth + 1,
      viewportWidth,
      dir: document.documentElement.dir,
      lang: document.documentElement.lang,
      cardsOutsideViewport,
      enabledButtons: buttons.filter((button) => !button.disabled).length,
      totalButtons: buttons.length,
      planOptionCount: options.length,
      planCards: options.map((option) => {
        const r = option.getBoundingClientRect();
        return { x: Math.round(r.left), y: Math.round(r.top), w: Math.round(r.width), h: Math.round(r.height) };
      }),
      ledgerWrapClientWidth: wrap ? Math.round(wrap.clientWidth) : -1,
      ledgerWrapScrollWidth: wrap ? Math.round(wrap.scrollWidth) : -1,
      remainingCreditsText: creditCard ? (creditCard.querySelector("strong")?.textContent ?? "").trim() : "",
      priceTexts: options.map((option) => (option.querySelector("b")?.textContent ?? "").trim()),
    };
  });
}

test.describe(`Billing route visual acceptance${BASELINE ? " (baseline control: route CSS stripped)" : ""}`, () => {
  test.setTimeout(120_000);

  for (const testCase of cases) {
    const { locale, width, height } = testCase;
    const strings = localized[locale];
    test(`/account/billing renders ${locale} at ${width}px`, async ({ page }, testInfo) => {
      await page.addInitScript((value) => window.localStorage.setItem("taslim-locale", value), locale);
      await mockBillingSurface(page);
      await page.setViewportSize({ width, height });
      await page.goto("/account/billing");

      await expect(page.locator(".billing-page")).toBeVisible();
      await expect(page.locator(".billing-plan-option")).toHaveCount(plans.length);
      await expect(page.locator("html")).toHaveAttribute("dir", strings.dir);
      await expect(page.locator("html")).toHaveAttribute("lang", strings.tag);
      await expect(page.getByRole("heading", { name: strings.title, exact: true })).toBeVisible();
      await expect(page.getByText(strings.status, { exact: true })).toBeVisible();

      // Localized plan labels, prices and credit figures are distinct visible text.
      // Figures are formatted inside the page so they use the exact browser ICU
      // data; ckb digit forms can differ between Node's ICU and Chromium's.
      const formatInPage = (value: number) =>
        page.evaluate(({ tag, num }) => new Intl.NumberFormat(tag).format(num), { tag: strings.tag, num: value });
      await expect(page.locator(".billing-current-badge")).toHaveCount(1);
      await expect(page.locator(".billing-plan-option").nth(0).locator("b")).toHaveText(strings.noCharge);
      await expect(page.locator(".billing-plan-option").nth(1).locator("b")).toHaveText(`$${plans[1].monthlyPriceUsd} / ${strings.month}`);
      await expect(page.locator(".billing-plan-option").nth(1)).toContainText(
        strings.includedCredits.replace("{count}", await formatInPage(plans[1].monthlyCreditAllowance)),
      );
      await expect(page.locator(".billing-plan-option").nth(0).locator("button")).toHaveText(strings.currentPlan);
      await expect(page.locator(".billing-plan-option").nth(1).locator("button")).toHaveText(strings.upgrade);
      await expect(page.locator(".billing-credit-card").locator("strong")).toHaveText(await formatInPage(billingFixture.credits.totalRemaining));
      await expect(page.locator(".billing-credit-card")).toContainText(
        strings.includedRemaining.replace("{count}", await formatInPage(billingFixture.credits.includedRemaining)),
      );
      await expect(page.getByText(billingFixture.actions.disabledReason, { exact: true })).toBeVisible();

      const metrics = await measureBillingPage(page);
      detectedCollisionsByCase.set(`${locale}@${width}`, metrics.collisions.length);

      expect(metrics.lang, "document lang").toBe(strings.tag);
      expect(metrics.dir, "document direction").toBe(strings.dir);

      if (BASELINE) {
        // Baseline mode records the pre-PR defect; the aggregate gate below is the hard assertion.
        expect.soft(metrics.cardsOutsideViewport, "baseline cards outside viewport").toBe(0);
      } else {
        expect(metrics.collisions, `text-box collisions: ${JSON.stringify(metrics.collisions.slice(0, 8))}`).toHaveLength(0);
        expect(metrics.pageOverflow, "page-level horizontal overflow").toBe(false);
        expect(metrics.cardsOutsideViewport, "plan/summary cards inside viewport").toBe(0);
        expect(metrics.enabledButtons, "all plan change / checkout controls stay disabled").toBe(0);
        expect(metrics.totalButtons).toBe(plans.length + 3);

        // Column structure by breakpoint band.
        const rows = [...new Set(metrics.planCards.map((card) => card.y))].sort((a, b) => a - b);
        if (width <= 680) {
          expect(metrics.planCards.every((card) => card.x === metrics.planCards[0].x), "single-column plan stack").toBe(true);
          expect(metrics.ledgerWrapScrollWidth, "ledger keeps an intentional inner scroll region at narrow widths").toBeGreaterThan(metrics.ledgerWrapClientWidth);
        } else if (width > 900) {
          expect(metrics.planCards.some((card) => card.x !== metrics.planCards[0].x), "multi-column plan grid on desktop").toBe(true);
          const firstRow = metrics.planCards.filter((card) => card.y === rows[0]);
          const ltrReadingOrder = firstRow.every((card, index) => index === 0 || card.x >= firstRow[index - 1].x);
          const rtlMirroredOrder = firstRow.every((card, index) => index === 0 || card.x <= firstRow[index - 1].x);
          expect(strings.dir === "ltr" ? ltrReadingOrder : rtlMirroredOrder, `first row order for ${strings.dir}`).toBe(true);
        }
      }

      const screenshotName = `billing-${locale}-${width}px${BASELINE ? "-baseline" : ""}`;
      const shotPath = testInfo.outputPath(`${screenshotName}.png`);
      await page.screenshot({ path: shotPath, fullPage: true });
      await testInfo.attach(screenshotName, { path: shotPath, contentType: "image/png" });
      if (process.env.BILLING_SHOTS_DIR) {
        mkdirSync(process.env.BILLING_SHOTS_DIR, { recursive: true });
        copyFileSync(shotPath, `${process.env.BILLING_SHOTS_DIR}/${screenshotName}.png`);
      }
      if (process.env.BILLING_METRICS_OUT) {
        appendFileSync(process.env.BILLING_METRICS_OUT, `${JSON.stringify({
          mode: BASELINE ? "baseline" : "head",
          locale,
          width,
          height,
          collisions: metrics.collisions.length,
          collisionPairs: metrics.collisions.slice(0, 12),
          pageOverflow: metrics.pageOverflow,
          cardsOutsideViewport: metrics.cardsOutsideViewport,
          enabledButtons: metrics.enabledButtons,
          totalButtons: metrics.totalButtons,
          dir: metrics.dir,
          lang: metrics.lang,
          planCards: metrics.planCards,
          ledgerWrapClientWidth: metrics.ledgerWrapClientWidth,
          ledgerWrapScrollWidth: metrics.ledgerWrapScrollWidth,
          priceTexts: metrics.priceTexts,
          remainingCreditsText: metrics.remainingCreditsText,
        })}\n`);
      }
    });
  }

  test("negative control aggregate: baseline run must detect the pre-PR collisions", async () => {
    test.skip(!BASELINE, "aggregate control only applies to the baseline (route CSS stripped) run");
    const total = [...detectedCollisionsByCase.values()].reduce((sum, count) => sum + count, 0);
    const narrowDetected = (detectedCollisionsByCase.get("en@320") ?? 0) + (detectedCollisionsByCase.get("ar@320") ?? 0) + (detectedCollisionsByCase.get("ku@320") ?? 0)
      + (detectedCollisionsByCase.get("en@375") ?? 0) + (detectedCollisionsByCase.get("ar@375") ?? 0) + (detectedCollisionsByCase.get("ku@375") ?? 0);
    expect(total, `baseline collisions detected per case: ${JSON.stringify([...detectedCollisionsByCase])}`).toBeGreaterThan(0);
    expect(narrowDetected, "the historical 320/375 run-together symptom must reproduce without the route CSS").toBeGreaterThanOrEqual(6);
  });

  test("cross-route CSS collision walk: /account geometry is unchanged by the billing route", async ({ page }, testInfo) => {
    test.skip(BASELINE, "cross-route walk only applies to the fixed head build");
    await page.addInitScript(() => window.localStorage.setItem("taslim-locale", "en"));
    await mockBillingSurface(page);
    await page.setViewportSize({ width: 1440, height: 900 });
    const snapshot = async () => page.evaluate(() => {
      const serialize = (el: Element) => {
        const r = (el as HTMLElement).getBoundingClientRect();
        const styles = getComputedStyle(el as HTMLElement);
        return `${Math.round(r.left)},${Math.round(r.top)},${Math.round(r.width)},${Math.round(r.height)}|${styles.display}|${styles.padding}|${styles.margin}|${styles.fontSize}`;
      };
      const cards = Array.from(document.querySelectorAll(".account-card")).map(serialize);
      const header = document.querySelector("h1") ? serialize(document.querySelector("h1") as Element) : "";
      return JSON.stringify({ scrollWidth: document.documentElement.scrollWidth, header, cards });
    });
    await page.goto("/account");
    await expect(page.getByRole("heading", { name: /^account$/i })).toBeVisible();
    const before = await snapshot();
    await page.goto("/account/billing");
    await expect(page.locator(".billing-page")).toBeVisible();
    await page.goto("/account");
    await expect(page.getByRole("heading", { name: /^account$/i })).toBeVisible();
    const after = await snapshot();
    expect(after, "geometry and computed styles of /account match the pre-billing-visit capture").toBe(before);
    const billingRoots = await page.locator(".billing-page").count();
    expect(billingRoots, "billing route root class is not present off-route").toBe(0);
    const shotPath = testInfo.outputPath("billing-cross-route-walk-account.png");
    await page.screenshot({ path: shotPath, fullPage: true });
    await testInfo.attach("billing-cross-route-walk-account", { path: shotPath, contentType: "image/png" });
  });
});
