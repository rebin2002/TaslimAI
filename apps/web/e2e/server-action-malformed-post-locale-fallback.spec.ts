/**
 * Issue #697 — localized end-to-end negative control for the malformed
 * Server Action failure class.
 *
 * Pinned elsewhere, deliberately not repeated here:
 *  - `e2e/server-action-malformed-post.spec.ts` (merged #764) records the
 *    request-handler contract in the default English browser context: exact
 *    400 text, same-origin 307 for the RSC shape, POST storm then a 200 GET.
 *  - `src/app/error.locale-boundary.test.tsx` isolates, provider-free, which
 *    boundary is capable of throwing the secondary locale error.
 *  - `src/lib/globalErrorI18n.browser-recovery.test.ts` (#1156) pins the
 *    browser locale read that the provider-free boundary performs.
 *  - `e2e/strict/next-start-strict-gate.mjs` (#1067) evaluates non-5xx
 *    admission against a production `next start` build and its synthetic
 *    deterministic-500 server, but never renders a localized fallback.
 *
 * The unclaimed seam is the joint property the production signal pairs
 * together: the HTTP 500 and `useLocale must be used within LocaleProvider`
 * were logged next to each other, and no test drives the malformed shapes
 * from inside a live hydrated Arabic/Sorani RTL document. This spec does:
 *  1. no malformed Server Action shape can produce an unhandled 5xx while the
 *     persisted non-English locale is active;
 *  2. the locale hook never throws in the page as a side effect of them;
 *  3. the localized fallback stays renderable afterwards - after a reload and
 *     across a client-side route switch.
 *
 * A passing run cannot be vacuous: the same dispatcher and the same 5xx
 * detector are first pointed at a synthetic deterministic 500 that is
 * intercepted inside this very browser context, and the control must be
 * flagged. If that attestation ever reports a non-5xx, this file can no
 * longer certify and fails loudly.
 *
 * Deterministic, loopback-only and provider-free: fixed anonymous constants,
 * no credentials, no production probe, and a request listener that fails the
 * run if any paid generation provider origin is contacted.
 */
import { test, expect, type Page } from "@playwright/test";
import { localeDirection, localeTag, translate, type Locale } from "../src/lib/i18n";

/** Shapes that production reported as malformed/unrecognized action references. */
const MALFORMED_ACTION_SHAPES: ReadonlyArray<{ name: string; actionId: string; rsc: boolean }> = [
  { name: "unrecognized 40-hex id", actionId: "0000000000000000000000000000000000000000", rsc: false },
  { name: "non-hex junk id", actionId: "zz-not-a-valid-id!!", rsc: false },
  { name: "RSC-shaped unknown id", actionId: "0000000000000000000000000000000000000000", rsc: true },
];

/** The persisted non-English locales named by the issue's fallback requirement. */
const FALLBACK_LOCALES = ["ar", "ku"] as const satisfies readonly Locale[];

const LOCALE_CRASH = "useLocale must be used within LocaleProvider";
const ROOT_BOUNDARY_COPY = "Something went wrong";
const REFERENCE_ID_LEAK = "Server Reference ID";

/** Loopback-only synthetic defect used to attest that the harness can see a 5xx. */
const SYNTHETIC_500_PATH = "/__taslim697-synthetic-500";
const SYNTHETIC_500_BODY = "synthetic-deterministic-500";

type ActionOutcome = { name: string; status: number; body: string };

/** The single decision the whole spec rests on: an unhandled server failure. */
const isUnhandledServerFailure = (outcome: ActionOutcome) => outcome.status >= 500;

/**
 * Dispatch every malformed shape from inside the live page, so the request
 * carries the hydrated document's origin, cookies and RSC router state rather
 * than a bare APIRequestContext. Transport rejections are recorded as status
 * 0 and are failures in their own right, never silently skipped.
 */
async function dispatchMalformedShapes(page: Page, path: string): Promise<ActionOutcome[]> {
  const shapes = MALFORMED_ACTION_SHAPES.map(({ name, actionId, rsc }) => ({ name, actionId, rsc }));
  return page.evaluate(async ({ target, entries }) => {
    const outcomes: Array<{ name: string; status: number; body: string }> = [];
    for (const entry of entries) {
      const headers: Record<string, string> = {
        "next-action": entry.actionId,
        "content-type": "text/plain;charset=UTF-8",
      };
      if (entry.rsc) headers.rsc = "1";
      try {
        const response = await fetch(target, { method: "POST", headers, body: "[]" });
        outcomes.push({ name: entry.name, status: response.status, body: (await response.text()).slice(0, 400) });
      } catch (caught) {
        outcomes.push({ name: entry.name, status: 0, body: String(caught) });
      }
    }
    return outcomes;
  }, { target: path, entries: shapes });
}

