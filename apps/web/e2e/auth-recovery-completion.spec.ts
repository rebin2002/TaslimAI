import { test, expect } from "./fixtures";
import type { Page } from "@playwright/test";
import { localeDirection, localeTag, translate, type Locale } from "../src/lib/i18n";
// Test-only browser acceptance lane for the #686 password-recovery feature.
//
// #788 (apps/web/e2e/auth-recovery-localized.spec.ts) deliberately stops at the
// request leg: "the localized login entry reaches /forgot-password and returns the
// generic accepted state". #686's own auth.spec.ts covers the forgot-password entry,
// the generic accepted state, the disabled state, and fragment scrubbing on the reset
// page - but no browser lane ever submits the reset form and follows the user out of
// the journey. The API-side lanes (#686 delivery tests, #739 expiry localization,
// #752 fake-SMTP transport refusals, #806 CORS/cookie-jar boundary) prove the server
// changes the password and never auto-signs the user in; they cannot prove the
// user-visible contract that closes the loop:
//   1. the accepted request state hands off to the reset link, whose fragment form
//      submits the new password and then renders the localized success state;
//   2. the credential inputs are gone after success and "Back to sign in" actually
//      lands the user on /login - in English and in the RTL Arabic locale carried by
//      the link's lang parameter;
//   3. a rejected (expired/replayed) token degrades to the localized invalid state
//      with both a "Forgot password?" re-request escape and the login return leg.
//
// No real mail, API server, or SMTP transport is contacted: every /api/* request the
// journey makes is intercepted by deterministic Playwright route fakes. Unexpected
// API paths are recorded and asserted empty. This file adds one new, unowned lane and
// intentionally does not modify auth.spec.ts, auth-recovery-localized.spec.ts,
// PasswordRecoveryForm.tsx, or any file canonically owned by #686/#721/#739/#752/#788/#806.
const RECOVERY_TEST_EMAIL = "completion-journey@example.test";
// A token that must never render or leak anywhere in the DOM once consumed.
const JOURNEY_TOKEN = "completion-journey-token-must-not-render";
const NEW_PASSWORD = "BrandNewPassphrase!23";
// Server bodies intentionally differ from the client's canonical localized copy:
// rendering them would echo server text instead of the safe localized state.
const FAKE_SERVER_ACCEPTED_MESSAGE =
  "If an account matches that email, password reset instructions will be sent.";
