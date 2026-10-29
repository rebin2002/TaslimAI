import { test, expect, loginInUi, logoutInUi, registerInUi } from "./fixtures";

test.describe("authentication and onboarding", () => {
  test("login exposes a working forgot-password route", async ({ page }) => {
    await page.goto("/login");
    const forgotPassword = page.getByRole("link", { name: /forgot password/i });
    await expect(forgotPassword).toHaveAttribute("href", "/forgot-password");
    await forgotPassword.click();
    await expect(page).toHaveURL(/\/forgot-password$/);
    await expect(page.getByRole("heading", { name: /reset your password/i })).toBeVisible();
    await expect(page.getByLabel(/email/i)).toBeVisible();
  });

  test("recovery request shows the same accepted-state copy without revealing account existence", async ({ page }) => {
    await page.goto("/login");
    const appOrigin = new URL(page.url()).origin;
    const corsHeaders = {
      "access-control-allow-origin": appOrigin,
      "access-control-allow-credentials": "true",
      "access-control-allow-methods": "GET, POST, OPTIONS",
      "access-control-allow-headers": "content-type, x-csrf-token",
    };
    await page.route("**/api/auth/csrf", (route) => route.fulfill({
      status: 200,
      headers: { ...corsHeaders, "content-type": "application/json" },
      body: JSON.stringify({ token: "e2e-csrf-token" }),
    }));
    let submittedEmail = "";
    await page.route("**/api/auth/password/forgot", async (route) => {
      if (route.request().method() === "OPTIONS") {
        await route.fulfill({ status: 204, headers: corsHeaders });
        return;
      }
      submittedEmail = (route.request().postDataJSON() as { email: string }).email;
      await route.fulfill({
        status: 202,
        headers: { ...corsHeaders, "content-type": "application/json" },
        body: JSON.stringify({ message: "If an account matches that email, password reset instructions will be sent." }),
      });
    });

    await page.getByRole("link", { name: /forgot password/i }).click();
    await expect(page).toHaveURL(/\/forgot-password$/);
    await expect(page.getByRole("heading", { name: /reset your password/i })).toBeVisible();
    const emailInput = page.getByLabel(/^email$/i);
    await emailInput.fill("recovery@example.test");
    await expect(emailInput).toHaveValue("recovery@example.test");
    await page.getByRole("button", { name: /send reset link/i }).click();
    await expect(page.getByRole("status")).toContainText(/if an account matches that email/i);
    expect(submittedEmail).toBe("recovery@example.test");
  });

  test("reset page removes fragment credentials before showing its form", async ({ page }) => {
    const email = encodeURIComponent("reset-link@example.test");
    const token = encodeURIComponent("private-reset-token-that-must-not-render");
    const response = await page.goto(`/reset-password#email=${email}&token=${token}&lang=en`);
    expect(response).not.toBeNull();
    expect(response?.headers()["cache-control"]).toContain("no-store");
    expect(response?.headers()["referrer-policy"]).toBe("no-referrer");
    await expect(page).toHaveURL(/\/reset-password$/);
    await expect(page.getByLabel(/new password/i)).toBeVisible();
    await expect(page.locator("body")).not.toContainText("private-reset-token-that-must-not-render");
  });

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
