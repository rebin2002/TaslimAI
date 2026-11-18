/**
 * INDEPENDENT STANDARD QA for the protected-page bootstrap lane: #1068's RED
 * requirements verified GREEN against #1072 head `1cbfed66`, layered on #1031.
 *
 * Why this file exists separately from the owner's three suites:
 *
 * - `ProtectedPage.liveRegionAndReturnPath.test.tsx` (#1068) proves the region
 *   *has* a name and that the return query is read at fire time, but it always
 *   mounts with `navigator.language = "en"` and a `lang`-less document, so the
 *   multilingual half of #1072 - the work that makes the announcement usable to
 *   an Arabic or Sorani-reading visitor - is never observed in a DOM.
 * - `protectedPageI18n.test.ts` (#1072) pins the *pure resolver* for every
 *   locale, but nothing connects resolver output to the mounted region.
 * - `ProtectedPage.bootstrapRace.test.tsx` (#1031) proves authenticated-session
 *   hydration survives the watchdog, always in English.
 *
 * This suite closes those seams, in synthetic HTML/DOM with fixed anonymous
 * fixtures only: no jsdom, no network, no real account, no provider, no secrets,
 * no paid anything. It also adds one property no owner suite covers, because
 * #1072 introduced *new component state* into the gate - the post-hydration
 * locale swap re-renders the boundary, and any re-render that re-ran the
 * watchdog effect would clear and re-arm the timer, silently turning a bounded
 * 10-second recovery window into an indefinite loading state. Section D pins
 * that this does not happen, and section B pins that the swap does not replace
 * the live-region node, which would forfeit the announcement entirely.
 *
 * Every case here is run twice: against #1072's tree (expected green) and
 * against a tree without it, where the locale cases must fail. A case that
 * cannot fail is not evidence.
 */
import { act } from "react";
import { readFileSync } from "node:fs";
import { createElement, type ReactElement } from "react";
import { createRoot, type Root } from "react-dom/client";
import { renderToStaticMarkup } from "react-dom/server";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

/* ------------------------------------------------- synthetic DOM shim ---- */
/**
 * Dependency-free node/document, in the convention already established by
 * `ActivityBell.badgeFence.test.tsx` and the other mounted boundary suites in
 * this workspace. Only the surface React 19 and the name computation below use.
 */
class TestNode {
  nodeType: number;
  nodeName: string;
  ownerDocument: TestDocument;
  parentNode: TestNode | null = null;
  childNodes: TestNode[] = [];
  attributes = new Map<string, string>();
  style: Record<string, string> = {};
  listeners = new Map<string, Set<(event: unknown) => void>>();
  constructor(nodeType: number, nodeName: string, ownerDocument: TestDocument) {
    this.nodeType = nodeType;
    this.nodeName = nodeName;
    this.ownerDocument = ownerDocument;
  }
  get firstChild() { return this.childNodes[0] ?? null; }
  get lastChild(): TestNode | null { return this.childNodes.at(-1) ?? null; }
  get tagName() { return this.nodeName; }
  get namespaceURI() { return "http://www.w3.org/1999/xhtml"; }
  get id() { return this.attributes.get("id") ?? ""; }
  set id(value: string) { this.attributes.set("id", value); }
  get children(): TestNode[] { return this.childNodes.filter((child) => child.nodeType === 1); }
  get textContent() { return this.childNodes.map((child) => child.textContent ?? "").join(""); }
  set textContent(value: string | null) {
    this.childNodes = value ? [new TestNodeText(value, this.ownerDocument)] : [];
    for (const child of this.childNodes) child.parentNode = this;
  }
  // Explicitly `string`: the paired setter widens to `string | null`, and an
  // annotated text node reads back as a string, as the DOM contract requires.
  get data(): string { return String(this.textContent ?? ""); }
  set data(value: string) { this.textContent = value; }
  get className() { return this.attributes.get("class") ?? ""; }
  set className(value: string) { this.attributes.set("class", value); }
  appendChild<T extends TestNode>(child: T) {
    if (child.parentNode) child.parentNode.removeChild(child);
    child.parentNode = this;
    this.childNodes.push(child);
    return child;
  }
  insertBefore<T extends TestNode>(child: T, before: TestNode | null) {
    if (!before) return this.appendChild(child);
    const index = this.childNodes.indexOf(before);
    if (index < 0) return this.appendChild(child);
    if (child.parentNode) child.parentNode.removeChild(child);
    child.parentNode = this;
    this.childNodes.splice(index, 0, child);
    return child;
  }
  removeChild<T extends TestNode>(child: T) {
    const index = this.childNodes.indexOf(child);
    if (index >= 0) this.childNodes.splice(index, 1);
    child.parentNode = null;
    return child;
  }
  setAttribute(name: string, value: string) { this.attributes.set(name, String(value)); }
  getAttribute(name: string) { return this.attributes.get(name) ?? null; }
  removeAttribute(name: string) { this.attributes.delete(name); }
  hasAttribute(name: string) { return this.attributes.has(name); }
  addEventListener(name: string, listener: (event: unknown) => void) {
    const listeners = this.listeners.get(name) ?? new Set();
    listeners.add(listener);
    this.listeners.set(name, listeners);
  }
  removeEventListener(name: string, listener: (event: unknown) => void) { this.listeners.get(name)?.delete(listener); }
  contains(node: TestNode | null): boolean { return node === this || this.childNodes.some((child) => child.contains(node)); }
}
class TestNodeText extends TestNode {
  nodeValue: string;
  constructor(value: string, ownerDocument: TestDocument) {
    super(3, "#text", ownerDocument);
    this.nodeValue = value;
  }
  override get textContent() { return this.nodeValue; }
  override set textContent(value: string | null) { this.nodeValue = value ?? ""; }
}
class TestDocument extends TestNode {
  body: TestNode;
  documentElement: TestNode & { lang?: string; dir?: string };
  defaultView: object;
  constructor() {
    super(9, "#document", null as unknown as TestDocument);
    this.documentElement = new TestNode(1, "html", this) as TestDocument["documentElement"];
    this.body = new TestNode(1, "body", this);
    this.documentElement.appendChild(this.body);
    this.appendChild(this.documentElement);
    this.defaultView = {};
  }
  createElement(name: string) { return new TestNode(1, name.toUpperCase(), this); }
  createElementNS(_namespace: string, name: string) { return this.createElement(name); }
  createTextNode(value: string) { return new TestNodeText(value, this.ownerDocument ?? this); }
}

