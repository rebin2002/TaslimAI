// Test-only fixture for issue #700, section B: Account → Plan & credits.
// Scope is deliberately narrow: the semantics of the disabled plan-change
// actions (plan-option checkout, upgrade, downgrade, cancel) when the billing
// account reports all change actions unavailable, re-checked at browser zoom
// levels and in English LTR / Arabic RTL. It never touches onboarding, never
// enables charging, never asserts prices or credit values beyond their literal
// round-trip, and calls no real API: every /api/ request is fulfilled locally.
import { test, expect, type Locator, type Page } from "@playwright/test";

const DISABLED_REASON = "Customer charging is disabled on this account.";

const session = {
  user: {
    id: "e2e-disabled-actions-user",
    email: "e2e+disabled-actions@example.test",
    displayName: "E2E Disabled Actions Owner",
    preferredLanguage: "en",
    personalWorkspaceId: "e2e-disabled-actions-workspace",
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
    id: "e2e-disabled-actions-workspace",
    name: "E2E Disabled Actions Workspace",
    slug: "e2e-disabled-actions",
    type: "Personal",
    role: "owner",
  },
};

type ChangeActions = {
  checkoutAvailable: boolean;
  upgradeAvailable: boolean;
  downgradeAvailable: boolean;
  cancelAvailable: boolean;
};

const ALL_DISABLED: ChangeActions = {
  checkoutAvailable: false,
  upgradeAvailable: false,
  downgradeAvailable: false,
  cancelAvailable: false,
};

const ALL_ENABLED: ChangeActions = {
  checkoutAvailable: true,
  upgradeAvailable: true,
  downgradeAvailable: true,
  cancelAvailable: true,
};

function billingAccount(actions: ChangeActions) {
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
      id: "e2e-disabled-actions-period",
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
    actions: { ...actions, disabledReason: DISABLED_REASON },
  };
}

type Harness = { page: Page; apiCalls: string[] };

// Fulfils every /api/ request locally; nothing leaves the browser context.
async function harness(page: Page, actions: ChangeActions): Promise<Harness> {
  const apiCalls: string[] = [];
  page.on("request", (request) => {
    const url = new URL(request.url());
    if (url.pathname.startsWith("/api/")) apiCalls.push(`${request.method()} ${url.pathname}`);
  });
  await page.route("**/api/**", (route) => {
    const pathname = new URL(route.request().url()).pathname;
    const json = (body: unknown) => route.fulfill({ json: body });
    if (pathname === "/api/auth/me") return json(session);
    if (pathname === "/api/auth/csrf") return json({ token: "e2e-disabled-actions" });
    if (/^\/api\/workspaces\/[^/]+\/billing$/.test(pathname)) return json(billingAccount(actions));
    return route.fulfill({ status: 204 });
  });
  return { page, apiCalls };
}

