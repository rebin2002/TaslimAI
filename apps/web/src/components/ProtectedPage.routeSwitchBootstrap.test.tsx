/**
 * Independent QA regression for the #1031 protected-page hydrated-session
 * gate: the bootstrap recovery watchdog across a CLIENT-SIDE ROUTE SWITCH.
 *
 * Why this file exists (gap audit against the lane's existing suites and the
 * sibling ProtectedPage lanes, all of which were left untouched):
 *
 *   - `ProtectedPage.test.tsx` pins the exported predicates and the
 *     render-time markup through a mocked `useAuth`, and it renders with
 *     `renderToStaticMarkup`. That path never runs effects, so the watchdog
 *     `setTimeout` and its cleanup - the exact mechanism that has to cancel an
 *     eviction for a signed-in user - are never executed there. Its
 *     `usePathname` is the constant `"/projects"`.
 *   - `ProtectedPage.bootstrapRace.test.tsx` drives the real `AuthProvider`
 *     over a deferred session read and shows that a session hydrating 1, 250,
 *     5_000 or `TIMEOUT - 1` ms into the window survives. Its `usePathname`
 *     mock is the constant `PROTECTED_ROUTE`, so every one of those timelines
 *     happens on a single route.
 *   - The sibling lanes vary `window.location.search` while the watchdog
 *     window is open (query fidelity and the live-region name). Neither of
 *     them mutates the pathname either.
 *
 * So no test in the repo pins what happens when the user *moves between
 * protected routes* while the mount-time session read is still unresolved -
 * the state the report actually describes, because a hydrated session whose
 * bootstrap flag never settles is exactly the state a client-side navigation
 * carries with it. `pathname` is a dependency of both gate effects, so a
 * route switch tears down and re-runs the very effect that owns the eviction
 * timer. That is a distinct code path, and a gate could pass every existing
 * test while still getting it wrong in two opposite directions: re-arming the
 * eviction for a signed-in user after the switch (the #923 defect, one route
 * later), or becoming sticky so a signed-out visitor is never recovered and
 * the deep link they are actually on is lost.
 *
 * Both directions are pinned here, and the two survival cases are paired with
 * structurally identical controls so neither can pass by accident: the
 * control shows the timer was genuinely live and merely re-armed by the
 * switch, not dead.
 *
 * Provider-free and synthetic: the gate is exercised through its own
 * `useAuth` / `next/navigation` seams over the dependency-free DOM shim the
 * lane already uses (`createRoot` + `act` + virtual clock, no jsdom). Nothing
 * here imports the auth provider or the API client, touches a transport,
 * charges a provider, or uses a real session, account or document.
 */
import { act, createElement, type ReactElement } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// One route object for the whole tree, matching production: Next's `useRouter`
// keeps a stable identity across renders, so the only thing that can re-run the
// gate's effects here is the auth state or the pathname under test. A fresh
// object per render would re-arm the recovery window on unrelated re-renders
// and hide exactly the behaviour these cases are about.
const nav = vi.hoisted(() => {
  const replace = vi.fn();
  return {
    replace,
    router: { replace, push: vi.fn(), prefetch: vi.fn() },
    pathname: "/create/research",
  };
});

const session = vi.hoisted(() => ({ state: { user: null as unknown, loading: true } }));

vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => ({
    user: session.state.user,
    loading: session.state.loading,
    signIn: vi.fn(),
    signOut: vi.fn(),
    refresh: vi.fn(),
  }),
}));

vi.mock("next/navigation", () => ({
  useRouter: () => nav.router,
  usePathname: () => nav.pathname,
}));

import { PROTECTED_PAGE_AUTH_TIMEOUT_MS, ProtectedPage } from "./ProtectedPage";

const FIRST_ROUTE = "/create/research";
const SECOND_ROUTE = "/personal/health";
// Held constant on purpose: query-string fidelity across the window is another
// lane's axis. It is carried here only so the eviction destination can be
// decoded unambiguously.
const LOCATION_SEARCH = "?docId=document-1";
const LOGIN_PREFIX = "/login?next=";

const PROTECTED_VIEW = createElement(
  "div",
  { className: "project-view" },
  "synthetic protected content",
) as ReactElement;

/** A signed-in account: a stable string identity, as the server publishes it. */
const HYDRATED_USER = { id: "synthetic-user-1", onboardingCompletedAt: null };
/** Truthy but unusable: not proof of a session, so the gate must stay shut. */
const MALFORMED_SESSION = { email: "owner@example.test" };

