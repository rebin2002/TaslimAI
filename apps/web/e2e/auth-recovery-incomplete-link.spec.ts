import { test, expect } from "./fixtures";
import type { Page } from "@playwright/test";
import { localeDirection, localeTag, translate, type Locale } from "../src/lib/i18n";
// Test-only browser acceptance lane for the #686 password-recovery feature.
//
// The gap this lane closes is the guest arrival at /reset-password WITHOUT usable
// link credentials. Every existing browser lane enters the reset page through a
// complete, fresh link:
//   * #686 apps/web/e2e/auth.spec.ts proves the /login entry, the generic accepted
//     state, the unavailable-recovery state, and that a complete fragment is scrubbed
//     before the form renders;
//   * #788 apps/web/e2e/auth-recovery-localized.spec.ts stops at the localized request
//     leg on /forgot-password;
//   * #808 apps/web/e2e/auth-recovery-completion.spec.ts drives a complete fragment to
//     a successful reset and to the localized invalid state only after the SERVER
//     rejects a populated token (PASSWORD_RESET_INVALID from the reset POST).
// What no lane covers is the client-side branch of PasswordRecoveryForm.tsx that marks
// the link invalid WITHOUT ever calling the API (linkState === "invalid" from the
// fragment length/emptiness guard, rendering auth.resetLinkMissing). That branch is the
// common real-world expiry path: fragments are scrubbed on first render, so a reload,
// a browser-bookmarked link, or an email client that strips the fragment lands the user
// on a credential-less /reset-password. This lane pins the safe contract:
//   1. the localized incomplete-link state renders and the credential inputs never
//      mount, so the page cannot even attempt a reset submission;
//   2. zero POSTs reach /api/auth/password/reset - the client never spends the
//      rate-limited reset budget or echoes a partial credential to the server;
//   3. the fragment values (email and token) are scrubbed from the URL and never
//      rendered, including an over-long token that the guard rejects on length alone;
//   4. both escapes from the dead end are localized, correctly targeted, and actually
//      navigate - "Forgot password?" back to the /forgot-password entrypoint and "Back
//      to sign in" to /login - in English and in the RTL Arabic rendering.
//
// No real mail, SMTP transport, API server, or user account is involved: the recipient
// address is an example.test placeholder, the token is a fixed non-secret test literal,
// and every /api/* request is intercepted by deterministic Playwright route fakes.
// Unexpected API paths are recorded and asserted empty. This file adds one new, unowned
// lane and intentionally does not modify auth.spec.ts,
// auth-recovery-localized.spec.ts, auth-recovery-completion.spec.ts,
// PasswordRecoveryForm.tsx, or any file canonically owned by #686/#721/#788/#808.
const INCOMPLETE_LINK_TEST_EMAIL = "incomplete-link@example.test";
// A non-secret placeholder that must never survive into the rendered DOM.
const FRAGMENT_TOKEN = "incomplete-link-token-must-never-render";
// The client guard rejects any fragment token longer than 4096 characters.
const OVERLONG_TOKEN = "x".repeat(4097);
// Server copy intentionally differs from the client's canonical localized state:
// rendering it would echo server text instead of the safe localized message.
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
 * Installs deterministic fakes for every API route this journey can touch and records
 * what the page asked for. Catch-all registered first so the specific handlers
 * (Playwright matches the most recently added route first) take precedence.
 */
