// Test-only browser regression for issue #700, section A: the blocking first-login onboarding
// guided flow measured at 200% browser zoom in English LTR plus Arabic and Kurdish Sorani RTL.
//
// WHY THIS FILE EXISTS AND WHY IT IS DISJOINT
// The #700 lane is already partitioned and no existing claim exercises first-login onboarding
// under a raised zoom factor:
//   - issue-700-onboarding-billing-layout.spec.ts (#846, the owner of OnboardingGate.tsx,
//     onboarding-billing-ui.css and useDialogAccessibility.ts): the onboarding dialog at 100% zoom
//     across 1440/768/375/320 widths, RTL direction, Tab/Shift+Tab wrap-around, Escape containment,
//     opener focus restoration, axe, and modal inner scroll at 375x520. Its only zoom measurement is
//     "keeps the billing layout within the viewport at 125% zoom", which is section B.
//   - billing-visual-acceptance.spec.ts (merged in #732), billing-tablet-navigation-overflow.spec.ts
//     (merged in #816), billing-plan-change-disabled-actions.spec.ts (#782),
//     billing-plan-credits-zoom-legibility.spec.ts (#790) and billing-narrow-zoom-200-reflow.spec.ts
//     (#1062): section B (Account -> Plan & credits) only. #1062 says so in its own header -- "the
//     first-login onboarding surfaces belong to #846" -- and never enters the gate.
//   - onboarding.test.ts and onboardingGateZhPreviewContract.test.ts (merged): workflow routing,
//     visibility rules and language-pack copy. No rendered geometry.
//   - auth.spec.ts (merged): one happy-path registration plus guided completion at the default
//     desktop viewport. No zoom, no RTL, no geometry.
// Issue #700 lists zoom among the non-negotiable acceptance gates for section A ("Handle mobile
// (320/375/768px), desktop (1280/1920px), height-constrained, zoom and English/Arabic/Kurdish
// Sorani RTL gracefully"). For the onboarding dialog that gate is still unmeasured.
//
// WHY 200% ZOOM IS ITS OWN STATE AND NOT JUST A SMALL WINDOW
// Raising zoom halves BOTH axes, so 1280x900 presents a 640x450 CSS px layout viewport and a
// 768x1024 tablet presents 384x512. #846 never measured the onboarding dialog at any zoom factor:
// its sweeps are all 100% (1440x900, 768x900, 375x812, 320x640), and its one short-viewport check is
// a single English 375x520 scroll-to-bottom assertion. So the state pinned here is new in three
// respects at once: it is reached through zoom rather than a resized window, it holds for Arabic and
// Kurdish Sorani as well as English, and it puts keyboard focus in motion while
// `max-height: calc(100dvh - 16px)` has forced the dialog into inner scroll. A dialog that scrolls
// internally can strand a focused control outside its scroll box, which is the acceptance property
// pinned here, and #846's Tab walk is performed at 1440x900 where nothing scrolls.
//
// Zoom is expressed as the CSS viewport size, device width divided by the zoom factor, rather than
// through `style.zoom`, following #1062's reasoning: the root zoom property leaves media queries and
// `documentElement.scrollWidth` unscaled, so assertions made through it can pass vacuously.
//
// THE THIRD CASE IS A DELIBERATE, SCOPED HANDOFF
// A 320 CSS px phone raised to 200% zoom presents a 160 CSS px layout viewport, below both WCAG 2.1
// SC 1.4.10's 320-CSS-pixel reference viewport and the product's own documented floor
// (`html { min-width: 320px }` in apps/web/src/app/globals.css). This spec does NOT demand that the
// dialog reflow into 160px. It pins the two properties that are still wrong *inside* that state
// regardless of the floor, measured against the 320px minimum canvas the same document enforces: the
// dialog is sized off the 160px visual viewport instead of that floor, and the workflow tiles that
// result grow taller than the viewport, so a focused tile can never be revealed. Both findings use
// `expect.soft`, so one run reports the whole set and the owner sees the exact measured numbers.
//
// SCOPE GUARD-RAILS (issue #700 acceptance gates)
// - Test-only. No production source file is touched and no path owned by #846 is claimed.
// - Hosted on `/projects`, so no billing route is mounted and no plan, price, allowance, credit
//   balance or upgrade-enablement value is read, shaped or derived. The traffic census asserts that
//   no billing, charging or provider endpoint is ever contacted.
// - Synthetic identities only: an @example.invalid address and fixed UUIDv4 fixture ids. No real
//   account, customer, database, migration, provider call, credential or secret is reachable. Every
//   `/api/` request is fulfilled locally, any non-GET request is refused with 405 and recorded, any
//   unlisted path is answered with 404 and recorded, and anything leaving the app origin is aborted.
// - The flow is only walked forward with the keyboard. No workflow choice is activated and Skip is
//   never pressed, so no onboarding completion is ever submitted and nothing is charged.
// - Step changes use `focus()` plus `Enter`, never pointer clicks, so the dev-only Next.js overlay
//   cannot influence the result and no dev artifact has to be hidden or patched.
import { test, expect, type Browser, type Page } from "@playwright/test";