/* --------------------------------------------------------------- shim ---- */
/**
 * The minimum DOM surface `react-dom/client` needs to commit this tree, in the
 * same dependency-free style as the lane's other harnesses.
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
  get children(): TestNode[] { return this.childNodes.filter((child) => child.nodeType === 1); }
  get textContent() { return this.childNodes.map((child) => child.textContent ?? "").join(""); }
  set textContent(value: string | null) {
    this.childNodes = value ? [new TestNodeText(value, this.ownerDocument)] : [];
    for (const child of this.childNodes) child.parentNode = this;
  }
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
  addEventListener(name: string, listener: (event: unknown) => void) {
    const listeners = this.listeners.get(name) ?? new Set();
    listeners.add(listener);
    this.listeners.set(name, listeners);
  }
  removeEventListener(name: string, listener: (event: unknown) => void) {
    this.listeners.get(name)?.delete(listener);
  }
  contains(node: TestNode | null): boolean {
    return node === this || this.childNodes.some((child) => child.contains(node));
  }
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
  documentElement: TestNode;
  defaultView: object;

  constructor() {
    super(9, "#document", null as unknown as TestDocument);
    this.documentElement = new TestNode(1, "html", this);
    this.body = new TestNode(1, "body", this);
    this.documentElement.appendChild(this.body);
    this.appendChild(this.documentElement);
    this.defaultView = {};
  }

  createElement(name: string) { return new TestNode(1, name.toUpperCase(), this); }
  createElementNS(_namespace: string, name: string) { return this.createElement(name); }
  createTextNode(value: string) { return new TestNodeText(value, this.ownerDocument ?? this); }
}

function findAll(node: TestNode, predicate: (candidate: TestNode) => boolean): TestNode[] {
  const found: TestNode[] = [];
  const walk = (current: TestNode) => {
    if (current.nodeType === 1 && predicate(current)) found.push(current);
    for (const child of current.childNodes) walk(child);
  };
  walk(node);
  return found;
}

/* ------------------------------------------------------------ harness ---- */
let dom: TestDocument;
let container: TestNode;
let root: Root | null = null;

/** The gate's own recovery window, observed through the virtual clock. */
function advance(ms: number) {
  return act(async () => {
    vi.advanceTimersByTime(ms);
    for (let tick = 0; tick < 8; tick += 1) await Promise.resolve();
  });
}

/**
 * Commits the gate again. In the browser a `useAuth` transition and a route
 * change are both a re-render of the same tree, so this is the harness's
 * faithful equivalent of "the provider settled" / "the user navigated".
 */
function commit() {
  return act(async () => {
    if (!root) root = createRoot(container as unknown as Element);
    root.render(createElement(ProtectedPage, null, PROTECTED_VIEW));
    for (let tick = 0; tick < 8; tick += 1) await Promise.resolve();
  });
}

/** The user completes sign-in while the bootstrap flag stays unresolved. */
function hydrateSession(user: unknown = HYDRATED_USER) {
  session.state = { user, loading: true };
  return commit();
}

/** The user navigates to another protected route, leaving the auth state as-is. */
function switchRoute(pathname: string) {
  nav.pathname = pathname;
  return commit();
}

function loginRedirects(): string[] {
  return nav.router.replace.mock.calls
    .map((call) => String(call[0]))
    .filter((target) => target.startsWith(LOGIN_PREFIX));
}

function returnPathOf(redirect: string): string {
  return new URL(redirect, "http://protected.test").searchParams.get("next") ?? "";
}

function showsProtectedContent(): boolean {
  return findAll(container, (node) => node.className === "project-view").length === 1;
}

function showsBootstrapSpinner(): boolean {
  return findAll(container, (node) => node.className === "loading-state").length === 1;
}

beforeEach(() => {
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "Date"] });
  dom = new TestDocument();
  container = dom.createElement("div");
  dom.body.appendChild(container);
  root = null;
  session.state = { user: null, loading: true };
  nav.pathname = FIRST_ROUTE;
  nav.router.replace.mockReset();
  vi.stubGlobal("document", dom);
  const windowStub = {
    document: dom,
    location: { search: LOCATION_SEARCH },
    HTMLElement: TestNode,
    HTMLIFrameElement: TestNode,
    Element: TestNode,
    Node: TestNode,
    // Deferred lookup binds the gate's `window.setTimeout` to the virtual
    // clock installed above, which is what makes the recovery window
    // observable rather than waited on.
    setTimeout: (handler: () => void, delay: number) => setTimeout(handler, delay),
    clearTimeout: (handle: ReturnType<typeof setTimeout>) => clearTimeout(handle),
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  };
  dom.defaultView = windowStub;
  vi.stubGlobal("window", windowStub);
  vi.stubGlobal("IS_REACT_ACT_ENVIRONMENT", true);
});

