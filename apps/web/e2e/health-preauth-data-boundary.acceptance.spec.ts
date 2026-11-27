import { test, expect } from "./fixtures";
/**
 * Issue #714 / PR #1055 follow-up lane: pin the *transport* half of the
 * "no unauthorized health document exposure" acceptance invariant that the
 * DOM-only assertions in `health-care-truth.acceptance.spec.ts` cannot see.
 *
 * The existing Health Care lanes all read rendered output:
 *  - `health-care.spec.ts` / `health-care-tile*.spec.ts`  copy, geometry, RTL
 *  - `health-care-truth.acceptance.spec.ts` (#1055)       tile truthfulness and
 *    `page.content()` / panel text fingerprints
 *  - `health-care-consent-interaction.spec.ts` (#812)     the consent journey
 *    source, which is still unmerged
 * A component that fetches `/api/health`, `/api/documents` or `/api/assets`, or
 * hydrates a hidden record payload without rendering it, stays invisible to all
 * of them. This lane closes that gap and nothing else:
 *
 *  1. the **initial** unauthenticated HTTP response for `/create/health` is
 *     inspected with redirects disabled, so the pre-auth boundary is the raw
 *     status/headers/body rather than a hydrated login DOM (EN/AR/KU + forged
 *     session);
 *  2. every request the browser issues during an anonymous Health Care
 *     navigation in EN, AR and KU is recorded and classified: health, document,
 *     asset, upload and record endpoints fail closed, with an explicit
 *     allow-list for the app-shell auth bootstrap;
 *  3. no user-record content and no document-capture control reaches an
 *     anonymous visitor before consent, proven against a synthetic fake record
 *     API rather than against the absence of evidence;
 *  4. a negative control mutates the same navigation with one injected health
 *     API request and one injected record payload, and the gate must flag both,
 *     so the green lane above is not vacuously green;
 *  5. the authenticated (pre-consent) Health Care surface issues no data-plane
 *     request and mounts no capture control.
 *
 * Fixtures are fake and provider-free. The record payload in this file is a
 * synthetic canary that exists only inside `page.route` fulfilments; no real
 * patient data, no upload, no provider call, no production write.
 */
async function capture(
  page: import("@playwright/test").Page,
  testInfo: import("@playwright/test").TestInfo,
  name: string,
) {
  const path = testInfo.outputPath(`${name}.png`);
  await page.screenshot({ path, fullPage: true });
  await testInfo.attach(`${name}.png`, { path, contentType: "image/png" });
}

const HEALTH_ROUTE = "/create/health";
const LOGIN_DESTINATION = "/login?next=%2Fcreate%2Fhealth";
/** `?` and `/` are regex syntax, so the browser-side match needs an escaped literal. */
const LOGIN_DESTINATION_PATTERN = /\/login\?next=%2Fcreate%2Fhealth$/;
/** The health panel only; the shared app navigation is allowed to link elsewhere. */
const healthCarePanel = (page: import("@playwright/test").Page) =>
  page.locator("[aria-labelledby='health-care-title']");
/**
 * A canary is a fingerprint, not a secret: it lets a test prove that a record
 * payload was never fetched and never rendered, even when nothing renders.
 */
const RECORD_CANARY = "SYNTHETIC-PATIENT-RECORD-7f3c9a4e";
/** The fake record API of this lane; it is fulfilled locally and never proxied. */
const syntheticRecordFixture = {
  items: [
    {
      id: "synthetic-record-0001",
      healthDocumentId: RECORD_CANARY,
      patientName: "Synthetic Patient",
      documentType: "lab-report",
      mimeType: "application/pdf",
      contentUrl: "/api/assets/synthetic-record-0001/content",
    },
  ],
  count: 1,
};
/**
 * Locales the Health Care surface ships in. Copy is taken verbatim from
 * `src/lib/healthCareI18n.ts`, because a silent string change in any locale is
 * itself a privacy-contract change: the privacy note is the claim under test.
 */
