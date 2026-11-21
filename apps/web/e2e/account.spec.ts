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

  test("accepts canonical zh-CN account preference while keeping generation language separate", async ({ authenticatedPage: page }) => {
    await page.goto("/account");
    await expect(page.getByRole("heading", { name: /^account$/i })).toBeVisible();

    await page.locator(".account-control-grid select").first().selectOption("zh-CN");
    await page.locator(".account-control-grid select").nth(1).selectOption("ar");
    await page.locator(".account-profile-card button.primary-button").click();

    const saved = await apiJson<AuthResponse>(page.request, "GET", "/api/auth/me");
    expect(saved.user.preferredLanguage).toBe("zh-CN");
    expect(saved.user.defaultGenerationLanguage).toBe("ar");

    await page.reload();
    await expect(page.locator("html")).toHaveAttribute("lang", "zh-CN");
    await expect(page.locator("html")).toHaveAttribute("dir", "ltr");
    await expect(page.locator(".account-control-grid select").first()).toHaveValue("zh-CN");
    await expect(page.locator(".account-control-grid select").nth(1)).toHaveValue("ar");
  });

  test("keeps a newer unsaved profile edit after a delayed zh-CN save echo, then saves it", async ({ authenticatedPage: page }) => {
    await page.goto("/account");
    await expect(page.locator(".account-profile-card")).toBeVisible();

    const displayName = page.locator(".account-profile-card input[required]").first();
    const interfaceLanguage = page.locator(".account-control-grid select").first();
    const generationLanguage = page.locator(".account-control-grid select").nth(1);
    const saveButton = page.locator(".account-profile-card button.primary-button");

    await displayName.fill("E2E Submitted Profile");
    await interfaceLanguage.selectOption("zh-CN");
    await generationLanguage.selectOption("ar");

    // Let the real API persist the first draft, but delay its response until
    // the user has typed a newer display name. This reproduces the exact
    // in-flight save echo that previously remounted AccountProfileForm and
    // discarded local edits. The provider and billing paths are never used.
    let releaseFirstResponse: () => void = () => {};
    let firstResponseSeen: () => void = () => {};
    const firstResponse = new Promise<void>((resolve) => { firstResponseSeen = resolve; });
    const responseGate = new Promise<void>((resolve) => { releaseFirstResponse = resolve; });
    let intercepted = false;
    await page.route("**/api/auth/profile", async (route) => {
      if (route.request().method() !== "PATCH" || intercepted) {
        await route.continue();
        return;
      }
      intercepted = true;
      const response = await route.fetch();
      firstResponseSeen();
      await responseGate;
      await route.fulfill({ response });
    });

    try {
      await saveButton.click();
      await firstResponse;
      await expect(saveButton).toBeDisabled();

      await displayName.fill("E2E Newer Unsaved Profile");
      await expect(displayName).toHaveValue("E2E Newer Unsaved Profile");
      releaseFirstResponse();

      await expect(saveButton).toBeEnabled();
      await expect(displayName).toHaveValue("E2E Newer Unsaved Profile");
      await expect(page.locator(".account-profile-card .form-success")).toHaveCount(0);
      await expect(interfaceLanguage).toHaveValue("zh-CN");
      await expect(generationLanguage).toHaveValue("ar");

      // The second submit, not the first echo, must persist the new edit.
      await saveButton.click();
      await expect(page.locator(".account-identity-mark")).toContainText("E2E Newer Unsaved Profile");
      const saved = await apiJson<AuthResponse>(page.request, "GET", "/api/auth/me");
      expect(saved.user).toMatchObject({
        displayName: "E2E Newer Unsaved Profile",
        preferredLanguage: "zh-CN",
        defaultGenerationLanguage: "ar",
      });

      await page.reload();
      await expect(page.locator("html")).toHaveAttribute("lang", "zh-CN");
      await expect(page.locator(".account-profile-card input[required]").first()).toHaveValue("E2E Newer Unsaved Profile");
      await expect(page.locator(".account-control-grid select").first()).toHaveValue("zh-CN");
      await expect(page.locator(".account-control-grid select").nth(1)).toHaveValue("ar");
    } finally {
      releaseFirstResponse();
      await page.unroute("**/api/auth/profile");
    }
  });
});
