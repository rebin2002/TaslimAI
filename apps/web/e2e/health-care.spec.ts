import { test, expect } from "./fixtures";

/** Attaches a full-page PNG so hosted CI keeps visual acceptance evidence even when
 *  the assertion itself passes. `apps/web/test-results` is uploaded as an artifact. */
async function capture(
  page: import("@playwright/test").Page,
  testInfo: import("@playwright/test").TestInfo,
  name: string,
) {
  const path = testInfo.outputPath(`${name}.png`);
  await page.screenshot({ path, fullPage: true });
  await testInfo.attach(`${name}.png`, { path, contentType: "image/png" });
}

test.describe("Health Care route", () => {
  test("redirects an anonymous visitor before rendering Health Care content", async ({ page }, testInfo) => {
    await page.goto("/create/health");

    await expect(page).toHaveURL(/\/login\?next=%2Fcreate%2Fhealth$/);
    await expect(page.getByRole("heading", { name: "Health Care", level: 1 })).toHaveCount(0);
    await expect(page.locator("body")).not.toContainText(/lab report|medication list|diagnosis/i);
    await capture(page, testInfo, "01-anonymous-redirects-to-login");
  });

  test("is keyboard reachable, privacy-first, responsive, and correctly rendered in RTL", async ({ authenticatedPage }, testInfo) => {
    const page = authenticatedPage;
    await page.goto("/create/health");

    const heading = page.getByRole("heading", { name: "Health Care", level: 1 });
    const panel = page.locator("[aria-labelledby='health-care-title']");
    await expect(heading).toBeVisible();
    await expect(page.getByRole("status")).toContainText("Coming soon");
    await expect(page.locator("button[disabled]")).toHaveCount(4);
    await expect(page.locator("button[disabled]").first()).toBeDisabled();
    await expect(page.locator("form, input, textarea, [type='file']")).toHaveCount(0);
    await expect(page.locator('meta[name="robots"]')).toHaveAttribute("content", /noindex/);
    await expect(page.locator('meta[name="robots"]')).toHaveAttribute("content", /nofollow/);
    await capture(page, testInfo, "02-authenticated-create-health-en-desktop");

    for (const width of [320, 375, 390, 768, 1440]) {
      await page.setViewportSize({ width, height: 900 });
      const bounds = await panel.boundingBox();
      expect(bounds, `Health Care panel is measurable at ${width}px`).not.toBeNull();
      if (!bounds) throw new Error(`Health Care panel is missing at ${width}px`);
      expect(bounds.x).toBeGreaterThanOrEqual(0);
      expect(bounds.x + bounds.width).toBeLessThanOrEqual(width + 1);
      await capture(page, testInfo, `03-responsive-${width}px`);
    }

    await page.setViewportSize({ width: 1440, height: 900 });
    await page.evaluate(() => {
      if (document.activeElement instanceof HTMLElement) document.activeElement.blur();
    });
    let reachedBackLink = false;
    let disabledActionReceivedFocus = false;
    for (let attempt = 0; attempt < 60; attempt += 1) {
      await page.keyboard.press("Tab");
      const active = await page.evaluate(() => {
        const element = document.activeElement;
        return {
          href: element instanceof HTMLAnchorElement ? element.getAttribute("href") : null,
          label: element instanceof HTMLElement ? element.innerText.trim() : "",
          disabledButton: element instanceof HTMLButtonElement && element.disabled,
        };
      });
      if (active.disabledButton) disabledActionReceivedFocus = true;
      if (active.href === "/create" && active.label.includes("Back to Create")) {
        reachedBackLink = true;
        break;
      }
    }
    expect(disabledActionReceivedFocus).toBe(false);
    expect(reachedBackLink).toBe(true);
    await page.keyboard.press("Enter");
    await expect(page).toHaveURL(/\/create$/);

    // A completed onboarding saves an account-scoped English choice. A device-level
    // taslim-locale override cannot supersede it (see account locale precedence #992).
    // Exercise the real authenticated language selector instead of writing an
    // anonymous storage key that the application must now ignore.
    await page.goto("/account");
    await expect(page.locator(".account-control-grid select").first()).toHaveValue("en");
    await page.locator(".account-control-grid select").first().selectOption("ar");
    await page.locator("form.profile-form button.primary-button").click();
    await expect(page.locator("html")).toHaveAttribute("lang", "ar");
    await page.goto("/create/health");
    await page.reload();
    await expect(page.locator("html")).toHaveAttribute("lang", "ar");
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
    await expect(page.getByRole("heading", { name: "الرعاية الصحية", level: 1 })).toBeVisible();
    await capture(page, testInfo, "04-ar-rtl-desktop");

    await page.goto("/account");
    await page.locator(".account-control-grid select").first().selectOption("ku");
    await page.locator("form.profile-form button.primary-button").click();
    await expect(page.locator("html")).toHaveAttribute("lang", "ckb");
    await page.goto("/create/health");
    await page.reload();
    await expect(page.locator("html")).toHaveAttribute("lang", "ckb");
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
    await expect(page.getByRole("heading", { name: "چاودێریی تەندروستی", level: 1 })).toBeVisible();
    await capture(page, testInfo, "05-ckb-rtl-desktop");

    await page.setViewportSize({ width: 375, height: 900 });
    await capture(page, testInfo, "06-ckb-rtl-375px");
  });

  test("keeps the Personal health surface on its original Home navigation", async ({ authenticatedPage }, testInfo) => {
    const page = authenticatedPage;
    await page.goto("/personal/health");

    await expect(page.getByRole("heading", { name: "Health Care", level: 1 })).toBeVisible();
    await expect(page.getByRole("link", { name: /Back to home/i })).toBeVisible();
    await expect(page.getByRole("link", { name: /Explore more/i })).toBeVisible();
    await expect(page.getByRole("link", { name: /Back to Create/i })).toHaveCount(0);
    await expect(page.locator("form, input, textarea, [type='file']")).toHaveCount(0);
    await capture(page, testInfo, "07-personal-health-keeps-home-navigation");
  });
});
