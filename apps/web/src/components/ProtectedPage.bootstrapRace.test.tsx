/**
 * Intermittent session-bootstrap race at the protected-page gate (#1031 lane,
 * current main, #923 family).
 *
 * ProtectedPage.test.tsx pins the exported predicates and the render-time markup
 * through a mocked `useAuth`. This file traverses the race instead: the REAL
 * `AuthProvider` and the REAL `ProtectedPage` are mounted together over a
 * deferred session read, the sign-in transition is driven through the
 * provider's own `signIn`, and the recovery watchdog is fired by moving a
 * virtual clock. The eviction is therefore observed as the redirect a signed-in
 * user actually receives, not as an assertion about source text.
 *
 * The race, from the provider's own source: `refresh()`, `signIn()` and
 * `signOut()` share one monotonic `authRequestGeneration`. The read issued at
 * mount takes generation 1; completing a sign-in takes generation 2 and
 * publishes the session. When generation 1 finally settles - it is still in
 * flight when sign-in wins, which is exactly why the defect is intermittent -
 * its `finally` branch finds itself superseded and returns *without* clearing
 * `loading`. The page then holds a fully hydrated session behind a permanently
 * unresolved bootstrap flag. On the pre-fix gate that stale flag both hid the
 * protected content and left the recovery watchdog armed, so after
 * `PROTECTED_PAGE_AUTH_TIMEOUT_MS` the signed-in user - a first-login account
 * mid-onboarding among them - was evicted to `/login` despite a valid,
 * persistent session.
 *
 * The security half of the contract is pinned just as hard, so the fix can
 * never be traded for a fail-open gate: a settled absence, a stalled signed-out
 * bootstrap and a malformed payload must all still be refused.
 *
 * Synthetic harness in the established style of
 * AuthProvider.localePersistence.test.tsx and
 * FullMovieWorkspaceView.storyAutosave.test.tsx: no jsdom, no network, no real
 * transport, fixed anonymous fixtures only.
 */
import { act } from "react";
import * as React from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { AuthResponse, LoginInput, User } from "@/lib/api";

const harness = vi.hoisted(() => ({
  me: vi.fn(),
  login: vi.fn(),
  logout: vi.fn(),
  register: vi.fn(),
  updateProfile: vi.fn(),
  completeOnboarding: vi.fn(),
  replace: vi.fn(),
  push: vi.fn(),
}));

vi.mock("@/lib/api", () => ({
  api: {
    me: () => harness.me(),
    login: (input: LoginInput) => harness.login(input),
    logout: () => harness.logout(),
    register: (input: unknown) => harness.register(input),
    updateProfile: (input: unknown) => harness.updateProfile(input),
    completeOnboarding: (input: unknown) => harness.completeOnboarding(input),
  },
}));
vi.mock("next/navigation", () => ({
  // One router object for the whole tree, so the gate's eviction redirect and
  // the provider's own post-login navigation are distinguishable by target.
  useRouter: () => ({ push: harness.push, replace: harness.replace }),
  usePathname: () => PROTECTED_ROUTE,
}));

// The deep link the report was filed against: an unsaved research brief opened
// straight from an email, so the query string is part of the destination.
const PROTECTED_ROUTE = "/create/research";
const LOCATION_SEARCH = "?projectId=project-1";
const EXPECTED_LOGIN_REDIRECT = `/login?next=${encodeURIComponent(`${PROTECTED_ROUTE}${LOCATION_SEARCH}`)}`;

import { PROTECTED_PAGE_AUTH_TIMEOUT_MS } from "./ProtectedPage";
import { AuthProvider, useAuth } from "./AuthProvider";
import { LocaleProvider } from "./LocaleProvider";
import { ProtectedPage } from "./ProtectedPage";

/* Deterministic synthetic DOM, sufficient for react-dom/client rendering. */
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
  appendChild<T extends TestNode>(child: T) { if (child.parentNode) child.parentNode.removeChild(child); child.parentNode = this; this.childNodes.push(child); return child; }
  insertBefore<T extends TestNode>(child: T, before: TestNode | null) { if (!before) return this.appendChild(child); const index = this.childNodes.indexOf(before); if (index < 0) return this.appendChild(child); if (child.parentNode) child.parentNode.removeChild(child); child.parentNode = this; this.childNodes.splice(index, 0, child); return child; }
  removeChild<T extends TestNode>(child: T) { const index = this.childNodes.indexOf(child); if (index >= 0) this.childNodes.splice(index, 1); child.parentNode = null; return child; }
  setAttribute(name: string, value: string) { this.attributes.set(name, String(value)); }
  getAttribute(name: string) { return this.attributes.get(name) ?? null; }
  removeAttribute(name: string) { this.attributes.delete(name); }
  addEventListener(name: string, listener: (event: unknown) => void) { const listeners = this.listeners.get(name) ?? new Set(); listeners.add(listener); this.listeners.set(name, listeners); }
  removeEventListener(name: string, listener: (event: unknown) => void) { this.listeners.get(name)?.delete(listener); }
  contains(node: TestNode | null): boolean { return node === this || this.childNodes.some((child) => child.contains(node)); }
}
class TestNodeText extends TestNode {
  nodeValue: string;
  constructor(value: string, ownerDocument: TestDocument) { super(3, "#text", ownerDocument); this.nodeValue = value; }
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

function memoryStorage(): Storage {
  const values = new Map<string, string>();
  return {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, String(value)); },
    removeItem: (key: string) => { values.delete(key); },
    clear: () => values.clear(),
    key: (index: number) => [...values.keys()][index] ?? null,
    get length() { return values.size; },
  } as unknown as Storage;
}

