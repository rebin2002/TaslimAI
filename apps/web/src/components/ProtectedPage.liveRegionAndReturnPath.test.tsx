import { act, type ReactElement } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

/**
 * Test-only acceptance suite for the two non-blocking findings recorded on PR
 * #1031 by independent QA, reproduced against current main:
 *
 *   1. Accessible name (QA finding 2). The bootstrap boundary is a
 *      `role="status" aria-live="polite"` live region whose only descendant is
 *      an `aria-hidden` spinner and which carries no `aria-label`, so the
 *      accname algorithm resolves an empty string: a screen-reader user on a
 *      stalled or expiring session hears nothing, then an unannounced route
 *      change. The committed assertion for this boundary is a source-text
 *      `toContain('aria-live="polite"')`, which passes no matter what the
 *      rendered region is named, so nothing in CI can see the silence.
 *   2. Stale redirect query (QA finding 3). The recovery watchdog snapshots
 *      `window.location.search` when it *arms* while the settled-absence
 *      redirect reads it at *fire* time, so a protected view that rewrites its
 *      own query during a stalled bootstrap evicts the user to a deep link they
 *      were no longer on.
 *
 * These are the *announcement* and *destination-fidelity* properties, which
 * `ProtectedPage.bootstrapRace.test.tsx` does not assert: that file drives the
 * real `AuthProvider` to pin eviction/survival of a hydrated session, and never
 * inspects the accessible name of the boundary or a query that changes while
 * the watchdog window is open. Nothing here mounts the auth provider.
 *
 * Convention: the repo's dependency-free DOM shim (`createRoot` + `act` +
 * virtual clock, no jsdom) already used by the lane's other tests. Synthetic
 * fixtures only - no accounts, network, providers or secrets.
 */

const PROTECTED_ROUTE = "/create/research";
const ARM_SEARCH = "?projectId=project-1";
const FIRE_SEARCH = "?projectId=project-2";
const LOGIN_PREFIX = "/login?next=";

/* --------------------------------------------------------------- shim ---- */
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
  get id() { return this.attributes.get("id") ?? ""; }
  set id(value: string) { this.attributes.set("id", value); }
  get children(): TestNode[] { return this.childNodes.filter((child) => child.nodeType === 1); }
  get textContent() { return this.childNodes.map((child) => child.textContent ?? "").join(""); }
  set textContent(value: string | null) {
    this.childNodes = value ? [new TestNodeText(value, this.ownerDocument)] : [];
    for (const child of this.childNodes) child.parentNode = this;
  }
  get className() { return this.attributes.get("class") ?? ""; }
  set className(value: string) { this.attributes.set("class", value); }
  appendChild<T extends TestNode>(child: T) { if (child.parentNode) child.parentNode.removeChild(child); child.parentNode = this; this.childNodes.push(child); return child; }
  insertBefore<T extends TestNode>(child: T, before: TestNode | null) { if (!before) return this.appendChild(child); const index = this.childNodes.indexOf(before); if (index < 0) return this.appendChild(child); if (child.parentNode) child.parentNode.removeChild(child); child.parentNode = this; this.childNodes.splice(index, 0, child); return child; }
  removeChild<T extends TestNode>(child: T) { const index = this.childNodes.indexOf(child); if (index >= 0) this.childNodes.splice(index, 1); child.parentNode = null; return child; }
  setAttribute(name: string, value: string) { this.attributes.set(name, String(value)); }
  getAttribute(name: string) { return this.attributes.get(name) ?? null; }
  removeAttribute(name: string) { this.attributes.delete(name); }
  hasAttribute(name: string) { return this.attributes.has(name); }
  addEventListener(name: string, listener: (event: unknown) => void) { const listeners = this.listeners.get(name) ?? new Set(); listeners.add(listener); this.listeners.set(name, listeners); }
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
  getElementById(id: string): TestNode | null {
    const walk = (node: TestNode): TestNode | null => {
      if (node.nodeType === 1 && node.getAttribute("id") === id) return node;
      for (const child of node.childNodes) { const found = walk(child); if (found) return found; }
      return null;
    };
    return walk(this);
  }
}

/* ------------------------------------------------- accname (subset) ---- */
/**
 * The portion of the accessible-name computation the live region actually
 * needs: authored `aria-label`, else `aria-labelledby`, else the concatenated
 * text of included descendants, with `aria-hidden` subtrees removed - the
 * reason a decorative spinner cannot name its own region.
 */