const locales = [
  {
    code: "en",
    tag: "en",
    dir: "ltr",
    acceptLanguage: "en-US,en;q=0.9",
    title: "Health Care",
    category: "Health & Wellness",
    eyebrow: "A private place for health information",
    status: "Coming soon",
    plannedHeading: "Planned, not available yet",
    actionUnavailable: "Unavailable — coming soon",
    privacyNote: "does not collect, upload, store, or analyze health information",
    features: [
      "Health documents and lab reports",
      "Medication and allergy organization",
      "Personal health timeline",
      "Questions to discuss with a clinician",
    ],
  },
  {
    code: "ar",
    tag: "ar",
    dir: "rtl",
    acceptLanguage: "ar-AE,ar;q=0.9",
    title: "الرعاية الصحية",
    category: "الصحة والعافية",
    eyebrow: "مساحة خاصة للمعلومات الصحية",
    status: "قريبًا",
    plannedHeading: "مخطط لها، وغير متاحة بعد",
    actionUnavailable: "غير متاح — قريبًا",
    privacyNote: "هذا العرض لا يجمع المعلومات الصحية أو يرفعها أو يخزنها أو يحللها",
    features: [
      "المستندات الصحية وتقارير المختبر",
      "تنظيم الأدوية والحساسيات",
      "الخط الزمني الصحي الشخصي",
      "أسئلة لمناقشتها مع مختص صحي",
    ],
  },
  {
    code: "ku",
    tag: "ckb",
    dir: "rtl",
    acceptLanguage: "ckb-IQ,ku;q=0.9",
    title: "چاودێریی تەندروستی",
    category: "تەندروستی و خۆشگوزەرانی",
    eyebrow: "شوێنێکی تایبەت بۆ زانیارییە تەندروستییەکان",
    status: "بەم زووانە",
    plannedHeading: "لە پلاندا هاتووە، بەڵام هێشتا بەردەست نییە",
    actionUnavailable: "بەردەست نییە — بەم زووانە",
    privacyNote: "ئەم پێشاندانە هیچ زانیارییەکی تەندروستی کۆ ناکاتەوە",
    features: [
      "بەڵگەنامە تەندروستییەکان و ڕاپۆرتەکانی تاقیگە",
      "ڕێکخستنی دەرمان و حەساسیەتەکان",
      "هێڵی کاتی تەندروستیی کەسی",
      "پرسیارەکان بۆ گفتوگۆ لەگەڵ پسپۆڕێکی تەندروستی",
    ],
  },
];
/**
 * A live data surface: an endpoint that could carry health documents, uploaded
 * files or records. Naming a planned capability in copy is not a violation,
 * calling the endpoint behind it before consent is.
 */
