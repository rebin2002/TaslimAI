import { test, expect, apiJson } from "./fixtures";

type AuthResponse = {
  user: {
    displayName: string;
    preferredLanguage: string;
    defaultGenerationLanguage: string;
    timeZone: string;
    outputPreference: string;
    includeSourceLinks: boolean;
  };
};

test.describe("account profile persistence", () => {
  test("persists identity and preferences across reload without starting generation", async ({ authenticatedPage: page }) => {
    await page.goto("/account");
    await expect(page.getByRole("heading", { name: /^account$/i })).toBeVisible();

    await page.getByLabel(/display name/i).fill("E2E Profile Owner");
    await page.getByLabel(/default generation language/i).selectOption("ar");
    await page.getByLabel(/timezone/i).selectOption("Asia/Baghdad");
    await page.getByLabel(/output style/i).selectOption("detailed");

    const includeSourceLinks = page.getByLabel(/include source links/i);
    if (await includeSourceLinks.isChecked()) await includeSourceLinks.uncheck();

    await page.getByRole("button", { name: /save changes/i }).click();
    await expect(page.locator(".account-identity-mark")).toContainText("E2E Profile Owner");

    const saved = await apiJson<AuthResponse>(page.request, "GET", "/api/auth/me");
    expect(saved.user).toMatchObject({
      displayName: "E2E Profile Owner",
      preferredLanguage: "en",
      defaultGenerationLanguage: "ar",
      timeZone: "Asia/Baghdad",
      outputPreference: "detailed",
      includeSourceLinks: false,
    });

    await page.reload();
    await expect(page.getByLabel(/display name/i)).toHaveValue("E2E Profile Owner");
    await expect(page.getByLabel(/interface language/i)).toHaveValue("en");
    await expect(page.getByLabel(/default generation language/i)).toHaveValue("ar");
    await expect(page.getByLabel(/timezone/i)).toHaveValue("Asia/Baghdad");
    await expect(page.getByLabel(/output style/i)).toHaveValue("detailed");
    await expect(page.getByLabel(/include source links/i)).not.toBeChecked();
  });
});