function includedText(node: TestNode): string {
  if (node.nodeType === 3) return String(node.textContent ?? "");
  if (node.getAttribute("aria-hidden") === "true") return "";
  return node.childNodes.map(includedText).join(" ");
}
function accessibleName(node: TestNode): string {
  const label = node.getAttribute("aria-label");
  if (label !== null && label.trim().length > 0) return label.trim();
  const labelledBy = node.getAttribute("aria-labelledby");
  if (labelledBy !== null) {
    const referred = labelledBy
      .split(/\s+/)
      .map((id) => dom.getElementById(id)?.textContent ?? "")
      .join(" ")
      .trim();
    if (referred.length > 0) return referred;
  }
  return includedText(node).replace(/\s+/g, " ").trim();
}
function findAll(node: TestNode, predicate: (n: TestNode) => boolean): TestNode[] {
  const found: TestNode[] = [];
  const walk = (current: TestNode) => {
    if (current.nodeType === 1 && predicate(current)) found.push(current);
    for (const child of current.childNodes) walk(child);
  };
  walk(node);
  return found;
}
/** The rendered announcement boundary, i.e. whatever the gate shows instead of children. */
function liveRegions(): TestNode[] {
  return findAll(container, (n) => n.getAttribute("role") === "status" || n.getAttribute("aria-live") !== null);
}

/* ------------------------------------------------------------ harness ---- */
const auth = vi.hoisted(() => ({ state: { user: null as unknown, loading: true } }));
const nav = vi.hoisted(() => ({
  replace: vi.fn(),
  pathname: "/create/research",
}));
vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => ({ user: auth.state.user, loading: auth.state.loading, signOut: vi.fn(), refresh: vi.fn() }),
}));
vi.mock("next/navigation", () => ({
  useRouter: () => ({ replace: nav.replace, push: vi.fn(), prefetch: vi.fn() }),
  usePathname: () => nav.pathname,
}));

import { PROTECTED_PAGE_AUTH_TIMEOUT_MS, ProtectedPage } from "./ProtectedPage";

let dom: TestDocument;
let container: TestNode;
let root: Root;
let windowStub: { location: { search: string }; [key: string]: unknown };

function advance(ms: number) {
  return act(async () => {
    vi.advanceTimersByTime(ms);
    for (let tick = 0; tick < 8; tick += 1) await Promise.resolve();
  });
}
function mount() {
  return act(async () => {
    root = createRoot(container as unknown as Element);
    root.render(<ProtectedPage>{<div className="research-view">research brief</div> as ReactElement}</ProtectedPage>);
    vi.advanceTimersByTime(0);
    for (let tick = 0; tick < 8; tick += 1) await Promise.resolve();
  });
}
function loginRedirects(): string[] {
  return nav.replace.mock.calls.map((call) => String(call[0])).filter((target) => target.startsWith(LOGIN_PREFIX));
}
function nextOf(redirect: string): string {
  return new URL(redirect, "http://protected.test").searchParams.get("next") ?? "";
}
/** Put the gate in the stalled-bootstrap state the findings describe. */
function stalledBootstrap() {
  auth.state = { user: null, loading: true };
}

