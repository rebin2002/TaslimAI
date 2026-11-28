/**
 * Language-selector persistence across EN / AR / Sorani switching.
 *
 * The signed-in UI persists the selector's choice under an account-scoped
 * storage key (`taslim-locale:user:<id>`) while the pre-login bootstrap
 * restores the device-level key (`taslim-locale`) after hydration. On current
 * main, when the bootstrap value is restored for a visit and the session then
 * resolves, AuthProvider deliberately declines to adopt the saved account
 * choice (source `anonymous-storage` is in its skip set) and the very next
 * effect writes that declined bootstrap value over the account key. The
 * user's own saved EN/AR/Sorani selection is therefore destroyed by sign-in,
 * permanently for this browser, and every later session replays the stale
 * anonymous value instead.
 *
 * Synthetic harness in the established style of
 * AssetDetail.historyFocus.test and AssetsViewListing.test: no jsdom, no
 * network, no real providers, fixed anonymous fixtures only.
 */
import { act } from "react";
import * as React from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { AuthResponse } from "@/lib/api";

const { meMock, updateProfileMock } = vi.hoisted(() => ({ meMock: vi.fn(), updateProfileMock: vi.fn() }));
vi.mock("@/lib/api", () => ({
  api: {
    me: () => meMock(),
    login: vi.fn(),
    register: vi.fn(),
    logout: vi.fn(),
    updateProfile: (input: unknown) => updateProfileMock(input),
    completeOnboarding: vi.fn(),
  },
}));
vi.mock("next/navigation", () => ({
  useRouter: () => ({ push: vi.fn(), replace: vi.fn() }),
}));

import { AuthProvider } from "./AuthProvider";
import { useAuth } from "./AuthProvider";
import { LocaleProvider, useLocale } from "./LocaleProvider";
import { accountLocaleStorageKey, anonymousLocaleStorageKey } from "@/lib/localePersistence";
import type { Locale } from "@/lib/i18n";

