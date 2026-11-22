/**
 * Issue #688 — browser-level acceptance for the SAME-TAB account switch in Social Studio.
 *
 * SYNTHETIC ONLY: no browser binary, no provider, no network, no real account, no secret and no
 * charged operation. It uses the repo's established synthetic-DOM harness (the style of
 * `SocialStudioCancellation.test.tsx`) and mounts the real `SocialStudioView`, so the assertions run
 * through the studio's own hydration + autosave path rather than a re-implementation of it. One
 * `Map` stands in for the single `sessionStorage` area a browser tab owns; the mocked auth context
 * then swaps the signed-in identity while KEEPING the workspace identifier identical — the exact
 * condition the finding turns on.
 *
 * Contract proven here:
 *   1. Account A's private draft is autosaved while A is signed in.
 *   2. Switching to Account B in the same tab renders an empty composer and never flashes A's
 *      prompt, brand voice or selected asset identifiers — not on the switch render, not after the
 *      autosave that follows it, and not after a reload of the same tab.
 *   3. B's own composition is stored under B, and cannot overwrite or delete A's.
 *   4. Navigating back to A restores A's draft (within-account navigation is preserved), and B's
 *      private copy stays unreachable from A.
 *   5. The departing identity's persisted drafts are scrubbed at the account boundary, and a
 *      pre-#688 workspace-only entry is removed without ever being shown.
 */
import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// ---- synthetic DOM harness, in the style of SocialStudioCancellation/ImageStudioBrowserLifecycle ----
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
  constructor(nodeType: number, nodeName: string, ownerDocument: TestDocument) {
    this.nodeType = nodeType;
    this.nodeName = nodeName;
    this.ownerDocument = ownerDocument;
  }
  get firstChild() { return this.childNodes[0] ?? null; }
  get lastChild(): TestNode | null { return this.childNodes.at(-1) ?? null; }
  get options() { return this.childNodes; }
  get tagName() { return this.nodeName; }
  get textContent() { return this.childNodes.map((child) => child.textContent ?? "").join(""); }
  set textContent(value: string | null) {
    this.childNodes = value ? [new TestNodeText(value, this.ownerDocument)] : [];
    for (const child of this.childNodes) child.parentNode = this;
  }
  get className() { return this.attributes.get("class") ?? ""; }
  set className(value: string) { this.attributes.set("class", value); }
  get type() { return this.attributes.get("type") ?? (this.nodeName === "TEXTAREA" ? "textarea" : "text"); }
  set type(value: string) { this.attributes.set("type", value); }
  get value() { return this.currentValue; }
  set value(value: string) { this.currentValue = String(value); }
  get checked() { return this.attributes.get("checked") === "true"; }
  set checked(value: boolean) { this.attributes.set("checked", String(value)); }
  appendChild<T extends TestNode>(child: T) { if (child.parentNode) child.parentNode.removeChild(child); child.parentNode = this; this.childNodes.push(child); return child; }
  insertBefore<T extends TestNode>(child: T, before: TestNode | null) { if (!before) return this.appendChild(child); const index = this.childNodes.indexOf(before); if (index < 0) return this.appendChild(child); if (child.parentNode) child.parentNode.removeChild(child); child.parentNode = this; this.childNodes.splice(index, 0, child); return child; }
  removeChild<T extends TestNode>(child: T) { const index = this.childNodes.indexOf(child); if (index >= 0) this.childNodes.splice(index, 1); child.parentNode = null; return child; }
  setAttribute(name: string, value: string) { this.attributes.set(name, String(value)); }
  getAttribute(name: string) { return this.attributes.get(name) ?? null; }
  removeAttribute(name: string) { this.attributes.delete(name); }
  addEventListener(name: string, listener: (event: unknown) => void) { const set = this.listeners.get(name) ?? new Set(); set.add(listener); this.listeners.set(name, set); }
  removeEventListener(name: string, listener: (event: unknown) => void) { this.listeners.get(name)?.delete(listener); }
  dispatchEvent(evt: TestEvent) {
    evt.target ??= this;
    evt.preventDefault ??= () => { evt.defaultPrevented = true; };
    evt.stopPropagation ??= () => { evt.propagationStopped = true; };
    this.notify(evt, this);
    return true;
  }
  private notify(evt: TestEvent, node: TestNode) {
    evt.currentTarget = node;
    for (const listener of node.listeners.get(evt.type) ?? []) listener(evt);
    if (!evt.propagationStopped && node.parentNode) this.notify(evt, node.parentNode);
  }
  contains(node: TestNode | null): boolean { return node === this || this.childNodes.some((child) => child.contains(node)); }
  attachEvent() { return true; }
  detachEvent() { return true; }
  focus() { this.ownerDocument.activeElement = this; }
}
type TestEvent = {
  type: string; bubbles?: boolean; cancelable?: boolean; target?: TestNode; currentTarget?: TestNode;
  defaultPrevented?: boolean; preventDefault?: () => void; stopPropagation?: () => void; propagationStopped?: boolean;
};
class TestNodeText extends TestNode {
  nodeValue: string;
  constructor(value: string, ownerDocument: TestDocument) { super(3, "#text", ownerDocument); this.nodeValue = value; }
  override get textContent() { return this.nodeValue; }
  override set textContent(value: string | null) { this.nodeValue = value ?? ""; }
}
class TestDocument extends TestNode {
  body: TestNode; documentElement: TestNode; activeElement: TestNode; defaultView: object;
  visibilityState = "visible";
  oninput: ((event: unknown) => void) | null = null;
  onchange: ((event: unknown) => void) | null = null;
  constructor() {
    super(9, "#document", null as unknown as TestDocument);
    this.documentElement = new TestNode(1, "html", this);
    this.body = new TestNode(1, "body", this);
    this.documentElement.appendChild(this.body);
    this.appendChild(this.documentElement);
    this.activeElement = this.body;
    this.defaultView = {};
  }
  createElement(name: string) { return new TestNode(1, name.toUpperCase(), this); }
  createElementNS(_ns: string, name: string) { return this.createElement(name); }
  createTextNode(value: string) { return new TestNodeText(value, this); }
  getElementById(id: string) { return this.find((n) => n.getAttribute("id") === id); }
  private find(p: (n: TestNode) => boolean): TestNode | null {
    const visit = (n: TestNode): TestNode | null => (p(n) ? n : n.childNodes.map(visit).find(Boolean) ?? null);
    return visit(this);
  }
}

