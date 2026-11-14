import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Locator, type Page } from "@playwright/test";
import { resolve } from "node:path";

const stylesheetPath = resolve(__dirname, "../src/app/onboarding-billing-ui.css");
const screenshotDir = resolve(process.cwd(), "test-results");

const fixtureBaseStyles = `
  <style>
    * { box-sizing: border-box; }
    html, body { width: 100%; min-width: 320px; margin: 0; font-family: Arial, Helvetica, sans-serif; }
    body { background: #f7f9fb; color: #172639; }
    .page-content { width: 100%; max-width: 1440px; margin: 0 auto; padding: 0 24px 64px; }
    .modal-backdrop { position: fixed; z-index: 50; inset: 0; display: grid; place-items: center; padding: 20px; background: rgba(7, 17, 31, .42); }
    .section-eyebrow { color: #1f5fb8; font-size: 10px; font-weight: 800; letter-spacing: .13em; text-transform: uppercase; }
    .account-page { max-width: 1040px; margin: 0 auto; padding-top: 42px; }
    .detail-header { display: flex; align-items: end; justify-content: space-between; gap: 28px; padding: 42px 0 26px; border-bottom: 1px solid #e8edf2; }
    .detail-header h1 { margin: 8px 0; }
    .account-card { min-width: 0; padding: 23px; border: 1px solid #e8edf2; border-radius: 13px; background: #fff; }
    .card-title { display: flex; align-items: center; gap: 10px; margin-bottom: 9px; }
    .card-title-icon { display: grid; width: 31px; height: 31px; flex: 0 0 auto; place-items: center; border-radius: 8px; background: #eaf2ff; color: #1f5fb8; }
    .card-title-icon.teal { background: #e7f7f4; color: #16877f; }
    .card-title h2 { margin: 0 0 4px; color: #0b1829; font-size: 16px; }
    .card-title p { margin: 0; color: #748496; font-size: 11px; }
    .primary-button, .secondary-button { display: inline-flex; align-items: center; justify-content: center; padding: 10px 14px; border: 0; border-radius: 8px; font: inherit; }
    .primary-button { background: #0b1829; color: white; }
    .secondary-button { background: #eef5ff; color: #1f5fb8; }
    .back-link { display: inline-flex; align-items: center; gap: 6px; color: #526273; font-size: 11px; }
    .detail-icon { display: grid; width: 43px; height: 43px; flex: 0 0 auto; place-items: center; border-radius: 12px; background: #eaf2ff; color: #1f5fb8; }
    .usage-table-wrap { max-width: 100%; overflow-x: auto; }
    .usage-empty { color: #748496; }
  </style>
`;

// Neutral fixture copy exercises card geometry only; it does not model product modules or availability.
const workflows: Array<[string, string]> = [
  ["Example workflow alpha", "Placeholder copy checks wrapping, card spacing, and readable supporting text."],
  ["Example workflow beta", "A second neutral example keeps the desktop grid populated."],
  ["Example workflow gamma", "Short fixture copy verifies consistent card alignment."],
  ["Example workflow delta", "Another neutral example checks the final row and mobile stacking."],
];

function onboardingFixture(language = "en", direction = "ltr") {
  const workflowMarkup = workflows.map(([title, description]) => `
    <button type="button" class="onboarding-workflow">
      <span class="onboarding-workflow-icon" aria-hidden="true">◇</span>
      <span><strong>${title}</strong><small>${description}</small></span>
      <span class="onboarding-workflow-arrow" aria-hidden="true">${direction === "rtl" ? "←" : "→"}</span>
    </button>
  `).join("");

  return `<!doctype html>
    <html lang="${language}" dir="${direction}">
      <head><meta name="viewport" content="width=device-width, initial-scale=1" /><title>Taslim.ai onboarding fixture</title>${fixtureBaseStyles}</head>
      <body>
        <div class="modal-backdrop onboarding-backdrop" role="presentation">
          <section class="onboarding-card" role="dialog" aria-modal="true" aria-labelledby="onboarding-title" tabindex="-1">
            <header class="onboarding-header">
              <div class="onboarding-brand"><span class="onboarding-brand-mark" aria-hidden="true">✦</span><span>Taslim.ai</span></div>
              <button type="button" class="onboarding-skip">Skip for now</button>
            </header>
            <div class="onboarding-progress" role="progressbar" aria-label="Step 3 of 3" aria-valuemin="1" aria-valuemax="3" aria-valuenow="3"><span class="is-complete"></span><span class="is-complete"></span><span class="is-complete"></span></div>
            <div class="onboarding-body onboarding-workflow-body">
              <p class="section-eyebrow">A workspace built around you</p>
              <h1 id="onboarding-title">Choose a workflow to get started</h1>
              <p class="onboarding-description">Neutral example tiles validate layout only and do not establish product-module availability.</p>
              <div class="onboarding-workflow-grid">${workflowMarkup}</div>
              <footer class="onboarding-actions">
                <button type="button" class="onboarding-back">Back</button>
                <span>You can skip this step and explore your workspace at any time.</span>
              </footer>
            </div>
          </section>
        </div>
      </body>
    </html>`;
}

