import { test, expect, loginInUi, logoutInUi, registerInUi } from "./fixtures";

test.describe("authentication and onboarding", () => {
  test("registers a user and completes the guided onboarding flow", async ({ page, testUser }) => {
    await registerInUi(page, testUser, false);
    const onboarding = page.getByRole("dialog");
    await expect(onboarding).toBeVisible();
    await expect(onboarding.getByRole("heading")).toBeVisible();
    await onboarding.getByRole("button", { name: /continue/i }).click();
    await expect(onboarding.getByRole("heading")).toBeVisible();
    await onboarding.getByRole("button", { name: /choose an action/i }).click();
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
    await expect(page.getByRole("heading", { name: new RegExp(`welcome.*${testUser.displayName}`, "i") })).toBeVisible();

    await logoutInUi(page);
    await page.goto("/projects");
    await expect(page).toHaveURL(/\/login\?next=%2Fprojects/);
  });

  test("authenticated home exposes the primary workspace entry points", async ({ authenticatedPage: page }) => {
    await page.goto("/");
    await expect(page.getByRole("heading", { name: /what would you like to create/i })).toBeVisible();
    await expect(page.getByRole("link", { name: /projects/i }).first()).toBeVisible();
    await expect(page.getByRole("link", { name: /assets/i }).first()).toBeVisible();
    await expect(page.getByRole("link", { name: /chat/i }).first()).toBeVisible();
  });
});
