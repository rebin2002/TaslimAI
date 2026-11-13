import { test, expect } from "./fixtures";
import type { Page } from "@playwright/test";
import { localeDirection, localeTag, translate, type Locale } from "../src/lib/i18n";

// Test-only browser acceptance lane for the #686 password-recovery feature.
//
// Proves the user-visible journey the API-side lanes (#686 delivery tests, #739 expiry
// localization, #752 fake-SMTP transport refusals) cannot cover from a browser:
//   1. the localized "Forgot password?" entry on /login reaches /forgot-password;
//   2. the recovery page is accessible and localized in English, Arabic, and Sorani
//      (document lang/dir, localized heading, labeled email field, labeled language
//      switch, and localized submit control are all reachable by role/label queries);
//   3. submitting the request renders the generic, non-enumerating accepted state in
//      the recipient's locale instead of echoing server text.
//
// No real mail is ever delivered: every /api/* request the page makes is intercepted
// by deterministic Playwright route fakes, so no API server, SMTP transport, or
// provider credential is contacted. Unexpected API paths are recorded and asserted
// empty. This file intentionally does NOT touch apps/web/e2e/auth.spec.ts or
// AuthForm.tsx (canonically owned by #686); it adds a separate, unowned lane.

const RECOVERY_TEST_EMAIL = "localized-recovery@example.test";

// Server body intentionally differs from the client's canonical copy for every locale:
// rendering it would be an unsafe echo of server text instead of the localized
// non-enumerating state, so each test asserts this exact string is absent.
const FAKE_SERVER_ACCEPTED_MESSAGE =
  "If an account matches that email, password reset instructions will be sent.";

const ALLOWED_API_PATHS = new Set([
  "/api/auth/me",
  "/api/auth/csrf",
  "/api/auth/password-policy",
  "/api/auth/password-recovery/availability",
  "/api/auth/password/forgot",
]);

function corsHeaders(page: Page) {
  return {
    "access-control-allow-origin": new URL(page.url()).origin || "http://localhost:3000",
    "access-control-allow-credentials": "true",
    "access-control-allow-methods": "GET, POST, OPTIONS",
    "access-control-allow-headers": "content-type, x-csrf-token",
  };
}

function jsonResponse(page: Page, status: number, body: unknown) {
  return {
    status,
    headers: { ...corsHeaders(page), "content-type": "application/json", "cache-control": "no-store" },
    body: JSON.stringify(body),
  };
}

/**
 * Installs deterministic fakes for every API route this journey touches and records
 * the payloads for assertions. Registered catch-all first so the specific handlers
 * (Playwright matches the most recently added route first) take precedence.
 */
function installMockedAuthApi(page: Page) {
  const record: { apiPaths: string[]; unexpectedPaths: string[]; forgotBodies: string[] } = {
    apiPaths: [],
    unexpectedPaths: [],
    forgotBodies: [],
  };

  page.route("**/api/**", async (route) => {
    const path = new URL(route.request().url()).pathname;
    record.apiPaths.push(path);
    if (!ALLOWED_API_PATHS.has(path)) record.unexpectedPaths.push(path);
    if (route.request().method() === "OPTIONS") {
      await route.fulfill({ status: 204, headers: corsHeaders(page) });
      return;
    }
    // Anything beyond the allowlist never reaches a real backend; fail it loudly.
    await route.fulfill(jsonResponse(page, 404, { error: { code: "UNEXPECTED_IN_JOURNEY", message: "blocked" } }));
  });

  page.route("**/api/auth/password/forgot", async (route) => {
    if (route.request().method() === "OPTIONS") {
      await route.fulfill({ status: 204, headers: corsHeaders(page) });
      return;
    }
    record.forgotBodies.push(route.request().postData() ?? "");
    await route.fulfill(jsonResponse(page, 202, { message: FAKE_SERVER_ACCEPTED_MESSAGE }));
  });

  page.route("**/api/auth/csrf", async (route) => {
    if (route.request().method() === "OPTIONS") {
      await route.fulfill({ status: 204, headers: corsHeaders(page) });
      return;
    }
    await route.fulfill(jsonResponse(page, 200, { token: "acceptance-csrf-token" }));
  });

  page.route("**/api/auth/password-policy", async (route) => {
    await route.fulfill(jsonResponse(page, 200, {
      requiredLength: 10,
      requireUppercase: true,
      requireLowercase: true,
      requireDigit: true,
      requireNonAlphanumeric: true,
      requiredUniqueChars: 1,
    }));
  });

  page.route("**/api/auth/password-recovery/availability", async (route) => {
    if (route.request().method() === "OPTIONS") {
      await route.fulfill({ status: 204, headers: corsHeaders(page) });
      return;
    }
    await route.fulfill(jsonResponse(page, 200, { available: true }));
  });

  return record;
}

