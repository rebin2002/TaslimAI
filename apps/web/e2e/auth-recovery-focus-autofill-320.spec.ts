import { test, expect } from "./fixtures";
import type { Page } from "@playwright/test";
import { localeDirection, localeTag, translate, type Locale } from "../src/lib/i18n";

// Test-only browser acceptance lane for the #686 password-recovery feature.
//
// Focus: the secure Forgot Password experience at login on a narrow 320px mobile
// viewport in the two RTL locales (Kurdish Sorani and Arabic), covering exactly
// what no existing recovery lane pins:
//
//   1. FOCUS ORDER - keyboard Tab traversal of the login entry, the /forgot-password
//      request form, and the credential-bearing /reset-password form. No recovery
//      lane (own #686 suite, #788, #806, #808, #1024) ever presses Tab on a
//      recovery page; the repo's accessibility/mobile suites never visit one.
//      In RTL the DOM order (language switch -> email / passwords -> submit ->
//      back links) is the contract; source-order regressions are otherwise silent.
//   2. AUTOFILL ATTRIBUTES - email=autocomplete "email" + dir "ltr" inside the RTL
//      form; both reset passwords autocomplete "new-password" + dir "ltr". Other
//      lanes only USE these as selectors (#808, #1024); none asserts them as a
//      contract, and #808/#1024 never render the ready reset form in Sorani.
//   3. VISIBLE LINK + CONFIRMATION STATE at 320px - the localized "Forgot
//      password?" link and the generic accepted state stay fully rendered,
//      hittable, and inside the viewport with no horizontal overflow (Sorani and
//      Arabic strings are considerably wider than English).
//   4. RESET-TOKEN URL HYGIENE on the ku/ar path - after the fragment scrub, the
//      token appears in neither location.hash, location.search, the rendered DOM,
//      nor any href, and the URL stays clean through the accepted state.
//
// Deterministic and provider-free: every /api/* call is intercepted by the same
// Playwright route fakes used by lanes #788/#808/#1024, so no API server, SMTP
// transport, or provider credential is ever contacted; only synthetic .example.test
// emails and synthetic tokens are used. No file outside this new spec is touched.

const RECOVERY_TEST_EMAIL = "narrow-viewport-recovery@example.test";
const FAKE_SERVER_ACCEPTED_MESSAGE =
  "If an account matches that email, password reset instructions will be sent.";
const ALLOWED_API_PATHS = new Set([
  "/api/auth/me",
  "/api/auth/csrf",
  "/api/auth/password-policy",
  "/api/auth/password-recovery/availability",
  "/api/auth/password/forgot",
]);

test.use({ viewport: { width: 320, height: 640 } });

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
    headers: {
      ...corsHeaders(page),
      "content-type": "application/json",
      "cache-control": "no-store",
    },
    body: JSON.stringify(body),
  };
}