function billingFixture(language = "en", direction = "ltr") {
  const plans: Array<[string, string, string, string, boolean]> = [
    ["Free", "Current", "No charge", "1,000 included credits", true],
    ["Paid plan", "", "$9 / month", "10,000 included credits", false],
  ];
  const planMarkup = plans.map(([name, badge, price, credits, current]) => `
    <article class="billing-plan-option${current ? " current" : ""}">
      <div><strong>${name}</strong>${badge ? `<span class="billing-current-badge">${badge}</span>` : ""}</div>
      <b>${price}</b>
      <span>${credits}</span>
      <button class="secondary-button" type="button" disabled>${current ? "Current plan" : "Upgrade unavailable"}</button>
    </article>
  `).join("");

  return `<!doctype html>
    <html lang="${language}" dir="${direction}">
      <head><meta name="viewport" content="width=device-width, initial-scale=1" /><title>Taslim.ai billing fixture</title>${fixtureBaseStyles}</head>
      <body>
        <main class="page-content">
          <div class="account-page billing-page">
            <div class="detail-header usage-header">
              <div><a class="back-link" href="#account">Account</a><p class="section-eyebrow">Account overview</p><h1>Plan &amp; credits</h1><p>Review your current plan, included credits, and account history.</p></div>
              <div class="detail-icon" aria-hidden="true">◈</div>
            </div>
            <div class="billing-notice"><span>Plan and credit details are shown here. Charging remains disabled.</span></div>
              <div class="billing-summary-grid">
              <div class="account-card billing-plan-card"><div class="card-title"><span class="card-title-icon">✦</span><div><h2>Current plan</h2><p>Subscription status</p></div></div><strong>Free</strong><span>No charge</span></div>
              <div class="account-card billing-credit-card"><div class="card-title"><span class="card-title-icon teal">◉</span><div><h2>Remaining credits</h2><p>Credit balance</p></div></div><strong>1,000</strong><span>1,000 included credits</span></div>
              <div class="account-card billing-period-card"><div class="card-title"><span class="card-title-icon">◷</span><div><h2>Billing period</h2><p>Current period</p></div></div><strong>Current period</strong><span>Renewal date shown in your account</span></div>
            </div>
            <div class="account-card billing-payment-status">
              <div class="card-title"><span class="card-title-icon">▤</span><div><h2>Payment status</h2><p>Customer charging status</p></div></div>
              <strong>Charging disabled</strong>
              <small>Customer charging is disabled on this account.</small>
            </div>
            <div class="account-card billing-plans">
              <div class="card-title"><span class="card-title-icon teal">✦</span><div><h2>Available plans</h2><p>Plan comparisons are informational; plan actions remain unavailable.</p></div></div>
              <div class="billing-plan-options">${planMarkup}</div>
            </div>
            <div class="account-grid billing-grid">
              <div class="account-card"><div class="card-title"><span class="card-title-icon teal">✦</span><div><h2>Change plan</h2><p>Plan changes are currently unavailable.</p></div></div><div class="button-row"><button class="secondary-button" type="button" disabled>Upgrade</button><button class="secondary-button" type="button" disabled>Downgrade</button></div></div>
              <div class="account-card"><div class="card-title"><span class="card-title-icon">◷</span><div><h2>Cancel subscription</h2><p>No cancellation is scheduled.</p></div></div><button class="secondary-button" type="button" disabled>Cancel unavailable</button></div>
            </div>
            <div class="account-card">
              <div class="card-title"><span class="card-title-icon">◉</span><div><h2>Credit allowance</h2><p>Included, purchased, and adjusted credits.</p></div></div>
              <dl class="billing-definition-list"><div><dt>Included allowance</dt><dd>1,000</dd></div><div><dt>Purchased credits</dt><dd>—</dd></div><div><dt>Corrections</dt><dd>—</dd></div></dl>
            </div>
            <div class="account-card billing-history">
              <div class="card-title"><span class="card-title-icon">▤</span><div><h2>Transaction history</h2><p>Recent credit ledger entries.</p></div></div>
              <div class="usage-table-wrap"><table class="usage-table billing-table"><thead><tr><th>Type</th><th>Reason</th><th>Amount</th><th>Date</th></tr></thead><tbody><tr><td><strong>Included grant</strong></td><td>Monthly included credit allowance</td><td class="billing-positive">+1,000</td><td>Oct 1, 2026</td></tr></tbody></table></div>
            </div>
          </div>
        </main>
      </body>
    </html>`;
}

const liveWorkspace = { id: "issue-700-workspace", name: "UI Regression Workspace", slug: "issue-700-ui-regression", type: "Personal", role: "Owner" };

function liveAuthResponse(onboardingCompletedAt: string | null) {
  return {
    user: {
      id: "issue-700-user",
      email: "ui-regression@example.invalid",
      displayName: "UI Regression",
      preferredLanguage: "en" as const,
      personalWorkspaceId: liveWorkspace.id,
      createdAt: "2026-10-01T00:00:00.000Z",
      defaultGenerationLanguage: "en" as const,
      timeZone: "UTC",
      outputPreference: "balanced" as const,
      includeSourceLinks: true,
      onboardingCompletedAt,
      onboardingIntent: null,
      isAdmin: false,
    },
    personalWorkspace: liveWorkspace,
  };
}

function liveBillingAccount() {
  return {
    currentPlan: { code: "free", name: "Free", monthlyPriceUsd: 0, monthlyCreditAllowance: 1_000, currency: "USD" },
    subscription: { status: "Current", currentPeriodStart: "2026-10-01T00:00:00.000Z", currentPeriodEnd: "2026-10-31T23:59:59.000Z", nextRenewalAt: "2026-10-31T23:59:59.000Z", cancelAtPeriodEnd: false },
    billingPeriod: { id: "issue-700-period", status: "Current", startsAt: "2026-10-01T00:00:00.000Z", endsAt: "2026-10-31T23:59:59.000Z", includedCredits: 1_000 },
    credits: { includedGranted: 1_000, includedRemaining: 1_000, purchasedRemaining: 0, adjustmentBalance: 0, totalRemaining: 1_000 },
    transactions: [{ id: "issue-700-grant", type: "grant", amount: 1_000, reason: "Monthly included credit allowance", createdAt: "2026-10-01T00:00:00.000Z" }],
    availablePlans: [
      { code: "free", name: "Free", monthlyPriceUsd: 0, monthlyCreditAllowance: 1_000, currency: "USD", isCurrent: true },
      { code: "paid", name: "Paid plan", monthlyPriceUsd: 9, monthlyCreditAllowance: 10_000, currency: "USD", isCurrent: false },
    ],
    paymentStatus: { status: "disabled", provider: null, lastFailureReason: null, lastPaymentAt: null },
    actions: { checkoutAvailable: false, upgradeAvailable: false, downgradeAvailable: false, cancelAvailable: false, disabledReason: "Customer charging is disabled on this account." },
  };
}