const FAKE_SERVER_INVALID_MESSAGE = "Reset token is invalid or expired.";
const ALLOWED_API_PATHS = new Set([
  "/api/auth/me",
  "/api/auth/csrf",
  "/api/auth/password-policy",
  "/api/auth/password-recovery/availability",
  "/api/auth/password/forgot",
  "/api/auth/password/reset",
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
 * the payloads for assertions. Catch-all registered first so the specific handlers
 * (Playwright matches the most recently added route first) take precedence.
 */
function installMockedAuthApi(page: Page, resetOutcome: "success" | "invalid" = "success") {
  const record: {
    apiPaths: string[];
    unexpectedPaths: string[];
    forgotBodies: string[];
    resetBodies: string[];
    resetCsrfHeaders: Array<string | undefined>;
  } = { apiPaths: [], unexpectedPaths: [], forgotBodies: [], resetBodies: [], resetCsrfHeaders: [] };
  page.route("**/api/**", async (route) => {
    const path = new URL(route.request().url()).pathname;
    record.apiPaths.push(path);
    if (!ALLOWED_API_PATHS.has(path)) record.unexpectedPaths.push(path);
    if (route.request().method() === "OPTIONS") {
      await route.fulfill({ status: 204, headers: corsHeaders(page) });
      return;
    }
    if (path === "/api/auth/me") {
      // Unauthenticated visitor; keeps /login from redirecting to /projects.
      await route.fulfill(jsonResponse(page, 401, { error: { code: "UNAUTHORIZED", message: "no session" } }));
      return;
    }
    // Anything beyond the faked handlers never reaches a real backend; fail it loudly.
    await route.fulfill(jsonResponse(page, 404, { error: { code: "UNEXPECTED_IN_JOURNEY", message: "blocked" } }));
  });
  page.route("**/api/auth/csrf", async (route) => {
    if (route.request().method() === "OPTIONS") {
      await route.fulfill({ status: 204, headers: corsHeaders(page) });
      return;
    }
    await route.fulfill(jsonResponse(page, 200, { token: "acceptance-csrf-token" }));
  });
  page.route("**/api/auth/password-policy", async (route) => {
    if (route.request().method() === "OPTIONS") {
      await route.fulfill({ status: 204, headers: corsHeaders(page) });
      return;
    }
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
  page.route("**/api/auth/password/forgot", async (route) => {
    if (route.request().method() === "OPTIONS") {
      await route.fulfill({ status: 204, headers: corsHeaders(page) });
      return;
    }
    record.forgotBodies.push(route.request().postData() ?? "");
    await route.fulfill(jsonResponse(page, 202, { message: FAKE_SERVER_ACCEPTED_MESSAGE }));
  });
  page.route("**/api/auth/password/reset", async (route) => {
    if (route.request().method() === "OPTIONS") {
      await route.fulfill({ status: 204, headers: corsHeaders(page) });
      return;
    }
    record.resetBodies.push(route.request().postData() ?? "");
    record.resetCsrfHeaders.push(route.request().headers()["x-csrf-token"]);
    if (resetOutcome === "success") {
      await route.fulfill(jsonResponse(page, 200, { success: true }));
      return;
    }
    await route.fulfill(jsonResponse(page, 400, {
      error: { code: "PASSWORD_RESET_INVALID", message: FAKE_SERVER_INVALID_MESSAGE },
    }));
  });
  return record;
}
// Both credential inputs carry autocomplete="new-password" (locale-independent);
// the new-password field's accessible name also embeds the live requirements list,
// so role/label queries are avoided inside the reset form.
const credentialInputs = (page: Page) => page.locator('input[autocomplete="new-password"]');
function resetUrl(locale: Locale) {
  const email = encodeURIComponent(RECOVERY_TEST_EMAIL);
  const token = encodeURIComponent(JOURNEY_TOKEN);
  return `/reset-password#email=${email}&token=${token}&lang=${locale}`;
}
async function renderInLocale(page: Page, locale: Locale) {
  if (locale === "en") return;
  await page.locator(".auth-language select").selectOption(locale);
  await expect(page.locator("html")).toHaveAttribute("lang", localeTag(locale));
}
async function submitNewPassword(page: Page, locale: Locale) {
  await expect(credentialInputs(page)).toHaveCount(2);
  await credentialInputs(page).nth(0).fill(NEW_PASSWORD);
  await credentialInputs(page).nth(1).fill(NEW_PASSWORD);
  await page.getByRole("button", { name: translate(locale, "auth.resetPassword"), exact: true }).click();
}
for (const locale of ["en", "ar"] as const) {
  test.describe(`password recovery completion journey (${locale})`, () => {
    test(`accepted request hands off to the reset link and a successful reset returns the user to sign in in ${locale}`, async ({ page }) => {
      const record = installMockedAuthApi(page, "success");
      // Leg 1: request the reset from /forgot-password and reach the generic
      // accepted state (the handoff point every email client then leaves from).
      await page.goto("/forgot-password");
      await expect(page.locator("body")).toBeVisible();
      await renderInLocale(page, locale);
      const requestPanel = page.getByRole("region", { name: translate(locale, "auth.forgotTitle"), exact: true });
      await requestPanel.getByLabel(translate(locale, "auth.email"), { exact: true }).fill(RECOVERY_TEST_EMAIL);
      await requestPanel.getByRole("button", { name: translate(locale, "auth.sendResetLink"), exact: true }).click();
      await expect(requestPanel.getByRole("status")).toContainText(translate(locale, "auth.resetRequestSent"));
      const requestStateText = await page.locator("body").innerText();
      expect(requestStateText).not.toContain(FAKE_SERVER_ACCEPTED_MESSAGE);
      expect(requestStateText).not.toContain(JOURNEY_TOKEN);
      // Leg 2: open the mailbox link exactly as the template builds it - fragment
      // credentials plus the recipient locale - and verify the scrubbed reset form.
      await page.goto(resetUrl(locale));
      await expect(page).toHaveURL(/\/reset-password$/);
      await expect(page.locator("html")).toHaveAttribute("lang", localeTag(locale));
      await expect(page.locator("html")).toHaveAttribute("dir", localeDirection(locale));
      const panel = page.getByRole("region", { name: translate(locale, "auth.resetTitle"), exact: true });
      await expect(panel.getByRole("heading", { level: 1, name: translate(locale, "auth.resetTitle"), exact: true })).toBeVisible();
      await expect(credentialInputs(page)).toHaveCount(2);
      await expect(page.locator("body")).not.toContainText(JOURNEY_TOKEN);
      // Leg 3: submit the new password; the CSRF-guarded POST carries the fragment
      // credentials and the form renders the localized success state, not server text.
      await submitNewPassword(page, locale);
      await expect.poll(() => record.resetBodies.length).toBe(1);
      expect(record.resetCsrfHeaders[0]).toBe("acceptance-csrf-token");
      const resetPayload = JSON.parse(record.resetBodies[0]) as Record<string, unknown>;
      expect(Object.keys(resetPayload).sort()).toEqual(["email", "newPassword", "token"]);
      expect(resetPayload.email).toBe(RECOVERY_TEST_EMAIL);
      expect(resetPayload.token).toBe(JOURNEY_TOKEN);
      expect(resetPayload.newPassword).toBe(NEW_PASSWORD);
      await expect(panel.getByRole("status")).toContainText(translate(locale, "auth.resetSuccess"));
      const successText = await page.locator("body").innerText();
      expect(successText).not.toContain(FAKE_SERVER_INVALID_MESSAGE);
      // The credential inputs are gone (unmounted with the form), the typed password
      // and the token never appear in the post-success DOM.
      await expect(credentialInputs(page)).toHaveCount(0);
      expect(successText).not.toContain(NEW_PASSWORD);
      expect(successText).not.toContain(JOURNEY_TOKEN);
      // Leg 4: the return-to-login control exists, points at /login, and lands there.
      const backToLogin = panel.getByRole("link", { name: translate(locale, "auth.backToLogin"), exact: true });
      await expect(backToLogin).toBeVisible();
      await expect(backToLogin).toHaveAttribute("href", "/login");
      await backToLogin.click();
      await expect(page).toHaveURL(/\/login$/);
      await expect(page.getByRole("link", { name: translate(locale, "auth.forgotPassword"), exact: true })).toBeVisible();
      // Deterministic-fake proof: the journey spent exactly one faked request leg and
      // nothing reached an unexpected (or real) backend path.
      expect(record.forgotBodies).toHaveLength(1);
      expect(record.unexpectedPaths).toEqual([]);
    });
  });
}
test.describe("password recovery completion journey (invalid token)", () => {
  test("a rejected reset token degrades to the localized invalid state with re-request and login escapes", async ({ page }) => {
    const locale: Locale = "en";
    const record = installMockedAuthApi(page, "invalid");
    await page.goto(resetUrl(locale));
    await expect(page).toHaveURL(/\/reset-password$/);
    const panel = page.getByRole("region", { name: translate(locale, "auth.resetTitle"), exact: true });
    await expect(credentialInputs(page)).toHaveCount(2);
    await submitNewPassword(page, locale);
    await expect.poll(() => record.resetBodies.length).toBe(1);
    // The canonical invalid response renders the localized generic copy, never the
    // server message, and the form drops out of the ready state.
    await expect(panel.getByRole("status")).toContainText(translate(locale, "auth.resetInvalid"));
    const bodyText = await page.locator("body").innerText();
    expect(bodyText).not.toContain(FAKE_SERVER_INVALID_MESSAGE);
    expect(bodyText).not.toContain(JOURNEY_TOKEN);
    await expect(credentialInputs(page)).toHaveCount(0);
    // Both escapes from the dead end are present and correctly targeted.
    const retry = panel.getByRole("link", { name: translate(locale, "auth.forgotPassword"), exact: true });
    await expect(retry).toHaveAttribute("href", "/forgot-password");
    const backToLogin = panel.getByRole("link", { name: translate(locale, "auth.backToLogin"), exact: true });
    await expect(backToLogin).toHaveAttribute("href", "/login");
    await retry.click();
    await expect(page).toHaveURL(/\/forgot-password$/);
    expect(record.unexpectedPaths).toEqual([]);
  });
});