function installMockedAuthApi(page: Page) {
  const record: { apiPaths: string[]; unexpectedPaths: string[]; resetBodies: string[] } = {
    apiPaths: [],
    unexpectedPaths: [],
    resetBodies: [],
  };
  page.route("**/api/**", async (route) => {
    const path = new URL(route.request().url()).pathname;
    record.apiPaths.push(path);
    if (!ALLOWED_API_PATHS.has(path)) record.unexpectedPaths.push(path);
    if (route.request().method() === "OPTIONS") {
      await route.fulfill({ status: 204, headers: corsHeaders(page) });
      return;
    }
    if (path === "/api/auth/me") {
      // Unauthenticated guest; keeps the auth provider from redirecting.
      await route.fulfill(jsonResponse(page, 401, { error: { code: "UNAUTHORIZED", message: "no session" } }));
      return;
    }
    // Anything beyond the faked handlers never reaches a real backend; fail loudly.
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
    await route.fulfill(jsonResponse(page, 202, { message: "If an account matches that email, password reset instructions will be sent." }));
  });
  page.route("**/api/auth/password/reset", async (route) => {
    if (route.request().method() === "OPTIONS") {
      await route.fulfill({ status: 204, headers: corsHeaders(page) });
      return;
    }
    // The incomplete-link journey must never reach this handler at all; recording the
    // body keeps the assertion observable rather than inferred from a missing request.
    record.resetBodies.push(route.request().postData() ?? "");
    await route.fulfill(jsonResponse(page, 400, {
      error: { code: "PASSWORD_RESET_INVALID", message: FAKE_SERVER_INVALID_MESSAGE },
    }));
  });
  return record;
}
// Both credential inputs carry autocomplete="new-password" (locale-independent);
// their accessible names embed the live requirements list, so the count is the stable
// probe for "the reset form mounted at all".
const credentialInputs = (page: Page) => page.locator('input[autocomplete="new-password"]');
const resetPanel = (page: Page, locale: Locale) =>
  page.getByRole("region", { name: translate(locale, "auth.resetTitle"), exact: true });