const APP_ORIGIN = new URL(process.env.E2E_WEB_URL ?? "http://localhost:3000").origin;
const CONFIGURED_API_ORIGIN = new URL(process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000").origin;

const ZOOM = 2;
/** The product's documented minimum canvas: `html { min-width: 320px }` in globals.css. */
const MINIMUM_CANVAS_CSS = 320;
/** WCAG 2.5.8 minimum target size, in CSS pixels. */
const MIN_TARGET_CSS = 24;

const WORKSPACE_ID = "8d3c1f60-52b7-4a19-9e4d-6b0f2c8a5317";
const USER_ID = "2b7f9c41-ae36-4d08-95a7-3f1c6d0b4e82";

type LocaleCase = { code: "en" | "ar" | "ku"; label: string; dir: "ltr" | "rtl" };
const LOCALES: readonly LocaleCase[] = [
  { code: "en", label: "English", dir: "ltr" },
  { code: "ar", label: "Arabic", dir: "rtl" },
  { code: "ku", label: "Kurdish Sorani", dir: "rtl" },
];

/** Zoomed states whose layout viewport still sits at or above the documented 320px canvas. */
const AT_OR_ABOVE_FLOOR = [
  { name: "desktop 1280x900 at 200%", deviceWidth: 1280, deviceHeight: 900 },
  { name: "tablet 768x1024 at 200%", deviceWidth: 768, deviceHeight: 1024 },
] as const;

/** A 320 CSS px phone at 200% zoom: a 160 CSS px layout viewport, below the documented floor. */
const SUB_FLOOR = { name: "phone 320x640 at 200%", deviceWidth: 320, deviceHeight: 640 } as const;

type Traffic = {
  apiCalls: Array<{ method: string; path: string }>;
  refusedMethods: string[];
  unmockedPaths: string[];
  externalRequests: string[];
  nativeDialogs: string[];
};

function firstLoginSession(locale: LocaleCase) {
  return {
    user: {
      id: USER_ID,
      email: "first-login-700-zoom@example.invalid",
      displayName: "First Login 700 Fixture",
      // The gate initialises its preferences from the hydrated account rather than from a transient
      // app locale (pinned by onboarding.test.ts), so the account field is the supported way to
      // present the dialog in Arabic or Kurdish Sorani.
      preferredLanguage: locale.code,
      personalWorkspaceId: WORKSPACE_ID,
      createdAt: "2026-11-01T00:00:00.000Z",
      defaultGenerationLanguage: locale.code,
      timeZone: "UTC",
      outputPreference: "balanced" as const,
      includeSourceLinks: false,
      // null is the only value that opens the blocking gate: shouldShowOnboarding(user).
      onboardingCompletedAt: null,
      onboardingIntent: null,
      isAdmin: false,
    },
    personalWorkspace: {
      id: WORKSPACE_ID,
      name: "First Login 700 Workspace",
      slug: "first-login-700-zoom",
      type: "Personal",
      role: "Owner",
    },
  };
}

/**
 * Fulfils every `/api/` request locally with synthetic first-login data. Read-only by construction:
 * a non-GET request is refused with 405 and recorded, an unlisted path is answered with 404 and
 * recorded, and anything leaving the app origin is aborted and recorded.
 */
async function interceptFirstLogin(page: Page, locale: LocaleCase): Promise<Traffic> {
  const traffic: Traffic = { apiCalls: [], refusedMethods: [], unmockedPaths: [], externalRequests: [], nativeDialogs: [] };
  const session = firstLoginSession(locale);
  page.on("dialog", async (dialog) => {
    // "No unnecessary prompts": a blocking first-login flow must not raise a native
    // alert/confirm/prompt of its own accord. Recorded, then dismissed so a failure cannot hang.
    traffic.nativeDialogs.push(`${dialog.type()}:${dialog.message().slice(0, 80)}`);
    await dialog.dismiss();
  });
  await page.route("**/*", async (route) => {
    const url = new URL(route.request().url());
    const method = route.request().method();
    if (url.pathname.startsWith("/api/") && (url.origin === APP_ORIGIN || url.origin === CONFIGURED_API_ORIGIN)) {
      traffic.apiCalls.push({ method, path: url.pathname });
      if (method !== "GET") {
        traffic.refusedMethods.push(`${method} ${url.pathname}`);
        await route.fulfill({ status: 405, contentType: "application/json", body: JSON.stringify({ error: "mock_only_read_test" }) });
        return;
      }
      let body: unknown;
      if (url.pathname === "/api/auth/me") body = session;
      else if (url.pathname === "/api/notifications/unread-count") body = { unreadCount: 0 };
      else if (url.pathname === `/api/workspaces/${WORKSPACE_ID}/projects`) body = { projects: [], items: [] };
      else {
        traffic.unmockedPaths.push(url.pathname);
        await route.fulfill({ status: 404, contentType: "application/json", body: JSON.stringify({ error: "unmocked_test_endpoint" }) });
        return;
      }
      await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(body) });
      return;
    }
    if (url.origin !== APP_ORIGIN) {
      traffic.externalRequests.push(url.href);
      await route.abort();
      return;
    }
    await route.continue();
  });
  return traffic;
}

