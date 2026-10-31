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

const workflows: Array<[string, string]> = [
  ["Project workspace", "Organize your work, collaborators, and creative deliverables in one connected project."],
  ["AI chat", "Ask questions, draft content, and work through ideas in a focused conversation."],
  ["Image studio", "Create and refine visual concepts with a clear, guided workflow."],
  ["Document studio", "Turn an outline or brief into a structured document you can review."],
  ["Presentation studio", "Build a polished presentation from your story, audience, and goals."],
  ["Research studio", "Explore a topic and organize useful sources into a readable report."],
];

function onboardingFixture(language = "en", direction = "ltr") {
  const workflowMarkup = workflows.map(([title, description]) => `
    <button type="button" class="onboarding-workflow">
      <span class="onboarding-workflow-icon" aria-hidden="true">◇</span>
      <span><strong>${title}</strong><small>${description}</small></span>
      <span class="onboarding-workflow-arrow" aria-hidden="true">→</span>
    </button>
  `).join("");

  return `<!doctype html>
    <html lang="${language}" dir="${direction}">
      <head><meta name="viewport" content="width=device-width, initial-scale=1" />${fixtureBaseStyles}</head>
      <body>
        <div class="modal-backdrop onboarding-backdrop" role="presentation">
          <section class="onboarding-card" role="dialog" aria-modal="true" aria-labelledby="onboarding-title" tabindex="-1">
            <header class="onboarding-header">
              <div class="onboarding-brand"><span class="onboarding-brand-mark" aria-hidden="true">✦</span><span>Taslim.ai</span></div>
              <button type="button" class="onboarding-skip">Skip for now</button>
            </header>
            <div class="onboarding-progress" aria-label="Step 3 of 3"><span class="is-complete"></span><span class="is-complete"></span><span class="is-complete"></span></div>
            <div class="onboarding-body onboarding-workflow-body">
              <p class="section-eyebrow">A workspace built around you</p>
              <h1 id="onboarding-title">Choose a workflow to get started</h1>
              <p class="onboarding-description">Pick a starting point for your next project. Your choice opens the existing Taslim workflow and does not change billing or account settings.</p>
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
    ["Creator", "", "$9 / month", "10,000 included credits", false],
    ["Studio", "", "$29 / month", "40,000 included credits", false],
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
      <head><meta name="viewport" content="width=device-width, initial-scale=1" />${fixtureBaseStyles}</head>
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
              <div class="account-card billing-period-card"><div class="card-title"><span class="card-title-icon">◷</span><div><h2>Billing period</h2><p>Current period</p></div></div><strong>Oct 1 – Oct 31, 2026</strong><span>Next renewal: Nov 1, 2026</span></div>
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
              <dl class="billing-definition-list"><div><dt>Included allowance</dt><dd>1,000</dd></div><div><dt>Purchased credits</dt><dd>0</dd></div><div><dt>Corrections</dt><dd>0</dd></div></dl>
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
  test("centers the onboarding dialog and keeps six workflow tiles distinct at desktop, tablet, and mobile widths", async ({ page }) => {
    await loadFixture(page, onboardingFixture());

    for (const viewport of [
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
      await expect(page.locator(".onboarding-workflow")).toHaveCount(6);

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
  });

  test("separates billing labels, values, and disabled actions with responsive plan and summary grids", async ({ page }) => {
    await loadFixture(page, billingFixture());

    for (const viewport of [
      { width: 1440, height: 900, summaryColumns: 3, planColumns: 3 },
      { width: 768, height: 900, summaryColumns: 2, planColumns: 2 },
      { width: 375, height: 812, summaryColumns: 1, planColumns: 1 },
    ]) {
      await page.setViewportSize({ width: viewport.width, height: viewport.height });
      expect(await gridColumnCount(page.locator(".billing-summary-grid"))).toBe(viewport.summaryColumns);
      expect(await gridColumnCount(page.locator(".billing-plan-options"))).toBe(viewport.planColumns);
      await expect(page.locator(".billing-summary-grid > .account-card")).toHaveCount(3);
      await expect(page.locator(".billing-plan-option")).toHaveCount(3);

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
      await expect(page.locator(".billing-plan-option button:disabled")).toHaveCount(3);
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
    await page.setViewportSize({ width: 375, height: 812 });
    await expect(historyScroller).toBeVisible();
    expect(await historyScroller.evaluate((element) => element.scrollWidth > element.clientWidth)).toBe(true);
  });

  test("preserves readable right-to-left layout and keyboard-visible focus for Arabic and Sorani", async ({ page }) => {
    for (const language of ["ar", "ku"]) {
      await loadFixture(page, onboardingFixture(language, "rtl"));
      await page.setViewportSize({ width: 375, height: 812 });
      await expect(page.getByRole("dialog")).toBeVisible();
      expect(await page.evaluate(() => document.documentElement.dir)).toBe("rtl");
      expect(await gridColumnCount(page.locator(".onboarding-workflow-grid"))).toBe(1);
      expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(375);
      expect(await page.locator(".onboarding-workflow-arrow").first().evaluate((element) => getComputedStyle(element).transform)).not.toBe("none");

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

  test("keeps onboarding language selectors readable and keyboard reachable in English, Arabic, and Sorani", async ({ page }) => {
    const locales = [
      { code: "en", direction: "ltr", display: "Display name", interface: "Interface language", generation: "Generation language", continue: "Continue" },
      { code: "ar", direction: "rtl", display: "الاسم", interface: "لغة الواجهة", generation: "لغة التوليد", continue: "متابعة" },
      { code: "ku", direction: "rtl", display: "ناو", interface: "زمانی ڕووکار", generation: "زمانی دروستکردن", continue: "بەردەوام بە" },
    ];

    for (const locale of locales) {
      const html = `<!doctype html>
        <html lang="${locale.code}" dir="${locale.direction}">
          <head><meta name="viewport" content="width=device-width, initial-scale=1" />${fixtureBaseStyles}</head>
          <body>
            <div class="modal-backdrop onboarding-backdrop" role="presentation">
              <section class="onboarding-card" role="dialog" aria-modal="true" aria-labelledby="onboarding-title" tabindex="-1">
                <header class="onboarding-header"><div class="onboarding-brand"><span class="onboarding-brand-mark" aria-hidden="true">✦</span><span>Taslim.ai</span></div><button type="button" class="onboarding-skip">Skip for now</button></header>
                <div class="onboarding-progress" aria-label="Step 1 of 3"><span class="is-complete"></span><span></span><span></span></div>
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

  test("keeps the billing layout within the viewport at 125% zoom", async ({ page }) => {
    await loadFixture(page, billingFixture());
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.evaluate(() => { document.documentElement.style.zoom = "1.25"; });
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(1280);
    await expect(page.locator(".billing-plan-option")).toHaveCount(3);
  });
});