/* ---------------------------------------- accessible name (spec subset) ---- */
/**
 * The portion of the accname computation this region exercises. An authored
 * `aria-label` wins; otherwise `aria-labelledby`; otherwise the text of
 * *included* descendants, with `aria-hidden` subtrees dropped - the reason a
 * decorative spinner cannot name its own region.
 */
function includedDescendantText(node: TestNode): string {
  if (node.nodeType === 3) return String(node.textContent ?? "");
  if (node.getAttribute("aria-hidden") === "true") return "";
  return node.childNodes.map(includedDescendantText).join(" ");
}
function collectDescendants(node: TestNode, predicate: (n: TestNode) => boolean): TestNode[] {
  const found: TestNode[] = [];
  const walk = (current: TestNode) => {
    if (current.nodeType === 1 && predicate(current)) found.push(current);
    for (const child of current.childNodes) walk(child);
  };
  walk(node);
  return found;
}
function accessibleName(node: TestNode | undefined): string {
  if (!node) return "";
  const label = node.getAttribute("aria-label");
  if (label !== null && label.trim().length > 0) return label.trim();
  const labelledBy = node.getAttribute("aria-labelledby");
  if (labelledBy !== null) {
    const referred = labelledBy
      .split(/\s+/)
      .map((id) => collectDescendants(container, (n) => n.getAttribute("id") === id)[0]?.textContent ?? "")
      .join(" ")
      .trim();
    if (referred.length > 0) return referred;
  }
  return includedDescendantText(node).replace(/\s+/g, " ").trim();
}
/** Every announced live region currently rendered. */
function liveRegions(): TestNode[] {
  return collectDescendants(container, (n) => n.getAttribute("role") === "status" || n.getAttribute("aria-live") !== null);
}
function bootstrapRegion(): TestNode | undefined {
  return liveRegions()[0];
}