async function openBillingAccount(
  page: Page,
  actions: ChangeActions,
  opts: { locale: "en" | "ar"; zoom?: string },
): Promise<Harness> {
  await page.addInitScript((locale) => window.localStorage.setItem("taslim-locale", locale), opts.locale);
  const h = await harness(page, actions);
  await page.goto("/account/billing");
  await expect(page.locator(".billing-plans .billing-plan-option")).toHaveCount(3);
  await expect(page.locator(".billing-grid button")).toHaveCount(3);
  if (opts.zoom) {
    await page.evaluate((zoom) => { document.documentElement.style.zoom = zoom; }, opts.zoom);
    await page.evaluate(() => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  }
  return h;
}

// toBeDisabled/toBeEnabled are single-element assertions in strict mode;
// apply them per matched control.
async function expectEachState(locator: Locator, enabled: boolean) {
  const count = await locator.count();
  expect(count).toBeGreaterThan(0);
  for (let index = 0; index < count; index += 1) {
    const control = locator.nth(index);
    if (enabled) await expect(control).toBeEnabled();
    else await expect(control).toBeDisabled();
  }
}

function intersects(a: { left: number; top: number; right: number; bottom: number }, b: { left: number; top: number; right: number; bottom: number }) {
  return a.left < b.right && b.left < a.right && a.top < b.bottom && b.top < a.bottom;
}

test.describe("Billing plan-change disabled-action semantics (#700 B)", () => {
  test("keeps every change-plan control disabled and explains why, in LTR and RTL at browser zoom", async ({ page }) => {
    for (const locale of ["en", "ar"] as const) {
      for (const zoom of ["1", "1.25", "1.5"]) {
        await openBillingAccount(page, ALL_DISABLED, { locale, zoom });
        await expect(page.locator("html")).toHaveAttribute("dir", locale === "ar" ? "rtl" : "ltr");

        // The disabled reason stays visible next to the payment status.
        await expect(page.locator(".billing-payment-status small")).toHaveText(DISABLED_REASON);

        // Every plan-change affordance carries the real disabled attribute:
        // per-plan checkout, upgrade, downgrade and cancel.
        await expect(page.locator(".billing-plan-option button")).toHaveCount(3);
        await expectEachState(page.locator(".billing-plan-option button"), false);
        await expect(page.locator(".billing-grid .button-row button")).toHaveCount(2);
        await expectEachState(page.locator(".billing-grid .button-row button"), false);
        await expectEachState(page.locator(".billing-grid > .account-card:last-child button"), false);
      }
    }
  });

  test("disabled controls stay inert across zoom and RTL: no API mutation traffic, no navigation", async ({ page }) => {
    for (const locale of ["en", "ar"] as const) {
      for (const zoom of ["1", "1.5"]) {
        const h = await openBillingAccount(page, ALL_DISABLED, { locale, zoom });
        const before = h.apiCalls.slice();
        const buttons = page.locator(".billing-plan-option button, .billing-grid button");
        const count = await buttons.count();
        expect(count).toBe(6);
        for (let index = 0; index < count; index += 1) {
          const button = buttons.nth(index);
          // A plain click never reaches a disabled control; dispatching the
          // event directly proves the inertness is semantic, not incidental.
          await button.evaluate((element) => (element as HTMLElement).click());
          await button.click({ force: true }).catch(() => {});
        }
        expect(h.apiCalls.filter((entry) => !before.includes(entry) && !entry.startsWith("GET ")),
          "disabled plan-change controls must not issue any charging or mutation request").toEqual([]);
        expect(page.url()).toContain("/account/billing");
      }
    }
  });

  test("disabled upgrade and downgrade actions remain separate, reachable elements in RTL and LTR at zoom", async ({ page }) => {
    const viewports = [
      { width: 1440, height: 900, label: "desktop" },
      { width: 390, height: 844, label: "narrow" },
    ];
    for (const locale of ["en", "ar"] as const) {
      for (const viewport of viewports) {
        await page.setViewportSize({ width: viewport.width, height: viewport.height });
        await openBillingAccount(page, ALL_DISABLED, { locale, zoom: "1.25" });
        const rects = await page.locator(".billing-grid .button-row button").evaluateAll((nodes) =>
          nodes.map((node) => {
            const box = node.getBoundingClientRect();
            return { left: box.left, top: box.top, right: box.right, bottom: box.bottom };
          }));
        expect(rects, "upgrade and downgrade must each render").toHaveLength(2);
        const [upgrade, downgrade] = rects;
        expect(intersects(upgrade, downgrade),
          `${locale} ${viewport.label} at 125% zoom: disabled actions must not overlap into one hit target`).toBe(false);
        // Neither control may be clipped out of the viewport horizontally.
        for (const rect of rects) {
          expect(rect.left, "left edge must stay in view").toBeGreaterThanOrEqual(-1);
          expect(rect.right, "right edge must stay in view").toBeLessThanOrEqual(viewport.width + 1);
        }
      }
    }
  });

  test("enablement is flag-driven only: the same fixture with available actions flips controls to enabled", async ({ page }) => {
    const h = await openBillingAccount(page, ALL_ENABLED, { locale: "ar", zoom: "1.25" });
    await expectEachState(page.locator(".billing-plan-option button"), true);
    await expectEachState(page.locator(".billing-grid button"), true);
    // Even when rendered enabled, the placeholder controls themselves are
    // display-only: interacting must still produce no mutation traffic.
    await page.locator(".billing-grid .button-row button").first().click();
    await page.locator(".billing-plan-option button").nth(1).click();
    expect(h.apiCalls.filter((entry) => !entry.startsWith("GET "))).toEqual([]);
    expect(page.url()).toContain("/account/billing");
  });
});
