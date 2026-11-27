import { expect, test, type Page } from "@playwright/test";
import { type Locale } from "../src/lib/i18n";
import { languagePackDirection, languagePackTag, translateLanguagePack } from "../src/lib/languagePackRegistry";
import { type LanguagePackId } from "../src/lib/languagePackTypes";
import { anonymousLanguagePackStorageKey } from "../src/lib/languagePackPersistence";
// Test-only UI smoke lane for the Forgot Password entry point (stacked on the
// #1121 integration re-cut of #686/#848; touches no other lane).
//
// Question this lane answers, and nothing else:
//   At the narrowest supported mobile canvas (320 CSS px), is the "Forgot
//   password?" link on /login accessible (locatable by role, visible, fully
//   inside the canvas, clickable) and does it navigate to /forgot-password,
//   for each supported UI language - English, Arabic, Kurdish Sorani, and
//   Simplified Chinese (zh-CN)?
//
// Language coverage notes:
//   * en/ar/ku are selected through the login page's own legacy switcher,
//     the same mechanism auth-recovery-localized.spec.ts proves.
//   * zh-CN is not in that legacy three-option switcher yet; the supported
//     route to a zh-CN login shell is the persisted anonymous language-pack
//     preference that LocaleProvider restores after hydration (see
//     language-pack.spec.ts). This lane seeds exactly that storage key, so it
//     tests what a real returning zh-CN visitor sees. The zh-CN pack ships
//     partial auth coverage with a documented English fallback (manifest
//     reviewFlags), so the assertions resolve copy through the app's own
//     translateLanguagePack path and pin that a rendered string - never a raw
//     key - carries the link.
//
// Account-enumeration boundary, UI-only: merely loading /login and clicking
// the entry must never fire /api/auth/password/forgot or any non-allowlisted
// API call, before or after an email is typed into the login field. The entry
// is gated solely by the static feature-flag availability endpoint; no
// request payload may contain the typed address. Token issuance, reset,
// outbox, migrations, SMTP and the recovery submit flow are owned elsewhere
// (#686/#848/#1121 lanes) and are deliberately NOT exercised here.
//
// Determinism: every /api/* request is intercepted by route fakes; no API
// server, SMTP transport, provider credential, or real account is contacted.
// Unexpected API paths are recorded and asserted empty.
const PROBE_EMAIL = "forgot-link-probe@example.test";
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
 * Installs deterministic fakes for every API route this journey may touch and
 * records the traffic for assertions. The catch-all is registered first so the
 * specific handlers below take precedence (Playwright matches the most
 * recently added route first).
 */
