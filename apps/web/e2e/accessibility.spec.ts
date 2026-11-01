import AxeBuilder from "@axe-core/playwright";
import type { Page } from "@playwright/test";
import { test, expect } from "./fixtures";
import { translate, type Locale } from "../src/lib/i18n";

const onboardingWorkspace = { id: "issue-109-workspace", name: "Accessibility Test Workspace", slug: "issue-109-accessibility", type: "Personal", role: "Owner" };

function onboardingAuthResponse() {
  return {
    user: {
      id: "issue-109-user",
      email: "onboarding-a11y@example.invalid",
      displayName: "Accessibility Test",
      preferredLanguage: "en" as const,
      personalWorkspaceId: onboardingWorkspace.id,
      createdAt: "2026-10-01T00:00:00.000Z",
      defaultGenerationLanguage: "en" as const,
      timeZone: "UTC",
      outputPreference: "balanced" as const,
      includeSourceLinks: true,
      onboardingCompletedAt: null,
      onboardingIntent: null,
      isAdmin: false,
    },
    personalWorkspace: onboardingWorkspace,
  };
}

function onboardingBillingResponse() {
  return {
    currentPlan: { code: "free", name: "Free", monthlyPriceUsd: 0, monthlyCreditAllowance: 1000, currency: "USD" },
    subscription: { status: "Current", currentPeriodStart: "2026-10-01T00:00:00.000Z", currentPeriodEnd: "2026-10-31T23:59:59.000Z", nextRenewalAt: "2026-10-31T23:59:59.000Z", cancelAtPeriodEnd: false },
    billingPeriod: { id: "issue-109-period", status: "Current", startsAt: "2026-10-01T00:00:00.000Z", endsAt: "2026-10-31T23:59:59.000Z", includedCredits: 1000 },
    credits: { includedGranted: 1000, includedRemaining: 1000, purchasedRemaining: 0, adjustmentBalance: 0, totalRemaining: 1000 },
    transactions: [],
    availablePlans: [{ code: "free", name: "Free", monthlyPriceUsd: 0, monthlyCreditAllowance: 1000, currency: "USD", isCurrent: true }],
    paymentStatus: { status: "disabled", provider: null, lastFailureReason: null, lastPaymentAt: null },
    actions: { checkoutAvailable: false, upgradeAvailable: false, downgradeAvailable: false, cancelAvailable: false, disabledReason: "Customer charging is disabled." },
  };
}

async function interceptOnboardingApi(page: Page) {
  const apiCalls: string[] = [];
  const unexpectedApiCalls: string[] = [];
  const externalRequests: string[] = [];
  const appOrigin = new URL(process.env.E2E_WEB_URL ?? "http://localhost:3000").origin;
  const apiOrigin = new URL(process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000").origin;
  const mockOrigins = new Set([appOrigin, apiOrigin]);

  await page.route("**/*", async (route) => {
    const url = new URL(route.request().url());
    const method = route.request().method();
    if (url.pathname.startsWith("/api/") && mockOrigins.has(url.origin)) {
      apiCalls.push(`${method} ${url.pathname}`);
      if (method !== "GET") {
        unexpectedApiCalls.push(`${method} ${url.pathname}`);
        await route.fulfill({ status: 405, contentType: "application/json", body: JSON.stringify({ error: "mock_only_read_test" }) });
        return;
      }
      let body: unknown;
      if (url.pathname === "/api/auth/me") body = onboardingAuthResponse();
      else if (url.pathname === "/api/notifications/unread-count") body = { unreadCount: 0 };
      else if (url.pathname === `/api/workspaces/${onboardingWorkspace.id}/billing`) body = onboardingBillingResponse();
      else {
        unexpectedApiCalls.push(`${method} ${url.pathname}`);
        await route.fulfill({ status: 404, contentType: "application/json", body: JSON.stringify({ error: "unmocked_test_endpoint" }) });
        return;
      }
      await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(body) });
      return;
    }
    if (url.origin !== appOrigin) {
      externalRequests.push(url.href);
      await route.abort();
      return;
    }
    await route.continue();
  });

  return { apiCalls, unexpectedApiCalls, externalRequests };
}

