import AxeBuilder from "@axe-core/playwright";
import { test, expect } from "./fixtures";

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

  test("project dialogs trap focus, dismiss with Escape, and restore the opener", async ({ authenticatedPage: page }) => {
    await page.goto("/projects");
    const opener = page.getByRole("button", { name: /new project/i }).first();
    await opener.click();

    const dialog = page.getByRole("dialog", { name: /create.*project/i });
    await expect(dialog).toBeVisible();
    await expect(dialog.getByRole("textbox", { name: /name/i })).toBeFocused();

    const submit = dialog.getByRole("button", { name: /create/i });
    const close = dialog.getByRole("button", { name: /close/i });
    await submit.focus();
    await page.keyboard.press("Tab");
    await expect(close).toBeFocused();

    await page.keyboard.press("Escape");
    await expect(dialog).toBeHidden();
    await expect(opener).toBeFocused();
  });
});