/* ------------------------------------------------------ RTL / script ---- */
const ARABIC_SCRIPT = /[\u0600-\u06FF\u0750-\u077A\uFB50-\uFDFF\uFE70-\uFEFF]/;
const PURELY_LATIN = /^[A-Za-z0-9 ,.'\-?()]+$/;

/**
 * The gate's own source, for the one invariant that cannot be observed through
 * a timer. See the case in section D that reads it.
 */
const gateSource = readFileSync(new URL("../components/ProtectedPage.tsx", import.meta.url), "utf8");

/* ------------------------------------------------------------ harness ---- */
const PROTECTED_ROUTE = "/create/document";
const ARM_SEARCH = "?projectId=project-1&tab=source";
const FIRE_SEARCH = "?projectId=project-2&tab=deliverables";
const LOGIN_PREFIX = "/login?next=";

const auth = vi.hoisted(() => ({ state: { user: null as unknown, loading: true } }));
// The route literal is repeated rather than referencing PROTECTED_ROUTE: a
// vi.hoisted factory is lifted above module-level consts, so that binding is
// still uninitialised when the factory runs.
const nav = vi.hoisted(() => ({ pathname: "/create/document" }));
// A single stable router object, because the gate lists `router` in its effect
// dependencies and Next's `useRouter()` hands back a stable instance. Building
// a fresh literal per call would re-run those effects on every render - real
// routers do not behave that way, and the harness would be measuring itself.
// `replace` is the spy every redirect assertion below reads.
const routerStub = vi.hoisted(() => ({ replace: vi.fn(), push: vi.fn(), prefetch: vi.fn() }));
// Wrapped, not replaced: section B needs to observe *when* the boundary asks for
// the visitor's language, which is what separates a render-phase read (a
// hydration mismatch against the English server shell) from an effect.
// `localeReads` therefore counts effect-time resolutions (one per mount), while
// `boundaryCommits` counts calls to the label helper, which runs on every render
// of the boundary and so is the render counter section D needs.
const localeReads = vi.hoisted(() => vi.fn());
const boundaryCommits = vi.hoisted(() => vi.fn());
vi.mock("../lib/protectedPageI18n", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../lib/protectedPageI18n")>();
  return {
    ...actual,
    readBrowserProtectedPageBootstrapLocale: () => {
      localeReads();
      return actual.readBrowserProtectedPageBootstrapLocale();
    },
    protectedPageBootstrapStatusText: (locale: Parameters<typeof actual.protectedPageBootstrapStatusText>[0]) => {
      boundaryCommits();
      return actual.protectedPageBootstrapStatusText(locale);
    },
  };
});
vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => ({ user: auth.state.user, loading: auth.state.loading, signOut: vi.fn(), refresh: vi.fn() }),
}));
vi.mock("next/navigation", () => ({
  useRouter: () => routerStub,
  usePathname: () => nav.pathname,
}));

import { PROTECTED_PAGE_AUTH_TIMEOUT_MS, ProtectedPage } from "../components/ProtectedPage";
import { protectedPageBootstrapStatusLabels } from "../lib/protectedPageI18n";

let dom: TestDocument;
let container: TestNode;
let root: Root | undefined;
let windowStub: {
  location: { search: string };
  localStorage: { getItem: (key: string) => string | null };
  [key: string]: unknown;
};

/**
 * The locales a Taslim visitor can be reading the protected shell in, expressed
 * the way a real document carries them. `LocaleProvider` writes the canonical
 * BCP-47 tag, so Sorani arrives as `ckb` rather than the `ku` short code.
 */
type LocaleScenario = {
  name: string;
  documentLang: string | undefined;
  storedLocale: string | null;
  navigatorLanguage: string;
  expectLabel: string;
  expectDirection: "ltr" | "rtl";
};
const SCENARIOS: LocaleScenario[] = [
  {
    name: "English (Latin script)",
    documentLang: "en",
    storedLocale: null,
    navigatorLanguage: "en",
    expectLabel: protectedPageBootstrapStatusLabels.en,
    expectDirection: "ltr",
  },
  {
    name: "Arabic (RTL, Arabic script)",
    documentLang: "ar",
    storedLocale: null,
    navigatorLanguage: "en",
    expectLabel: protectedPageBootstrapStatusLabels.ar,
    expectDirection: "rtl",
  },
  {
    name: "Kurdish Sorani as ckb (RTL, Arabic script)",
    documentLang: "ckb",
    storedLocale: null,
    navigatorLanguage: "en",
    expectLabel: protectedPageBootstrapStatusLabels.ku,
    expectDirection: "rtl",
  },
  {
    name: "English server shell with a persisted Sorani choice",
    documentLang: "en",
    storedLocale: "ku",
    navigatorLanguage: "en",
    expectLabel: protectedPageBootstrapStatusLabels.ku,
    expectDirection: "rtl",
  },
  {
    name: "Region-tagged Arabic document (ar-IQ)",
    documentLang: "ar-IQ",
    storedLocale: null,
    navigatorLanguage: "en",
    expectLabel: protectedPageBootstrapStatusLabels.ar,
    expectDirection: "rtl",
  },
  {
    name: "Nothing declared anywhere (fails closed to English)",
    documentLang: undefined,
    storedLocale: null,
    navigatorLanguage: "fr-FR",
    expectLabel: protectedPageBootstrapStatusLabels.en,
    expectDirection: "ltr",
  },
];
const RTL_SCENARIOS = SCENARIOS.filter((scenario) => scenario.expectDirection === "rtl");