/** Asserts the localized incomplete-link dead end and its two localized escapes. */
async function expectIncompleteLinkState(page: Page, locale: Locale, record: { resetBodies: string[]; unexpectedPaths: string[] }) {
  await expect(page).toHaveURL(/\/reset-password$/);
  // The credential-less URL must never fall back to the ready form or a spinner.
  await expect(resetPanel(page, locale).getByRole("status")).toContainText(translate(locale, "auth.resetLinkMissing"));
  await expect(credentialInputs(page)).toHaveCount(0);
  await expect(page.getByRole("button", { name: translate(locale, "auth.resetPassword"), exact: true })).toHaveCount(0);
  const bodyText = await page.locator("body").innerText();
  expect(bodyText).not.toContain(FAKE_SERVER_INVALID_MESSAGE);
  expect(bodyText).not.toContain(FRAGMENT_TOKEN);
  expect(bodyText).not.toContain(INCOMPLETE_LINK_TEST_EMAIL);
  // Client-side rejection: no reset POST was ever attempted, so no rate-limit budget
  // was spent and no partial credential was echoed to the server.
  expect(record.resetBodies).toEqual([]);
  const reRequest = resetPanel(page, locale).getByRole("link", { name: translate(locale, "auth.forgotPassword"), exact: true });
  await expect(reRequest).toHaveAttribute("href", "/forgot-password");
  const backToLogin = resetPanel(page, locale).getByRole("link", { name: translate(locale, "auth.backToLogin"), exact: true });
  await expect(backToLogin).toHaveAttribute("href", "/login");
}
test.describe("password recovery incomplete-link browser acceptance", () => {
  test("a credential-less /reset-password arrival renders the localized incomplete-link state and returns to sign in", async ({ page }) => {
    const locale: Locale = "en";
    const record = installMockedAuthApi(page);
    // A guest follows a bare /reset-password: a bookmark, a shared route, or an email
    // client that dropped the fragment entirely.
    await page.goto("/reset-password");
    await expect(page.locator("html")).toHaveAttribute("lang", localeTag(locale));
    await expect(page.locator("html")).toHaveAttribute("dir", localeDirection(locale));
    await expectIncompleteLinkState(page, locale, record);
    // The dead end is exitable by clicking, not just by href inspection: "Back to sign
    // in" lands the guest on /login with the recovery entrypoint available again.
    await resetPanel(page, locale)
      .getByRole("link", { name: translate(locale, "auth.backToLogin"), exact: true })
      .click();
    await expect(page).toHaveURL(/\/login$/);
    await expect(page.getByRole("link", { name: translate(locale, "auth.forgotPassword"), exact: true })).toBeVisible();
    expect(record.unexpectedPaths).toEqual([]);
  });
  test("reloading the reset page after its fragment is scrubbed degrades to the incomplete-link state without any reset POST", async ({ page }) => {
    const locale: Locale = "en";
    const record = installMockedAuthApi(page);
    // First visit through a complete link: the form mounts and the client scrubs the
    // credentials out of the URL before rendering.
    const email = encodeURIComponent(INCOMPLETE_LINK_TEST_EMAIL);
    const token = encodeURIComponent(FRAGMENT_TOKEN);
    await page.goto(`/reset-password#email=${email}&token=${token}&lang=${locale}`);
    await expect(page).toHaveURL(/\/reset-password$/);
    await expect(credentialInputs(page)).toHaveCount(2);
    expect(await page.evaluate(() => window.location.hash)).toBe("");
    // The user reloads (or returns from history): there is nothing left to submit with,
    // and the page must say so safely instead of showing an empty, submittable form.
    await page.reload();
    await expectIncompleteLinkState(page, locale, record);
    expect(record.unexpectedPaths).toEqual([]);
  });
  test("an over-long fragment token is rejected in place, scrubbed, and never rendered or submitted", async ({ page }) => {
    const locale: Locale = "en";
    const record = installMockedAuthApi(page);
    const email = encodeURIComponent(INCOMPLETE_LINK_TEST_EMAIL);
    await page.goto(`/reset-password#email=${email}&token=${OVERLONG_TOKEN}&lang=${locale}`);
    await expect(page).toHaveURL(/\/reset-password$/);
    // The guard rejects on length alone: no credential form, and the oversized value is
    // gone from the live URL and never appears in the DOM.
    await expect(credentialInputs(page)).toHaveCount(0);
    expect(await page.evaluate(() => window.location.hash)).toBe("");
    expect(await page.evaluate(() => window.location.search)).toBe("");
    const bodyText = await page.locator("body").innerText();
    expect(bodyText).not.toContain(OVERLONG_TOKEN.slice(0, 64));
    expect(bodyText).not.toContain(INCOMPLETE_LINK_TEST_EMAIL);
    await expect(resetPanel(page, locale).getByRole("status")).toContainText(translate(locale, "auth.resetLinkMissing"));
    expect(record.resetBodies).toEqual([]);
    // The other escape re-arms the entrypoint instead of stranding the guest.
    await resetPanel(page, locale)
      .getByRole("link", { name: translate(locale, "auth.forgotPassword"), exact: true })
      .click();
    await expect(page).toHaveURL(/\/forgot-password$/);
    await expect(page.getByLabel(translate(locale, "auth.email"), { exact: true })).toBeVisible();
    expect(record.unexpectedPaths).toEqual([]);
  });
  for (const locale of ["ar"] as const) {
    test(`the incomplete-link dead end stays localized and RTL for ${locale} while the lang parameter is carried`, async ({ page }) => {
      const record = installMockedAuthApi(page);
      // The email client rendered an Arabic link that lost only its token: the lang
      // parameter still drives the locale of the safe state.
      const email = encodeURIComponent(INCOMPLETE_LINK_TEST_EMAIL);
      await page.goto(`/reset-password#email=${email}&lang=${locale}`);
      await expect(page.locator("html")).toHaveAttribute("lang", localeTag(locale));
      await expect(page.locator("html")).toHaveAttribute("dir", localeDirection(locale));
      await expectIncompleteLinkState(page, locale, record);
      await resetPanel(page, locale)
        .getByRole("link", { name: translate(locale, "auth.backToLogin"), exact: true })
        .click();
      await expect(page).toHaveURL(/\/login$/);
      expect(record.unexpectedPaths).toEqual([]);
    });
  }
});