type LiveApiTraffic = { apiCalls: Array<{ method: string; path: string }>; unexpectedApiCalls: string[]; externalRequests: string[] };

async function interceptLiveAppApi(page: Page, onboardingCompletedAt: string | null): Promise<LiveApiTraffic> {
  const apiCalls: LiveApiTraffic["apiCalls"] = [];
  const unexpectedApiCalls: string[] = [];
  const externalRequests: string[] = [];
  const appOrigin = new URL(process.env.E2E_WEB_URL ?? "http://localhost:3000").origin;
  const configuredApiOrigin = new URL(process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000").origin;
  const mockOrigins = new Set([appOrigin, configuredApiOrigin]);
  const auth = liveAuthResponse(onboardingCompletedAt);

  await page.route("**/*", async (route) => {
    const url = new URL(route.request().url());
    const method = route.request().method();
    if (url.pathname.startsWith("/api/") && mockOrigins.has(url.origin)) {
      apiCalls.push({ method, path: url.pathname });
      if (method !== "GET") {
        unexpectedApiCalls.push(`${method} ${url.pathname}`);
        await route.fulfill({ status: 405, contentType: "application/json", body: JSON.stringify({ error: "mock_only_read_test" }) });
        return;
      }
      let body: unknown;
      if (url.pathname === "/api/auth/me") body = auth;
      else if (url.pathname === "/api/notifications/unread-count") body = { unreadCount: 0 };
      else if (url.pathname === `/api/workspaces/${liveWorkspace.id}/billing`) body = liveBillingAccount();
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

function expectIsolatedReadOnlyTraffic(traffic: LiveApiTraffic, allowedPaths: string[]) {
  expect(traffic.externalRequests).toEqual([]);
  expect(traffic.unexpectedApiCalls).toEqual([]);
  expect(traffic.apiCalls.every(({ method }) => method === "GET")).toBe(true);
  expect(traffic.apiCalls.every(({ path }) => allowedPaths.includes(path))).toBe(true);
}

async function assertNoSeriousOrCriticalAxeViolations(page: Page, includeSelector: string) {
  const results = await new AxeBuilder({ page }).include(includeSelector).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze();
  const seriousOrCritical = results.violations.filter((violation) => ["serious", "critical"].includes(violation.impact ?? ""));
  expect(seriousOrCritical.map((violation) => ({ id: violation.id, description: violation.description, nodes: violation.nodes.length }))).toEqual([]);
}

async function loadFixture(page: Page, html: string) {
  await page.setContent(html);
  await page.addStyleTag({ path: stylesheetPath });
}

type Box = { x: number; y: number; width: number; height: number };

async function boxOf(locator: Locator): Promise<Box> {
  const box = await locator.boundingBox();
  expect(box, `Expected a visible box for ${locator}`).not.toBeNull();
  return box!;
}

function overlaps(a: Box, b: Box): boolean {
  return a.x < b.x + b.width - 1 && b.x < a.x + a.width - 1 && a.y < b.y + b.height - 1 && b.y < a.y + a.height - 1;
}

async function gridColumnCount(locator: Locator): Promise<number> {
  return locator.evaluate((element) => getComputedStyle(element).gridTemplateColumns.split(" ").filter(Boolean).length);
}

test.describe("issue #700 onboarding and billing layout", () => {
  test("centers the onboarding dialog and keeps neutral fixture tiles distinct at desktop, tablet, and mobile widths", async ({ page }) => {
    await loadFixture(page, onboardingFixture());

    for (const viewport of [
      { width: 1920, height: 1080, columns: 2 },
      { width: 1440, height: 900, columns: 2 },
      { width: 768, height: 900, columns: 2 },
      { width: 375, height: 812, columns: 1 },
      { width: 320, height: 640, columns: 1 },
    ]) {
      await page.setViewportSize({ width: viewport.width, height: viewport.height });
      const dialog = page.getByRole("dialog", { name: "Choose a workflow to get started" });
      const dialogBox = await boxOf(dialog);
      expect(Math.abs(dialogBox.x + dialogBox.width / 2 - viewport.width / 2)).toBeLessThanOrEqual(2);
      expect(dialogBox.width).toBeLessThanOrEqual(viewport.width - 20);
      expect(dialogBox.y).toBeGreaterThanOrEqual(0);
      expect(dialogBox.y + dialogBox.height).toBeLessThanOrEqual(viewport.height + 1);
      expect(await gridColumnCount(page.locator(".onboarding-workflow-grid"))).toBe(viewport.columns);
      await expect(page.locator(".onboarding-workflow")).toHaveCount(workflows.length);

      const tileBoxes = await page.locator(".onboarding-workflow").evaluateAll((elements) => elements.map((element) => {
        const rect = element.getBoundingClientRect();
        return { x: rect.x, y: rect.y, width: rect.width, height: rect.height, border: getComputedStyle(element).borderTopStyle };
      }));
      expect(tileBoxes.every((box) => box.border === "solid")).toBe(true);
      for (let left = 0; left < tileBoxes.length; left += 1) {
        for (let right = left + 1; right < tileBoxes.length; right += 1) {
          expect(overlaps(tileBoxes[left], tileBoxes[right]), `Workflow tiles ${left + 1} and ${right + 1} overlap at ${viewport.width}px`).toBe(false);
        }
      }

      const firstTile = page.locator(".onboarding-workflow").first();
      const icon = await boxOf(firstTile.locator(".onboarding-workflow-icon"));
      const copy = await boxOf(firstTile.locator(":scope > span:nth-child(2)"));
      const arrow = await boxOf(firstTile.locator(".onboarding-workflow-arrow"));
      expect(icon.x + icon.width).toBeLessThanOrEqual(copy.x + 1);
      expect(copy.x + copy.width).toBeLessThanOrEqual(arrow.x + 2);

      if (process.env.ISSUE_700_SAVE_SHOTS === "1" && (viewport.width === 1440 || viewport.width === 375)) {
        await page.screenshot({ path: resolve(screenshotDir, `issue-700-onboarding-after-${viewport.width}.png`), fullPage: true });
      }
    }

    await page.setViewportSize({ width: 375, height: 520 });
    const dialog = page.locator(".onboarding-card");
    await expect.poll(() => dialog.evaluate((element) => element.scrollHeight > element.clientHeight)).toBe(true);
    expect(await dialog.evaluate((element) => getComputedStyle(element).overflowY)).toBe("auto");
    await dialog.evaluate((element) => { element.scrollTop = element.scrollHeight; });
    await expect(page.locator(".onboarding-workflow").last()).toBeInViewport();
    await expect(page.locator(".onboarding-actions")).toBeInViewport();
    if (process.env.ISSUE_700_SAVE_SHOTS === "1") {
      await page.screenshot({ path: resolve(screenshotDir, "issue-700-onboarding-after-375-bottom.png"), fullPage: true });
    }
  });

  test("separates billing labels, values, and disabled actions with responsive plan and summary grids", async ({ page }) => {
    await loadFixture(page, billingFixture());

    for (const viewport of [
      { width: 1920, height: 1080, summaryColumns: 3, planColumns: 2 },
      { width: 1440, height: 900, summaryColumns: 3, planColumns: 2 },
      { width: 768, height: 900, summaryColumns: 2, planColumns: 2 },
      { width: 375, height: 812, summaryColumns: 1, planColumns: 1 },
      { width: 320, height: 812, summaryColumns: 1, planColumns: 1 },
    ]) {
      await page.setViewportSize({ width: viewport.width, height: viewport.height });
      expect(await gridColumnCount(page.locator(".billing-summary-grid"))).toBe(viewport.summaryColumns);
      const renderedPlanColumns = await page.locator(".billing-plan-option").evaluateAll((elements) => new Set(elements.map((element) => Math.round(element.getBoundingClientRect().x))).size);
      expect(renderedPlanColumns).toBe(viewport.planColumns);
      await expect(page.locator(".billing-summary-grid > .account-card")).toHaveCount(3);
      await expect(page.locator(".billing-plan-option")).toHaveCount(2);

      const paymentStatus = page.locator(".billing-payment-status");
      const status = await boxOf(paymentStatus.locator(":scope > strong"));
      const detail = await boxOf(paymentStatus.locator(":scope > small"));
      expect(status.y + status.height).toBeLessThanOrEqual(detail.y + 1);
      expect(await paymentStatus.locator(":scope > strong").textContent()).toBe("Charging disabled");
      expect(await paymentStatus.locator(":scope > small").textContent()).toContain("Customer charging");

      const plan = page.locator(".billing-plan-option").first();
      const planName = await boxOf(plan.locator(":scope > div:first-child > strong"));
      const price = await boxOf(plan.locator(":scope > b"));
      const allowance = await boxOf(plan.locator(":scope > span:not(.billing-current-badge)"));
      const planButton = await boxOf(plan.locator(":scope > button"));
      expect(planName.y + planName.height).toBeLessThanOrEqual(price.y + 1);
      expect(price.y + price.height).toBeLessThanOrEqual(allowance.y + 1);
      expect(allowance.y + allowance.height).toBeLessThanOrEqual(planButton.y + 2);
      await expect(plan.locator("button")).toBeDisabled();
      await expect(page.locator(".billing-plan-option button:disabled")).toHaveCount(2);
      await expect(page.locator(".billing-grid button:disabled")).toHaveCount(3);

      const actionButtons = page.locator(".billing-page .button-row button");
      const firstAction = await boxOf(actionButtons.nth(0));
      const secondAction = await boxOf(actionButtons.nth(1));
      const sameRow = Math.abs(firstAction.y + firstAction.height / 2 - secondAction.y - secondAction.height / 2) < 2;
      const actualGap = sameRow
        ? Math.abs(firstAction.x + firstAction.width / 2 - secondAction.x - secondAction.width / 2) - (firstAction.width + secondAction.width) / 2
        : Math.abs(firstAction.y + firstAction.height / 2 - secondAction.y - secondAction.height / 2) - (firstAction.height + secondAction.height) / 2;
      expect(actualGap, `Change-plan actions need a visible gap at ${viewport.width}px`).toBeGreaterThanOrEqual(8);

      const pageWidth = await page.evaluate(() => document.documentElement.scrollWidth);
      expect(pageWidth, `Billing page has horizontal overflow at ${viewport.width}px`).toBeLessThanOrEqual(viewport.width);

      if (viewport.width === 1440) {
        const planButtons = await page.locator(".billing-plan-option > button").evaluateAll((elements) => elements.map((element) => element.getBoundingClientRect().bottom));
        expect(Math.max(...planButtons) - Math.min(...planButtons)).toBeLessThanOrEqual(1);
        if (process.env.ISSUE_700_SAVE_SHOTS === "1") {
          await page.screenshot({ path: resolve(screenshotDir, "issue-700-billing-after-1440.png"), fullPage: true });
        }
      }
      if (viewport.width === 375 && process.env.ISSUE_700_SAVE_SHOTS === "1") {
        await page.screenshot({ path: resolve(screenshotDir, "issue-700-billing-after-375.png"), fullPage: true });
      }
    }

    const historyScroller = page.locator(".billing-history .usage-table-wrap");
    await page.setViewportSize({ width: 320, height: 812 });
    await expect(historyScroller).toBeVisible();
    expect(await historyScroller.evaluate((element) => element.scrollWidth > element.clientWidth)).toBe(true);
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(320);
  });

  test("preserves RTL layout and visible keyboard focus in the static fixture for Arabic and Sorani", async ({ page }) => {
    for (const language of ["ar", "ku"]) {
      await loadFixture(page, onboardingFixture(language, "rtl"));
      await page.setViewportSize({ width: 375, height: 812 });
      await expect(page.getByRole("dialog")).toBeVisible();
      expect(await page.evaluate(() => document.documentElement.dir)).toBe("rtl");
      expect(await gridColumnCount(page.locator(".onboarding-workflow-grid"))).toBe(1);
      expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(375);
      await expect(page.locator(".onboarding-workflow-arrow").first()).toHaveText("←");
      expect(await page.locator(".onboarding-workflow-arrow").first().evaluate((element) => getComputedStyle(element).transform)).toBe("none");

      await page.keyboard.press("Tab");
      await expect(page.locator(":focus")).toHaveClass(/onboarding-skip/);
      await page.keyboard.press("Tab");
      await expect(page.locator(":focus")).toHaveClass(/onboarding-workflow/);
      const outline = await page.locator(":focus").evaluate((element) => ({ style: getComputedStyle(element).outlineStyle, width: getComputedStyle(element).outlineWidth }));
      expect(outline.style).toBe("solid");
      expect(parseFloat(outline.width)).toBeGreaterThanOrEqual(3);

      await loadFixture(page, billingFixture(language, "rtl"));
      await page.setViewportSize({ width: 375, height: 812 });
      await expect(page.locator(".billing-payment-status > strong")).toBeVisible();
      expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(375);
      expect(await gridColumnCount(page.locator(".billing-summary-grid"))).toBe(1);
    }
  });

  test("checks preference-control layout and visible keyboard focus in static fixtures for English, Arabic, and Sorani", async ({ page }) => {
    const locales = [
      { code: "en", direction: "ltr", display: "Display name", interface: "Interface language", generation: "Generation language", continue: "Continue" },
      { code: "ar", direction: "rtl", display: "الاسم", interface: "لغة الواجهة", generation: "لغة التوليد", continue: "متابعة" },
      { code: "ku", direction: "rtl", display: "ناو", interface: "زمانی ڕووکار", generation: "زمانی دروستکردن", continue: "بەردەوام بە" },
    ];

    for (const locale of locales) {
      const html = `<!doctype html>
        <html lang="${locale.code}" dir="${locale.direction}">
          <head><meta name="viewport" content="width=device-width, initial-scale=1" /><title>Taslim.ai preferences fixture (${locale.code})</title>${fixtureBaseStyles}</head>
          <body>
            <div class="modal-backdrop onboarding-backdrop" role="presentation">
              <section class="onboarding-card" role="dialog" aria-modal="true" aria-labelledby="onboarding-title" tabindex="-1">
                <header class="onboarding-header"><div class="onboarding-brand"><span class="onboarding-brand-mark" aria-hidden="true">✦</span><span>Taslim.ai</span></div><button type="button" class="onboarding-skip">Skip for now</button></header>
                <div class="onboarding-progress" role="progressbar" aria-label="Step 1 of 3" aria-valuemin="1" aria-valuemax="3" aria-valuenow="1"><span class="is-complete"></span><span></span><span></span></div>
                <div class="onboarding-body">
                  <p class="section-eyebrow">Personalize your workspace</p>
                  <h1 id="onboarding-title">Set up your preferences</h1>
                  <p class="onboarding-description">Choose how Taslim should address you and select the languages you want to use across the interface and generated content.</p>
                  <div class="onboarding-fields">
                    <label><span>${locale.display}</span><input aria-label="${locale.display}" placeholder="Your name" /></label>
                    <label><span>${locale.interface}</span><select aria-label="${locale.interface}"><option>English</option><option>العربية</option><option>کوردی</option></select></label>
                    <label><span>${locale.generation}</span><select aria-label="${locale.generation}"><option>English</option><option>العربية</option><option>کوردی</option></select></label>
                  </div>
                  <footer class="onboarding-actions"><span>Your preferences can be changed later.</span><button type="button" class="primary-button"><span>${locale.continue}</span><span aria-hidden="true">→</span></button></footer>
                </div>
              </section>
            </div>
          </body>
        </html>`;
      await loadFixture(page, html);

      for (const viewport of [
        { width: 1440, height: 900, columns: 2 },
        { width: 768, height: 900, columns: 2 },
        { width: 375, height: 812, columns: 1 },
        { width: 320, height: 640, columns: 1 },
      ]) {
        await page.setViewportSize({ width: viewport.width, height: viewport.height });
        expect(await gridColumnCount(page.locator(".onboarding-fields"))).toBe(viewport.columns);
        await expect(page.locator(".onboarding-fields select")).toHaveCount(2);
        await expect(page.locator(".onboarding-progress span")).toHaveCount(3);
        for (const control of await page.locator(".onboarding-fields input, .onboarding-fields select").all()) {
          const box = await boxOf(control);
          expect(box.height).toBeGreaterThanOrEqual(44);
          expect(box.width).toBeGreaterThan(180);
          expect(box.x).toBeGreaterThanOrEqual(0);
          expect(box.x + box.width).toBeLessThanOrEqual(viewport.width + 1);
        }
        expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(viewport.width);
        expect(parseFloat(await page.locator(".onboarding-actions").evaluate((element) => getComputedStyle(element).columnGap))).toBeGreaterThanOrEqual(8);
      }

      await page.setViewportSize({ width: 375, height: 812 });
      await page.keyboard.press("Tab");
      await expect(page.getByRole("button", { name: "Skip for now" })).toBeFocused();
      await page.keyboard.press("Tab");
      await expect(page.getByRole("textbox", { name: locale.display })).toBeFocused();
      await page.keyboard.press("Tab");
      const interfaceSelector = page.getByRole("combobox", { name: locale.interface });
      await expect(interfaceSelector).toBeFocused();
      const focusStyle = await interfaceSelector.evaluate((element) => ({ outline: getComputedStyle(element).outlineStyle, width: getComputedStyle(element).outlineWidth }));
      expect(focusStyle.outline).toBe("solid");
      expect(parseFloat(focusStyle.width)).toBeGreaterThanOrEqual(3);
    }
  });

  test("reports no serious or critical axe violations in onboarding and billing fixtures", async ({ page }) => {
    const fixtures = [onboardingFixture(), billingFixture()];
    for (const fixture of fixtures) {
      await loadFixture(page, fixture);
      const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze();
      const seriousOrCritical = results.violations.filter((violation) => ["serious", "critical"].includes(violation.impact ?? ""));
      expect(seriousOrCritical.map((violation) => ({ id: violation.id, description: violation.description, nodes: violation.nodes.length }))).toEqual([]);
    }
  });

  test("mounts real first-login onboarding with intercepted read-only traffic without asserting module availability", async ({ page }) => {
    const traffic = await interceptLiveAppApi(page, null);
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto("/account/billing");

    const dialog = page.getByRole("dialog");
    await expect(dialog).toBeVisible();
    await expect(dialog).toHaveAttribute("aria-modal", "true");
    await expect(page.locator(".onboarding-progress span")).toHaveCount(3);
    const interfaceLanguage = page.locator(".onboarding-fields select").first();
    for (const locale of ["ar", "ku", "en"] as const) {
      await interfaceLanguage.selectOption(locale);
      await expect(page.locator("html")).toHaveAttribute("dir", locale === "en" ? "ltr" : "rtl");
    }

    await interfaceLanguage.selectOption("ku");
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
    await page.locator(".onboarding-actions .primary-button").click();
    await page.locator(".onboarding-actions .primary-button").click();
    // This smoke test covers layout/RTL; the focused Free-account test below checks choices and modal behavior.
    await expect(page.locator(".onboarding-workflow-grid")).toBeVisible();
    await expect(page.locator(".onboarding-workflow").first()).toBeVisible();
    const firstWorkflowArrow = page.locator(".onboarding-workflow-arrow").first();
    expect(await firstWorkflowArrow.evaluate((element) => getComputedStyle(element).transform)).toBe("none");
    const arrowPaths = await firstWorkflowArrow.locator("path").evaluateAll((paths) => paths.map((path) => path.getAttribute("d")));
    expect(arrowPaths).toContain("M19 12H5");
    await assertNoSeriousOrCriticalAxeViolations(page, ".onboarding-backdrop");

    for (const viewport of [
      { width: 1440, height: 900, columns: 2 },
      { width: 768, height: 900, columns: 2 },
      { width: 375, height: 812, columns: 1 },
      { width: 320, height: 640, columns: 1 },
    ]) {
      await page.setViewportSize({ width: viewport.width, height: viewport.height });
      expect(await gridColumnCount(page.locator(".onboarding-workflow-grid"))).toBe(viewport.columns);
      expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(viewport.width);
      const dialogBox = await boxOf(dialog);
      expect(Math.abs(dialogBox.x + dialogBox.width / 2 - viewport.width / 2)).toBeLessThanOrEqual(2);
      if (process.env.ISSUE_700_SAVE_SHOTS === "1" && (viewport.width === 1440 || viewport.width === 375)) {
        await page.screenshot({ path: resolve(screenshotDir, `issue-700-live-onboarding-after-${viewport.width}.png`), fullPage: viewport.width === 1440 });
      }
    }

    await page.setViewportSize({ width: 375, height: 520 });
    await dialog.evaluate((element) => { element.scrollTop = element.scrollHeight; });
    await expect(page.locator(".onboarding-workflow").last()).toBeInViewport();
    await expect(page.locator(".onboarding-actions .onboarding-back")).toBeInViewport();
    if (process.env.ISSUE_700_SAVE_SHOTS === "1") {
      await page.screenshot({ path: resolve(screenshotDir, "issue-700-live-onboarding-after-375-bottom.png"), fullPage: false });
    }

    expect(traffic.apiCalls.some(({ path }) => path === "/api/auth/me")).toBe(true);
    expectIsolatedReadOnlyTraffic(traffic, ["/api/auth/me", "/api/notifications/unread-count", `/api/workspaces/${liveWorkspace.id}/billing`]);
  });

  test("mounts the real Account → Plan & credits route with intercepted billing data and disabled actions", async ({ page }) => {
    const traffic = await interceptLiveAppApi(page, "2026-10-01T00:00:00.000Z");
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto("/account/billing");
    await expect(page.locator(".billing-page h1")).toHaveText("Plan & credits");
    await expect(page.locator(".billing-plan-card > strong")).toHaveText("Free");
    await expect(page.locator(".billing-plan-card > span")).toHaveText("No charge");
    await expect(page.locator(".billing-plan-option")).toHaveCount(2);
    const paidPlan = page.locator(".billing-plan-option").nth(1);
    await expect(paidPlan.locator("div > strong")).toHaveText("Paid plan");
    await expect(paidPlan.locator("b")).toContainText("$9");
    await expect(paidPlan.locator(":scope > span")).toContainText("10,000");
    await expect(page.locator(".billing-payment-status strong")).toBeVisible();
    await expect(page.locator(".billing-payment-status small")).toHaveText("Customer charging is disabled on this account.");
    await expect(page.locator(".billing-page button")).toHaveCount(5);
    for (const button of await page.locator(".billing-page button").all()) await expect(button).toBeDisabled();
    const actionGap = await page.locator(".billing-page .button-row").evaluate((element) => getComputedStyle(element).columnGap);
    expect(parseFloat(actionGap)).toBeGreaterThanOrEqual(8);
    await assertNoSeriousOrCriticalAxeViolations(page, ".billing-page");
    for (const locale of ["ar", "ku"]) {
      await page.locator(".language-select select").selectOption(locale);
      await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
      expect(await gridColumnCount(page.locator(".billing-summary-grid"))).toBe(3);
    }
    await page.locator(".language-select select").selectOption("en");
    await expect(page.locator("html")).toHaveAttribute("dir", "ltr");

    for (const viewport of [
      { width: 1920, height: 1080, summaryColumns: 3, planColumns: 2 },
      { width: 1440, height: 900, summaryColumns: 3, planColumns: 2 },
      { width: 768, height: 900, summaryColumns: 2, planColumns: 2 },
      { width: 375, height: 812, summaryColumns: 1, planColumns: 1 },
      { width: 320, height: 812, summaryColumns: 1, planColumns: 1 },
    ]) {
      await page.setViewportSize({ width: viewport.width, height: viewport.height });
      expect(await gridColumnCount(page.locator(".billing-summary-grid"))).toBe(viewport.summaryColumns);
      const renderedPlanColumns = await page.locator(".billing-plan-option").evaluateAll((elements) => new Set(elements.map((element) => Math.round(element.getBoundingClientRect().x))).size);
      expect(renderedPlanColumns).toBe(viewport.planColumns);
      expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(viewport.width);
      if (process.env.ISSUE_700_SAVE_SHOTS === "1" && (viewport.width === 1440 || viewport.width === 375)) {
        await page.screenshot({ path: resolve(screenshotDir, `issue-700-live-billing-after-${viewport.width}.png`), fullPage: viewport.width === 1440 });
      }
    }

    await page.setViewportSize({ width: 375, height: 812 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(375);
    await page.locator(".billing-plans").scrollIntoViewIfNeeded();
    if (process.env.ISSUE_700_SAVE_SHOTS === "1") {
      await page.screenshot({ path: resolve(screenshotDir, "issue-700-live-billing-plans-after-375.png"), fullPage: false });
    }

    expect(traffic.apiCalls.some(({ path }) => path === `/api/workspaces/${liveWorkspace.id}/billing`)).toBe(true);
    expectIsolatedReadOnlyTraffic(traffic, ["/api/auth/me", "/api/notifications/unread-count", `/api/workspaces/${liveWorkspace.id}/billing`]);
    expect(traffic.apiCalls.some(({ path }) => /checkout|payments|providers/i.test(path))).toBe(false);
  });

  test("keeps the billing layout within the viewport at 125% zoom", async ({ page }) => {
    await loadFixture(page, billingFixture());
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.evaluate(() => { document.documentElement.style.zoom = "1.25"; });
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(1280);
    await expect(page.locator(".billing-plan-option")).toHaveCount(2);
  });

  test("checks first-login beta chooser presentation and modal keyboard accessibility for a mocked Free account", async ({ page }, testInfo) => {
    test.setTimeout(60_000);
    const traffic = await interceptLiveAppApi(page, null);
    let releaseAuth!: () => void;
    let markAuthRequested!: () => void;
    const authRequested = new Promise<void>((resolveAuth) => { markAuthRequested = resolveAuth; });
    const authGate = new Promise<void>((release) => { releaseAuth = release; });
    let didSignalAuthRequest = false;
    let onboardingCompletedAt: string | null = null;
    let csrfMockCount = 0;
    let completionMockCount = 0;

    // Hold synthetic first-login responses long enough to focus the real AppShell
    // link which should regain focus if the modal closes.
    await page.route("**/api/auth/me", async (route) => {
      if (!didSignalAuthRequest) {
        didSignalAuthRequest = true;
        markAuthRequested();
      }
      // React may issue concurrent auth refreshes; hold every response so none
      // can mount the modal before the opener focus precondition is established.
      await authGate;
      await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(liveAuthResponse(onboardingCompletedAt)) });
    });
    await page.route("**/api/auth/csrf", async (route) => {
      csrfMockCount += 1;
      await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ token: "issue-700-synthetic-csrf" }) });
    });
    await page.route("**/api/auth/onboarding/complete", async (route) => {
      completionMockCount += 1;
      onboardingCompletedAt = "2026-11-01T00:00:00.000Z";
      await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(liveAuthResponse(onboardingCompletedAt)) });
    });
    await page.route(`**/api/workspaces/${liveWorkspace.id}/projects*`, async (route) => {
      await route.fulfill({ status: 200, contentType: "application/json", body: "[]" });
    });

    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto("/account/billing");
    await authRequested;
    const appShellOpener = page.getByRole("link", { name: "Create", exact: true });
    await expect(appShellOpener).toBeVisible();
    await appShellOpener.focus();
    await expect(appShellOpener).toBeFocused();
    releaseAuth();

    const dialog = page.getByRole("dialog");
    await expect(dialog).toBeVisible();
    const dialogContainsFocus = () => dialog.evaluate((element) => element.contains(document.activeElement));
    expect.soft(await dialogContainsFocus(), ".onboarding-card must receive/contain focus on first-login open").toBe(true);

    const progress = dialog.locator(".onboarding-progress");
    const assertAccessibleProgress = async (step: number) => {
      const state = await progress.evaluate((element) => ({
        role: element.getAttribute("role"),
        min: element.getAttribute("aria-valuemin"),
        max: element.getAttribute("aria-valuemax"),
        now: element.getAttribute("aria-valuenow"),
        label: element.getAttribute("aria-label"),
      }));
      expect.soft(state, `.onboarding-progress must expose step ${step} with progressbar semantics`).toEqual({
        role: "progressbar",
        min: "1",
        max: "3",
        now: String(step),
        label: expect.any(String),
      });
    };

    await assertAccessibleProgress(1);
    const skip = dialog.locator(".onboarding-skip");
    const next = dialog.locator(".onboarding-actions .primary-button");
    await skip.focus();
    await page.keyboard.press("Shift+Tab");
    expect.soft(await next.evaluate((element) => document.activeElement === element), "Shift+Tab from the first dialog control must wrap to its last control").toBe(true);
    await page.keyboard.press("Tab");
    expect.soft(await skip.evaluate((element) => document.activeElement === element), "Tab from the last dialog control must wrap to its first control").toBe(true);
    await next.click();
    await assertAccessibleProgress(2);
    expect.soft(await dialogContainsFocus(), ".onboarding-card must contain focus after onboarding step 1 to 2").toBe(true);
    await next.click();
    await assertAccessibleProgress(3);
    expect.soft(await dialogContainsFocus(), ".onboarding-card must contain focus after onboarding step 2 to 3").toBe(true);

    const workflowNames = (await dialog.locator(".onboarding-workflow strong").allTextContents()).map((name) => name.trim()).sort();
    expect.soft(workflowNames, ".onboarding-workflow strong must expose only Chat, Image, Document, Presentation, and Movie").toEqual([
      "Create a document",
      "Create a presentation",
      "Create an image",
      "Movie Studio",
      "Talk to Taslim",
    ].sort());
    const selectableExcluded = await dialog.locator(".onboarding-workflow").filter({ hasText: /Research|Social|Music|Voice/i }).allTextContents();
    expect.soft(selectableExcluded, ".onboarding-workflow must not expose Research, Social, Music, or Voice choices").toEqual([]);
    const onboardingForbiddenLinks = await dialog.locator("a[href^='/create/']").evaluateAll((elements) => elements.map((element) => (element as HTMLAnchorElement).getAttribute("href")));
    expect.soft(onboardingForbiddenLinks, ".onboarding-card a[href^='/create/'] must not advertise excluded module routes").toEqual([]);

    const onboardingScreenshotPath = testInfo.outputPath("issue-700-free-onboarding-beta-access-current.png");
    await page.screenshot({ path: onboardingScreenshotPath, fullPage: true });
    await testInfo.attach("issue-700-free-onboarding-beta-access-current", { path: onboardingScreenshotPath, contentType: "image/png" });

    await skip.focus();
    await page.keyboard.press("Escape");
    expect.soft(await dialog.isVisible(), "Escape must not silently dismiss or submit blocking onboarding").toBe(true);
    expect.soft(await dialogContainsFocus(), "Escape must leave keyboard focus contained by the open dialog").toBe(true);
    // Skip is the explicit, mocked close action used to verify opener focus restoration.
    await skip.click();
    await expect(dialog).toBeHidden();
    expect.soft(completionMockCount, "POST /api/auth/onboarding/complete must be intercepted as a synthetic close action").toBe(1);
    expect.soft(csrfMockCount, "GET /api/auth/csrf must be intercepted with a synthetic token").toBeGreaterThanOrEqual(1);
    await expect.soft(appShellOpener, "a[href='/create'] must regain focus after Skip closes onboarding").toBeFocused();

    await appShellOpener.click();
    await expect(page).toHaveURL(/\/create\/?$/);
    for (const width of [375, 768, 1440]) {
      await page.setViewportSize({ width, height: 900 });
      const visibleStudioHrefs = await page.locator("a.studio-card[href^='/create/']").evaluateAll((elements) => elements
        .filter((element) => {
          const link = element as HTMLAnchorElement;
          const style = window.getComputedStyle(link);
          return style.display !== "none" && style.visibility !== "hidden" && link.getClientRects().length > 0;
        })
        .map((element) => (element as HTMLAnchorElement).pathname)
        .sort());
      expect.soft(visibleStudioHrefs, `visible a.studio-card[href^='/create/'] links at ${width}px must be only Image, Document, Presentation, and short-form Movie`).toEqual([
        "/create/document",
        "/create/image",
        "/create/movie",
        "/create/presentation",
      ].sort());
      for (const excludedPath of ["/create/research", "/create/social", "/create/music", "/create/voice", "/create/health", "/personal/health", "/health"]) {
        expect.soft(await page.locator(`a.studio-card[href='${excludedPath}']`).isVisible(), `a.studio-card[href='${excludedPath}'] must not be visible in the beta chooser at ${width}px`).toBe(false);
      }
      expect.soft(await page.evaluate(() => document.documentElement.scrollWidth), `Create chooser must not overflow horizontally at ${width}px`).toBeLessThanOrEqual(width);
    }
    await expect(page.locator(".studio-category:has(a.studio-card[href='/create/social'])")).toBeHidden();
    await expect(page.locator(".studio-chooser-count")).toBeHidden();
    expect.soft(await page.locator("a.studio-chat-link[href='/chat']").count(), "a.studio-chat-link[href='/chat'] must advertise allowed Chat").toBe(1);
    expect.soft(traffic.externalRequests, "network requests outside the local app/test API must remain blocked").toEqual([]);
    expect.soft(traffic.unexpectedApiCalls, "only explicitly mocked synthetic API routes may be contacted").toEqual([]);
  });
});
