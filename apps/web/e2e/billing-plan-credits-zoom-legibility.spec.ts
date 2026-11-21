// Test-only DOM-geometry regression for issue #700 section B: Account → Plan & credits.
// Scope is deliberately narrow: the readability and spacing of the CURRENT plan
// label and the available-credit amounts (the run-on pairs the issue reports as
// "FreeCurrent", "No charge1,000 included credits" and "$9 / month40,000 included
// credits") at 200% browser zoom in Arabic (ar) and Kurdish Sorani (ku → lang=ckb)
// RTL. It is not about disabled plan-change action semantics (owned by
// billing-plan-change-disabled-actions.spec.ts) nor the 125%-zoom LTR containment
// case (owned by issue-700-onboarding-billing-layout.spec.ts), and it never edits
// or re-derives billing calculations: prices, allowances and balances are synthetic
// constants checked only as literal round-trips. No real API is contacted — every
// /api/ request is fulfilled locally, so no charging endpoint is reachable.
import { test, expect, type Locator, type Page } from "@playwright/test";

const ZOOM = "2";
const zoomFactor = Number(ZOOM);
const desktopViewport = { width: 1280, height: 900 };
// Separation floor per issue #700: distinct label/value text must keep a visible
// gap. 4 CSS px in layout space, compared against zoom-scaled viewport pixels.
const minGap = 4 * zoomFactor;

const session = {
  user: {
    id: "e2e-zoom-legibility-user",
    email: "e2e+zoom-legibility@example.test",
    displayName: "E2E Zoom Legibility Owner",
    preferredLanguage: "en",
    personalWorkspaceId: "e2e-zoom-legibility-workspace",
    createdAt: "2026-01-01T00:00:00Z",
    defaultGenerationLanguage: "en",
    timeZone: "UTC",
    outputPreference: "balanced",
    includeSourceLinks: false,
    onboardingCompletedAt: "2026-01-02T00:00:00Z",
    onboardingIntent: "project",
    isAdmin: false,
  },
  personalWorkspace: {
    id: "e2e-zoom-legibility-workspace",
    name: "E2E Zoom Legibility Workspace",
    slug: "e2e-zoom-legibility",
    type: "Personal",
    role: "owner",
  },
};

// A fixed, charging-disabled snapshot. Values are layout constants, not product
// data: distinct digits per field so a merged run-on line stays detectable.
function billingAccount() {
  return {
    currentPlan: { code: "free", name: "Free", monthlyPriceUsd: 0, monthlyCreditAllowance: 1000, currency: "usd" },
    subscription: {
      status: "active",
      currentPeriodStart: "2026-11-01T00:00:00Z",
      currentPeriodEnd: "2026-12-01T00:00:00Z",
      nextRenewalAt: "2026-12-01T00:00:00Z",
      cancelAtPeriodEnd: false,
    },
    billingPeriod: {
      id: "e2e-zoom-legibility-period",
      status: "active",
      startsAt: "2026-11-01T00:00:00Z",
      endsAt: "2026-12-01T00:00:00Z",
      includedCredits: 1000,
    },
    credits: { includedGranted: 1000, includedRemaining: 750, purchasedRemaining: 250, adjustmentBalance: 0, totalRemaining: 1000 },
    transactions: [
      { id: "tx-1", type: "grant", amount: 1000, reason: "Monthly included allowance", createdAt: "2026-11-01T00:00:00Z" },
      { id: "tx-2", type: "debit", amount: -250, reason: "Image generation", createdAt: "2026-11-05T00:00:00Z" },
    ],
    availablePlans: [
      { code: "free", name: "Free", monthlyPriceUsd: 0, monthlyCreditAllowance: 1000, currency: "usd", isCurrent: true },
      { code: "plus", name: "Plus", monthlyPriceUsd: 9, monthlyCreditAllowance: 10000, currency: "usd", isCurrent: false },
      { code: "pro", name: "Pro", monthlyPriceUsd: 29, monthlyCreditAllowance: 40000, currency: "usd", isCurrent: false },
    ],
    paymentStatus: { status: "disabled", provider: null, lastFailureReason: null, lastPaymentAt: null },
    actions: {
      checkoutAvailable: false,
      upgradeAvailable: false,
      downgradeAvailable: false,
      cancelAvailable: false,
      disabledReason: "Customer charging is disabled on this account.",
    },
  };
}

type Box = { left: number; top: number; right: number; bottom: number; width: number; height: number };