const harness = vi.hoisted(() => ({
  // One tab: the SAME storage map survives an identity swap, exactly as sessionStorage does.
  auth: { workspace: null as { id: string } | null, user: null as { id: string } | null },
  api: {
    listProjects: vi.fn(), listFiles: vi.fn(), listAssets: vi.fn(), getGenerationJob: vi.fn(),
    createSocialGenerationJob: vi.fn(), cancelGenerationJob: vi.fn(),
  },
}));
vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => ({ workspace: harness.auth.workspace, user: harness.auth.user }),
}));
const translate = (key: string, variables?: Record<string, string>) =>
  Object.entries(variables ?? {}).reduce((value, [name, replacement]) => value.replace(`{${name}}`, replacement), key);
vi.mock("@/components/LocaleProvider", () => ({ useLocale: () => ({ locale: "en", t: translate }) }));
const searchParams = vi.hoisted(() => new URLSearchParams());
vi.mock("next/navigation", () => ({ useSearchParams: () => searchParams }));
vi.mock("next/link", () => ({ default: (p: { href: string; children?: unknown }) => <a href={p.href}>{p.children as never}</a> }));
vi.mock("@/lib/api", () => ({
  api: harness.api,
  ApiError: class ApiError extends Error {
    status: number | null; code: string | null;
    constructor(status: number | null, message: string, _f?: unknown, code?: string | null) {
      super(message); this.name = "ApiError"; this.status = status; this.code = code ?? null;
    }
  },
}));
vi.mock("lucide-react", () => {
  const Icon = (p: Record<string, unknown>) => <span {...p} />;
  return { Check: Icon, CheckCircle2: Icon, Copy: Icon, FileText: Icon, Image: Icon, LoaderCircle: Icon,
    RefreshCw: Icon, Share2: Icon, ShieldCheck: Icon, Sparkles: Icon, WandSparkles: Icon, XCircle: Icon };
});