afterEach(() => {
  act(() => root?.unmount());
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe("protected-page bootstrap watchdog across a client-side route switch", () => {
  it("cancels the eviction armed for the first route when the session hydrates, even after the user navigates", async () => {
    // The reported timeline, one navigation later: the session read issued at
    // mount is still in flight, so the recovery watchdog arms for
    // /create/research; sign-in then completes and supersedes that read, which
    // leaves a real session behind a permanently unresolved bootstrap flag;
    // the user then moves to another protected page. The pending timer was
    // armed for a signed-out visitor who no longer exists, so the route switch
    // must re-run the effect into the *disarmed* branch rather than re-arm it.
    await commit();
    expect(showsBootstrapSpinner(), "stalled bootstrap before sign-in").toBe(true);

    await advance(5_000);
    await hydrateSession();

    await advance(1_000);
    await switchRoute(SECOND_ROUTE);

    await advance(PROTECTED_PAGE_AUTH_TIMEOUT_MS * 3);
    expect(loginRedirects(), "a hydrated session must never be evicted after navigating").toEqual([]);
    expect(showsProtectedContent(), "the signed-in view stays mounted on the new route").toBe(true);
    expect(showsBootstrapSpinner(), "no bootstrap boundary after hydration").toBe(false);
  });

  it("cancels the eviction when the user navigates first and the session hydrates later in the window", async () => {
    // The other interleaving of the same two events, because which one lands
    // first is network timing rather than application logic: navigating
    // re-arms the window for the new route, and the sign-in that settles
    // inside that fresh window has to tear it down again.
    await commit();
    await advance(3_000);
    await switchRoute(SECOND_ROUTE);

    await advance(3_000);
    await hydrateSession();

    await advance(PROTECTED_PAGE_AUTH_TIMEOUT_MS * 3);
    expect(loginRedirects(), "hydration after a route switch must still disarm the watchdog").toEqual([]);
    expect(showsProtectedContent(), "the signed-in view stays mounted").toBe(true);
  });

  it("re-arms the recovery window for the route the user switched to when no session ever hydrates", async () => {
    // Paired control for the two cases above, and deliberately identical in
    // shape: nothing hydrates, so the gate must still recover. If the survival
    // cases were passing because the timer was never live, this would fail.
    // It also pins that switching routes restarts the window and re-points the
    // destination at the route the visitor is actually on, rather than
    // silently evicting on the deadline measured from the route they left.
    await commit();
    await advance(5_000);
    await switchRoute(SECOND_ROUTE);

    await advance(5_000);
    expect(loginRedirects(), "the deadline is measured from the switch, not from mount").toEqual([]);

    await advance(5_001);
    const redirects = loginRedirects();
    expect(redirects, "a signed-out bootstrap is still recovered exactly once").toHaveLength(1);
    expect(returnPathOf(redirects[0])).toBe(`${SECOND_ROUTE}${LOCATION_SEARCH}`);
    expect(showsBootstrapSpinner()).toBe(true);
    expect(showsProtectedContent()).toBe(false);
  });

  it("keeps the gate fail-closed for a malformed payload across a route switch", async () => {
    // The fix must not become sticky in either direction: a truthy object with
    // no usable identity is not a hydrated session, so navigating cannot make
    // it the thing that suppresses recovery.
    await commit();
    await hydrateSession(MALFORMED_SESSION);
    await advance(5_000);
    await switchRoute(SECOND_ROUTE);

    await advance(PROTECTED_PAGE_AUTH_TIMEOUT_MS + 1);
    const redirects = loginRedirects();
    expect(redirects, "a malformed payload never holds the gate open").toHaveLength(1);
    expect(returnPathOf(redirects[0])).toBe(`${SECOND_ROUTE}${LOCATION_SEARCH}`);
    expect(showsProtectedContent(), "malformed identity never mounts protected children").toBe(false);
  });
});