function watchForLocaleCrash(page: Page) {
  const crashes: string[] = [];
  page.on("pageerror", (error) => {
    const message = String(error?.message ?? error);
    if (message.includes(LOCALE_CRASH)) crashes.push(`pageerror: ${message}`);
  });
  page.on("console", (message) => {
    if (message.text().includes(LOCALE_CRASH)) crashes.push(`console: ${message.text()}`);
  });
  return crashes;
}

for (const locale of FALLBACK_LOCALES) {
  const lang = localeTag(locale);
  const dir = localeDirection(locale);

  test.describe(`malformed Server Action POSTs with a persisted ${locale} fallback (#697)`, () => {
    test(`no shape is an unhandled 5xx and the ${lang} document stays renderable`, async ({ page }) => {
      const crashes = watchForLocaleCrash(page);
      const providerRequests: string[] = [];
      page.on("request", (request) => {
        if (/sunoapi|runway|\/api\/(generation|payments|checkout|provider)/i.test(request.url())) {
          providerRequests.push(request.url());
        }
      });

      // Seed the persisted choice exactly like the in-app language selector,
      // before any document script runs, so hydration applies the RTL locale.
      await page.addInitScript((seed) => {
        window.localStorage.setItem("taslim-locale", seed);
      }, locale);
      await page.goto("/login");

      // Pre-condition: the localized shell is genuinely up. Without this the
      // post-request assertions below could pass on an English or crashed page.
      await expect(page.locator("html")).toHaveAttribute("lang", lang);
      await expect(page.locator("html")).toHaveAttribute("dir", dir);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(translate(locale, "auth.loginTitle"));

      // Harness attestation: prove this context can observe a 5xx at all.
      await page.route(`**${SYNTHETIC_500_PATH}`, (route) =>
        route.fulfill({ status: 500, contentType: "text/plain", body: SYNTHETIC_500_BODY }),
      );
      const control = await dispatchMalformedShapes(page, SYNTHETIC_500_PATH);
      await page.unroute(`**${SYNTHETIC_500_PATH}`);
      expect(control, "every shape must be dispatched and answered").toHaveLength(MALFORMED_ACTION_SHAPES.length);
      expect(
        control.filter(isUnhandledServerFailure).length,
        "negative control broken: the harness cannot see a deterministic 500, so this spec cannot certify #697",
      ).toBe(MALFORMED_ACTION_SHAPES.length);
      expect(control.every((outcome) => outcome.body.includes(SYNTHETIC_500_BODY)), "attestation must read the body").toBe(true);

      // The real thing: the same shapes against the live root route.
      const outcomes = await dispatchMalformedShapes(page, "/");
      expect(outcomes).toHaveLength(MALFORMED_ACTION_SHAPES.length);
      for (const outcome of outcomes) {
        expect(outcome.status, `${outcome.name} must complete with an HTTP status`).toBeGreaterThan(0);
        expect(outcome.status, `${outcome.name} must not be an unhandled 5xx`).toBeLessThan(500);
        expect(outcome.body, `${outcome.name} must not leak the locale crash`).not.toContain(LOCALE_CRASH);
        expect(outcome.body, `${outcome.name} must not leak the server reference detail`).not.toContain(REFERENCE_ID_LEAK);
        expect(outcome.body, `${outcome.name} must not leak a stack trace`).not.toMatch(/at .*\(.*:\d+:\d+\)/);
      }
      expect(crashes, `no shape may trigger the secondary locale error: ${crashes.join(" | ")}`).toEqual([]);

      // The localized fallback is still renderable after the malformed traffic.
      await page.reload();
      await expect(page.locator("html")).toHaveAttribute("lang", lang);
      await expect(page.locator("html")).toHaveAttribute("dir", dir);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(translate(locale, "auth.loginTitle"));
      const body = await page.locator("body").innerText();
      expect(body).not.toContain(LOCALE_CRASH);
      expect(body).not.toContain(ROOT_BOUNDARY_COPY);
      expect(body, "the route must not fall into the recovery boundary").not.toContain(translate(locale, "error.title"));

      // ...and the client router still works in the same locale afterwards.
      await page.locator(".auth-switch a").click();
      await expect(page).toHaveURL(/\/register$/);
      await expect(page.locator("html")).toHaveAttribute("lang", lang);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(translate(locale, "auth.registerTitle"));

      expect(providerRequests, "no paid provider may be contacted").toEqual([]);
    });
  });
}