type Deferred<T> = { promise: Promise<T>; resolve: (value: T) => void; reject: (reason?: unknown) => void };
function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}

// Fixed anonymous fixtures. `id` is the stable identity the hardened predicate
// requires; `onboardingCompletedAt: null` is the first-login account the defect
// stranded back on the sign-in screen.
const RETURNING_USER: User = {
  id: "user-returning",
  email: "returning@example.test",
  displayName: "Fixture Author",
  preferredLanguage: "en",
  personalWorkspaceId: "workspace-1",
  createdAt: "2026-11-01T00:00:00Z",
  defaultGenerationLanguage: "en",
  timeZone: "UTC",
  outputPreference: "balanced",
  includeSourceLinks: true,
  onboardingCompletedAt: "2026-11-01T00:00:00Z",
  onboardingIntent: "chat",
  isAdmin: false,
};
const FIRST_LOGIN_USER: User = { ...RETURNING_USER, id: "user-first-login", onboardingCompletedAt: null };

function sessionFor(user: User): AuthResponse {
  return {
    user,
    personalWorkspace: { id: "workspace-1", name: "Personal", slug: "personal", type: "personal", role: "owner" },
  };
}
// A truthy session body that carries no stable identity: the shape a misrouted
// proxy or a placeholder response produces. It is not proof of a session.
const MALFORMED_SESSION = {
  user: { email: "placeholder@example.test" },
  personalWorkspace: { id: "workspace-1", name: "Personal", slug: "personal", type: "personal", role: "owner" },
} as unknown as AuthResponse;

// Captures the provider's live state and its two transition triggers, so the
// race is driven through the same entry points the login form uses.
let signIn: ((input: LoginInput, redirectTo?: string) => Promise<void>) | null = null;
let signOut: (() => Promise<void>) | null = null;
let providerState: { user: User | null; loading: boolean } = { user: null, loading: true };

function AuthProbe() {
  const auth = useAuth();
  // Recording runs in an effect so the probe stays a pure render function.
  React.useEffect(() => {
    providerState = { user: auth.user, loading: auth.loading };
    signIn = auth.signIn;
    signOut = auth.signOut;
  });
  return null;
}

function Host() {
  return (
    <LocaleProvider>
      <AuthProvider>
        <AuthProbe />
        <ProtectedPage>
          <div className="research-view">research brief</div>
        </ProtectedPage>
      </AuthProvider>
    </LocaleProvider>
  );
}

let storage: Storage;
let dom: TestDocument;
let container: TestNode;
let root: Root;

function renderState() {
  const classes: string[] = [];
  const walk = (node: TestNode) => {
    if (node.nodeType === 1) classes.push(node.className);
    for (const child of node.childNodes) walk(child);
  };
  walk(container);
  return {
    text: container.textContent,
    showsProtectedContent: classes.includes("research-view"),
    showsBootstrapSpinner: classes.includes("loading-state"),
  };
}
/** Only the gate's own eviction targets: the provider navigates elsewhere. */
function loginRedirects(): string[] {
  return harness.replace.mock.calls
    .map((call) => String(call[0]))
    .filter((target) => target.startsWith("/login"));
}

// Fake timers are installed before mount, so the watchdog the gate arms on the
// first commit is a virtual one and the ten-second eviction window is
// observable without waiting on it per case.
function drain() {
  return act(async () => {
    vi.advanceTimersByTime(0);
    for (let tick = 0; tick < 8; tick += 1) await Promise.resolve();
  });
}
function advance(ms: number) {
  return act(async () => {
    vi.advanceTimersByTime(ms);
    for (let tick = 0; tick < 8; tick += 1) await Promise.resolve();
  });
}
async function mount(): Promise<void> {
  await act(async () => {
    root = createRoot(container as unknown as Element);
    root.render(<Host />);
    vi.advanceTimersByTime(0);
    for (let tick = 0; tick < 8; tick += 1) await Promise.resolve();
  });
}
/** Complete a sign-in while the mount-time session read is still in flight. */
function signInDuringStalledRead(email: string) {
  return act(async () => {
    const pending = signIn?.({ email, password: "correct horse battery staple" });
    vi.advanceTimersByTime(0);
    for (let tick = 0; tick < 8; tick += 1) await Promise.resolve();
    await pending;
    for (let tick = 0; tick < 8; tick += 1) await Promise.resolve();
  });
}