/* Deterministic synthetic DOM sufficient for react-dom/client rendering. */
class TestNode {
  nodeType: number;
  nodeName: string;
  ownerDocument: TestDocument;
  parentNode: TestNode | null = null;
  childNodes: TestNode[] = [];
  attributes = new Map<string, string>();
  style: Record<string, string> = {};
  listeners = new Map<string, Set<(event: unknown) => void>>();
  private currentValue = "";
  disabled = false;
  selected = false;
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
  get value() { return this.currentValue; }
  set value(value: string) { this.currentValue = String(value); }
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

function authResponse(userId: string, preferredLanguage: AuthResponse["user"]["preferredLanguage"]): AuthResponse {
  return {
    user: {
      id: userId,
      email: `${userId}@example.test`,
      displayName: "Fixture Author",
      preferredLanguage,
      personalWorkspaceId: "workspace-1",
      createdAt: "2026-11-01T00:00:00Z",
      defaultGenerationLanguage: "en",
      timeZone: "UTC",
      outputPreference: "balanced",
      includeSourceLinks: true,
      onboardingCompletedAt: "2026-11-01T00:00:00Z",
      onboardingIntent: "chat",
      isAdmin: false,
    },
    personalWorkspace: { id: "workspace-1", name: "Personal", slug: "personal", type: "personal", role: "owner" },
  };
}

let storage: Storage;
let document: TestDocument;
let container: TestNode;
let root: Root;
let latestLocale: Locale;
let selector: ((locale: Locale) => void) | null;
let saveProfile: ((preferredLanguage: Locale) => Promise<void>) | null;

// Records the live locale and exposes a selector-equivalent interaction: the
// header select calls setLocale(next) with no options, i.e. source "user".
// The recording runs in an effect so the probe stays a pure render function.
function LocaleProbe() {
  const { locale, setLocale } = useLocale();
  const { updateProfile } = useAuth();
  React.useEffect(() => {
    latestLocale = locale;
    selector = (next: Locale) => setLocale(next);
    saveProfile = (preferredLanguage: Locale) =>
      updateProfile({ displayName: "Fixture Author", preferredLanguage });
  });
  return <div className="locale-probe">{locale}</div>;
}

function Host() {
  return (
    <LocaleProvider>
      <AuthProvider>
        <LocaleProbe />
      </AuthProvider>
    </LocaleProvider>
  );
}

async function drain() {
  await new Promise((resolve) => setTimeout(resolve, 0));
}

async function mount(): Promise<void> {
  latestLocale = "en";
  selector = null;
  saveProfile = null;
  await act(async () => {
    root = createRoot(container as unknown as Element);
    root.render(<Host />);
    await drain();
  });
}

async function interact(mutation: () => void) {
  await act(async () => {
    mutation();
    await drain();
  });
}

beforeEach(() => {
  storage = memoryStorage();
  document = new TestDocument();
  container = document.createElement("div");
  document.body.appendChild(container);
  vi.stubGlobal("document", document);
  const window = {
    document,
    localStorage: storage,
    HTMLElement: TestNode,
    HTMLIFrameElement: TestNode,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  };
  document.defaultView = window;
  vi.stubGlobal("window", window);
  vi.stubGlobal("navigator", { userAgent: "test" });
  vi.stubGlobal("IS_REACT_ACT_ENVIRONMENT", true);
  meMock.mockReset();
  updateProfileMock.mockReset();
});

afterEach(() => {
  act(() => root?.unmount());
  vi.unstubAllGlobals();
});

describe("language selector account persistence across EN/AR/Sorani switching", () => {
  it("sign-in restores the account-saved Sorani choice instead of overwriting it with the device bootstrap", async () => {
    // A previously signed-out visit left the anonymous bootstrap at Arabic.
    storage.setItem(anonymousLocaleStorageKey, "ar");
    // The account's own saved selector choice for this user is Sorani.
    storage.setItem(accountLocaleStorageKey("user/a"), "ku");
    meMock.mockResolvedValue(authResponse("user/a", "en"));
    await mount();
    // The saved account choice must win the session...
    expect(latestLocale).toBe("ku");
    expect(document.documentElement.lang).toBe("ckb");
    expect(document.documentElement.dir).toBe("rtl");
    // ...and must survive in its account key. On current main the persistence
    // effect writes the declined bootstrap over it, so sign-in destroys the
    // saved choice and every later session replays Arabic instead.
    expect(storage.getItem(accountLocaleStorageKey("user/a"))).toBe("ku");
    // The consumed device bootstrap is cleaned up, as the migration always did.
    expect(storage.getItem(anonymousLocaleStorageKey)).toBeNull();
  });

  it("keeps an explicit in-session selector choice ahead of the account preference", async () => {
    // The session resolves only after the user has already switched the
    // selector while unauthenticated; that explicit choice keeps the floor.
    let resolveMe: ((value: AuthResponse) => void) | null = null;
    meMock.mockReturnValue(new Promise<AuthResponse>((resolve) => { resolveMe = resolve; }));
    await mount();
    await interact(() => { selector?.("ar"); });
    expect(latestLocale).toBe("ar");
    await act(async () => {
      resolveMe?.(authResponse("user/a", "ku"));
      await drain();
    });
    expect(latestLocale).toBe("ar");
    expect(storage.getItem(accountLocaleStorageKey("user/a"))).toBe("ar");
    expect(storage.getItem(anonymousLocaleStorageKey)).toBeNull();
  });

  it("adopts the server profile language on a fresh device without forging a saved choice", async () => {
    meMock.mockResolvedValue(authResponse("user/a", "ku"));
    await mount();
    expect(latestLocale).toBe("ku");
    expect(document.documentElement.lang).toBe("ckb");
    // Automatic adoption of the server profile is not an explicit selector
    // choice: it must not occupy the account key and later outvote one.
    expect(storage.getItem(accountLocaleStorageKey("user/a"))).toBeNull();
  });

  it("leaves a device bootstrap in place while the account has no saved selector choice", async () => {
    // The behavior device-level seeded sessions rely on: with no per-account
    // explicit selection stored on this browser, the pre-login bootstrap wins
    // the visit instead of being overridden by the server profile language.
    storage.setItem(anonymousLocaleStorageKey, "ar");
    meMock.mockResolvedValue(authResponse("user/a", "en"));
    await mount();
    expect(latestLocale).toBe("ar");
    expect(document.documentElement.lang).toBe("ar");
    expect(storage.getItem(accountLocaleStorageKey("user/a"))).toBeNull();
    expect(storage.getItem(anonymousLocaleStorageKey)).toBeNull();
  });

  it("records a profile language save as the explicit account choice", async () => {
    meMock.mockResolvedValue(authResponse("user/a", "en"));
    await mount();
    updateProfileMock.mockResolvedValue(authResponse("user/a", "ku"));
    await interact(async () => {
      await saveProfile?.("ku");
    });
    expect(latestLocale).toBe("ku");
    expect(document.documentElement.lang).toBe("ckb");
    expect(document.documentElement.dir).toBe("rtl");
    expect(storage.getItem(accountLocaleStorageKey("user/a"))).toBe("ku");
  });

  it("prefers the persisted account selector value over the server profile language", async () => {
    storage.setItem(accountLocaleStorageKey("user/a"), "ar");
    meMock.mockResolvedValue(authResponse("user/a", "ku"));
    await mount();
    expect(latestLocale).toBe("ar");
    expect(document.documentElement.lang).toBe("ar");
    expect(document.documentElement.dir).toBe("rtl");
    expect(storage.getItem(accountLocaleStorageKey("user/a"))).toBe("ar");
  });
});