for (const locale of ["en", "ar", "ku"] as const) {
  test.describe(`password recovery browser acceptance (${locale})`, () => {
    test(`the localized login entry reaches an accessible localized recovery page and returns the generic accepted state in ${locale}`, async ({ page }) => {
      const record = installMockedAuthApi(page);

      // 1. Localized entry point on the login page.
      await page.goto("/login");
      await expect(page.locator("body")).toBeVisible();
      await page.locator(".auth-language select").selectOption(locale);
      await expect(page.locator("html")).toHaveAttribute("lang", localeTag(locale));
      await expect(page.locator("html")).toHaveAttribute("dir", localeDirection(locale));

      const forgotLink = page.getByRole("link", { name: translate(locale, "auth.forgotPassword"), exact: true });
      await expect(forgotLink).toBeVisible();
      await expect(forgotLink).toHaveAttribute("href", "/forgot-password");
      await forgotLink.click();
      await expect(page).toHaveURL(/\/forgot-password$/);

      // 2. Accessible, localized recovery page (heading, language switch, labeled field,
      //    localized submit control, and RTL/LTR direction all reachable by role/label).
      await expect(page.locator("html")).toHaveAttribute("lang", localeTag(locale));
      await expect(page.locator("html")).toHaveAttribute("dir", localeDirection(locale));
      await expect(
        page.getByRole("heading", { level: 1, name: translate(locale, "auth.forgotTitle"), exact: true }),
      ).toBeVisible();

      // The recovery panel is a landmark region named by its localized h1
      // (aria-labelledby="recovery-title"), so every control is reached inside it.
      const recoveryPanel = page.getByRole("region", { name: translate(locale, "auth.forgotTitle"), exact: true });
      await expect(recoveryPanel.getByLabel(translate(locale, "navigation.language"), { exact: true })).toHaveValue(locale);

      const emailField = recoveryPanel.getByLabel(translate(locale, "auth.email"), { exact: true });
      await expect(emailField).toBeVisible();
      await emailField.fill(RECOVERY_TEST_EMAIL);

      const sendButton = recoveryPanel.getByRole("button", { name: translate(locale, "auth.sendResetLink"), exact: true });
      await expect(sendButton).toBeEnabled();
      await sendButton.click();

      // 3. Safe generic response: the localized non-enumerating accepted state renders,
      //    server text is never echoed, and no token, link, or account signal appears.
      await expect(recoveryPanel.getByRole("status")).toContainText(translate(locale, "auth.resetRequestSent"));
      const bodyText = await page.locator("body").innerText();
      expect(bodyText).not.toContain(FAKE_SERVER_ACCEPTED_MESSAGE);
      expect(page.locator('a[href*="reset-password"]')).toHaveCount(0);

      // Deterministic-fake proof: exactly one mocked forgot request with an email-only
      // payload, and no API path outside the allowlist (nothing reached a mail transport).
      expect(record.forgotBodies).toHaveLength(1);
      const forgotPayload = JSON.parse(record.forgotBodies[0]) as Record<string, unknown>;
      expect(Object.keys(forgotPayload).sort()).toEqual(["email"]);
      expect(forgotPayload.email).toBe(RECOVERY_TEST_EMAIL);
      expect(record.unexpectedPaths).toEqual([]);
    });
  });
}
