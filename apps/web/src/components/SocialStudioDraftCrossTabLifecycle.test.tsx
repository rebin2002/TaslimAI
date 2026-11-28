/**
 * SYNTHETIC TEST ONLY — issue #688 (persisted Social drafts vs account/workspace switch).
 *
 * WHAT THIS LANE COVERS, AND WHY IT IS NOT ALREADY COVERED
 * Issue #688 acceptance criterion 3 asks a fix to "consider anonymous-to-auth transitions,
 * fast switching and *multi-tab lifecycle*", and criterion 4 asks for isolation that holds
 * "on reload and on account transitions". The two live #688 lanes are both same-tab:
 *  - #1054 `socialStudioDraftAccountIsolation.test.ts` models "one browser tab == one
 *    sessionStorage" and switches principals by which call site reads next;
 *  - #1073 `socialStudioDraftIsolation.test.ts` / `SocialStudioDraftAccountSwitch.test.tsx`
 *    state in their own headers that multi-tab is out of scope ("scoping is per-tab and
 *    per-identity, so no cross-tab remedy is attempted here").
 * Neither exercises the browsing-context boundary. This file does: it drives the rendered
 * studio through a *new tab created from a signed-in tab*, which is where `sessionStorage`
 * stops being a defence and becomes the delivery mechanism.
 *
 * THE BROWSER SEMANTICS BEING MODELLED (HTML Living Standard, session storage)
 * 1. `sessionStorage` is per top-level browsing context, so it survives a *reload* of the
 *    same tab but is not shared between independently opened tabs.
 * 2. When a new tab is created *from* an existing one (a link with `target="_blank"`,
 *    `window.open`, a duplicated tab), the new context receives a point-in-time CLONE of the
 *    opener's session storage.
 * 3. After creation the two partitions are independent: writes in one tab never appear in
 *    the other, and `sessionStorage` fires no cross-tab `storage` event.
 * Consequence for #688: a draft persisted under the workspace-only address
 * `taslim:social-draft:<workspaceId>` (`socialStudioState.ts`, current `main`) is CLONED into
 * the child tab. Sign out of Account A there and sign in as Account B — a different principal
 * whose workspace id may collide with A's, exactly the worst case the audit assumes — and the
 * studio hydrates B's composer from A's unsent private copy. That is a cross-tab identity
 * hand-off, not a same-tab switch, and no existing test in either lane can see it.
 *
 * WHY THESE EXPECTATIONS ARE SATISFIABLE (the trap this lane deliberately avoids)
 * Identity is never an input to the one-argument draft API, so a pure library-level harness
 * cannot distinguish "A reads" from "B reads" — the same three calls would need opposite
 * results, which is the objection raised against #1054 on #1073. Here the principal enters
 * through the seam the studio already consumes (`useAuth().user.id` → `accountId`,
 * `SocialStudioView.tsx:100`), so the isolation cases and the retention guards are genuinely
 * different sequences and one implementation can satisfy both.
 *
 * WHY THE ASSERTIONS ARE BEHAVIOURAL, NOT STORAGE-SHAPED
 * #688 permits two remedies: bind the persisted draft to authenticated identity *and*
 * workspace, or clear the stale private state at a correctly identified login/logout boundary.
 * Every case below asserts only what the studio RENDERS to the principal that arrives next, so
 * both remedies satisfy the isolation cases (B's sign-out can only clear A's entries, and an
 * identity-scoped address is unreachable by B), while the guards stop any remedy that
 * destroys a legitimate same-identity hand-off or invents cross-tab sharing.
 *
 * EXPECTED STATUS ON `main` (41f9fcc6)
 * The four isolation expectations are RED by design — they certify the required end state.
 * They MUST NOT gate release CI before an owner-approved fix, because flipping them to encode
 * today's behaviour would certify the leak. The four guards are GREEN today and must stay
 * GREEN. Nothing here edits an owned file: `socialStudioState.ts`, `SocialStudioView.tsx`,
 * `socialStudioState.test.ts`, `sessionDraftScope.ts`, #1054's file and AuthProvider are all
 * untouched; this is one new, self-contained test file.
 *
 * Fixtures are prefixed `SYNTHETIC-688-XTAB`; only those sentinels can appear in failure
 * output, so CI logs cannot expose real draft content. No network, no provider call, no
 * charge, no secret, no real account, no storage service, no Playwright browser.
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
  private currentType: string | undefined = undefined;
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
  get value() { return this.currentValue; }
  set value(value: string) { this.currentValue = String(value); }
  /** `input[type]` reports "text" when unset; React keys its change dispatch off this value. */
  get type(): string | undefined { return this.currentType ?? this.attributes.get("type") ?? (this.nodeName === "INPUT" ? "text" : undefined); }
  set type(value: string | undefined) { this.currentType = value === undefined ? undefined : String(value); }
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
  auth: { workspace: null as { id: string } | null, user: null as { id: string } | null },
  api: { listProjects: vi.fn(), listFiles: vi.fn(), listAssets: vi.fn(), getGenerationJob: vi.fn(), createSocialGenerationJob: vi.fn(), cancelGenerationJob: vi.fn() },
}));
vi.mock("@/components/AuthProvider", () => ({ useAuth: () => ({ workspace: harness.auth.workspace, user: harness.auth.user }) }));
const translate = (key: string, variables?: Record<string, string>) =>
  Object.entries(variables ?? {}).reduce((value, [name, replacement]) => value.replace(`{${name}}`, replacement), key);