import { SocialStudioView } from "./SocialStudioView";
import { legacySocialDraftStorageKey, persistSocialDraft, socialDraftStorageKey, type SocialStudioDraft } from "@/lib/socialStudioState";

/** Synthetic identities sharing ONE workspace id — the condition #688 turns on. */
const ACCOUNT_A = "synthetic-account-a-688";
const ACCOUNT_B = "synthetic-account-b-688";
const SHARED_WORKSPACE = { id: "synthetic-workspace-688-shared" };
const OTHER_WORKSPACE = { id: "synthetic-workspace-688-other" };
const PRIVATE_A = "SYNTHETIC-688-A private brief never real data";
const PRIVATE_A_VOICE = "SYNTHETIC-688-A private brand voice";
const PRIVATE_A_ASSET = "synthetic-asset-a-688";
const PRIVATE_B = "SYNTHETIC-688-B private brief never real data";

const storage = new Map<string, string>();
let doc: TestDocument;
let container: TestNode;
let root: Root;

async function drain() { await new Promise((resolve) => setTimeout(resolve, 0)); }
async function settle(fn: () => void) { await act(async () => { fn(); await drain(); }); }
function allNodes(node: TestNode): TestNode[] { return [node, ...node.childNodes.flatMap(allNodes)]; }
function promptBox() { return allNodes(container).find((n) => n.nodeName === "TEXTAREA"); }
/** Every character the tab is currently showing to whoever holds it. */
function renderedText() { return allNodes(container).map((n) => `${n.getAttribute("value") ?? ""}|${n.textContent}`).join("\n"); }
function storedText() { return [...storage.values()].join("\n"); }
/** The studio persists on the next commit, so let the autosave effect flush. */
async function flushAutosave() { await settle(() => undefined); await settle(() => undefined); }

async function signInAs(userId: string, workspace: { id: string } = SHARED_WORKSPACE) {
  harness.auth.user = { id: userId };
  harness.auth.workspace = workspace;
}

async function mount() {
  await act(async () => { root = createRoot(container as unknown as Element); root.render(<SocialStudioView />); await drain(); });
}
/** A re-render of the mounted studio, i.e. the tab stayed open and only the session changed. */
async function rerender() { await act(async () => { root.render(<SocialStudioView />); await drain(); }); }
/** A tab reload: same sessionStorage, fresh component state. */
async function reloadTab() {
  await act(async () => { root.unmount(); });
  await mount();
}
async function typePrompt(text: string) {
  const box = promptBox();
  if (!box) throw new Error("no textarea rendered");
  const descriptor = Object.getOwnPropertyDescriptor(TestNode.prototype, "value");
  await settle(() => { box.dispatchEvent({ type: "focusin", bubbles: true }); });
  await settle(() => {
    descriptor!.set!.call(box, text);
    box.dispatchEvent({ type: "input", bubbles: true });
    box.dispatchEvent({ type: "keyup", bubbles: true });
  });
}

