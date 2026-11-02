import { test, expect } from "./fixtures";

test.describe("Health Care route", () => {
  test("redirects an anonymous visitor before rendering Health Care content", async ({ page }) => {
    await page.goto("/create/health");

    await expect(page).toHaveURL(/\/login\?next=%2Fcreate%2Fhealth$/);
    await expect(page.getByRole("heading", { name: "Health Care", level: 1 })).toHaveCount(0);
  });

  test("is keyboard reachable, privacy-first, responsive, and correctly rendered in RTL", async ({ authenticatedPage }) => {
    const page = authenticatedPage;
    await page.goto("/create/health");

    const heading = page.getByRole("heading", { name: "Health Care", level: 1 });
    const panel = page.locator("[aria-labelledby='health-care-title']");
    await expect(heading).toBeVisible();
    await expect(page.getByRole("status")).toContainText("Coming soon");
    await expect(page.locator("button[disabled]")).toHaveCount(4);
    await expect(page.locator("button[disabled]").first()).toBeDisabled();
    await expect(page.locator("form, input, textarea, [type='file']")).toHaveCount(0);

    for (const width of [320, 390, 768, 1440]) {
      await page.setViewportSize({ width, height: 900 });
      const bounds = await panel.boundingBox();
      expect(bounds, `Health Care panel is measurable at ${width}px`).not.toBeNull();
      if (!bounds) throw new Error(`Health Care panel is missing at ${width}px`);
      expect(bounds.x).toBeGreaterThanOrEqual(0);
      expect(bounds.x + bounds.width).toBeLessThanOrEqual(width + 1);
    }

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

    await page.goto("/create/health");
    await page.evaluate(() => localStorage.setItem("taslim-locale", "ar"));
    await page.reload();
    await expect(page.locator("html")).toHaveAttribute("lang", "ar");
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
    await expect(page.getByRole("heading", { name: "الرعاية الصحية", level: 1 })).toBeVisible();

    await page.evaluate(() => localStorage.setItem("taslim-locale", "ku"));
    await page.reload();
    await expect(page.locator("html")).toHaveAttribute("lang", "ckb");
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
    await expect(page.getByRole("heading", { name: "چاودێریی تەندروستی", level: 1 })).toBeVisible();
  });
});
