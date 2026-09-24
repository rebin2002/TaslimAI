import { test, expect } from "./fixtures";

test.describe("RTL smoke", () => {
  test("keeps Arabic navigation and primary controls usable", async ({ authenticatedPage: page }) => {
    await page.goto("/projects");
    await expect(page.getByRole("heading", { name: /projects/i })).toBeVisible();
    const language = page.locator(".language-select select").first();
    await language.selectOption("ar");
    await expect.poll(() => page.evaluate(() => window.localStorage.getItem("taslim-locale"))).toBe("ar");
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
    await expect(page.locator("html")).toHaveAttribute("lang", "ar");
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
    await expect(page.locator("main")).toBeVisible();
    await expect(page.locator("nav.mobile-bottom-nav")).toContainText(/مشاريع|پڕۆژە/);
    await expect(page.getByRole("button").first()).toBeVisible();

    await page.keyboard.press("Tab");
    await expect(page.locator(":focus")).toBeVisible();
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 2);
    expect(overflow).toBe(true);
  });
});