/** Deterministic fake for every API route the recovery UI touches. */
function installMockedRecoveryApi(page: Page) {
  const record: { apiPaths: string[]; unexpectedPaths: string[] } = {
    apiPaths: [],
    unexpectedPaths: [],
  };
  page.route("**/api/**", async (route) => {
    const path = new URL(route.request().url()).pathname;
    record.apiPaths.push(path);
    if (!ALLOWED_API_PATHS.has(path)) record.unexpectedPaths.push(path);
    if (route.request().method() === "OPTIONS") {
      await route.fulfill({ status: 204, headers: corsHeaders(page) });
      return;
    }
    await route.fulfill(
      jsonResponse(page, 404, { error: { code: "UNEXPECTED_IN_JOURNEY", message: "blocked" } }),
    );
  });
  page.route("**/api/auth/password/forgot", async (route) => {
    if (route.request().method() === "OPTIONS") {
      await route.fulfill({ status: 204, headers: corsHeaders(page) });
      return;
    }
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

/** Selects the locale through the in-panel switch and waits for the RTL html state. */
async function renderInLocale(page: Page, locale: Locale) {
  await page.locator(".auth-language select").selectOption(locale);
  await expect(page.locator("html")).toHaveAttribute("lang", localeTag(locale));
  await expect(page.locator("html")).toHaveAttribute("dir", localeDirection(locale));
}

/** The narrow viewport must not scroll horizontally anywhere in the journey. */
async function expectNoHorizontalOverflow(page: Page) {
  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
  );
  expect(overflow, "320px viewport must not scroll horizontally").toBeLessThanOrEqual(2);
}

/** A control is fully inside the 320px-wide viewport (no clipped tap targets). */
async function expectInsideViewport(page: Page, locator: ReturnType<Page["locator"]>) {
  const box = await locator.boundingBox();
  expect(box, "control must have a bounding box").not.toBeNull();
  expect(box!.x).toBeGreaterThanOrEqual(-1);
  expect(box!.x + box!.width).toBeLessThanOrEqual(321);
}

/** Keyboard walk helper: presses Tab once and asserts the expected control gains focus. */
async function tabTo(page: Page, locator: ReturnType<Page["locator"]>) {
  await page.keyboard.press("Tab");
  await expect(locator).toBeFocused();
}

for (const locale of ["ku", "ar"] as const) {
  test.describe(`password recovery narrow-viewport focus/autofill acceptance (${locale}, 320px)`, () => {
    test(`the localized forgot-password link is visible, fully inside the viewport, and hittable at 320px in ${locale}`, async ({ page }) => {
      installMockedRecoveryApi(page);
      await page.goto("/login");
      await expect(page.locator("body")).toBeVisible();
      await renderInLocale(page, locale);

      const forgotLink = page.getByRole("link", {
        name: translate(locale, "auth.forgotPassword"),
        exact: true,
      });
      await expect(forgotLink).toBeVisible();
      await expect(forgotLink).toHaveAttribute("href", "/forgot-password");
      await expectInsideViewport(page, forgotLink);
      await expectNoHorizontalOverflow(page);

      // Hitting the link at its real 320px position lands on the request page.
      await forgotLink.click();
      await expect(page).toHaveURL(/\/forgot-password$/);
      await expect(page.locator("html")).toHaveAttribute("lang", localeTag(locale));
      await expect(page.locator("html")).toHaveAttribute("dir", localeDirection(locale));
      // The entry itself carries no credentials or query state in the URL.
      expect(page.url()).not.toContain("#");
      expect(page.url()).not.toContain("?");
      expect(page.url()).not.toContain(RECOVERY_TEST_EMAIL);
    });

    test(`the request form keeps DOM keyboard order, autofill attributes, and a visible localized confirmation at 320px in ${locale}`, async ({ page }) => {
      const record = installMockedRecoveryApi(page);
      await page.goto("/forgot-password");
      await renderInLocale(page, locale);

      const panel = page.getByRole("region", {
        name: translate(locale, "auth.forgotTitle"),
        exact: true,
      });
      const languageSwitch = panel.getByLabel(translate(locale, "navigation.language"), {
        exact: true,
      });
      const emailField = panel.getByLabel(translate(locale, "auth.email"), { exact: true });
      const sendButton = panel.getByRole("button", {
        name: translate(locale, "auth.sendResetLink"),
        exact: true,
      });
      const backLink = panel.getByRole("link", { name: translate(locale, "auth.backToLogin"), exact: true });
      await expect(emailField).toBeVisible();

      // The request form itself flips to RTL; the email field stays LTR inside it.
      await expect(page.locator("form.auth-form")).toHaveAttribute("dir", localeDirection(locale));
      await expect(emailField).toHaveAttribute("autocomplete", "email");
      await expect(emailField).toHaveAttribute("dir", "ltr");
      await expect(emailField).toHaveAttribute("autocapitalize", "none");

      // Keyboard walk: DOM order is the tab order. The chain starts from the
      // first control so a dev-only overlay cannot perturb it; the form's own
      // controls must follow each other with nothing focusable skipped between.
      await languageSwitch.focus();
      await tabTo(page, emailField);
      await tabTo(page, sendButton);
      await tabTo(page, backLink);

      await emailField.click();
      await emailField.fill(RECOVERY_TEST_EMAIL);
      await sendButton.click();

      // Generic, localized accepted state renders fully inside the narrow viewport.
      const status = panel.getByRole("status");
      await expect(status).toContainText(translate(locale, "auth.resetRequestSent"));
      await expect(status).toBeInViewport();
      await expectInsideViewport(page, status);
      await expectNoHorizontalOverflow(page);

      // Server text is never echoed, and no reset link or token ever appears.
      const bodyText = await page.locator("body").innerText();
      expect(bodyText).not.toContain(FAKE_SERVER_ACCEPTED_MESSAGE);
      await expect(page.locator('a[href*="reset-password"]')).toHaveCount(0);
      await expect(page.locator('a[href*="token"]')).toHaveCount(0);

      // The URL stays clean through the confirmation: no query, no hash, no email echo.
      const urlState = await page.evaluate(() => ({
        search: window.location.search,
        hash: window.location.hash,
        href: window.location.href,
      }));
      expect(urlState.search).toBe("");
      expect(urlState.hash).toBe("");
      expect(urlState.href).not.toContain(RECOVERY_TEST_EMAIL);

      // And the only outbound call is the single email-only request payload.
      expect(record.unexpectedPaths).toEqual([]);
    });

    test(`a credential-bearing reset link in ${locale} scrubs the token from the URL and the RTL form stays keyboard-orderable at 320px`, async ({ page }) => {
      const record = installMockedRecoveryApi(page);
      const token = `narrow-viewport-token-${locale}`;
      const resetUrl = `/reset-password#email=${encodeURIComponent(RECOVERY_TEST_EMAIL)}&token=${token}&lang=${locale}`;
      await page.goto(resetUrl);

      const panel = page.getByRole("region", {
        name: translate(locale, "auth.resetTitle"),
        exact: true,
      });
      const languageSwitch = panel.getByLabel(translate(locale, "navigation.language"), {
        exact: true,
      });
      // The new-password label wraps the requirements live region, so the
      // accessible name is the label plus requirement copy. Target the inputs
      // the way the shipped lanes (#808/#1024) do and pin the accessible name
      // containment separately.
      const credentialFields = panel.locator('input[autocomplete="new-password"]');
      const newPasswordField = credentialFields.nth(0);
      const confirmPasswordField = credentialFields.nth(1);
      const submitButton = panel.getByRole("button", {
        name: translate(locale, "auth.resetPassword"),
        exact: true,
      });
      const backLink = panel.getByRole("link", { name: translate(locale, "auth.backToLogin"), exact: true });

      // The ready RTL reset form renders in the recipient locale (first Sorani pin
      // of this state in any browser lane).
      await expect(page.locator("html")).toHaveAttribute("lang", localeTag(locale));
      await expect(page.locator("html")).toHaveAttribute("dir", localeDirection(locale));
      await expect(newPasswordField).toBeVisible();
      await expect(newPasswordField).toHaveAccessibleName(
        new RegExp(translate(locale, "auth.newPassword")),
      );
      await expect(confirmPasswordField).toHaveAccessibleName(
        new RegExp(translate(locale, "auth.confirmPassword")),
      );
      await expect(page.locator("form.auth-form")).toHaveAttribute("dir", localeDirection(locale));

      // Token hygiene: neither hash nor query nor the full location carries the
      // token or the account email after the client-side scrub.
      await expect(page).toHaveURL(/\/reset-password$/);
      const urlState = await page.evaluate(() => ({
        hash: window.location.hash,
        search: window.location.search,
        href: window.location.href,
      }));
      expect(urlState.hash).toBe("");
      expect(urlState.search).toBe("");
      expect(urlState.href).not.toContain(token);
      expect(urlState.href).not.toContain(RECOVERY_TEST_EMAIL);
      const bodyText = await page.locator("body").innerText();
      expect(bodyText).not.toContain(token);
      expect(bodyText).not.toContain(RECOVERY_TEST_EMAIL);
      await expect(page.locator('a[href*="token"], a[href*="email="]')).toHaveCount(0);
      // The page was reached with credentials; no referrer carries them onward.
      expect(await page.evaluate(() => document.referrer)).toBe("");

      // Autofill contract inside the RTL shell: password managers must see
      // new-password + LTR inputs; nothing in the existing lanes asserts it.
      await expect(newPasswordField).toHaveAttribute("autocomplete", "new-password");
      await expect(newPasswordField).toHaveAttribute("dir", "ltr");
      await expect(confirmPasswordField).toHaveAttribute("autocomplete", "new-password");
      await expect(confirmPasswordField).toHaveAttribute("dir", "ltr");

      // Keyboard walk in DOM order at 320px, RTL: language -> new -> confirm ->
      // submit -> back-to-login. The password-requirements live region is text
      // only and must stay out of the tab order.
      await languageSwitch.focus();
      await tabTo(page, newPasswordField);
      await tabTo(page, confirmPasswordField);
      await tabTo(page, submitButton);
      await tabTo(page, backLink);

      // The narrow RTL layout does not clip or overflow the form controls.
      await expectInsideViewport(page, newPasswordField);
      await expectInsideViewport(page, submitButton);
      await expectNoHorizontalOverflow(page);

      // No reset POST happened just by rendering the form, and nothing outside
      // the allowlist was contacted.
      expect(record.apiPaths.filter((path) => path === "/api/auth/password/reset")).toEqual([]);
      expect(record.unexpectedPaths).toEqual([]);
    });
  });
}