const dataPlanePath =
  /\/api\/(?:health|documents?|assets|uploads?|files|records|reports|labs?|medications|prescriptions|patients)(?:\/|$|\?|#)/i;
/** Requests an anonymous app shell is allowed to make (session + CSRF bootstrap). */
const authShellPath = /^\/api\/auth\//i;
/** Record-shaped content that must never reach an anonymous visitor. */
const recordShapedContent =
  /healthDocumentId|patient\s*(?:name|record)|medical\s*record|prescription|lab\s*report|clinical\s*note/i;

type RecordedRequest = {
  method: string;
  url: string;
  pathname: string;
  isApi: boolean;
  isDataPlane: boolean;
  isAuthShell: boolean;
};
type NetworkGate = {
  requests: () => RecordedRequest[];
  dataPlaneRequests: () => RecordedRequest[];
  unexpectedApiRequests: () => RecordedRequest[];
  stubHits: () => string[];
  attach: (testInfo: import("@playwright/test").TestInfo, name: string) => Promise<void>;
};

function classify(url: string): RecordedRequest {
  const parsed = new URL(url);
  const pathname = parsed.pathname;
  return {
    method: "recorded",
    url,
    pathname,
    isApi: pathname.includes("/api/"),
    isDataPlane: dataPlanePath.test(pathname),
    isAuthShell: authShellPath.test(pathname),
  };
}
function findRecordLeak(text: string, forbiddenCopy: readonly string[]) {
  const leaks: string[] = [];
  if (text.includes(RECORD_CANARY)) leaks.push("synthetic record canary");
  if (recordShapedContent.test(text)) leaks.push("record-shaped content");
  for (const phrase of forbiddenCopy) {
    if (phrase && text.includes(phrase)) leaks.push(`health copy: ${phrase}`);
  }
  return leaks;
}
/**
 * Records every request the page issues and, optionally, mounts the fake record
 * API. Mounting the stub is what makes "nothing was fetched" a measurement
 * instead of an assumption: if a component does reach for records, the stub
 * answers with the canary and both the request log and the content check fire.
 */
async function installNetworkGate(
  page: import("@playwright/test").Page,
  options: { stubRecordApi?: boolean } = {},
): Promise<NetworkGate> {
  const requests: RecordedRequest[] = [];
  const stubHits: string[] = [];
  page.on("request", (request: import("@playwright/test").Request) => {
    const record = classify(request.url());
    requests.push({ ...record, method: request.method() });
  });
  if (options.stubRecordApi !== false) {
    // Newest routes win in Playwright, so the broad shell catch-all is never
    // registered: only the record endpoints are intercepted, everything else
    // goes to the real (provider-disabled) stack.
    for (const pattern of [
      "**/api/health/**",
      "**/api/documents/**",
      "**/api/assets/**",
      "**/api/uploads/**",
      "**/api/records/**",
    ]) {
      await page.route(pattern, async (route) => {
        stubHits.push(`${route.request().method()} ${new URL(route.request().url()).pathname}`);
        await route.fulfill({
          status: 200,
          contentType: "application/json",
          body: JSON.stringify(syntheticRecordFixture),
        });
      });
    }
  }
  return {
    requests: () => requests,
    dataPlaneRequests: () => requests.filter((entry) => entry.isDataPlane),
    unexpectedApiRequests: () =>
      requests.filter((entry) => entry.isApi && !entry.isAuthShell && !entry.isDataPlane),
    stubHits: () => stubHits,
    attach: async (testInfo, name) => {
      await testInfo.attach(`${name}.requests.json`, {
        body: JSON.stringify({ recorded: requests, stubHits }, null, 2),
        contentType: "application/json",
      });
    },
  };
}
async function anonymousLocalePage(
  browser: import("@playwright/test").Browser,
  locale: (typeof locales)[number],
) {
  const context = await browser.newContext({
    locale: locale.tag,
    // A clean jar: no session cookie, so the lane really is unauthenticated.
    storageState: { cookies: [], origins: [] },
    viewport: { width: 1280, height: 900 },
  });
  // The locale is a client-side choice; seed it before any document loads so
  // the AR and KU routes are exercised rather than the default English shell.
  await context.addInitScript((value) => {
    window.localStorage.setItem("taslim-locale", value);
  }, locale.code);
  return { context, page: await context.newPage() };
}
const allLocalizedHealthCopy = locales.flatMap((locale) => [
  locale.title,
  locale.category,
  locale.eyebrow,
  locale.status,
  locale.plannedHeading,
  locale.actionUnavailable,
  locale.privacyNote,
  ...locale.features,
]);
test.describe("Health Care pre-consent data boundary (#714)", () => {
  test("serves only the login redirect in the initial HTTP response, in EN/AR/KU", async ({ page }, testInfo) => {
    const summary: string[] = [];
    for (const locale of locales) {
      const response = await page.request.get(HEALTH_ROUTE, {
        maxRedirects: 0,
        failOnStatusCode: false,
        headers: { "Accept-Language": locale.acceptLanguage },
      });
      const status = response.status();
      const body = await response.text();
      summary.push(`${locale.code}: ${status} ${response.headers()["location"] ?? "-"}`);
      // The boundary is the raw response, so the gate is asserted on the wire:
      // a 200 health document would fail here even if it redirected client-side.
      expect(status, `${locale.code}: unauthenticated ${HEALTH_ROUTE} is a redirect, not a document`).toBe(307);
      expect(response.headers()["location"], `${locale.code}: redirect target`).toBe(LOGIN_DESTINATION);
      expect(body, `${locale.code}: the redirect body is not an HTML health document`)
        .not.toMatch(/<h1\b[^>]*>\s*health\b/i);
      const leaks = findRecordLeak(body, allLocalizedHealthCopy);
      expect(leaks, `${locale.code}: initial response leaks ${leaks.join(", ") || "nothing"}`).toEqual([]);
      expect(body, `${locale.code}: initial response advertises no data-plane endpoint`)
        .not.toMatch(dataPlanePath);
      await response.dispose();
    }
    // A forged session must not widen the boundary either.
    await page.goto("/login");
    await page.context().addCookies([
      { name: "taslim.auth", value: "forged-cookie-not-a-session", url: new URL(page.url()).origin },
    ]);
    const forged = await page.request.get(HEALTH_ROUTE, { maxRedirects: 0, failOnStatusCode: false });
    summary.push(`forged: ${forged.status()}`);
    expect(forged.status(), "a forged session cookie does not unlock the health document").toBe(307);
    const forgedBody = await forged.text();
    expect(findRecordLeak(forgedBody, allLocalizedHealthCopy), "forged session leaks health content").toEqual([]);
    await forged.dispose();
    await testInfo.attach("initial-response-boundary.txt", {
      body: summary.join("\n"),
      contentType: "text/plain",
    });
  });
  test("records no health, document or asset request during an anonymous navigation, in EN/AR/KU", async ({ browser }, testInfo) => {
    for (const locale of locales) {
      const { context, page } = await anonymousLocalePage(browser, locale);
      const gate = await installNetworkGate(page);
      await page.goto(HEALTH_ROUTE);
      await expect(page, `${locale.code}: anonymous entry lands on login`).toHaveURL(
        LOGIN_DESTINATION_PATTERN,
      );
      // Prove the locale is actually applied, so the three checks below are
      // three routes and not one route measured three times.
      await expect(page.locator("html"), `${locale.code}: html lang`).toHaveAttribute("lang", locale.tag);
      await expect(page.locator("html"), `${locale.code}: html dir`).toHaveAttribute("dir", locale.dir);
      // Gate 1 (transport): no data-plane request was issued, and every other
      // API call is confined to the auth bootstrap allow-list.
      const violations = gate.dataPlaneRequests();
      expect(violations, `${locale.code}: data-plane requests ${JSON.stringify(violations)}`).toEqual([]);
      const outsideAllowList = gate.unexpectedApiRequests();
      expect(outsideAllowList, `${locale.code}: non-shell API requests ${JSON.stringify(outsideAllowList)}`)
        .toEqual([]);
      expect(gate.stubHits(), `${locale.code}: the fake record API was called`).toEqual([]);
      // Gate 2 (content): neither the served document nor the hydrated DOM
      // carries health copy or record content in any locale.
      const served = await page.content();
      expect(findRecordLeak(served, allLocalizedHealthCopy), `${locale.code}: served HTML leaks health content`)
        .toEqual([]);
      const rendered = await page.locator("body").innerText();
      expect(findRecordLeak(rendered, allLocalizedHealthCopy), `${locale.code}: rendered DOM leaks health content`)
        .toEqual([]);
      // Gate 3 (capture): nothing can take a document in before consent.
      await expect(page.locator("input[type='file']"), `${locale.code}: no file capture control`).toHaveCount(0);
      await expect(healthCarePanel(page), `${locale.code}: no health panel mounted`).toHaveCount(0);
      await capture(page, testInfo, `anonymous-${locale.code}-health-entry-boundary`);
      await gate.attach(testInfo, `anonymous-${locale.code}`);
      await context.close();
    }
  });
  test("keeps the login bootstrap observable and fails closed on an injected health request", async ({ browser }, testInfo) => {
    const { context, page } = await anonymousLocalePage(browser, locales[0]);
    const gate = await installNetworkGate(page);
    await page.goto(HEALTH_ROUTE);
    await expect(page).toHaveURL(LOGIN_DESTINATION_PATTERN);
    const baseline = gate.dataPlaneRequests();
    expect(baseline, "the pristine lane issues no data-plane request").toEqual([]);
    // Specificity of the classifiers, asserted on synthetic traffic so the
    // mutation below cannot pass by matching nothing at all.
    const allowed = [
      "http://localhost:3000/api/auth/me",
      "http://localhost:3000/api/auth/csrf",
      "http://localhost:3000/_next/static/css/app.css",
      "http://localhost:3000/login?next=%2Fcreate%2Fhealth",
    ];
    expect(allowed.filter((url) => classify(url).isDataPlane), "allow-list misclassified as data plane").toEqual([]);
    const flagged = [
      "http://localhost:3000/api/health/documents",
      "http://localhost:3000/api/documents/synthetic/content",
      "http://localhost:3000/api/assets/synthetic-record-0001/content",
      "http://localhost:5000/api/uploads/health",
      "http://localhost:5000/api/patients/synthetic/records",
    ];
    expect(flagged.filter((url) => classify(url).isDataPlane).length, "a data-plane URL escaped the gate")
      .toBe(flagged.length);
    expect(flagged.filter((url) => classify(url).isAuthShell), "a data-plane URL was excused as app shell")
      .toEqual([]);
    // Mutation 1: one unwanted health API request inside the real navigation.
    const payload = await page.evaluate(async () => {
      const response = await fetch("/api/health/documents");
      return await response.text();
    });
    expect(payload, "the fake record API answered locally").toContain(RECORD_CANARY);
    const afterMutation = gate.dataPlaneRequests();
    expect(afterMutation, "the gate did not fail closed on an injected health request").not.toEqual([]);
    expect(afterMutation[0].pathname, "the flagged request is the injected one").toBe("/api/health/documents");
    expect(gate.stubHits().length, "the fake record API recorded the injection").toBeGreaterThan(0);
    // Mutation 2: the same payload rendered into the DOM must trip the content
    // check, which is what makes the green lanes above evidence rather than luck.
    const injectedLeaks = findRecordLeak(payload, allLocalizedHealthCopy);
    expect(injectedLeaks, "a rendered record payload is invisible to the content gate").not.toEqual([]);
    await page.evaluate((canary) => {
      const marker = document.createElement("div");
      marker.id = "synthetic-leak-probe";
      marker.textContent = canary;
      document.body.appendChild(marker);
    }, RECORD_CANARY);
    expect(findRecordLeak(await page.locator("body").innerText(), allLocalizedHealthCopy))
      .toContain("synthetic record canary");
    await page.evaluate(() => document.getElementById("synthetic-leak-probe")?.remove());
    expect(findRecordLeak(await page.locator("body").innerText(), allLocalizedHealthCopy), "probe left residue")
      .toEqual([]);
    await capture(page, testInfo, "negative-control-injected-health-request");
    await gate.attach(testInfo, "negative-control");
    await context.close();
  });
  test("issues no data-plane request and mounts no capture control on the authenticated pre-consent surface", async ({ authenticatedPage }, testInfo) => {
    const page = authenticatedPage;
    // Installed after registration so only Health Care navigation is measured.
    const gate = await installNetworkGate(page, { stubRecordApi: false });
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(HEALTH_ROUTE);
    await expect(page.getByRole("heading", { name: locales[0].title, level: 1 })).toBeVisible();
    const panel = healthCarePanel(page);
    await expect(panel).toBeVisible();
    await expect(panel).toContainText(locales[0].privacyNote);
    // Let any deferred hydration or polling settle before closing the ledger.
    await page.waitForLoadState("load");
    await page.waitForTimeout(2_000);
    const violations = gate.dataPlaneRequests();
    expect(violations, `health surface fetched ${JSON.stringify(violations)}`).toEqual([]);
    // Nothing captures or retains a document on this surface before consent:
    // no file control anywhere and no health-shaped entry in client storage.
    expect(await page.locator("[type='file']").count(), "no document capture control").toBe(0);
    const retained = await page.evaluate((pattern) => {
      const hit = (storage: Storage) =>
        Object.keys(storage)
          .filter((key) => new RegExp(pattern, "i").test(key) || new RegExp(pattern, "i").test(storage.getItem(key) ?? ""))
          .map((key) => `${key}=${storage.getItem(key)?.slice(0, 40) ?? ""}`);
      return { local: hit(window.localStorage), session: hit(window.sessionStorage) };
    }, "healthDocument|patientRecord|prescription|labReport|health-document|health_upload");
    expect(retained, `health payload retained in client storage: ${JSON.stringify(retained)}`).toEqual({
      local: [],
      session: [],
    });
    await capture(page, testInfo, "authenticated-preconsent-no-data-plane");
    await gate.attach(testInfo, "authenticated-preconsent");
  });
});