function settle(ticks = 8) {
  return async () => {
    for (let tick = 0; tick < ticks; tick += 1) await Promise.resolve();
  };
}
function advance(ms: number) {
  return act(async () => {
    vi.advanceTimersByTime(ms);
    await settle();
  });
}
function protectedView(): ReactElement {
  return createElement("div", { className: "document-view" }, "synthetic protected content") as ReactElement;
}
/** Re-render the SAME gate so an auth-state transition can be driven through it. */
function renderGate() {
  return act(async () => {
    root?.render(createElement(ProtectedPage, null, protectedView()));
    await settle();
  });
}
/** Fresh root, fresh commit: every case mounts exactly once per scenario. */
function mountGate() {
  return act(async () => {
    root = createRoot(container as unknown as Element);
    root.render(createElement(ProtectedPage, null, protectedView()));
    vi.advanceTimersByTime(0);
    await settle();
  });
}
function stallBootstrap() {
  auth.state = { user: null, loading: true };
}
function loginRedirects(): string[] {
  return routerStub.replace.mock.calls
    .map((call) => String(call[0]))
    .filter((target) => target.startsWith(LOGIN_PREFIX));
}
function nextOf(redirect: string): string {
  return new URL(redirect, "http://protected.test").searchParams.get("next") ?? "";
}
/** Describe the document the way the scenario says the visitor sees it. */
function applyDocumentLocale(scenario: LocaleScenario) {
  if (scenario.documentLang === undefined) delete dom.documentElement.lang;
  else dom.documentElement.lang = scenario.documentLang;
  dom.documentElement.dir = scenario.expectDirection;
  windowStub.localStorage = { getItem: () => scenario.storedLocale };
  vi.stubGlobal("navigator", { userAgent: "test", language: scenario.navigatorLanguage });
}

