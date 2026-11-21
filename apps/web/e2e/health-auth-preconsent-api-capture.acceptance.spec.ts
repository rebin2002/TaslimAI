import { test, expect } from "./fixtures";
import type { Page, Request, TestInfo } from "@playwright/test";
/**
 * Privacy acceptance for the REAL authenticated Health Care surface: deny-by-default `/api/*`
 * capture, measured on the actually-mounted `/create/health` navigation, per shipped locale.
 *
 * The gap this closes, in the #714 release-gate chain #1055 -> #1070 -> #1093:
 *
 *   - #1070 `health-preauth-data-boundary.acceptance.spec.ts` does drive the real route while
 *     authenticated, but lane 4 asserts only `gate.dataPlaneRequests()`, whose classifier is the
 *     enumerated prefix list `/api/(health|documents|assets|uploads|files|records|reports|labs|
 *     medications|prescriptions|patients)`. A component reaching a record through `/api/search?
 *     kind=health`, `/api/generation/jobs/{id}` or `/api/graphql` keeps it green; the
 *     `unexpectedApiRequests()` it already computes is never asserted on that surface, only one
 *     locale is measured, and nothing is refused - the lane records and reports.
 *   - #1093 `health-preconsent-shell-allowlist.acceptance.spec.ts` does invert the verdict to an
 *     allow-list, but its own body says lanes 3-4 are pinned "against a synthetic shell" whose
 *     requests are all fulfilled by `page.route`, with "no backend, no login", and that it "is
 *     not privacy acceptance".
 *
 * So the invariant is currently asserted either on the real route with a known-bad list, or
 * deny-by-default against a fabricated shell - never both. This lane is the intersection: a real
 * Next.js server, the real mounted route, a real authenticated session, the allow-list applied
 * as an actual network refusal, and one measurement per locale.
 *
 * Lanes:
 *   1. policy: the allow-list is complete, query-blind, a strict superset of #1070's rule, and
 *      carries an executable statement of the blind spot it closes;
 *   2. en, 3. ar, 4. ckb: one real authenticated pre-consent navigation each - zero non-allow-listed
 *      `/api/*` requests reached the wire, the guard refused nothing, only the two configured
 *      origins were contacted, and no record material reached the DOM, client storage or a body;
 *   5. negative control in the same real navigation: the three generic endpoint families are
 *      injected from inside the page and must be denied *and* flagged *and* be invisible to
 *      #1070's rule, while the canary-driven scanner is shown to fire on a DOM write.
 *
 * Fixtures are disposable and synthetic. Locally this runs against a throwaway session/app
 * fixture that holds one fake user and serves no records; in CI the Browser E2E job supplies the
 * provider-disabled stack. No real health data, no production network, no paid provider, no
 * credential, no migration, no merge.
 */
const HEALTH_ROUTE = "/create/health";
/** The two origins a browser session may ever speak to. Anything else is an exfiltration. */
const API_ORIGIN = new URL(
  process.env.E2E_API_URL ?? process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000",
).origin;
/** Exists only in this file's injections; it must never be observable anywhere else. */
const RECORD_CANARY = "SYNTHETIC-PATIENT-RECORD-CANARY-9ff3c1";
/**
 * #1070's classifier, copied verbatim from `health-preauth-data-boundary.acceptance.spec.ts` at
 * head `1ce8011ebffb447e97860d80fe4a4d084ae4809c` (line 143) for cross-checking only. Nothing this
 * lane allows may that rule flag: the lane stays a superset rather than a replacement.
 */
