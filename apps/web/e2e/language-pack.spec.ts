import { test, expect, registerInUi } from "./fixtures";

/**
 * The account form holds a draft: the interface language becomes visible when the
 * draft is saved, which keeps the "Save changes" control readable in the language
 * the user is currently reading and preserves unsaved edits. The header selector is
 * the immediate switch, so both paths are covered here.
 */
test.describe("language-pack foundation", () => {
  test("persists zh-CN chosen during real first-login onboarding without changing generation language", async ({ page, testUser }) => {
    await registerInUi(page, testUser, false);
    const onboarding = page.getByRole("dialog");
    await expect(onboarding).toBeVisible();

    const selectors = onboarding.locator(".onboarding-fields select");
    await selectors.first().selectOption("zh-CN");
    await expect(page.locator("html")).toHaveAttribute("lang", "zh-CN");
    await expect(page.locator("html")).toHaveAttribute("dir", "ltr");
    await expect(selectors.nth(1)).toHaveValue("en");
    await expect(onboarding.getByText(/Simplified Chinese is in preview/)).toBeVisible();

    // The skip path must persist the account locale without launching paid generation.
    await onboarding.locator(".onboarding-skip").click();
    await expect(onboarding).toBeHidden();
    await expect(page).toHaveURL((url) => url.pathname === "/projects");
    const response = await page.request.get("/api/auth/me");
    expect(response.ok()).toBe(true);
    const session = await response.json() as { user: { preferredLanguage: string; defaultGenerationLanguage: string; onboardingCompletedAt: string | null } };
    expect(session.user.preferredLanguage).toBe("zh-CN");
    expect(session.user.defaultGenerationLanguage).toBe("en");
    expect(session.user.onboardingCompletedAt).toBeTruthy();

    await page.reload();
    await expect(page.locator("html")).toHaveAttribute("lang", "zh-CN");
    await page.goto("/account");
    await expect(page.locator(".account-control-grid select").first()).toHaveValue("zh-CN");
  });

  test("switches between zh-CN and Arabic, persists the choice, and keeps direction metadata correct", async ({ authenticatedPage: page }) => {
    await page.goto("/account");
    await expect(page.getByRole("heading", { name: /^account$/i })).toBeVisible();

    const language = page.locator(".account-control-grid select").first();
    await language.selectOption("zh-CN");
    await page.getByRole("button", { name: /save changes/i }).click();
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

  test("keeps an unsaved account draft when a language is selected but not saved", async ({ authenticatedPage: page }) => {
    await page.goto("/account");
    const displayName = page.getByLabel(/display name/i);
    await displayName.fill("Draft Owner");

    await page.locator(".account-control-grid select").first().selectOption("zh-CN");
    // Selecting a language in the draft must not re-mount the form and discard the
    // name typed above, and must not switch the interface before it is saved.
    await expect(displayName).toHaveValue("Draft Owner");
    await expect(page.locator("html")).toHaveAttribute("lang", "en");

    await page.locator(".account-control-grid select").first().selectOption("en");
    await page.getByRole("button", { name: /save changes/i }).click();
    await expect(page.locator("html")).toHaveAttribute("lang", "en");
    const current = await page.request.get("/api/auth/me");
    const body = (await current.json()) as { user: { preferredLanguage: string } };
    expect(body.user.preferredLanguage).toBe("en");
  });
});