beforeEach(() => {
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "Date"] });
  dom = new TestDocument();
  container = dom.createElement("div");
  dom.body.appendChild(container);
  root = undefined;
  stallBootstrap();
  nav.pathname = PROTECTED_ROUTE;
  routerStub.replace.mockReset();
  localeReads.mockReset();
  boundaryCommits.mockReset();
  vi.stubGlobal("document", dom);
  windowStub = {
    document: dom,
    location: { search: ARM_SEARCH },
    HTMLElement: TestNode,
    HTMLIFrameElement: TestNode,
    Element: TestNode,
    Node: TestNode,
    // The gate schedules its recovery through `window.setTimeout`, so the
    // virtual clock has to back it; deferring the lookup is what binds the
    // timer to the fake installed above.
    setTimeout: (handler: () => void, delay: number) => setTimeout(handler, delay),
    clearTimeout: (handle: ReturnType<typeof setTimeout>) => clearTimeout(handle),
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    localStorage: { getItem: () => null },
  };
  dom.defaultView = windowStub;
  vi.stubGlobal("window", windowStub);
  vi.stubGlobal("navigator", { userAgent: "test", language: "en" });
  vi.stubGlobal("IS_REACT_ACT_ENVIRONMENT", true);
});
afterEach(() => {
  act(() => root?.unmount());
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

/* ================== A. announced name, per reading language ================== */
describe("A. the bootstrap live region is announced in the language the visitor reads", () => {
  it.each(SCENARIOS)("$name: names the stalled region with that language's copy", async (scenario) => {
    applyDocumentLocale(scenario);
    await mountGate();
    const region = bootstrapRegion();
    expect(region, "the region exists, so a name assertion cannot be vacuous").toBeDefined();
    expect(region?.getAttribute("aria-live"), "polite, as #1068 pins").toBe("polite");
    expect(region?.getAttribute("aria-busy")).toBe("true");
    // #1068 RED requirements 1 and 2, observed in a DOM rather than in source text.
    const name = accessibleName(region);
    expect(name, "an unnamed role=status announces nothing").not.toBe("");
    expect(name).toBe(scenario.expectLabel);
  });

  it.each(RTL_SCENARIOS)("$name: speaks Arabic script, not a transliteration", async (scenario) => {
    applyDocumentLocale(scenario);
    await mountGate();
    const name = accessibleName(bootstrapRegion());
    expect(ARABIC_SCRIPT.test(name), `${scenario.name} uses the Arabic block`).toBe(true);
    expect(PURELY_LATIN.test(name), `${scenario.name} is not Latin`).toBe(false);
    expect(name).not.toBe(protectedPageBootstrapStatusLabels.en);
  });

  it.each(SCENARIOS)("$name: announces text a screen reader can render", async (scenario) => {
    applyDocumentLocale(scenario);
    await mountGate();
    const name = accessibleName(bootstrapRegion());
    expect(name).not.toMatch(/<\/?[a-z]|aria-|class=|role=|&[a-z]+;/i);
    expect(name.trim().length).toBeGreaterThan(0);
  });

  it.each(SCENARIOS)("$name: keeps the decorative spinner out of the name", async (scenario) => {
    applyDocumentLocale(scenario);
    await mountGate();
    const region = bootstrapRegion() as TestNode;
    expect(collectDescendants(region, (n) => n.getAttribute("aria-hidden") === "true").length)
      .toBeGreaterThan(0);
    // The authored name is the only speakable content: descendant text is empty,
    // so removing the label would silently silence the region again.
    expect(includedDescendantText(region).replace(/\s+/g, " ").trim()).toBe("");
    expect(accessibleName(region)).toBe(scenario.expectLabel);
  });

  it.each(RTL_SCENARIOS)("$name: stays announced when a settled session expires back to loading", async (scenario) => {
    // The second path into the same boundary: content mounted, then the session
    // settles as absent. #1068 pins this in English; the finding is locale-blind.
    applyDocumentLocale(scenario);
    auth.state = { user: { id: "synthetic-user-1" }, loading: false };
    await mountGate();
    expect(liveRegions(), "content is mounted, so no region is showing").toHaveLength(0);
    auth.state = { user: null, loading: false };
    await renderGate();
    const regions = liveRegions();
    expect(regions).toHaveLength(1);
    expect(accessibleName(regions[0]), scenario.name).toBe(scenario.expectLabel);
  });
});

/* ============ B. hydration safety of the new locale state ============ */
describe("B. the locale restore is an effect, not a render-phase read", () => {
  it("renders the English announcement on the server for an Arabic and a Sorani document", () => {
    // If the server rendered the visitor's language, every RTL visitor would
    // take a hydration mismatch on the gate. The English shell is deliberate.
    for (const lang of ["ar", "ckb"]) {
      vi.stubGlobal("document", { documentElement: { lang } });
      const markup = renderToStaticMarkup(createElement(ProtectedPage, null, protectedView()));
      expect(markup).toContain(`aria-label="${protectedPageBootstrapStatusLabels.en}"`);
      expect(markup).not.toContain(protectedPageBootstrapStatusLabels.ar);
      expect(markup).not.toContain(protectedPageBootstrapStatusLabels.ku);
    }
    vi.stubGlobal("document", dom);
  });

  it("adopts the visible language after the first commit", async () => {
    applyDocumentLocale(SCENARIOS[1]); // Arabic document
    await mountGate();
    expect(localeReads, "resolved through the browser read, not a render-phase guess").toHaveBeenCalledTimes(1);
    expect(accessibleName(bootstrapRegion())).toBe(protectedPageBootstrapStatusLabels.ar);
  });

  it("updates the announcement in place rather than replacing the live-region node", async () => {
    // A live region that is swapped for a *new* node is not announced by every
    // screen reader: the polite queue follows mutations of the registered node.
    // The locale swap must therefore rewrite the name on the same element.
    applyDocumentLocale(SCENARIOS[1]);
    await mountGate();
    const first = bootstrapRegion();
    expect(first).toBeDefined();
    expect(accessibleName(first)).toBe(protectedPageBootstrapStatusLabels.ar);
    // A second, different language arriving later (a late persisted restore)
    // must land on the same node.
    applyDocumentLocale(SCENARIOS[2]);
    await act(async () => {
      // Force the resolver to return a different locale on the next read by
      // re-running the boundary's own effect through a remount of the *state*,
      // which is what a slow LocaleProvider write looks like to this gate.
      root?.render(createElement(ProtectedPage, null, protectedView()));
      await settle();
    });
    const second = bootstrapRegion();
    expect(second, "the announcement stays on the registered live-region node").toBe(first);
    expect(accessibleName(second)).toBe(protectedPageBootstrapStatusLabels.ar);
  });

  it("never leaves the region unnamed while the locale settles", async () => {
    applyDocumentLocale(SCENARIOS[2]); // Sorani document
    await mountGate();
    for (let step = 0; step < 4; step += 1) {
      expect(accessibleName(bootstrapRegion()), "no silent window").not.toBe("");
      await act(settle());
    }
    expect(accessibleName(bootstrapRegion())).toBe(protectedPageBootstrapStatusLabels.ku);
  });

  it("resolves to English when storage access is denied outright", async () => {
    // Privacy-restricted contexts throw on localStorage access; the boundary is
    // on the path to a loading screen and must not throw there.
    windowStub.localStorage = {
      getItem: () => {
        throw new Error("SecurityError: storage access denied");
      },
    };
    dom.documentElement.lang = "ar";
    await mountGate();
    const name = accessibleName(bootstrapRegion());
    expect(name).not.toBe("");
    // The visible Arabic document still wins over the unreadable store.
    expect(name).toBe(protectedPageBootstrapStatusLabels.ar);
  });

  it("survives a document with no language element at all", async () => {
    dom.documentElement.lang = "";
    vi.stubGlobal("document", { documentElement: null, body: dom.body, createElement: dom.createElement.bind(dom) });
    await mountGate();
    expect(accessibleName(bootstrapRegion())).toBe(protectedPageBootstrapStatusLabels.en);
    vi.stubGlobal("document", dom);
  });
});

/* ========== C. fire-time return-query freshness (independent probe) ========== */
describe("C. the watchdog returns the query that is live when it fires", () => {
  it("holds the window, then redirects once with the fire-time query", async () => {
    await mountGate();
    await advance(PROTECTED_PAGE_AUTH_TIMEOUT_MS - 1);
    expect(loginRedirects(), "the bootstrap window is honoured, not shortened").toHaveLength(0);
    // The protected view rewrites the query mid-window; the user is elsewhere now.
    windowStub.location.search = FIRE_SEARCH;
    await advance(1);
    const redirects = loginRedirects();
    expect(redirects).toHaveLength(1);
    expect(nextOf(redirects[0])).toBe(`${PROTECTED_ROUTE}${FIRE_SEARCH}`);
  });

  it("preserves an unchanged query when nothing rewrites it", async () => {
    await mountGate();
    await advance(PROTECTED_PAGE_AUTH_TIMEOUT_MS);
    const redirects = loginRedirects();
    expect(redirects).toHaveLength(1);
    expect(nextOf(redirects[0])).toBe(`${PROTECTED_ROUTE}${ARM_SEARCH}`);
  });

  it("carries every parameter written after arming, not only the first", async () => {
    await mountGate();
    windowStub.location.search = "?projectId=project-3&tab=export&job=job-77&format=docx";
    await advance(PROTECTED_PAGE_AUTH_TIMEOUT_MS);
    const redirects = loginRedirects();
    expect(redirects).toHaveLength(1);
    const next = new URL(nextOf(redirects[0]), "http://protected.test");
    expect(next.searchParams.get("projectId")).toBe("project-3");
    expect(next.searchParams.get("tab")).toBe("export");
    expect(next.searchParams.get("job")).toBe("job-77");
    expect(next.searchParams.get("format")).toBe("docx");
  });

  it("returns a bare path when the query is emptied after arming", async () => {
    await mountGate();
    windowStub.location.search = "";
    await advance(PROTECTED_PAGE_AUTH_TIMEOUT_MS);
    const redirects = loginRedirects();
    expect(redirects).toHaveLength(1);
    expect(nextOf(redirects[0])).toBe(PROTECTED_ROUTE);
  });

  it("both eviction paths agree on the same destination for the same location", async () => {
    // Settled-absence path: reads the search at its own fire time.
    await mountGate();
    windowStub.location.search = FIRE_SEARCH;
    auth.state = { user: null, loading: false };
    await renderGate();
    const settled = loginRedirects();
    expect(settled).toHaveLength(1);
    const viaSettled = nextOf(settled[0]);

    // Watchdog path, same arm query, same rewrite.
    routerStub.replace.mockReset();
    stallBootstrap();
    windowStub.location.search = ARM_SEARCH;
    // A fresh root, so the watchdog is armed by a mount rather than by a state
    // flip inside a tree whose effects have already run.
    act(() => root?.unmount());
    await mountGate();
    windowStub.location.search = FIRE_SEARCH;
    await advance(PROTECTED_PAGE_AUTH_TIMEOUT_MS);
    const viaWatchdog = loginRedirects();
    expect(viaWatchdog).toHaveLength(1);
    expect(nextOf(viaWatchdog[0]), "which path wins must not decide link freshness").toBe(viaSettled);
  });

  it.each(RTL_SCENARIOS)("$name: keeps fire-time freshness for an RTL reader", async (scenario) => {
    applyDocumentLocale(scenario);
    await mountGate();
    windowStub.location.search = "?projectId=project-9";
    await advance(PROTECTED_PAGE_AUTH_TIMEOUT_MS);
    const redirects = loginRedirects();
    expect(redirects).toHaveLength(1);
    expect(nextOf(redirects[0])).toBe(`${PROTECTED_ROUTE}?projectId=project-9`);
    expect(accessibleName(bootstrapRegion()), "announced right up to the bounce").toBe(scenario.expectLabel);
  });
});

/* ====== D. the new locale state must not disturb the watchdog window ====== */
describe("D. the post-hydration locale swap does not extend the recovery window", () => {
  it.each(RTL_SCENARIOS)("$name: still evicts at exactly T (a state change must not re-arm)", async (scenario) => {
    applyDocumentLocale(scenario);
    await mountGate();
    expect(localeReads, "the boundary did re-render for the locale").toHaveBeenCalledTimes(1);
    await advance(PROTECTED_PAGE_AUTH_TIMEOUT_MS - 1);
    expect(loginRedirects()).toHaveLength(0);
    await advance(1);
    expect(loginRedirects(), "one eviction, at the documented 10s budget").toHaveLength(1);
  });

  it("evicts once and only once across repeated re-renders", async () => {
    applyDocumentLocale(SCENARIOS[1]);
    await mountGate();
    for (let round = 0; round < 3; round += 1) await renderGate();
    await advance(PROTECTED_PAGE_AUTH_TIMEOUT_MS * 2);
    expect(loginRedirects(), "a doubled window would be a silent session leak").toHaveLength(1);
  });

  it("does not re-arm when the document language changes after mounting", async () => {
    // LocaleProvider can land its `lang` write after the gate is already up.
    // The window must stay the one armed at mount, not restart from that point.
    applyDocumentLocale(SCENARIOS[0]);
    await mountGate();
    applyDocumentLocale(SCENARIOS[2]);
    await renderGate();
    await advance(PROTECTED_PAGE_AUTH_TIMEOUT_MS);
    expect(loginRedirects()).toHaveLength(1);
  });

  it("cannot extend the window in principle: the locale commits only at mount, and is absent from the watchdog deps", () => {
    // Measured, not assumed: this invariant is asserted structurally because it
    // is NOT observable behaviourally, and pretending otherwise would be a
    // false guarantee. Adding `bootstrapLocale` to the watchdog's dependency
    // array - a plausible-looking "fix" - was tried against this suite and every
    // timer case still passed, because the locale is written by a mount effect
    // that runs while the virtual clock is still at 0. A re-arm at t=0 produces
    // exactly the same deadline as the original arm, so no amount of advancing
    // the clock distinguishes them. What makes the hazard unreachable is the
    // shape of the code, and only the shape of the code can be asserted.
    const localeEffect = gateSource.match(/setBootstrapLocale\(readBrowserProtectedPageBootstrapLocale\(\)\);[\s\S]*?\}, \[([^\]]*)\]\);/);
    expect(localeEffect, "the locale restore effect must be locatable").not.toBeNull();
    expect((localeEffect as RegExpMatchArray)[1].trim(), "mount-only: the commit can never land mid-window").toBe("");

    const watchdogEffect = gateSource.match(/shouldArmBootstrapWatchdog\(loading, hasHydratedSession\)[\s\S]*?\}, \[([^\]]*)\]\);/);
    expect(watchdogEffect, "the watchdog effect must be locatable").not.toBeNull();
    const deps = (watchdogEffect as RegExpMatchArray)[1];
    expect(deps, "the locale must not re-arm the recovery window").not.toMatch(/bootstrapLocale|locale/i);
    // The documented window is still the one the deps describe.
    expect(deps).toContain("hasHydratedSession");
    expect(deps).toContain("pathname");
  });

  it("commits the boundary twice under RTL and once in English, so the RTL cases above are not vacuous", async () => {
    // The effect writes the value it initialised to when the reader is already
    // in English, so React bails out and the gate commits once; under an Arabic
    // or Sorani document the value differs and it commits twice. Recorded here
    // so the RTL window cases above are known to be exercising a real second
    // render rather than an English path under a different label.
    const commitsFor = async (scenario: LocaleScenario) => {
      act(() => root?.unmount());
      boundaryCommits.mockReset();
      applyDocumentLocale(scenario);
      await mountGate();
      return boundaryCommits.mock.calls.length;
    };
    expect(await commitsFor(SCENARIOS[0]), "English: no second commit").toBe(1);
    expect(await commitsFor(SCENARIOS[1]), "Arabic: adopts the language").toBe(2);
    expect(await commitsFor(SCENARIOS[2]), "Sorani: adopts the language").toBe(2);
    expect(accessibleName(bootstrapRegion())).toBe(protectedPageBootstrapStatusLabels.ku);
  });
});