test.describe("basic accessibility smoke", () => {
  test("home and projects have no serious or critical axe violations", async ({ authenticatedPage: page }) => {
    for (const path of ["/", "/projects"]) {
      await page.goto(path);
      const results = await new AxeBuilder({ page }).analyze();
      const seriousOrCritical = results.violations.filter((violation) => ["serious", "critical"].includes(violation.impact ?? ""));
      expect(seriousOrCritical, `${path} accessibility violations`).toEqual([]);
    }
  });

  test("authentication forms expose labels and keyboard focus", async ({ page }) => {
    await page.goto("/login");
    await expect(page.getByLabel(/email/i)).toBeVisible();
    await expect(page.getByRole("textbox", { name: /^password/i })).toBeVisible();
    await page.keyboard.press("Tab");
    await expect(page.locator(":focus")).toBeVisible();
    await page.keyboard.press("Tab");
    await expect(page.locator(":focus")).toBeVisible();
  });


  test("onboarding retains focus across every step, announces progress, and offers only five launch choices", async ({ page }) => {
    test.setTimeout(120_000);
    const traffic = await interceptOnboardingApi(page);
    const profiles: Array<{ locale: Locale; lang: string; dir: string; width: number; height: number }> = [
      { locale: "en", lang: "en", dir: "ltr", width: 1440, height: 900 },
      { locale: "ar", lang: "ar", dir: "rtl", width: 1440, height: 900 },
      { locale: "ku", lang: "ckb", dir: "rtl", width: 1440, height: 900 },
      { locale: "en", lang: "en", dir: "ltr", width: 375, height: 812 },
      { locale: "ar", lang: "ar", dir: "rtl", width: 375, height: 812 },
      { locale: "ku", lang: "ckb", dir: "rtl", width: 375, height: 812 },
    ];

    for (const profile of profiles) {
      await page.setViewportSize({ width: profile.width, height: profile.height });
      await page.goto("/account/billing");
      const dialog = page.getByRole("dialog");
      await expect(dialog).toBeVisible();
      await expect(dialog.locator("[data-dialog-autofocus]")).toBeFocused();
      await page.evaluate(() => {
        const outside = document.createElement("button");
        outside.type = "button";
        outside.dataset.onboardingFocusProbe = "true";
        outside.textContent = "Outside focus probe";
        document.body.appendChild(outside);
        outside.focus();
      });
      expect(await dialog.evaluate((element) => element.contains(document.activeElement))).toBe(true);
      await page.locator("[data-onboarding-focus-probe]").evaluate((element) => element.remove());
      const progress = dialog.getByRole("progressbar");
      const heading = dialog.locator("#onboarding-title");
      const assertProgressAndFocus = async (step: number) => {
        await expect(progress).toHaveAttribute("aria-valuemin", "1");
        await expect(progress).toHaveAttribute("aria-valuemax", "3");
        await expect(progress).toHaveAttribute("aria-valuenow", String(step));
        await expect(progress).toHaveAttribute("aria-label", translate(profile.locale, "onboarding.progress", { current: String(step), total: "3" }));
        await expect(heading).toBeFocused();
        expect(await dialog.evaluate((element) => element.contains(document.activeElement))).toBe(true);
      };

      const skip = dialog.locator(".onboarding-skip");
      const next = dialog.locator(".onboarding-actions .primary-button");
      await skip.focus();
      await page.keyboard.press("Shift+Tab");
      await expect(next).toBeFocused();
      await page.keyboard.press("Tab");
      await expect(skip).toBeFocused();
      await page.keyboard.press("Escape");
      await expect(dialog).toBeVisible();
      expect(await dialog.evaluate((element) => element.contains(document.activeElement))).toBe(true);

      await dialog.locator(".onboarding-fields select").first().selectOption(profile.locale);
      await expect(page.locator("html")).toHaveAttribute("lang", profile.lang);
      await expect(page.locator("html")).toHaveAttribute("dir", profile.dir);
      await next.click();
      await assertProgressAndFocus(2);
      await next.click();
      await assertProgressAndFocus(3);

      const workflowNames = await dialog.locator(".onboarding-workflow strong").allTextContents();
      expect(workflowNames.map((name) => name.trim()).sort()).toEqual([
        translate(profile.locale, "onboarding.workflow.chat.title"),
        translate(profile.locale, "onboarding.workflow.image.title"),
        translate(profile.locale, "onboarding.workflow.document.title"),
        translate(profile.locale, "onboarding.workflow.presentation.title"),
        translate(profile.locale, "navigation.movieStudio"),
      ].sort());
      await expect(dialog.locator(".onboarding-workflow")).toHaveCount(5);
      await expect(dialog.locator(".onboarding-workflow").filter({ hasText: translate(profile.locale, "navigation.movieStudio") }))
        .toContainText(translate(profile.locale, "movieBody.quick.oneBrief"));
      await expect(dialog.locator("a[href^='/create/']")).toHaveCount(0);

      await dialog.locator(".onboarding-actions .onboarding-back").click();
      await assertProgressAndFocus(2);
      await dialog.locator(".onboarding-actions .onboarding-back").click();
      await assertProgressAndFocus(1);
    }

    expect(traffic.unexpectedApiCalls).toEqual([]);
    expect(traffic.externalRequests).toEqual([]);
    expect(traffic.apiCalls.every((call) => call.startsWith("GET "))).toBe(true);
  });
});