beforeEach(() => {
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "Date"] });
  storage = memoryStorage();
  dom = new TestDocument();
  container = dom.createElement("div");
  dom.body.appendChild(container);
  signIn = null;
  signOut = null;
  providerState = { user: null, loading: true };
  vi.stubGlobal("document", dom);
  const window = {
    document: dom,
    localStorage: storage,
    HTMLElement: TestNode,
    HTMLIFrameElement: TestNode,
    location: { search: LOCATION_SEARCH },
    // The gate schedules its recovery through `window.setTimeout`, so the
    // virtual clock has to back it. The lookup is deferred to call time, which
    // is what binds it to the fake installed above.
    setTimeout: (handler: () => void, delay: number) => setTimeout(handler, delay),
    clearTimeout: (handle: ReturnType<typeof setTimeout>) => clearTimeout(handle),
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  };
  dom.defaultView = window;
  vi.stubGlobal("window", window);
  vi.stubGlobal("navigator", { userAgent: "test", language: "en" });
  vi.stubGlobal("IS_REACT_ACT_ENVIRONMENT", true);
  for (const mock of Object.values(harness)) mock.mockReset();
});

afterEach(() => {
  act(() => root?.unmount());
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe("protected page session-bootstrap race through the real auth provider", () => {
  it("reproduces the state the eviction needs: a hydrated session behind an unresolved bootstrap flag", async () => {
    // The mount-time read is still in flight when sign-in completes, so its
    // generation is superseded and it never clears `loading`. This pins the
    // precondition the gate has to tolerate instead of assuming it away.
    const stalledRead = deferred<AuthResponse>();
    harness.me.mockReturnValue(stalledRead.promise);
    harness.login.mockResolvedValue(sessionFor(RETURNING_USER));
    await mount();

    // Nobody is signed in yet and the read has not settled: bootstrap state.
    expect(providerState).toEqual({ user: null, loading: true });
    expect(renderState().showsBootstrapSpinner).toBe(true);
    expect(renderState().showsProtectedContent).toBe(false);

    await signInDuringStalledRead("returning@example.test");

    // Hydrated session, still-unresolved bootstrap flag: the superseded read is
    // left hanging on purpose, exactly as a slow real response leaves it.
    expect(providerState.user?.id).toBe("user-returning");
    expect(providerState.loading).toBe(true);

    // Even when that read later fails it must not resurrect the stale flag, nor
    // take the hydrated session with it.
    stalledRead.reject(new Error("superseded session read"));
    await drain();
    expect(providerState.user?.id).toBe("user-returning");
    expect(providerState.loading).toBe(true);
  });

  it("never evicts a hydrated returning session when the bootstrap watchdog window elapses", async () => {
    const stalledRead = deferred<AuthResponse>();
    harness.me.mockReturnValue(stalledRead.promise);
    harness.login.mockResolvedValue(sessionFor(RETURNING_USER));
    await mount();
    await signInDuringStalledRead("returning@example.test");

    // Pre-fix behaviour was the bootstrap spinner here; a signed-in user sees
    // their page.
    expect(renderState().showsProtectedContent).toBe(true);
    expect(renderState().showsBootstrapSpinner).toBe(false);

    // Across the whole recovery window and far beyond it: a watchdog armed
    // before hydration must have been cancelled by it, not left running.
    await advance(PROTECTED_PAGE_AUTH_TIMEOUT_MS - 1);
    expect(loginRedirects()).toEqual([]);
    await advance(1);
    expect(loginRedirects()).toEqual([]);
    await advance(60_000);
    expect(loginRedirects()).toEqual([]);
    expect(renderState().showsProtectedContent).toBe(true);
    // The user also keeps the deep link they were working on.
    expect(renderState().text).toContain("research brief");
  });

  it("keeps a first-login account inside onboarding instead of bouncing it to /login", async () => {
    // The account that motivated the report: registration just completed, so
    // `onboardingCompletedAt` is still null and the protected shell is holding
    // the onboarding flow itself. Losing that mount loses the flow.
    const stalledRead = deferred<AuthResponse>();
    harness.me.mockReturnValue(stalledRead.promise);
    harness.login.mockResolvedValue(sessionFor(FIRST_LOGIN_USER));
    await mount();
    await signInDuringStalledRead("first-login@example.test");

    expect(providerState.user).toMatchObject({ id: "user-first-login", onboardingCompletedAt: null });
    expect(renderState().showsProtectedContent).toBe(true);

    await advance(PROTECTED_PAGE_AUTH_TIMEOUT_MS * 6);
    expect(loginRedirects()).toEqual([]);
    expect(renderState().showsProtectedContent).toBe(true);
  });

  // Whether the session hydrates one millisecond after mount or one millisecond
  // before the watchdog fires is pure network timing, so the outcome must not
  // depend on it. A gate that only rescued the fast hydration would still evict
  // the slow sign-in that motivated the report.
  for (const hydrateAt of [1, 250, 5_000, PROTECTED_PAGE_AUTH_TIMEOUT_MS - 1]) {
    it(`holds a hydrated session that arrives ${hydrateAt}ms into the bootstrap window`, async () => {
      const stalledRead = deferred<AuthResponse>();
      harness.me.mockReturnValue(stalledRead.promise);
      harness.login.mockResolvedValue(sessionFor(RETURNING_USER));
      await mount();
      await advance(hydrateAt);
      await signInDuringStalledRead("returning@example.test");
      await advance(PROTECTED_PAGE_AUTH_TIMEOUT_MS * 3);
      expect(loginRedirects()).toEqual([]);
      expect(renderState().showsProtectedContent).toBe(true);
    });
  }

  it("still recovers a stalled signed-out bootstrap and preserves the deep link", async () => {
    // Strict-gating half of the contract: with nobody signed in and the session
    // read never settling, recovery must fire at exactly the deadline and must
    // carry the query string protected views hydrate their state from.
    harness.me.mockReturnValue(deferred<AuthResponse>().promise);
    await mount();
    expect(renderState().showsBootstrapSpinner).toBe(true);

    await advance(PROTECTED_PAGE_AUTH_TIMEOUT_MS - 1);
    expect(loginRedirects()).toEqual([]);

    await advance(1);
    expect(loginRedirects()).toEqual([EXPECTED_LOGIN_REDIRECT]);
    expect(renderState().showsProtectedContent).toBe(false);
  });

  it("still redirects a settled absence of a session without waiting for the watchdog", async () => {
    // A rejected session read is a settled signed-out state.
    harness.me.mockRejectedValue(new Error("401 unauthorized"));
    await mount();
    expect(loginRedirects()).toEqual([EXPECTED_LOGIN_REDIRECT]);
    expect(renderState().showsProtectedContent).toBe(false);
  });

  it("refuses a malformed session payload instead of trusting any truthy value", async () => {
    // Security contract: a payload with no stable identity is not proof of a
    // session, even when it arrives through the real provider after sign-in. It
    // must never mount protected children, and the gate must treat it as the
    // unauthenticated state it is rather than opening on it.
    const stalledRead = deferred<AuthResponse>();
    harness.me.mockReturnValue(stalledRead.promise);
    harness.login.mockResolvedValue(MALFORMED_SESSION);
    await mount();
    await signInDuringStalledRead("placeholder@example.test");

    expect(renderState().showsProtectedContent).toBe(false);
    expect(renderState().showsBootstrapSpinner).toBe(true);

    // The stalled, unauthenticated state is recovered exactly like a
    // signed-out bootstrap: the malformed payload cannot hold the gate open.
    await advance(PROTECTED_PAGE_AUTH_TIMEOUT_MS + 1);
    expect(loginRedirects()).toEqual([EXPECTED_LOGIN_REDIRECT]);
    expect(renderState().showsProtectedContent).toBe(false);
  });

  it("closes the gate again when a hydrated session signs out", async () => {
    // The fix must not turn the gate sticky: once the session really goes away
    // the protected page has to come back down.
    const stalledRead = deferred<AuthResponse>();
    harness.me.mockReturnValue(stalledRead.promise);
    harness.login.mockResolvedValue(sessionFor(RETURNING_USER));
    harness.logout.mockResolvedValue({ success: true });
    await mount();
    await signInDuringStalledRead("returning@example.test");
    expect(renderState().showsProtectedContent).toBe(true);

    await act(async () => {
      const pending = signOut?.();
      vi.advanceTimersByTime(0);
      for (let tick = 0; tick < 8; tick += 1) await Promise.resolve();
      await pending;
      for (let tick = 0; tick < 8; tick += 1) await Promise.resolve();
    });

    expect(providerState.user).toBeNull();
    expect(renderState().showsProtectedContent).toBe(false);
    // And it is refused, not merely hidden: the gate returns the visitor to the
    // sign-in screen once the state settles.
    await advance(PROTECTED_PAGE_AUTH_TIMEOUT_MS + 1);
    expect(loginRedirects()).toContain(EXPECTED_LOGIN_REDIRECT);
  });
});