// Fulfils every /api/ request locally; returns the observed request log so the
// test can prove no mutation or charging traffic ever left the page.
async function localApiHarness(page: Page): Promise<string[]> {
  const apiCalls: string[] = [];
  page.on("request", (request) => {
    const url = new URL(request.url());
    if (url.pathname.startsWith("/api/")) apiCalls.push(`${request.method()} ${url.pathname}`);
  });
  await page.route("**/api/**", (route) => {
    const pathname = new URL(route.request().url()).pathname;
    const json = (body: unknown) => route.fulfill({ json: body });
    if (pathname === "/api/auth/me") return json(session);
    if (pathname === "/api/auth/csrf") return json({ token: "e2e-zoom-legibility" });
    if (/^\/api\/workspaces\/[^/]+\/billing$/.test(pathname)) return json(billingAccount());
    return route.fulfill({ status: 204 });
  });
  return apiCalls;
}

async function openBillingAt200Zoom(page: Page, locale: "ar" | "ku"): Promise<string[]> {
  const apiCalls = await localApiHarness(page);
  await page.addInitScript((value) => window.localStorage.setItem("taslim-locale", value), locale);
  await page.setViewportSize(desktopViewport);
  await page.goto("/account/billing");
  await expect(page.locator(".billing-plan-option")).toHaveCount(3);
  await page.evaluate((zoom) => {
    document.documentElement.style.zoom = zoom;
  }, ZOOM);
  await page.evaluate(() => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  return apiCalls;
}

const boxOf = (locator: Locator): Promise<Box> =>
  locator.evaluate((element) => {
    const rect = element.getBoundingClientRect();
    return { left: rect.left, top: rect.top, right: rect.right, bottom: rect.bottom, width: rect.width, height: rect.height };
  });

function intersects(a: Box, b: Box): boolean {
  return a.left < b.right && b.left < a.right && a.top < b.bottom && b.top < a.bottom;
}

function verticalGap(a: Box, b: Box): number {
  return Math.max(b.top - a.bottom, a.top - b.bottom);
}

function horizontalGap(a: Box, b: Box): number {
  return Math.max(a.left - b.right, b.left - a.right);
}

// The exact failure mode of issue #700 section B is two separate DOM text nodes
// rendered as one continuous line: zero vertical separation AND zero horizontal
// gap (adjacent, not necessarily intersecting). A readable pair must keep a
// visible gap on at least one axis and must never overlap.
function assertReadablePair(label: string, a: Box, b: Box) {
  expect(intersects(a, b), `${label}: label and value boxes must not overlap at 200% zoom RTL`).toBe(false);
  const separated = verticalGap(a, b) >= minGap || horizontalGap(a, b) >= minGap;
  expect(separated,
    `${label}: expected a visible gap of at least ${minGap}px on one axis, got vertical ${verticalGap(a, b).toFixed(1)}px / horizontal ${horizontalGap(a, b).toFixed(1)}px`).toBe(true);
}

function assertRenderedText(label: string, box: Box) {
  expect(box.width, `${label}: text must occupy a real line box`).toBeGreaterThan(0);
  expect(box.height, `${label}: text must occupy a real line box`).toBeGreaterThan(0);
}

function assertInsideCard(label: string, child: Box, card: Box) {
  expect(child.left, `${label}: left edge must stay inside its card`).toBeGreaterThanOrEqual(card.left - 1);
  expect(child.right, `${label}: right edge must stay inside its card`).toBeLessThanOrEqual(card.right + 1);
  expect(child.top, `${label}: top edge must stay inside its card`).toBeGreaterThanOrEqual(card.top - 1);
  expect(child.bottom, `${label}: bottom edge must stay inside its card`).toBeLessThanOrEqual(card.bottom + 1);
}

test.describe("Billing plan & credits legibility at 200% zoom in Arabic/Sorani RTL (#700 B)", () => {
  test("keeps current-plan and credit-amount label/value pairs separated, visible and unclipped at 200% zoom", async ({ page }) => {
    test.setTimeout(90_000);
    const cases = [
      { locale: "ar" as const, lang: "ar" },
      { locale: "ku" as const, lang: "ckb" },
    ];
    for (const { locale, lang } of cases) {
      const apiCalls = await openBillingAt200Zoom(page, locale);
      await expect(page.locator("html"), `${locale}: document must render RTL`).toHaveAttribute("dir", "rtl");
      await expect(page.locator("html"), `${locale}: must expose its real language tag`).toHaveAttribute("lang", lang);

      // Literal round-trip only — values come from the synthetic snapshot and no
      // balance is computed by this test.
      await expect(page.locator(".billing-plan-card > strong")).toHaveText("Free");
      const creditTotalText = await page.locator(".billing-credit-card > strong").textContent();
      expect(creditTotalText?.trim(), `${locale}: credit total must render digits`).toMatch(/[0-9\u0660-\u0669]/);
      const creditsNoteText = await page.locator(".billing-credit-card > span").textContent();
      expect(creditsNoteText?.trim(), `${locale}: credit note must not be empty`).not.toBe("");

      // A. Current-plan summary card: plan name above its price line.
      const planCard = await boxOf(page.locator(".billing-plan-card"));
      const planStrong = await boxOf(page.locator(".billing-plan-card > strong"));
      const planSpan = await boxOf(page.locator(".billing-plan-card > span"));
      assertRenderedText(`${locale}: plan name`, planStrong);
      assertRenderedText(`${locale}: plan price line`, planSpan);
      assertReadablePair(`${locale}: current-plan card ("Free" vs price line)`, planStrong, planSpan);
      assertInsideCard(`${locale}: plan name`, planStrong, planCard);
      assertInsideCard(`${locale}: plan price line`, planSpan, planCard);

      // B. Available-credit summary card: credit total above its allowance note.
      const creditCard = await boxOf(page.locator(".billing-credit-card"));
      const creditStrong = await boxOf(page.locator(".billing-credit-card > strong"));
      const creditSpan = await boxOf(page.locator(".billing-credit-card > span"));
      assertRenderedText(`${locale}: remaining credits`, creditStrong);
      assertRenderedText(`${locale}: included-remaining note`, creditSpan);
      assertReadablePair(`${locale}: available-credit card ("1,000" vs "… included credits")`, creditStrong, creditSpan);
      assertInsideCard(`${locale}: remaining credits`, creditStrong, creditCard);
      assertInsideCard(`${locale}: included-remaining note`, creditSpan, creditCard);

      // C. Plan comparison rows: each price line stays above its included-credits
      // line ("$29 / month40,000 included credits" is the run-on the issue cites),
      // and every text stays inside its card instead of spilling or clipping.
      const options = page.locator(".billing-plan-option");
      await expect(options).toHaveCount(3);
      for (let index = 0; index < 3; index += 1) {
        const option = options.nth(index);
        const optionCard = await boxOf(option);
        const name = await boxOf(option.locator("div:first-child > strong"));
        const price = await boxOf(option.locator("b"));
        const credits = await boxOf(option.locator("span:not(.billing-current-badge)"));
        assertRenderedText(`${locale}: plan option ${index} name`, name);
        assertRenderedText(`${locale}: plan option ${index} price`, price);
        assertRenderedText(`${locale}: plan option ${index} credits`, credits);
        assertReadablePair(`${locale}: plan option ${index} price vs included credits`, price, credits);
        assertInsideCard(`${locale}: plan option ${index} name`, name, optionCard);
        assertInsideCard(`${locale}: plan option ${index} price`, price, optionCard);
        assertInsideCard(`${locale}: plan option ${index} credits`, credits, optionCard);
      }

      // D. "FreeCurrent": the current-plan badge must stay visibly separated
      // from the plan name on whichever axis the layout places it.
      const currentOption = page.locator(".billing-plan-option.current");
      await expect(currentOption).toHaveCount(1);
      const currentName = await boxOf(currentOption.locator("div:first-child > strong"));
      const badge = await boxOf(currentOption.locator(".billing-current-badge"));
      assertRenderedText(`${locale}: current badge`, badge);
      assertReadablePair(`${locale}: current plan name vs "Current" badge`, currentName, badge);
      assertInsideCard(`${locale}: current badge`, badge, await boxOf(currentOption));

      // The RTL page must not blow out its viewport at 200% zoom.
      expect(await page.evaluate(() => document.documentElement.scrollWidth),
        `${locale}: 200% zoom must not introduce horizontal overflow`).toBeLessThanOrEqual(desktopViewport.width + 2);

      // Provider-free guarantee: read-only intercepted traffic only, and the
      // charging-related endpoints were never even requested.
      expect(apiCalls.filter((entry) => !entry.startsWith("GET ")),
        `${locale}: legibility test must never issue a mutation request`).toEqual([]);
      expect(apiCalls.filter((entry) => /checkout|payment|charge/i.test(entry)),
        `${locale}: legibility test must never touch a charging endpoint`).toEqual([]);
    }
  });
});