type Box = { x: number; y: number; width: number; height: number };
function overlaps(a: Box, b: Box): boolean {
  return a.x < b.x + b.width - 1 && b.x < a.x + a.width - 1 && a.y < b.y + b.height - 1 && b.y < a.y + a.height - 1;
}

/** Advances the guided flow with the keyboard only, so no pointer hit-testing is involved. */
async function advanceStep(page: Page): Promise<void> {
  const next = page.locator(".onboarding-actions .primary-button");
  await next.focus();
  await page.keyboard.press("Enter");
}

type TabStop = {
  tag: string;
  cls: string;
  insideDialog: boolean;
  fullyVisible: boolean;
  width: number;
  height: number;
  outlineStyle: string;
  outlineWidth: string;
};

async function walkTabStops(page: Page, stops: number): Promise<TabStop[]> {
  const visited: TabStop[] = [];
  for (let index = 0; index < stops; index += 1) {
    await page.keyboard.press("Tab");
    visited.push(
      await page.evaluate(() => {
        const element = document.activeElement as HTMLElement | null;
        if (!element) {
          return { tag: "none", cls: "", insideDialog: false, fullyVisible: false, width: 0, height: 0, outlineStyle: "none", outlineWidth: "0px" };
        }
        const rect = element.getBoundingClientRect();
        const style = getComputedStyle(element);
        return {
          tag: element.tagName.toLowerCase(),
          cls: typeof element.className === "string" ? element.className.trim().slice(0, 40) : "",
          insideDialog: Boolean(element.closest('[role="dialog"]')),
          fullyVisible: rect.top >= -0.5 && rect.bottom <= innerHeight + 0.5 && rect.left >= -0.5 && rect.right <= innerWidth + 0.5,
          width: Math.round(rect.width),
          height: Math.round(rect.height),
          outlineStyle: style.outlineStyle,
          outlineWidth: style.outlineWidth,
        } satisfies TabStop;
      }),
    );
  }
  return visited;
}