beforeEach(() => {
  doc = new TestDocument();
  container = doc.createElement("div");
  doc.body.appendChild(container);
  storage.clear();
  const windowStub = {
    document: doc, HTMLElement: TestNode, HTMLIFrameElement: TestNode, Element: TestNode, Node: TestNode,
    addEventListener: vi.fn(), removeEventListener: vi.fn(),
    // Inert: this lane never asserts on job polling/restoration timers, and a synchronous timer
    // would let the restore effect re-enter the render loop being observed.
    setTimeout: () => 0,
    clearTimeout: () => undefined,
    sessionStorage: {
      getItem: (key: string) => storage.get(key) ?? null,
      setItem: (key: string, value: string) => { storage.set(key, value); },
      removeItem: (key: string) => { storage.delete(key); },
      key: (index: number) => [...storage.keys()][index] ?? null,
      get length() { return storage.size; },
    },
  };
  doc.defaultView = windowStub;
  vi.stubGlobal("document", doc);
  vi.stubGlobal("window", windowStub);
  vi.stubGlobal("navigator", { userAgent: "test" });
  vi.stubGlobal("IS_REACT_ACT_ENVIRONMENT", true);
  harness.api.listProjects.mockReset().mockResolvedValue([]);
  harness.api.listFiles.mockReset().mockResolvedValue([]);
  harness.api.listAssets.mockReset().mockResolvedValue({ items: [{ id: PRIVATE_A_ASSET, name: "synthetic asset", assetType: "image", status: "Active", hasFile: true }], page: 1, pageSize: 100, totalCount: 1, totalPages: 1 });
  harness.api.getGenerationJob.mockReset().mockResolvedValue(null);
  harness.api.createSocialGenerationJob.mockReset();
  harness.api.cancelGenerationJob.mockReset();
  harness.auth.workspace = null;
  harness.auth.user = null;
});
afterEach(() => { act(() => root?.unmount()); vi.unstubAllGlobals(); });