vi.mock("@/components/LocaleProvider", () => ({ useLocale: () => ({ locale: "en", t: translate }) }));
// A stable instance: the studio keeps searchParams in an effect dependency list, so a fresh
// object per render would re-run the hydration effect forever.
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

/** Independent synthetic principals that both resolve the SAME workspace id (the #688 collision). */
const ACCOUNT_A = "synthetic-xtab-account-a-688";
const ACCOUNT_B = "synthetic-xtab-account-b-688";
const SHARED_WORKSPACE = "synthetic-xtab-workspace-shared-688";
const OTHER_WORKSPACE = "synthetic-xtab-workspace-other-688";
/** Clearly synthetic private copy; never real user content. */
const PRIVATE_A = {
  prompt: "SYNTHETIC-688-XTAB-A private brief: Q3 launch narrative (never real data)",
  audience: "SYNTHETIC-688-XTAB-A private audience segment",
  brandVoice: "SYNTHETIC-688-XTAB-A private brand voice rules",
  callToAction: "SYNTHETIC-688-XTAB-A private CTA",
} as const;
const PRIVATE_B = {
  prompt: "SYNTHETIC-688-XTAB-B private brief: hiring announcement (never real data)",
} as const;

/** One browsing context: its own session-storage partition, document and studio mount. */
type Tab = {
  id: string;
  storage: Map<string, string>;
  doc: TestDocument;
  container: TestNode;
  principal: string | null;
  workspaceId: string | null;
};
let tabs = new Map<string, Tab>();
let active: Tab | null = null;
let liveRoot: Root | null = null;
let timers = new Map<number, () => void>();
let nextTimerId = 1;

async function drain() { await new Promise((r) => setTimeout(r, 0)); }
async function settle(fn: () => void) { await act(async () => { fn(); await drain(); }); }