const PR_1070_DATA_PLANE_PATH =
  /\/api\/(?:health|documents?|assets|uploads?|files|records|reports|labs?|medications|prescriptions|patients)(?:\/|$|\?|#)/i;
/** Generic families that can carry a record without naming one in the path. */
const GENERIC_RECORD_BEARING_PATHS = ["/api/search?kind=health", "/api/generation/jobs/synthetic-job-1", "/api/graphql"] as const;
/**
 * Payload markers. Deliberately narrower than #1070's copy scan: this surface legitimately
 * *names* planned capabilities ("Health documents and lab reports"), which is copy, not a record.
 * What must never appear is record material - identifiers, values, or this suite's canary.
 */
const RECORD_PAYLOAD_MARKERS = /healthDocumentId|patientRecord|prescriptionId|labResult|SYNTHETIC-PATIENT/i;
/**
 * The app-shell allow-list, keyed to the code that actually speaks on this route: `lib/api.ts`
 * `csrf()` and `api.me()` (AuthProvider bootstrap, plus the server-side probe in `serverAuth.ts`),
 * and `ActivityBell`'s `getNotificationUnreadCount()` poll mounted by `AppShell`. Everything else
 * - `/api/search`, `/api/graphql`, `/api/generation/*`, `/api/projects/*`, `/api/workspaces/{id}/…`
 * and every data-plane family - is absent, so granting it is a reviewable edit to this list.
 */
const SHELL_API_PATHS: readonly RegExp[] = [
  /^\/api\/auth\/(?:csrf|login|logout|me|onboarding\/complete|password-policy|profile|sessions|sessions\/revoke)$/i,
  /^\/api\/workspaces$/i,
  /^\/api\/notifications\/unread-count$/i,
  /^\/api\/activity\/unread-count$/i,
];
/** Copy read verbatim from `src/lib/healthCareI18n.ts`; each locale is its own measurement. */
const locales = [
  {
    code: "en",
    select: "en",
    tag: "en",
    dir: "ltr",
    title: "Health Care",
    privacyNote:
      "This preview does not collect, upload, store, or analyze health information and sends no health information to external services.",
  },
  {
    code: "ar",
    select: "ar",
    tag: "ar",
    dir: "rtl",
    title: "الرعاية الصحية",
    privacyNote:
      "هذا العرض لا يجمع المعلومات الصحية أو يرفعها أو يخزنها أو يحللها، ولا يرسلها إلى خدمات خارجية.",
  },
  {
    code: "ckb",
    select: "ku",
    tag: "ckb",
    dir: "rtl",
    title: "چاودێریی تەندروستی",
    privacyNote:
      "ئەم پێشاندانە هیچ زانیارییەکی تەندروستی کۆ ناکاتەوە، بار ناکات، هەڵی ناگرێت یان شیکاری ناکات، و هیچ زانیارییەکی تەندروستی نانێرێت بۆ خزمەتگوزارییە دەرەکییەکان.",
  },
] as const;

type Capture = {
  method: string;
  url: string;
  origin: string;
  pathname: string;
  search: string;
  isApi: boolean;
  appShell: boolean;
  enumeratedByPr1070: boolean;
  bodyBytes: number;
  recordBearingBody: boolean;
};
type Guard = {
  captured: () => Capture[];
  refused: () => Capture[];
  offAllowList: () => Capture[];
  outsideConfiguredOrigins: () => Capture[];
  leaks: (page: Page) => Promise<string[]>;
  assertNoLeak: (page: Page, testInfo: TestInfo, label: string) => Promise<void>;
};

function classify(url: string, method = "GET", body: string | null = null): Capture {
  const parsed = new URL(url);
  const pathname = parsed.pathname;
  const isApi = /^\/api\//i.test(pathname);
  return {
    method,
    url,
    origin: parsed.origin,
    pathname,
    search: parsed.search,
    isApi,
    appShell: isApi && SHELL_API_PATHS.some((pattern) => pattern.test(pathname)),
    enumeratedByPr1070: PR_1070_DATA_PLANE_PATH.test(pathname),
    bodyBytes: body ? body.length : 0,
    recordBearingBody: body ? RECORD_PAYLOAD_MARKERS.test(body) : false,
  };
}
function shape(entry: Capture) {
  return `${entry.method} ${entry.pathname}${entry.search}`;
}
/** Failure text: name the request, keep the query, and say whether #1070 could have seen it. */
function verdict(entries: readonly Capture[]) {
  return JSON.stringify(
    entries.map((entry) => ({
      request: shape(entry),
      appShell: entry.appShell,
      enumeratedByPr1070: entry.enumeratedByPr1070,
      bodyBytes: entry.bodyBytes,
      recordBearingBody: entry.recordBearingBody,
    })),
  );
}
async function bodyOf(request: Request): Promise<string | null> {
  try {
    return request.postData();
  } catch {
    // A binary body is a capture control; the DOM and storage scans cover it.
    return null;
  }
}
/**
 * Records all traffic and refuses `/api/*` outside the allow-list. `page.route` is used only as
 * the refusal mechanism: the document, its server-side auth probe, its hydration and its own
 * bootstrap requests all come from the real mounted route - the difference from #1093.
 */
async function installShellGuard(page: Page): Promise<Guard> {
  const captured: Capture[] = [];
  const refused: Capture[] = [];
  const webOrigin = new URL(page.url()).origin;
  page.on("request", (request) => {
    void bodyOf(request).then((body) => captured.push(classify(request.url(), request.method(), body)));
  });
  await page.route("**/api/**", async (route) => {
    const request = route.request();
    const entry = classify(request.url(), request.method(), await bodyOf(request));
    // Out of scope stays untouched: chunks, fonts and HMR are not `/api/*`.
    if (!entry.isApi || entry.appShell) return route.continue();
    refused.push(entry);
    await route.abort("failed");
  });
  const leaks = async (innerPage: Page) => {
    const found: string[] = [];
    const surfaces: readonly (readonly [string, string])[] = [
      ["rendered DOM", await innerPage.evaluate(() => document.body.innerText)],
      ["served HTML", await innerPage.content()],
    ];
    for (const [surface, text] of surfaces) {
      if (text.includes(RECORD_CANARY)) found.push(`${surface}: synthetic record canary`);
      if (RECORD_PAYLOAD_MARKERS.test(text)) found.push(`${surface}: record-shaped payload marker`);
    }
    // Only traffic that was permitted out can leak. A refused request never reached the network,
    // so its body is this lane's own injection; `offAllowList()` and `refused()` are where that
    // is asserted, and counting it as a leak would make the scanner fire on its own controls.
    const permitted = captured.filter((entry) => !refused.some((denial) => shape(denial) === shape(entry)));
    for (const entry of permitted) {
      if (entry.recordBearingBody || entry.url.includes(RECORD_CANARY)) found.push(`request: ${shape(entry)}`);
    }
    const retained = await innerPage.evaluate((pattern) => {
      const hit = (storage: Storage) =>
        Object.keys(storage)
          .filter((key) => new RegExp(pattern, "i").test(key) || new RegExp(pattern, "i").test(storage.getItem(key) ?? ""))
          .map((key) => `${key}=${(storage.getItem(key) ?? "").slice(0, 40)}`);
      return { local: hit(window.localStorage), session: hit(window.sessionStorage) };
    }, "healthDocument|patientRecord|prescription|labResult|SYNTHETIC-PATIENT|health_upload");
    if (retained.local.length || retained.session.length) found.push(`client storage: ${JSON.stringify(retained)}`);
    return found;
  };
  return {
    captured: () => captured,
    refused: () => refused,
    leaks,
    offAllowList: () => captured.filter((entry) => entry.isApi && !entry.appShell),
    outsideConfiguredOrigins: () => captured.filter((entry) => entry.origin !== webOrigin && entry.origin !== API_ORIGIN),
    assertNoLeak: async (innerPage, testInfo, label) => {
      const found = await leaks(innerPage);
      expect(found, `${label}: record material reached a surface that outlives the request: ${found.join("; ")}`).toEqual([]);
      await testInfo.attach(`${label}.capture.json`, {
        body: JSON.stringify({ webOrigin, apiOrigin: API_ORIGIN, captured, refused }, null, 2),
        contentType: "application/json",
      });
    },
  };
}
/** The user's stored choice, driven through the real account selector rather than a device key. */
async function chooseLocale(page: Page, locale: (typeof locales)[number]) {
  await page.goto("/account");
  const select = page.locator(".account-control-grid select").first();
  await expect(select).toBeVisible();
  await select.selectOption(locale.select);
  await page.locator("form.account-profile-card button.primary-button").click();
  await expect(page.locator("html")).toHaveAttribute("lang", locale.tag);
}
/** The settled pre-consent surface: informational copy, nothing that could start a capture. */
async function expectPreConsentSurface(page: Page, locale: (typeof locales)[number]) {
  const panel = page.locator('section[aria-labelledby="health-care-title"]');
  await expect(panel, `${locale.code}: the Health Care surface did not mount`).toBeVisible();
  await expect(page.getByRole("heading", { name: locale.title, level: 1 })).toBeVisible();
  await expect(panel).toContainText(locale.privacyNote);
  await expect(page.locator("html")).toHaveAttribute("lang", locale.tag);
  await expect(page.locator("html")).toHaveAttribute("dir", locale.dir);
  // Before consent nothing is collected: no form, no field, no file control anywhere.
  await expect(page.locator("form, input, textarea, [type='file']")).toHaveCount(0);
  // The planned capabilities are inert: every action is a disabled button, and none of them is
  // a link or a live control that could begin a capture. (The surface's own "Back to Create"
  // navigation sits outside this section and is not an affordance, so it is not counted here.)
  const planned = panel.locator('section[aria-labelledby="health-care-planned-title"]');
  await expect(planned.locator("button[disabled]"), `${locale.code}: a planned action is enabled`).toHaveCount(4);
  await expect(planned.locator("button:not([disabled]), a[href], input, textarea")).toHaveCount(0);
}
test.describe("Health Care authenticated route: deny-by-default API capture on the real mount (#714)", () => {
  test("allow-list is complete, query-blind and a strict superset of #1070's enumerated rule", () => {
    const shell = [
      "http://localhost:5000/api/auth/me",
      "http://localhost:5000/api/auth/csrf",
      "http://localhost:5000/api/auth/password-policy",
      "http://localhost:5000/api/workspaces",
      "http://localhost:5000/api/notifications/unread-count?workspaceId=synthetic-ws-1",
      "http://localhost:3000/api/auth/me",
    ];
    expect(
      shell.filter((url) => !classify(url).appShell),
      "app-shell traffic is not allow-listed, so every real navigation here would be RED",
    ).toEqual([]);
    for (const url of shell.filter((entry) => !entry.includes("?"))) {
      expect(classify(`${url}?kind=health`).appShell, `${url}: a query string changed the verdict`).toBe(true);
    }
    // The gap statement, executable. These are the families #1070's review named and its
    // classifier cannot see; this policy refuses all three.
    for (const path of GENERIC_RECORD_BEARING_PATHS) {
      const entry = classify(`http://localhost:5000${path}`);
      expect(entry.enumeratedByPr1070, `${path}: #1070 already flags this, so it is not the gap`).toBe(false);
      expect(entry.appShell, `${path}: a generic endpoint is not app shell`).toBe(false);
    }
    // Superset direction: everything #1070 does flag stays refused here.
    for (const path of ["/api/health/documents", "/api/documents", "/api/assets/a1", "/api/patients/me"]) {
      expect(classify(`http://localhost:5000${path}`).enumeratedByPr1070, `${path}: #1070's rule`).toBe(true);
      expect(classify(`http://localhost:5000${path}`).appShell, `${path}: excused as shell`).toBe(false);
    }
    // Out of scope is reported, never silently allowed.
    expect(classify("https://telemetry.example.test/v1/collect").appShell, "third-party traffic is not shell").toBe(false);
    expect(classify("http://localhost:3000/_next/static/chunks/app.js").isApi, "a chunk is out of scope").toBe(false);
  });
  for (const locale of locales) {
    test(`${locale.code}: the real authenticated pre-consent navigation stays inside the allow-list`, async ({ authenticatedPage }, testInfo) => {
      const page = authenticatedPage;
      await chooseLocale(page, locale);
      // Installed after the locale write so only the Health Care navigation is measured.
      const guard = await installShellGuard(page);
      await page.goto(HEALTH_ROUTE);
      await expectPreConsentSurface(page, locale);
      // Let deferred hydration and the notification poll settle before closing the ledger.
      await page.waitForLoadState("load");
      await page.waitForTimeout(2_000);
      const captured = guard.captured();
      // Non-vacuity: a green lane must have watched the real bootstrap actually happen.
      expect(
        captured.filter((entry) => entry.appShell).map(shape),
        `${locale.code}: the real route issued no allow-listed traffic, so nothing was measured`,
      ).toContain("GET /api/auth/me");
      const offAllowList = guard.offAllowList();
      expect(offAllowList, `${locale.code}: non-allow-listed API traffic on ${HEALTH_ROUTE}: ${verdict(offAllowList)}`).toEqual([]);
      const refused = guard.refused();
      expect(refused, `${locale.code}: the guard had to refuse traffic on ${HEALTH_ROUTE}: ${verdict(refused)}`).toEqual([]);
      const outside = guard.outsideConfiguredOrigins();
      expect(outside, `${locale.code}: traffic left the configured origins: ${verdict(outside)}`).toEqual([]);
      expect(
        captured.filter((entry) => entry.recordBearingBody || entry.url.includes(RECORD_CANARY)),
        `${locale.code}: record material was sent in a request`,
      ).toEqual([]);
      await guard.assertNoLeak(page, testInfo, `${locale.code}-preconsent`);
    });
  }
  test("negative control: generic endpoint families are denied and flagged in the same real navigation", async ({ authenticatedPage }, testInfo) => {
    const page = authenticatedPage;
    const guard = await installShellGuard(page);
    await page.goto(HEALTH_ROUTE);
    await expectPreConsentSurface(page, locales[0]);
    expect(guard.offAllowList(), "the pristine navigation was already RED").toEqual([]);
    // Inject from inside the real page, in the shape a future component would use: a generic
    // search, a generation job read, and a GraphQL query that asks for a health record.
    const attempts = await page.evaluate(async (targets) => {
      const out: { target: string; outcome: string }[] = [];
      for (const target of targets) {
        const isGraph = target === "/api/graphql";
        try {
          const response = await fetch(target, {
            method: isGraph ? "POST" : "GET",
            credentials: "include",
            ...(isGraph
              ? { headers: { "Content-Type": "application/json" }, body: JSON.stringify({ query: "{ healthDocumentId }" }) }
              : {}),
          });
          out.push({ target, outcome: `answered ${response.status}` });
        } catch {
          out.push({ target, outcome: "refused" });
        }
      }
      return out;
    }, [...GENERIC_RECORD_BEARING_PATHS]);
    await page.waitForTimeout(500);
    const flagged = guard.offAllowList();
    const flaggedText = verdict(flagged);
    expect(flagged.map(shape), `${flaggedText}: an injected family was not flagged`).toEqual(
      expect.arrayContaining(["GET /api/search?kind=health", "GET /api/generation/jobs/synthetic-job-1", "POST /api/graphql"]),
    );
    expect(
      flagged.filter((entry) => !entry.enumeratedByPr1070).length,
      `${flaggedText}: every flagged request is one #1070 could already see, so this proves nothing`,
    ).toBeGreaterThanOrEqual(GENERIC_RECORD_BEARING_PATHS.length);
    expect(attempts.every((attempt) => attempt.outcome === "refused"), `${flaggedText}: a generic request was answered instead of refused`).toBe(true);
    // Refusal must not have cost the shell its own bootstrap, nor broken the surface.
    expect(
      guard.captured().filter((entry) => entry.appShell).map(shape),
      "the allow-listed bootstrap stopped after the guard fired",
    ).toContain("GET /api/auth/me");
    await expect(page.getByRole("heading", { name: locales[0].title, level: 1 })).toBeVisible();
    await guard.assertNoLeak(page, testInfo, "negative-control-denied");
    // The scanner itself is specific: it fires on a payload it was not told about, then clears.
    await page.evaluate((canary) => {
      const marker = document.createElement("div");
      marker.id = "synthetic-leak-probe";
      marker.textContent = `${canary} healthDocumentId=abc prescriptionId=def`;
      document.body.appendChild(marker);
    }, RECORD_CANARY);
    expect(await guard.leaks(page), "the leak scanner is decorative").toEqual(
      expect.arrayContaining(["rendered DOM: synthetic record canary", "rendered DOM: record-shaped payload marker"]),
    );
    await page.evaluate(() => document.getElementById("synthetic-leak-probe")?.remove());
    await guard.assertNoLeak(page, testInfo, "negative-control-scanner");
  });
});
