import { expect, type Page } from "@playwright/test";

/** Save the synthetic account's language before navigating to a fresh protected route. */
export async function saveHealthTestLocale(page: Page, code: string, tag: string) {
  await page.goto("/account");
  const form = page.locator("form.account-profile-card");
  const language = form.locator("select").first();
  await expect(language).toBeVisible();
  if (await language.inputValue() !== code) {
    await language.selectOption(code);
    const saved = page.waitForResponse((response) =>
      response.request().method() === "PATCH" && new URL(response.url()).pathname === "/api/auth/profile",
    );
    await form.locator("button.primary-button").click();
    expect((await saved).ok()).toBe(true);
  }
  await expect(page.locator("html")).toHaveAttribute("lang", tag);
}