beforeEach(() => {
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "Date"] });
  dom = new TestDocument();
  container = dom.createElement("div");
  dom.body.appendChild(container);
  stalledBootstrap();
  nav.pathname = PROTECTED_ROUTE;
  nav.replace.mockReset();
  vi.stubGlobal("document", dom);
  windowStub = {
    document: dom,
    location: { search: ARM_SEARCH },
    HTMLElement: TestNode,
    HTMLIFrameElement: TestNode,
    Element: TestNode,
    Node: TestNode,
    // Deferred lookup binds the gate's timer to the virtual clock installed
    // above, so the recovery window is observable rather than waited on.
    setTimeout: (handler: () => void, delay: number) => setTimeout(handler, delay),
    clearTimeout: (handle: ReturnType<typeof setTimeout>) => clearTimeout(handle),
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
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

describe("protected-page bootstrap boundary, announced to assistive tech", () => {
  it("renders one polite live region while the session read is stalled", async () => {
    await mount();
    const regions = liveRegions();
    expect(regions).toHaveLength(1);
    expect(regions[0].getAttribute("aria-live")).toBe("polite");
    expect(regions[0].getAttribute("aria-busy")).toBe("true");
    // The spinner stays decorative, which is what makes the name the only
    // thing a screen reader can announce.
    expect(findAll(regions[0], (n) => n.getAttribute("aria-hidden") === "true").length).toBeGreaterThan(0);
  });

  it("names the live region so the stalled bootstrap is actually announced", async () => {
    await mount();
    const [region] = liveRegions();
    // QA finding 2: an unnamed `role="status"` region announces nothing, and
    // no axe rule requires a name, so this is invisible to the e2e scan.
    expect(accessibleName(region)).not.toBe("");
  });

  it("carries an announcement that a screen reader can render as text", async () => {
    await mount();
    const [region] = liveRegions();
    const name = accessibleName(region);
    expect(name.trim().length).toBeGreaterThan(0);
    expect(/[A-Za-z\u0600-\u06FF\u0750-\u077A]/.test(name)).toBe(true);
  });

  it("keeps the boundary announced when a settled session expires back to loading", async () => {
    // The other path into the same region: content mounted, then the session
    // settles as absent. The user must still be told what is happening rather
    // than being silently rerouted.
    auth.state = { user: { id: "user-1" }, loading: false };
    await mount();
    auth.state = { user: null, loading: false };
    await act(async () => {
      root.render(<ProtectedPage>{<div className="research-view">research brief</div> as ReactElement}</ProtectedPage>);
      for (let tick = 0; tick < 8; tick += 1) await Promise.resolve();
    });
    const regions = liveRegions();
    expect(regions).toHaveLength(1);
    expect(accessibleName(regions[0])).not.toBe("");
  });
});

describe("protected-page recovery redirect keeps the deep link the user is on", () => {
  it("fires the watchdog at the end of the bootstrap window (control, no query change)", async () => {
    await mount();
    await advance(PROTECTED_PAGE_AUTH_TIMEOUT_MS - 1);
    expect(loginRedirects()).toHaveLength(0);
    await advance(1);
    const redirects = loginRedirects();
    expect(redirects).toHaveLength(1);
    expect(nextOf(redirects[0])).toBe(`${PROTECTED_ROUTE}${ARM_SEARCH}`);
  });

  it("returns the query that is live when the watchdog fires, not when it armed", async () => {
    await mount();
    await advance(PROTECTED_PAGE_AUTH_TIMEOUT_MS - 1);
    // A protected view hydrates its working state from the query string and is
    // free to rewrite it; the user is now on project-2.
    windowStub.location.search = FIRE_SEARCH;
    await advance(1);
    const redirects = loginRedirects();
    expect(redirects).toHaveLength(1);
    // QA finding 3: the arm-time snapshot returns the deep link the user left
    // behind, so the signed-out bounce restores a stale project.
    expect(nextOf(redirects[0])).toBe(`${PROTECTED_ROUTE}${FIRE_SEARCH}`);
  });

  it("agrees with the settled-absence path on the same location change", async () => {
    // The settled-absence effect already reads window.location.search at fire
    // time; both eviction paths must produce the same destination for the same
    // location, otherwise which one fires decides whether the link is fresh.
    await mount();
    windowStub.location.search = FIRE_SEARCH;
    auth.state = { user: null, loading: false };
    await act(async () => {
      root.render(<ProtectedPage>{<div className="research-view">research brief</div> as ReactElement}</ProtectedPage>);
      for (let tick = 0; tick < 8; tick += 1) await Promise.resolve();
    });
    const settled = loginRedirects();
    expect(settled).toHaveLength(1);
    const viaSettled = nextOf(settled[0]);

    nav.replace.mockReset();
    stalledBootstrap();
    windowStub.location.search = ARM_SEARCH;
    await act(async () => {
      root.render(<ProtectedPage>{<div className="research-view">research brief</div> as ReactElement}</ProtectedPage>);
      for (let tick = 0; tick < 8; tick += 1) await Promise.resolve();
    });
    windowStub.location.search = FIRE_SEARCH;
    await advance(PROTECTED_PAGE_AUTH_TIMEOUT_MS);
    const viaWatchdog = loginRedirects();
    expect(viaWatchdog).toHaveLength(1);
    expect(nextOf(viaWatchdog[0])).toBe(viaSettled);
  });
});