describe("Social Studio same-tab account switch, rendered through the studio (#688)", () => {
  it("autosaves Account A's private draft under A's identity", async () => {
    await signInAs(ACCOUNT_A);
    await mount();
    await typePrompt(PRIVATE_A);
    await flushAutosave();
    expect(promptBox()?.value).toBe(PRIVATE_A);
    expect(storage.has(socialDraftStorageKey({ userId: ACCOUNT_A, workspaceId: SHARED_WORKSPACE.id }) as string)).toBe(true);
    // And nothing is written to the shared, unattributed workspace-only address.
    expect(storage.has(legacySocialDraftStorageKey(SHARED_WORKSPACE.id))).toBe(false);
  });

  it("never flashes Account A's private draft when Account B signs in in the same tab", async () => {
    await signInAs(ACCOUNT_A);
    await mount();
    await typePrompt(PRIVATE_A);
    await flushAutosave();
    expect(promptBox()?.value).toBe(PRIVATE_A);

    // Boundary: A signs out and B signs in, resolving the SAME workspace id in the SAME tab.
    await signInAs(ACCOUNT_B);
    await rerender();
    // The switch render itself: an empty composer, no prior content anywhere in the tree.
    expect(promptBox()?.value ?? "").toBe("");
    expect(renderedText()).not.toContain(PRIVATE_A);
    expect(renderedText()).not.toContain(PRIVATE_A_VOICE);

    // The autosave that follows the switch must not pull A's text back in.
    await flushAutosave();
    expect(promptBox()?.value ?? "").toBe("");
    expect(renderedText()).not.toContain(PRIVATE_A);

    // B composes; a reload of the same tab shows B's own copy and still never A's.
    await typePrompt(PRIVATE_B);
    await flushAutosave();
    await reloadTab();
    expect(promptBox()?.value).toBe(PRIVATE_B);
    expect(renderedText()).not.toContain(PRIVATE_A);
    expect(storedText()).toContain(PRIVATE_B);
    // A's private prompt never became reachable through B's address.
    expect(storage.has(socialDraftStorageKey({ userId: ACCOUNT_B, workspaceId: SHARED_WORKSPACE.id }) as string)).toBe(true);
  });

  it("does not let one identity's save overwrite another identity's entry", async () => {
    // B's entry is seeded directly (an account switch in the studio also triggers the #688 boundary
    // scrub, which is asserted separately below); the point here is address separation on write.
    const bDraft: SocialStudioDraft = { projectId: "", selectedFiles: [], selectedAssets: [], prompt: PRIVATE_B, socialType: "auto", platform: "multi", tone: "professional", language: "auto", audience: "", brandVoice: "", callToAction: "", includeHashtags: true, includeEmojis: false, generateVariants: true };
    persistSocialDraft({ userId: ACCOUNT_B, workspaceId: SHARED_WORKSPACE.id }, bDraft);
    const addressB = socialDraftStorageKey({ userId: ACCOUNT_B, workspaceId: SHARED_WORKSPACE.id }) as string;
    const seededForB = storage.get(addressB);
    expect(seededForB).toContain(PRIVATE_B);

    await signInAs(ACCOUNT_A);
    await mount();
    // A arrives at a different address, so A's hydration cannot see B's seeded entry...
    await flushAutosave();
    expect(promptBox()?.value ?? "").toBe("");
    // ...and A's own composition is written to A's address only.
    await typePrompt(PRIVATE_A);
    await flushAutosave();
    expect(storage.get(addressB)).toBe(seededForB);
    expect(storage.get(socialDraftStorageKey({ userId: ACCOUNT_A, workspaceId: SHARED_WORKSPACE.id }) as string)).toContain(PRIVATE_A);
    expect(storage.get(addressB)).not.toContain(PRIVATE_A);
  });

  it("restores Account A's own draft when A returns, preserving within-account navigation", async () => {
    await signInAs(ACCOUNT_A);
    await mount();
    await typePrompt(PRIVATE_A);
    await flushAutosave();

    // Same account, different workspace: a navigation, not a new session.
    await signInAs(ACCOUNT_A, OTHER_WORKSPACE);
    await rerender();
    await flushAutosave();
    expect(promptBox()?.value ?? "").toBe("");

    // Back to the shared workspace: A's draft must still be A's.
    await signInAs(ACCOUNT_A, SHARED_WORKSPACE);
    await rerender();
    await flushAutosave();
    expect(promptBox()?.value).toBe(PRIVATE_A);
  });

  it("scrubs the departing identity's drafts at the account boundary", async () => {
    await signInAs(ACCOUNT_A);
    await mount();
    await typePrompt(PRIVATE_A);
    await flushAutosave();
    const addressA = socialDraftStorageKey({ userId: ACCOUNT_A, workspaceId: SHARED_WORKSPACE.id }) as string;
    expect(storage.has(addressA)).toBe(true);

    await signInAs(ACCOUNT_B);
    await rerender();
    await flushAutosave();

    // A is gone from this tab: neither its payload nor its address remains.
    expect(storage.has(addressA)).toBe(false);
    expect(storedText()).not.toContain(PRIVATE_A);
    expect(promptBox()?.value ?? "").toBe("");
  });

  it("refuses to show or keep a pre-#688 workspace-only draft", async () => {
    // Seed what an older build left in this tab: no owner attribution at all.
    storage.set(legacySocialDraftStorageKey(SHARED_WORKSPACE.id), JSON.stringify({ prompt: PRIVATE_A, brandVoice: PRIVATE_A_VOICE, selectedAssets: [PRIVATE_A_ASSET] }));
    await signInAs(ACCOUNT_B);
    await mount();
    await flushAutosave();
    expect(promptBox()?.value ?? "").toBe("");
    expect(renderedText()).not.toContain(PRIVATE_A);
    expect(renderedText()).not.toContain(PRIVATE_A_VOICE);
    expect(storage.has(legacySocialDraftStorageKey(SHARED_WORKSPACE.id))).toBe(false);
    expect(storedText()).not.toContain(PRIVATE_A);
  });

  it("signing out leaves no persisted draft behind for the next occupant of the tab", async () => {
    await signInAs(ACCOUNT_A);
    await mount();
    await typePrompt(PRIVATE_A);
    await flushAutosave();
    expect(storage.size).toBeGreaterThan(0);

    // Sign out: authenticated session resolved to nothing, workspace gone with it.
    harness.auth.user = null;
    harness.auth.workspace = null;
    await rerender();
    await flushAutosave();
    expect(storedText()).not.toContain(PRIVATE_A);
    expect([...storage.keys()].filter((key) => key.includes(SHARED_WORKSPACE.id) || key.includes(PRIVATE_A_ASSET))).toEqual([]);
    expect(renderedText()).not.toContain(PRIVATE_A);
  });
});