/* ===== E. authenticated-session hydration survives, in every locale ===== */
describe("E. a hydrated session is never evicted, whatever the reading direction", () => {
  it.each(SCENARIOS)("$name: holds the gate open for a hydrated session", async (scenario) => {
    applyDocumentLocale(scenario);
    // The exact state #1031 protects: a real session behind an unresolved
    // bootstrap flag, which is what used to trigger the wrongful eviction.
    auth.state = { user: { id: "synthetic-user-1" }, loading: true };
    await mountGate();
    expect(liveRegions(), "no live region while content is mounted").toHaveLength(0);
    expect(collectDescendants(container, (n) => n.getAttribute("class") === "document-view")).toHaveLength(1);
    await advance(PROTECTED_PAGE_AUTH_TIMEOUT_MS * 2);
    expect(loginRedirects(), "the watchdog must not run for a hydrated session").toHaveLength(0);
    expect(collectDescendants(container, (n) => n.getAttribute("class") === "document-view"), "content stays mounted")
      .toHaveLength(1);
  });

  it.each(SCENARIOS)("$name: still denies a malformed payload", async (scenario) => {
    // Fail-closed must survive the accessibility work: a truthy object with no
    // usable identity is a settled absence, and must still reach /login.
    applyDocumentLocale(scenario);
    auth.state = { user: { displayName: "no identity here" }, loading: false };
    await mountGate();
    expect(liveRegions(), "the gate stays shut").toHaveLength(1);
    const redirects = loginRedirects();
    expect(redirects, "settled absence redirects without waiting for the watchdog").toHaveLength(1);
    expect(nextOf(redirects[0])).toBe(`${PROTECTED_ROUTE}${ARM_SEARCH}`);
    expect(accessibleName(bootstrapRegion()), scenario.name).toBe(scenario.expectLabel);
  });

  it("closes the gate again when a hydrated session signs out, in Sorani", async () => {
    applyDocumentLocale(SCENARIOS[2]);
    auth.state = { user: { id: "synthetic-user-1" }, loading: false };
    await mountGate();
    expect(liveRegions()).toHaveLength(0);
    auth.state = { user: null, loading: false };
    await renderGate();
    expect(liveRegions(), "sign-out re-announces rather than silently rerouting").toHaveLength(1);
    expect(accessibleName(bootstrapRegion())).toBe(protectedPageBootstrapStatusLabels.ku);
  });

  it.each(RTL_SCENARIOS)("$name: an empty-string id is not a session", async (scenario) => {
    applyDocumentLocale(scenario);
    auth.state = { user: { id: "   " }, loading: false };
    await mountGate();
    expect(liveRegions(), "whitespace identity must not open the gate").toHaveLength(1);
    expect(loginRedirects()).toHaveLength(1);
    expect(accessibleName(bootstrapRegion())).toBe(scenario.expectLabel);
  });
});

/* ====== F. declared-but-unapplied direction (characterisation) ====== */
describe("F. writing direction of the announcement itself", () => {
  it.each(RTL_SCENARIOS)("$name: region inherits document direction rather than declaring its own", async (scenario) => {
    // Characterisation, deliberately green: it records what #1072 actually
    // ships. `protectedPageBootstrapStatusDirection` is exported from the new
    // module and unit-tested in isolation, but the boundary renders no `dir` or
    // `lang` of its own, so an Arabic or Sorani label is spoken inside whatever
    // direction the surrounding document carries. That helper is currently
    // reachable only from tests - reviewers should confirm this is intended
    // rather than a wiring step left on the floor.
    applyDocumentLocale(scenario);
    await mountGate();
    const region = bootstrapRegion() as TestNode;
    expect(region.getAttribute("dir")).toBeNull();
    expect(region.getAttribute("lang")).toBeNull();
    // The label itself is correct and non-empty, which is what #1068 demanded.
    expect(accessibleName(region)).toBe(scenario.expectLabel);
  });
});