type DialogGeometry = {
  dir: string;
  dialogCount: number;
  card: Box;
  cardScroll: { scrollHeight: number; clientHeight: number; overflowY: string };
  documentScrollWidth: number;
  documentClientWidth: number;
  viewportWidth: number;
  viewportHeight: number;
  gridColumns: string;
  tiles: Array<{ box: Box; icon: Box | null; text: Box | null; arrow: Box | null; height: number; label: string }>;
  clippedText: string[];
};

async function measureDialog(page: Page): Promise<DialogGeometry> {
  return page.evaluate(() => {
    const card = (document.querySelector('[role="dialog"]') ?? document.querySelector(".onboarding-card")) as HTMLElement | null;
    if (!card) throw new Error("the first-login onboarding dialog is not mounted");
    const rect = card.getBoundingClientRect();
    const box = (element: Element | null | undefined): Box | null => {
      if (!element) return null;
      const value = element.getBoundingClientRect();
      return { x: value.x, y: value.y, width: value.width, height: value.height };
    };
    const grid = document.querySelector(".onboarding-workflow-grid");
    const clipped: string[] = [];
    for (const node of Array.from(card.querySelectorAll("h1, h2, p, span, small, strong, button, label, select, input"))) {
      const element = node as HTMLElement;
      const style = getComputedStyle(element);
      const ownText = Array.from(element.childNodes).some((child) => child.nodeType === Node.TEXT_NODE && (child.textContent ?? "").trim().length > 0);
      if (!ownText) continue;
      if (element.scrollWidth - element.clientWidth > 1 && style.overflowX !== "visible" && style.textOverflow !== "ellipsis") {
        clipped.push(`${element.tagName.toLowerCase()}.${typeof element.className === "string" ? element.className.trim().slice(0, 24) : ""} sw=${element.scrollWidth} cw=${element.clientWidth} "${(element.textContent ?? "").replace(/\s+/g, " ").trim().slice(0, 24)}"`);
      }
    }
    return {
      dir: document.documentElement.dir,
      dialogCount: document.querySelectorAll('[role="dialog"]').length,
      card: { x: rect.x, y: rect.y, width: rect.width, height: rect.height },
      cardScroll: { scrollHeight: card.scrollHeight, clientHeight: card.clientHeight, overflowY: getComputedStyle(card).overflowY },
      documentScrollWidth: document.documentElement.scrollWidth,
      documentClientWidth: document.documentElement.clientWidth,
      viewportWidth: innerWidth,
      viewportHeight: innerHeight,
      gridColumns: grid ? getComputedStyle(grid).gridTemplateColumns : "",
      tiles: Array.from(document.querySelectorAll(".onboarding-workflow")).map((tile) => {
        const own = tile.getBoundingClientRect();
        return {
          box: { x: own.x, y: own.y, width: own.width, height: own.height },
          icon: box(tile.querySelector(".onboarding-workflow-icon")),
          text: box(tile.querySelector("strong")),
          arrow: box(tile.querySelector(".onboarding-workflow-arrow")),
          height: Math.round(own.height),
          label: (tile.textContent ?? "").replace(/\s+/g, " ").trim().slice(0, 36),
        };
      }),
      clippedText: clipped,
    };
  });
}