function makeWindowStub(tab: Tab) {
  return {
    document: tab.doc, HTMLElement: TestNode, HTMLIFrameElement: TestNode, Element: TestNode, Node: TestNode,
    addEventListener: vi.fn(), removeEventListener: vi.fn(),
    setTimeout: (cb: () => void) => { const id = nextTimerId++; timers.set(id, cb); return id; },
    clearTimeout: (id: number) => { timers.delete(id); },
    // Each browsing context resolves storage against ITS OWN partition. This is the whole point
    // of the lane: a workspace-only key is meaningless once the partition itself is inherited.
    sessionStorage: {
      getItem: (k: string) => tab.storage.get(k) ?? null,
      setItem: (k: string, v: string) => { tab.storage.set(k, v); },
      removeItem: (k: string) => { tab.storage.delete(k); },
    },
  };
}
function activate(tab: Tab) {
  active = tab;
  const windowStub = makeWindowStub(tab);
  tab.doc.defaultView = windowStub;
  vi.stubGlobal("document", tab.doc);
  vi.stubGlobal("window", windowStub);
  vi.stubGlobal("navigator", { userAgent: "test" });
  vi.stubGlobal("IS_REACT_ACT_ENVIRONMENT", true);
}

/**
 * `cloneFrom` reproduces the HTML session-storage rule for a tab created FROM another tab:
 * a point-in-time copy of the opener's partition. Omitting it opens an independent tab, whose
 * session storage starts empty — the path that must keep passing, so the isolation cases below
 * cannot be satisfied by simply making storage empty everywhere.
 */
async function openTab(id: string, principal: string, workspaceId: string, cloneFrom?: string) {
  const source = cloneFrom ? tabs.get(cloneFrom) : undefined;
  const tab: Tab = {
    id,
    storage: new Map(source ? source.storage : []),
    doc: new TestDocument(),
    container: null as unknown as TestNode,
    principal,
    workspaceId,
  };
  tab.container = tab.doc.createElement("div");
  tab.doc.body.appendChild(tab.container);
  tabs.set(id, tab);
  await leaveStudio();
  activate(tab);
  await enterStudio();
  return tab;
}

/** Navigate to Social Studio as the active tab's current principal. */
async function enterStudio() {
  const tab = active!;
  harness.auth.user = tab.principal ? { id: tab.principal } : null;
  harness.auth.workspace = tab.workspaceId ? { id: tab.workspaceId } : null;
  await act(async () => { liveRoot = createRoot(tab.container as unknown as Element); liveRoot.render(<SocialStudioView />); await drain(); });
}
async function leaveStudio() {
  const root = liveRoot;
  liveRoot = null;
  if (root) await act(async () => { root.unmount(); await drain(); });
}
/** Reload: same partition, same principal, a fresh mount of the studio. */
async function reloadStudio() { await leaveStudio(); await enterStudio(); }
/** Account boundary in THIS tab: sign out, then sign in as someone else, then navigate back in. */
async function switchAccountInThisTab(principal: string, workspaceId: string) {
  const tab = active!;
  await leaveStudio();
  tab.principal = principal;
  tab.workspaceId = workspaceId;
  await enterStudio();
}

function promptNode() {
  // Looked up by tag rather than by id: the repo's synthetic DOM records attributes the way
  // SocialStudioCancellation.test.tsx does, and only its class/tag helpers are proven.
  return walk(active!.container).find((node) => node.nodeName === "TEXTAREA");
}
function promptValue() { return promptNode()?.value ?? ""; }
/** Advanced fields render as `<label class="field"><span>{t(key)}</span><input …/></label>`. */
function advancedValue(labelKey: string) {
  const labels = walk(active!.container).filter((n) => n.nodeName === "LABEL" && (n.getAttribute("class")?.split(/\s+/) ?? []).includes("field") && n.childNodes.some((child) => child.textContent === labelKey));
  const input = labels.flatMap((label) => walk(label)).find((n) => n.nodeName === "INPUT");
  return input?.value ?? "";
}
function walk(node: TestNode): TestNode[] { return [node, ...node.childNodes.flatMap(walk)]; }
/** Everything the arriving principal can read back out of the rendered composer. */
function disclosedCopy() {
  return [promptValue(), advancedValue("social.audience"), advancedValue("social.brandVoice"), advancedValue("social.callToAction")].join("\n");
}
async function typeInto(node: TestNode, text: string) {
  const descriptor = Object.getOwnPropertyDescriptor(TestNode.prototype, "value")!;
  await settle(() => { node.dispatchEvent({ type: "focusin", bubbles: true }); });
  await settle(() => {
    descriptor.set!.call(node, text);
    node.dispatchEvent({ type: "input", bubbles: true });
    node.dispatchEvent({ type: "keyup", bubbles: true });
  });
}
async function composePrivateDraftA() {
  await typeInto(promptNode()!, PRIVATE_A.prompt);
  const fields = walk(active!.container).filter((node) => node.nodeName === "LABEL" && (node.getAttribute("class")?.split(/\s+/) ?? []).includes("field"));
  const entries: readonly [string, string][] = [
    ["social.audience", PRIVATE_A.audience],
    ["social.brandVoice", PRIVATE_A.brandVoice],
    ["social.callToAction", PRIVATE_A.callToAction],
  ];
  for (const [labelKey, value] of entries) {
    const label = fields.find((candidate) => candidate.childNodes.some((child) => child.textContent === labelKey));
    const input = label ? walk(label).find((node) => node.nodeName === "INPUT") : undefined;
    if (!input) throw new Error(`synthetic field ${labelKey} is not rendered`);
    await typeInto(input, value);
  }
}

