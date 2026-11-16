import { test, expect } from "./fixtures";

test.describe("language-pack foundation", () => {
  test("switches between zh-CN and Arabic, persists the choice, and keeps direction metadata correct", async ({ authenticatedPage: page }) => {
    await page.goto("/account");
    await expect(page.getByRole("heading", { name: /^account$/i })).toBeVisible();

    const language = page.locator(".account-control-grid select").first();
    await language.selectOption("zh-CN");
    await expect(page.locator("html")).toHaveAttribute("lang", "zh-CN");
    await expect(page.locator("html")).toHaveAttribute("dir", "ltr");
    await expect(language).toHaveValue("zh-CN");
    await expect(page.getByText("界面语言", { exact: true })).toBeVisible();
    await expect.poll(() => page.evaluate(() => {
      const key = Object.keys(window.localStorage).find((candidate) => candidate.startsWith("taslim-language-pack"));
      return key ? window.localStorage.getItem(key) : null;
    })).toBe("zh-CN");

    await page.reload();
    await expect(page.locator("html")).toHaveAttribute("lang", "zh-CN");
    await expect(page.locator("html")).toHaveAttribute("dir", "ltr");
    await expect(page.locator(".account-control-grid select").first()).toHaveValue("zh-CN");

    await page.locator(".language-select select").first().selectOption("ar");
    await expect(page.locator("html")).toHaveAttribute("lang", "ar");
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");

    await page.locator(".language-select select").first().selectOption("zh-CN");
    await expect(page.locator("html")).toHaveAttribute("lang", "zh-CN");
    await expect(page.locator("html")).toHaveAttribute("dir", "ltr");
  });
});