/** Opens a page whose only job is to host the gate at one zoomed viewport in one locale. */
async function openFirstLogin(browser: Browser, locale: LocaleCase, viewport: { width: number; height: number }): Promise<{ page: Page; traffic: Traffic }> {
  const page = await browser.newPage({ viewport });
  const traffic = await interceptFirstLogin(page, locale);
  // `browser.newPage()` deliberately bypasses the `use` fixtures, so the base URL is applied here.
  await page.goto(new URL("/projects", APP_ORIGIN).href);
  await expect(page.getByRole("dialog")).toBeVisible();
  return { page, traffic };
}

test.describe("issue #700 section A: first-login onboarding at 200% browser zoom", () => {
  test("centres the dialog, keeps it inner-scrolling and reveals every focused control at 200% zoom", async ({ browser }) => {
    for (const locale of LOCALES) {
      for (const state of AT_OR_ABOVE_FLOOR) {
        const viewport = { width: state.deviceWidth / ZOOM, height: state.deviceHeight / ZOOM };
        const context = `${locale.label} / ${state.name} -> ${viewport.width}x${viewport.height} CSS px`;
        const { page, traffic } = await openFirstLogin(browser, locale, viewport);

        const dialog = page.getByRole("dialog");
        await expect(dialog, `the gate must expose dialog semantics (${context})`).toHaveAttribute("aria-modal", "true");
        await expect(dialog, `the gate must be labelled (${context})`).toHaveAttribute("aria-labelledby", /onboarding-title/);

        let geometry = await measureDialog(page);
        expect(geometry.dir, `document direction must follow the interface locale (${context})`).toBe(locale.dir);
        expect(geometry.dialogCount, `exactly one prompt may be raised, never a stack (${context})`).toBe(1);

        // A zoomed desktop lands in the narrow and the short branch at once, so the dialog must fit
        // the halved layout viewport, stay centred on the inline axis and never push the page sideways.
        expect(Math.round(geometry.card.width), `the dialog must fit the zoomed viewport width (${context})`).toBeLessThanOrEqual(viewport.width + 1);
        expect(Math.round(geometry.card.height), `the dialog must fit the zoomed viewport height (${context})`).toBeLessThanOrEqual(viewport.height + 1);
        expect(Math.round(geometry.card.x), `the dialog must stay inside the zoomed viewport (${context})`).toBeGreaterThanOrEqual(-1);
        expect(Math.abs(geometry.card.x + geometry.card.width / 2 - viewport.width / 2), `the dialog must stay centred on the inline axis (${context})`).toBeLessThanOrEqual(2);
        expect(geometry.documentScrollWidth, `200% zoom must not introduce horizontal page scrolling (${context})`).toBeLessThanOrEqual(geometry.documentClientWidth);

        // Step 1 -> step 3. The zoomed height forces the dialog into inner scroll; moving between
        // steps must never stack a second prompt nor clip a text run.
        for (let step = 2; step <= 3; step += 1) {
          await advanceStep(page);
          geometry = await measureDialog(page);
          expect(geometry.dialogCount, `moving to step ${step} must not stack another prompt (${context})`).toBe(1);
          expect(Math.round(geometry.card.width), `the dialog must keep fitting the zoomed viewport on step ${step} (${context})`).toBeLessThanOrEqual(viewport.width + 1);
          expect(geometry.documentScrollWidth, `step ${step} must not introduce horizontal page scrolling (${context})`).toBeLessThanOrEqual(geometry.documentClientWidth);
          expect(geometry.clippedText, `no text run may be clipped by its box on step ${step} (${context}): ${JSON.stringify(geometry.clippedText.slice(0, 6))}`).toHaveLength(0);
        }

        // The workflow step is the reported defect ("six choices render as a continuous, unaligned
        // text row ... icon/title/description/action arrows overlap"). The set shipped on this head
        // is five tiles -- Chat, Image, Document, Presentation plus Movie Studio, with Project and
        // Research deliberately withheld as beta module choices -- and the enumeration itself belongs
        // to #846, so only the count floor and the geometry are asserted here. At this zoom the grid
        // must be a single column of distinct, separated cards.
        expect(geometry.tiles.length, `the guided flow must keep its full choice set of five tiles (${context})`).toBe(5);
        expect(geometry.gridColumns.split(" ").filter(Boolean).length, `workflow choices must collapse to one column at this zoomed width (${context})`).toBe(1);
        const collidingTiles: string[] = [];
        for (let i = 0; i < geometry.tiles.length; i += 1) {
          for (let j = i + 1; j < geometry.tiles.length; j += 1) {
            if (overlaps(geometry.tiles[i].box, geometry.tiles[j].box)) collidingTiles.push(`tile ${i} overlaps tile ${j}`);
          }
        }
        expect(collidingTiles, `workflow choice tiles must never overlap (${context})`).toEqual([]);
        const collidingParts: string[] = [];
        for (const tile of geometry.tiles) {
          if (tile.icon && tile.text && overlaps(tile.icon, tile.text)) collidingParts.push(`icon/title "${tile.label}"`);
          if (tile.arrow && tile.text && overlaps(tile.arrow, tile.text)) collidingParts.push(`arrow/title "${tile.label}"`);
          if (tile.box.x + tile.box.width > geometry.viewportWidth + 1) collidingParts.push(`tile past the inline end "${tile.label}"`);
        }
        expect(collidingParts, `icon, title and action arrow must stay separated (${context})`).toEqual([]);

        // Keyboard, the point of the zoom state: the dialog now scrolls itself, so every control
        // reached by Tab must be revealed rather than left outside the scroll box.
        await page.locator(".onboarding-skip").focus();
        const stops = await walkTabStops(page, geometry.tiles.length + 2);
        expect(stops.every((stop) => stop.insideDialog), `Tab must never escape the blocking dialog (${context}): ${JSON.stringify(stops.map((stop) => `${stop.tag}.${stop.cls}`))}`).toBe(true);
        expect(
          stops.filter((stop) => !stop.fullyVisible).map((stop) => `${stop.tag}.${stop.cls} ${stop.width}x${stop.height}`),
          `every focused control must be scrolled into view inside the zoomed dialog (${context})`,
        ).toEqual([]);
        expect(
          stops.filter((stop) => Math.min(stop.width, stop.height) < MIN_TARGET_CSS).map((stop) => `${stop.tag}.${stop.cls} ${stop.width}x${stop.height}`),
          `keyboard-operable controls need a ${MIN_TARGET_CSS}x${MIN_TARGET_CSS} CSS px target (WCAG 2.5.8) (${context})`,
        ).toEqual([]);
        expect(stops.some((stop) => stop.outlineStyle === "solid" && parseFloat(stop.outlineWidth) >= 2), `focus must be visible on at least one tab stop (${context})`).toBe(true);

        expect(traffic.nativeDialogs, `no native alert/confirm/prompt may be raised (${context})`).toEqual([]);
        await page.close();
      }
    }
  });

  test("stays read-only, never enters billing and keeps every request local while the zoomed flow is walked", async ({ browser }) => {
    const { page, traffic } = await openFirstLogin(browser, LOCALES[0], { width: 1280 / ZOOM, height: 900 / ZOOM });
    await advanceStep(page);
    await advanceStep(page);
    await page.locator(".onboarding-skip").focus();
    await walkTabStops(page, 4);

    expect(traffic.refusedMethods, `only GET requests may be attempted: ${JSON.stringify(traffic.refusedMethods)}`).toEqual([]);
    expect(traffic.unmockedPaths, `only allow-listed endpoints may be read: ${JSON.stringify(traffic.unmockedPaths)}`).toEqual([]);
    expect(traffic.externalRequests, `nothing may leave the app origin: ${JSON.stringify(traffic.externalRequests)}`).toEqual([]);
    expect(traffic.apiCalls.map((call) => call.path), "the session endpoint must have been read").toContain("/api/auth/me");
    expect(
      traffic.apiCalls.filter((call) => /billing|checkout|payment|subscription|credit|charge|provider|onboarding/i.test(call.path)).map((call) => `${call.method} ${call.path}`),
      "no billing, charging, provider or onboarding-write endpoint may be contacted",
    ).toEqual([]);
    await page.close();
  });

  test("pins the guided flow against the product's own 320px minimum canvas when a 320px phone is zoomed to 200%", async ({ browser }) => {
    const viewport = { width: SUB_FLOOR.deviceWidth / ZOOM, height: SUB_FLOOR.deviceHeight / ZOOM };
    for (const locale of LOCALES) {
      const context = `${locale.label} / ${SUB_FLOOR.name} -> ${viewport.width}x${viewport.height} CSS px`;
      const { page, traffic } = await openFirstLogin(browser, locale, viewport);
      // The finding concerns the choice grid, so the guided flow is walked to the workflow step
      // first, with the keyboard, exactly as a user at this zoom would have to do it.
      await advanceStep(page);
      await advanceStep(page);
      await expect(page.locator(".onboarding-workflow-grid"), `the guided flow must still reach its choices (${context})`).toBeVisible();
      const geometry = await measureDialog(page);

      // Properties that must hold even inside a sub-floor viewport.
      expect.soft(geometry.dialogCount, `exactly one prompt may be raised (${context})`).toBe(1);
      expect.soft(geometry.dir, `direction must follow the interface locale (${context})`).toBe(locale.dir);
      expect.soft(geometry.clippedText, `no text run may be clipped by its box (${context}): ${JSON.stringify(geometry.clippedText.slice(0, 6))}`).toHaveLength(0);
      expect.soft(geometry.tiles.length, `the choice set must stay complete at five tiles (${context})`).toBe(5);
      expect.soft(
        geometry.tiles.filter((tile, index) => geometry.tiles.slice(index + 1).some((other) => overlaps(tile.box, other.box))).map((tile) => tile.label),
        `workflow choice tiles must never overlap (${context})`,
      ).toEqual([]);
      expect.soft(traffic.nativeDialogs, `no native alert/confirm/prompt may be raised (${context})`).toEqual([]);

      // The findings. The dialog is laid out against the 160px visual viewport rather than the 320px
      // canvas that same document enforces through `html { min-width: 320px }`, and the tiles that
      // result grow taller than the viewport, so focus can never reveal the control it lands on.
      expect.soft(geometry.documentScrollWidth, `the document must respect its own ${MINIMUM_CANVAS_CSS}px floor (${context})`).toBeLessThanOrEqual(MINIMUM_CANVAS_CSS + 1);
      expect.soft(
        Math.round(geometry.card.width),
        `the onboarding dialog must not be laid out narrower than the ${MINIMUM_CANVAS_CSS}px minimum canvas the same document enforces (measured ${Math.round(geometry.card.width)}px wide at ${viewport.width}x${viewport.height} CSS px) (${context})`,
      ).toBeGreaterThanOrEqual(MINIMUM_CANVAS_CSS);
      const unrevealable = geometry.tiles.filter((tile) => tile.height > geometry.viewportHeight).map((tile) => `${tile.height}px "${tile.label}"`);
      expect.soft(
        unrevealable,
        `every workflow choice must be short enough to be revealed while focused in the ${viewport.height}px zoomed viewport (offenders: ${JSON.stringify(unrevealable.slice(0, 5))}) (${context})`,
      ).toEqual([]);

      await page.locator(".onboarding-skip").focus();
      const stops = await walkTabStops(page, 4);
      expect.soft(stops.every((stop) => stop.insideDialog), `Tab must stay inside the blocking dialog (${context})`).toBe(true);
      await page.close();
    }
  });
});