beforeEach(() => {
  tabs = new Map();
  active = null;
  liveRoot = null;
  timers = new Map();
  nextTimerId = 1;
  harness.api.listProjects.mockReset().mockResolvedValue([]);
  harness.api.listFiles.mockReset().mockResolvedValue([]);
  harness.api.listAssets.mockReset().mockResolvedValue({ items: [], page: 1, pageSize: 100, totalCount: 0, totalPages: 1 });
  harness.api.getGenerationJob.mockReset();
  harness.api.createSocialGenerationJob.mockReset();
  harness.api.cancelGenerationJob.mockReset();
  harness.auth.workspace = null;
  harness.auth.user = null;
});
afterEach(async () => { await leaveStudio(); vi.unstubAllGlobals(); });

describe("Social Studio persisted draft across a new tab cloned from a signed-in tab (#688 multi-tab lifecycle)", () => {
  // --- Isolation contract: RED by design on main until an owner-approved fix lands. ---
  it("does not hand Account A's private prompt to Account B after a cross-tab sign-out", async () => {
    await openTab("tab-1", ACCOUNT_A, SHARED_WORKSPACE);
    await composePrivateDraftA();
    // The user opens Social Studio in a new tab from A's tab: the browser clones A's
    // session storage into it. A signs out there and B signs in on the colliding workspace.
    await openTab("tab-2", ACCOUNT_A, SHARED_WORKSPACE, "tab-1");
    await switchAccountInThisTab(ACCOUNT_B, SHARED_WORKSPACE);
    expect(promptValue()).not.toContain(PRIVATE_A.prompt);
    expect(promptValue()).toBe("");
  });

  it("does not hand Account A's private audience, brand voice or CTA copy to Account B", async () => {
    await openTab("tab-1", ACCOUNT_A, SHARED_WORKSPACE);
    await composePrivateDraftA();
    await openTab("tab-2", ACCOUNT_A, SHARED_WORKSPACE, "tab-1");
    await switchAccountInThisTab(ACCOUNT_B, SHARED_WORKSPACE);
    const copy = disclosedCopy();
    expect(copy).not.toContain(PRIVATE_A.audience);
    expect(copy).not.toContain(PRIVATE_A.brandVoice);
    expect(copy).not.toContain(PRIVATE_A.callToAction);
  });

  it("does not let Account A's private copy survive B's own autosave and a reload of the cloned tab", async () => {
    await openTab("tab-1", ACCOUNT_A, SHARED_WORKSPACE);
    await composePrivateDraftA();
    await openTab("tab-2", ACCOUNT_A, SHARED_WORKSPACE, "tab-1");
    await switchAccountInThisTab(ACCOUNT_B, SHARED_WORKSPACE);
    // B edits: today the hydrated foreign state is what the autosave writes back, so B
    // re-authors A's private copy as B's own draft instead of discarding it.
    await typeInto(promptNode()!, PRIVATE_B.prompt);
    await reloadStudio();
    expect(promptValue()).toBe(PRIVATE_B.prompt);
    const copy = disclosedCopy();
    expect(copy).not.toContain(PRIVATE_A.audience);
    expect(copy).not.toContain(PRIVATE_A.brandVoice);
    expect(copy).not.toContain(PRIVATE_A.callToAction);
  });

  it("does not resurrect Account A's draft when B switches workspace and back in the cloned tab", async () => {
    await openTab("tab-1", ACCOUNT_A, SHARED_WORKSPACE);
    await composePrivateDraftA();
    await openTab("tab-2", ACCOUNT_A, SHARED_WORKSPACE, "tab-1");
    // B starts from a different workspace and returns: leaving and coming back must not
    // deliver the draft that arrived with the tab, because B never composed it.
    await switchAccountInThisTab(ACCOUNT_B, OTHER_WORKSPACE);
    expect(promptValue()).toBe("");
    const tab = active!;
    tab.workspaceId = SHARED_WORKSPACE;
    await reloadStudio();
    expect(promptValue()).not.toContain(PRIVATE_A.prompt);
    expect(promptValue()).toBe("");
  });

  // --- Retention and browser-semantics guards: GREEN today, and must stay GREEN. ---
  it("guard: Account A's own duplicated tab still restores A's draft", async () => {
    await openTab("tab-1", ACCOUNT_A, SHARED_WORKSPACE);
    await composePrivateDraftA();
    await openTab("tab-2", ACCOUNT_A, SHARED_WORKSPACE, "tab-1");
    expect(promptValue()).toContain(PRIVATE_A.prompt);
    expect(advancedValue("social.brandVoice")).toBe(PRIVATE_A.brandVoice);
  });

  it("guard: work in the cloned tab never rewrites the original tab's composition", async () => {
    await openTab("tab-1", ACCOUNT_A, SHARED_WORKSPACE);
    await composePrivateDraftA();
    await openTab("tab-2", ACCOUNT_A, SHARED_WORKSPACE, "tab-1");
    await switchAccountInThisTab(ACCOUNT_B, SHARED_WORKSPACE);
    await typeInto(promptNode()!, PRIVATE_B.prompt);
    // Independent partitions: no cross-tab propagation and no `storage` event for
    // sessionStorage. A remedy that invents shared storage to "fix" the leak breaks this.
    const tabOne = tabs.get("tab-1")!;
    activate(tabOne);
    await enterStudio();
    expect(promptValue()).toContain(PRIVATE_A.prompt);
    expect(promptValue()).not.toContain(PRIVATE_B.prompt);
  });

  it("guard: an independently opened tab hands the next identity nothing at all", async () => {
    await openTab("tab-1", ACCOUNT_A, SHARED_WORKSPACE);
    await composePrivateDraftA();
    // No cloneFrom: a tab opened directly (new window, bookmark, typed URL) starts with an
    // empty session storage. Green today — so the isolation cases above cannot be satisfied
    // by weakening restore into a no-op everywhere.
    await openTab("tab-2", ACCOUNT_B, SHARED_WORKSPACE);
    expect(promptValue()).toBe("");
    // The join in disclosedCopy() separates fields with newlines, so an empty
    // composer is whitespace rather than an empty string.
    expect(disclosedCopy().trim()).toBe("");
  });

  it("guard: synthetic fixtures stay non-overlapping so isolation cannot pass by accident", () => {
    const values = Object.values(PRIVATE_A);
    expect(new Set(values).size).toBe(values.length);
    expect(values.every((value) => !PRIVATE_B.prompt.includes(value))).toBe(true);
    expect(ACCOUNT_A).not.toBe(ACCOUNT_B);
    expect(SHARED_WORKSPACE).not.toBe(OTHER_WORKSPACE);
  });
});