function installMockedAuthApi(page: Page) {
  const record = {
    paths: [] as string[],
    payloads: [] as string[],
    unexpectedPaths: [] as string[],
  };
  page.route("**/api/**", async (route) => {
    const url = new URL(route.request().url());
    record.paths.push(url.pathname);
    const post = route.request().postData() ?? "";
    if (url.search || post) record.payloads.push(`${url.search} ${post}`.trim());
    if (!ALLOWED_API_PATHS.has(url.pathname)) record.unexpectedPaths.push(url.pathname);
    if (route.request().method() === "OPTIONS") {
      await route.fulfill({ status: 204, headers: corsHeaders(page) });
      return;
    }
    // Everything else the page may still ask (for example the logged-out
    // /api/auth/me probe) fails closed with a 404 so no request can reach a
    // real backend; 404 is also the truthful logged-out answer the app handles.
    await route.fulfill(jsonResponse(page, 404, { error: { code: "UNEXPECTED_IN_JOURNEY", message: "blocked" } }));
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
  // Availability is a static feature flag: the same answer regardless of
  // anything the visitor types. This is what keeps the entry identical for
  // every account state and therefore unable to reveal account existence at
  // the UI seam.
  page.route("**/api/auth/password-recovery/availability", async (route) => {
    await route.fulfill(jsonResponse(page, 200, { available: true }));
  });
  return record;
}
type EntryCase = Readonly<{
  packId: LanguagePackId;
  name: string;
  /** Legacy switcher option to select after load; null keeps the rendered default. */
  switcher: Locale | null;
  /** Anonymous language-pack id seeded into localStorage before the first visit. */
  seededPack: LanguagePackId | null;
}>;
const cases: readonly EntryCase[] = [
  { packId: "en", name: "English", switcher: null, seededPack: null },
  { packId: "ar", name: "Arabic", switcher: "ar", seededPack: null },
  { packId: "ku", name: "Kurdish Sorani", switcher: "ku", seededPack: null },
  { packId: "zh-CN", name: "Simplified Chinese", switcher: null, seededPack: "zh-CN" },
];
for (const entryCase of cases) {
  test.describe(`forgot-password login entry at 320px (${entryCase.packId})`, () => {
    test(`the ${entryCase.name} entry is accessible on the 320px canvas, never probes the server per email, and navigates to /forgot-password`, async ({ page }) => {
      const record = installMockedAuthApi(page);
      await page.setViewportSize({ width: 320, height: 640 });
      if (entryCase.seededPack) {
        const seeded = entryCase.seededPack;
        await page.addInitScript(([key, value]) => {
          window.localStorage.setItem(key, value);
        }, [anonymousLanguagePackStorageKey, seeded] as const);
      }
      await page.goto("/login");
      if (entryCase.switcher) {
        await page.locator(".auth-language select").selectOption(entryCase.switcher);
      }
      await expect(page.locator("html")).toHaveAttribute("lang", languagePackTag(entryCase.packId));
      await expect(page.locator("html")).toHaveAttribute("dir", languagePackDirection(entryCase.packId));

      const label = translateLanguagePack(entryCase.packId, "auth.forgotPassword");
      const title = translateLanguagePack(entryCase.packId, "auth.forgotTitle");
      const emailLabel = translateLanguagePack(entryCase.packId, "auth.email");
      // The resolved copy must be a rendered string, never a raw catalog key,
      // whether the pack ships it natively or falls back to English.
      expect(label).not.toBe("auth.forgotPassword");
      expect(title).not.toBe("auth.forgotTitle");
      expect(emailLabel).not.toBe("auth.email");

      const forgotLink = page.getByRole("link", { name: label, exact: true });
      await expect(forgotLink).toBeVisible();
      await expect(forgotLink).toHaveAttribute("href", "/forgot-password");

      // Fully laid out on the 320px canvas: reachable without horizontal escape.
      const box = await forgotLink.boundingBox();
      expect(box, "the entry must have a rendered box at 320px").not.toBeNull();
      if (!box) throw new Error("the entry must have a rendered box at 320px");
      expect(box.x).toBeGreaterThanOrEqual(0);
      expect(box.x + box.width).toBeLessThanOrEqual(320);
      expect(box.y).toBeGreaterThanOrEqual(0);
      expect(box.height).toBeGreaterThan(0);

      // Entry-time enumeration boundary: nothing account-dependent has fired.
      expect(record.paths.filter((path) => path === "/api/auth/password/forgot")).toHaveLength(0);

      // Typing an email must not change the entry or trigger any probe.
      await page.getByLabel(emailLabel, { exact: true }).fill(PROBE_EMAIL);
      await expect(forgotLink).toBeVisible();
      expect(record.paths.filter((path) => path === "/api/auth/password/forgot")).toHaveLength(0);
      expect(record.payloads.filter((payload) => payload.includes(PROBE_EMAIL))).toEqual([]);

      // Accessible + clickable at mobile width; lands on the recovery page.
      await forgotLink.click();
      await expect(page).toHaveURL(/\/forgot-password$/);
      await expect(
        page.getByRole("heading", { level: 1, name: title, exact: true }),
      ).toBeVisible();

      // The whole journey never submitted a recovery request or hit a
      // non-allowlisted endpoint; only static page/auth-bootstrap calls occurred.
      expect(record.paths.filter((path) => path === "/api/auth/password/forgot")).toHaveLength(0);
      expect(record.payloads.filter((payload) => payload.includes(PROBE_EMAIL))).toEqual([]);
      expect(record.unexpectedPaths).toEqual([]);
    });
  });
}
