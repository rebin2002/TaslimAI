import { test, expect, loginInUi, logoutInUi, registerInUi } from "./fixtures";

test.describe("authentication and onboarding", () => {
  test("registers a user and completes the guided onboarding flow", async ({ page, testUser }) => {
    await registerInUi(page, testUser, false);
    const onboarding = page.getByRole("dialog");
    await expect(onboarding).toBeVisible();
    await expect(onboarding.locator("#onboarding-title")).toBeVisible();
    await onboarding.getByRole("button", { name: /continue/i }).click();
    await expect(onboarding.locator("#onboarding-title")).toBeVisible();
    await onboarding.getByRole("button", { name: /choose .*action/i }).click();
    await onboarding.getByRole("button").filter({ hasText: /project/i }).click();
    await expect(onboarding).toBeHidden();
    await expect(page).toHaveURL(/\/projects/);
    await expect(page.getByRole("heading", { name: /projects/i })).toBeVisible();
  });

  test("logs in, bypasses completed onboarding, and logs out", async ({ page, testUser }) => {
    await registerInUi(page, testUser);
    await logoutInUi(page);

    await loginInUi(page, testUser);
    await expect(page.getByRole("dialog")).toHaveCount(0);
    await page.goto("/");
    await expect(page).toHaveURL(/\/$/);
    await expect(page.getByText(new RegExp(`good evening,?\\s*${testUser.displayName}`, "i"))).toBeVisible();

    await logoutInUi(page);
    await page.goto("/projects");
    await expect(page).toHaveURL(/\/login\?next=%2Fprojects/);
  });

  test("protects password changes and requires the replacement credential on reauthentication", async ({ authenticatedPage: page, testUser }) => {
    const replacementPassword = "E2eChangedPassword!123";
    await page.goto("/account");
    await expect(page.getByRole("heading", { name: /^account$/i })).toBeVisible();

    const currentPassword = page.getByLabel(/current password/i);
    const newPassword = page.getByLabel(/new password/i);
    const confirmPassword = page.getByLabel(/confirm password/i);
    const updatePassword = page.getByRole("button", { name: /update password/i });

    await currentPassword.fill("WrongCurrentPassword!123");
    await newPassword.fill(replacementPassword);
    await confirmPassword.fill(replacementPassword);
    await updatePassword.click();
    await expect(page.getByRole("alert").filter({ hasText: /current password is not correct/i })).toBeVisible();
    await expect(page.getByText(/your password was updated/i)).toHaveCount(0);

    await currentPassword.fill(testUser.password);
    await newPassword.fill("too-weak");
    await confirmPassword.fill("too-weak");
    await updatePassword.click();
    await expect(page.getByRole("alert").filter({ hasText: /password that meets the requirements/i })).toBeVisible();
    await expect(page.getByText(/your password was updated/i)).toHaveCount(0);

    await newPassword.fill(replacementPassword);
    await confirmPassword.fill(replacementPassword);
    await updatePassword.click();
    await expect(page.getByText(/your password was updated/i)).toBeVisible();
    await expect(currentPassword).toHaveValue("");
    await expect(newPassword).toHaveValue("");
    await expect(confirmPassword).toHaveValue("");
    await page.reload();
    await expect(page.getByRole("heading", { name: /^account$/i })).toBeVisible();

    await logoutInUi(page);
    await loginInUi(page, { ...testUser, password: replacementPassword });
    await logoutInUi(page);

    await page.goto("/login");
    await page.getByLabel(/email/i).fill(testUser.email);
    await page.getByRole("textbox", { name: /^password/i }).fill(testUser.password);
    await page.getByRole("button", { name: /sign in/i }).click();
    await expect(page.getByRole("alert").filter({ hasText: /invalid email or password/i })).toBeVisible();
    await expect(page).toHaveURL(/\/login$/);
  });

  test("authenticated home exposes the primary workspace entry points", async ({ authenticatedPage: page }) => {
    await page.goto("/");
    await expect(page.getByRole("heading", { name: /what would you like to make today/i })).toBeVisible();
    await expect(page.getByRole("link", { name: /projects/i }).first()).toBeVisible();
    await expect(page.getByRole("link", { name: /assets/i }).first()).toBeVisible();
    await expect(page.getByRole("link", { name: /chat/i }).first()).toBeVisible();
  });

  test("applies a saved account interface language immediately and after reload", async ({ authenticatedPage: page }) => {
    await page.goto("/account");
    const interfaceLanguage = page.locator(".account-profile-card select").first();
    await expect(interfaceLanguage).toHaveValue("en");

    await interfaceLanguage.selectOption("ar");
    await page.getByRole("button", { name: /save changes/i }).click();

    await expect(page.locator("html")).toHaveAttribute("lang", "ar");
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
    await page.reload();
    await expect(page.locator(".account-profile-card select").first()).toHaveValue("ar");
    await expect(page.locator("html")).toHaveAttribute("lang", "ar");
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  });
});
