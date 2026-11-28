/**
 * Issue #688 — provider-free browser-lifecycle acceptance for the **selected source file and asset
 * identifiers** across a *cloned new tab*, stacked on #1073's identity+workspace scoping.
 *
 * WHY THIS LANE EXISTS (and why it is not a duplicate)
 * `sessionStorage` is per top-level browsing context, but per the HTML Living Standard a tab created
 * FROM a tab receives a point-in-time clone of the opener's session storage. So a persisted private
 * draft can walk into a second tab before any account boundary is observed there. That lifecycle is
 * pinned by the test-only lane #1148 and the remedy is implemented by #1073 — but what neither one
 * measures is the identifier half of #688's own acceptance criterion 2: "Account B must not see A's
 * prompt, **selected file/asset identifiers** or cached draft".
 *   * #1148's cross-tab file asserts prompt / audience / brand voice / CTA *text* and carries no
 *     file or asset assertion at all (`selectedAssets`, `selectedFiles`, `assetId`: zero hits).
 *   * #1073's suites do assert identifiers, but only inside ONE tab (`SocialStudioDraftAccountSwitch`
 *     models a single storage map) — never across the tab boundary this lane is about.
 * Selected identifiers are not cosmetic copy. The studio transmits them on generate
 * (`assetIds: selectedAssets, attachmentIds: selectedFiles`), so a foreign selection that survives
 * hydration is spent under the arriving identity against somebody else's private inputs.
 *
 * CONTRACT PROVEN HERE — always through the studio's own hydration + autosave + submit path, never a
 * re-implementation of it, and always about what a principal can *observe* or *transmit*:
 *   1. Account B, first to mount in a tab cloned from Account A's tab on a colliding workspace id, is
 *      rendered none of A's private copy and NONE of A's selection, and transmits none of A's ids.
 *   2. Same after A signs out inside that cloned tab.
 *   3. A's selection does not survive B's own autosave and a reload of the cloned tab.
 *   4. Leaving to another workspace and returning inside the cloned tab does not resurrect it.
 *   5. Guards that keep any fix honest: Account A's own authorized reload, and A's own duplicated
 *      tab, still restore A's draft INCLUDING the selected file and asset; work in the cloned tab
 *      never rewrites the original tab's partition; an independently opened tab hands nothing over.
 *
 * WHAT THIS LANE DELIBERATELY DOES NOT ASSERT
 * It does not require that A's persisted bytes vanish from the cloned tab's partition. #1073 scopes
 * the address by identity and owner-binds the envelope, so the entry is unreachable to B, and it
 * keeps that entry precisely so A can come back to it — an arrival-side purge would break guard 5 and
 * #1073's own "restores Account A's own draft when A returns". Clearing the departing identity's
 * bytes when the studio is NOT mounted is the sign-out-boundary question #1073 already routes to the
 * auth-session lane (#279/#301, the owners of `AuthProvider.tsx`), not to this one. So the assertions
 * below are behavioural, not storage-shaped, and both permitted #688 remedies satisfy them.
 *
 * STATUS: GREEN on the #1073 head this branch is stacked on; the four isolation expectations are RED
 * on `main`, verified in this PR's description. It must not be re-based onto `main` while #1073 is
 * unmerged: flipping these to encode today's behaviour would certify the leak. No owned file is
 * edited — this is one new, self-contained test file, and it imports nothing from #1073's modules,
 * so it stays branch-agnostic and adoption needs no edit here.
 *
 * SYNTHETIC ONLY: no browser binary, no Playwright, no dev server, no network, no provider call, no
 * charge, no secret, no real account, no production data. Sentinels are prefixed
 * `SYNTHETIC-688-XTAB-ASSET`, so a failure log can only ever echo synthetic text.
 */
import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// ---- synthetic DOM harness, in the style of SocialStudioCancellation / #1073's account-switch lane ----
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
  /**
   * Plain field, deliberately. React drives a controlled checkbox by assigning the DOM `checked`
   * *property* and never the attribute, so an attribute-backed accessor would read back false
   * forever and every selection assertion below would pass for the wrong reason.
   */
  checked = false;
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
  /** `input[type]` reports "text" when unset; React keys its change dispatch off this value. */
  get type(): string | undefined { return this.currentType ?? this.attributes.get("type") ?? (this.nodeName === "INPUT" ? "text" : undefined); }
  set type(value: string | undefined) { this.currentType = value === undefined ? undefined : String(value); }
  get value() { return this.currentValue; }
  set value(value: string) { this.currentValue = String(value); }
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
// A stable instance: the studio keeps searchParams in an effect dependency list, so a fresh object
// per render would re-run the hydration effect forever.
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
const ACCOUNT_A = "synthetic-xtab-asset-account-a-688";
const ACCOUNT_B = "synthetic-xtab-asset-account-b-688";
const SHARED_WORKSPACE_ID = "synthetic-xtab-asset-workspace-shared-688";
const OTHER_WORKSPACE_ID = "synthetic-xtab-asset-workspace-other-688";

/** Clearly synthetic private copy; only these sentinels can ever appear in a failure log. */
const PRIVATE_A = {
  prompt: "SYNTHETIC-688-XTAB-ASSET A private brief never real data",
  audience: "SYNTHETIC-688-XTAB-ASSET A private audience",
  brandVoice: "SYNTHETIC-688-XTAB-ASSET A private brand voice",
  callToAction: "SYNTHETIC-688-XTAB-ASSET A private CTA",
} as const;
const PRIVATE_B = { prompt: "SYNTHETIC-688-XTAB-ASSET B private brief never real data" } as const;

/**
 * The two source items A selected. Both are LISTABLE by either principal, because the colliding
 * workspace id resolves the same input catalogue for both — the worst case #688 assumes. Catalogue
 * membership is therefore NOT the leak: a workspace file B is entitled to list is not disclosed by
 * appearing in B's list. What IS disclosed is A's *selection* of it (which reveals what A was
 * working on) and, far worse, the studio spending it: `create` sends
 * `assetIds: selectedAssets, attachmentIds: selectedFiles`, so a foreign selection surviving
 * hydration is transmitted under B's identity against A's private inputs. Every assertion below is
 * consequently about selection and transmission, never about membership in the shared listing.
 */
const A_SOURCE_FILE_ID = "synthetic-xtab-asset-file-a-688";
const A_SOURCE_FILE_NAME = "SYNTHETIC-688-XTAB-ASSET private-source-file";
const A_ASSET_ID = "synthetic-xtab-asset-image-a-688";
const A_ASSET_NAME = "SYNTHETIC-688-XTAB-ASSET private-asset-name";
function sourceFileFixtures() {
  return [{
    id: A_SOURCE_FILE_ID, workspaceId: SHARED_WORKSPACE_ID, projectId: null, conversationId: null,
    originalFileName: A_SOURCE_FILE_NAME, contentType: "application/pdf", extension: ".pdf", sizeBytes: 20480,
    storageProvider: "synthetic", status: "Ready" as const, textExtractionStatus: "Ready" as const,
    extractedTextLength: 100, createdAt: "2026-01-01T00:00:00Z", processedAt: "2026-01-01T00:00:00Z",
  }];
}
function assetFixtures() {
  return [{
    id: A_ASSET_ID, workspaceId: SHARED_WORKSPACE_ID, projectId: null, projectName: null,
    name: A_ASSET_NAME, description: null, assetType: "image" as const, mimeType: "image/png",
    status: "Active" as const, hasFile: true, canPreview: true, fileSizeBytes: 1024, sourceStudio: null,
    sourceJobTitle: null, createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z",
    archivedAt: null, representations: [],
  }];
}
/** What only A selected: must never arrive pre-selected for, or be transmitted by, anyone else. */
const A_SELECTED_IDS = [A_SOURCE_FILE_ID, A_ASSET_ID] as const;
/** A's private copy, as a whole: must never be observable to another principal. */
const A_PRIVATE_COPY = [PRIVATE_A.prompt, PRIVATE_A.audience, PRIVATE_A.brandVoice, PRIVATE_A.callToAction] as const;

// ---- one browsing context per tab: its own session-storage partition, document and mount ----
type Tab = {
  id: string;
  storage: Map<string, string>;
  doc: TestDocument;
  container: TestNode;
  root: Root | null;
  principal: string | null;
  workspaceId: string | null;
};
let tabs = new Map<string, Tab>();
let active: Tab | null = null;

async function drain() { await new Promise((resolve) => setTimeout(resolve, 0)); }
async function settle(fn: () => void) { await act(async () => { fn(); await drain(); }); }
function walk(node: TestNode): TestNode[] { return [node, ...node.childNodes.flatMap(walk)]; }

function makeWindowStub(tab: Tab) {
  return {
    document: tab.doc, HTMLElement: TestNode, HTMLIFrameElement: TestNode, Element: TestNode, Node: TestNode,
    addEventListener: vi.fn(), removeEventListener: vi.fn(),
    // Inert: this lane observes hydration / autosave / submit, not job polling timers, and a live
    // timer would let a restore effect re-enter the render loop being measured.
    setTimeout: () => 0,
    clearTimeout: () => undefined,
    // Each browsing context resolves storage against ITS OWN partition. That is the whole point of
    // this lane: a workspace-only address is meaningless once the partition itself is inherited.
    sessionStorage: {
      getItem: (key: string) => tab.storage.get(key) ?? null,
      setItem: (key: string, value: string) => { tab.storage.set(key, value); },
      removeItem: (key: string) => { tab.storage.delete(key); },
      key: (index: number) => [...tab.storage.keys()][index] ?? null,
      get length() { return tab.storage.size; },
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
 * `cloneFrom` reproduces the HTML session-storage rule for a tab created FROM another tab: a
 * point-in-time copy of the opener's partition. Omitting it opens an INDEPENDENT tab, whose session
 * storage starts empty — the path that must keep passing, so the isolation cases below cannot be
 * satisfied by simply making storage empty everywhere.
 */
async function openTab(id: string, principal: string, workspaceId: string, cloneFrom?: string) {
  const source = cloneFrom ? tabs.get(cloneFrom) : undefined;
  if (cloneFrom && !source) throw new Error(`unknown source tab ${cloneFrom}`);
  const tab: Tab = {
    id,
    storage: new Map(source ? source.storage : []),
    doc: new TestDocument(),
    container: null as unknown as TestNode,
    root: null,
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
/** Navigate to Social Studio as the tab's current principal: a fresh mount, so hydration re-runs. */
async function enterStudio() {
  const tab = active!;
  harness.auth.user = tab.principal ? { id: tab.principal } : null;
  harness.auth.workspace = tab.workspaceId ? { id: tab.workspaceId } : null;
  await act(async () => { tab.root = createRoot(tab.container as unknown as Element); tab.root.render(<SocialStudioView />); await drain(); });
  // The studio resolves its input catalogue asynchronously, so the selection checkboxes and the
  // hydrated selection land only after those promises settle.
  await flushAutosave();
}
async function leaveStudio() {
  const tab = active;
  const root = tab?.root;
  if (tab) tab.root = null;
  if (root) await act(async () => { root.unmount(); await drain(); });
}
/** Reload: same partition, same principal, a fresh mount of the studio. */
async function reloadStudio() { await leaveStudio(); await enterStudio(); }
/** Account boundary in THIS tab: swap the signed-in identity, then navigate back into the studio. */
async function switchAccountInThisTab(principal: string, workspaceId: string) {
  const tab = active!;
  tab.principal = principal;
  tab.workspaceId = workspaceId;
  await leaveStudio();
  await enterStudio();
}
/** Same principal, different workspace, then back: a navigation, exercised by the guards. */
async function navigateWorkspace(workspaceId: string) {
  const tab = active!;
  tab.workspaceId = workspaceId;
  await leaveStudio();
  await enterStudio();
}
/** The studio persists on the next commit, so let the autosave effect flush. */
async function flushAutosave() { await settle(() => undefined); await settle(() => undefined); }

function promptNode(tab: Tab = active!) { return walk(tab.container).find((n) => n.nodeName === "TEXTAREA"); }
function promptValue(tab: Tab = active!) { return promptNode(tab)?.value ?? ""; }
/** Advanced fields render as `<label class="field"><span>{t(key)}</span><input …/></label>`. */
function advancedValue(labelKey: string, tab: Tab = active!) {
  const labels = walk(tab.container).filter((n) => n.nodeName === "LABEL"
    && (n.getAttribute("class")?.split(/\s+/) ?? []).includes("field")
    && n.childNodes.some((child) => child.textContent === labelKey));
  const input = labels.flatMap((label) => walk(label)).find((n) => n.nodeName === "INPUT");
  return input?.value ?? "";
}
/** Everything the arriving principal can read back out of the rendered composer. */
function disclosedCopy(tab: Tab = active!) {
  return [promptValue(tab), advancedValue("social.audience", tab), advancedValue("social.brandVoice", tab), advancedValue("social.callToAction", tab)].join("\n");
}
/** The rendered source-file / asset option rows with their checkbox state. */
function selectionOptions(tab: Tab = active!) {
  return walk(tab.container)
    .filter((n) => n.nodeName === "LABEL" && (n.getAttribute("class") ?? "").includes("social-selection-option"))
    .map((label) => ({ name: label.textContent, input: walk(label).find((n) => n.nodeName === "INPUT" && n.type === "checkbox") }))
    .filter((option): option is { name: string; input: TestNode } => !!option.input);
}
/** The rows this principal sees as ticked — i.e. the private selection actually handed over. */
function selectedOptionNames(tab: Tab = active!) { return selectionOptions(tab).filter((option) => option.input.checked).map((option) => option.name); }
/** The persisted draft for THIS tab's resolved principal, as the studio wrote it. */
function draftPayloads(tab: Tab = active!) { return [...tab.storage.values()].filter((value) => value.includes("\"prompt\"")); }

async function typeInto(node: TestNode, text: string) {
  const descriptor = Object.getOwnPropertyDescriptor(TestNode.prototype, "value")!;
  await settle(() => { node.dispatchEvent({ type: "focusin", bubbles: true }); });
  await settle(() => {
    descriptor.set!.call(node, text);
    node.dispatchEvent({ type: "input", bubbles: true });
    node.dispatchEvent({ type: "keyup", bubbles: true });
  });
}
async function click(node: TestNode) { await settle(() => { node.dispatchEvent({ type: "click", bubbles: true }); }); }
async function fieldInput(labelKey: string) {
  const labels = walk(active!.container).filter((n) => n.nodeName === "LABEL"
    && (n.getAttribute("class")?.split(/\s+/) ?? []).includes("field")
    && n.childNodes.some((child) => child.textContent === labelKey));
  const input = labels.flatMap((label) => walk(label)).find((n) => n.nodeName === "INPUT");
  if (!input) throw new Error(`synthetic field ${labelKey} is not rendered`);
  return input;
}
/** Type A's private copy and select A's private source file + asset, then let autosave persist it. */
async function composeAndSelectAsA() {
  await typeInto(promptNode()!, PRIVATE_A.prompt);
  const advancedFields: readonly [string, string][] = [
    ["social.audience", PRIVATE_A.audience],
    ["social.brandVoice", PRIVATE_A.brandVoice],
    ["social.callToAction", PRIVATE_A.callToAction],
  ];
  for (const [labelKey, value] of advancedFields) await typeInto(await fieldInput(labelKey), value);
  const fileOption = selectionOptions().find((option) => option.name.includes(A_SOURCE_FILE_NAME));
  const assetOption = selectionOptions().find((option) => option.name.includes(A_ASSET_NAME));
  if (!fileOption || !assetOption) throw new Error("synthetic source file / asset options are not rendered");
  await click(fileOption.input);
  await click(assetOption.input);
  await flushAutosave();
  expect(selectedOptionNames()).toHaveLength(2);
}
/** Drive the studio's own submit so the outgoing generation payload can be inspected. */
async function submitGeneration() {
  const form = walk(active!.container).find((n) => n.nodeName === "FORM");
  if (!form) throw new Error("no form rendered");
  harness.api.createSocialGenerationJob.mockReset().mockResolvedValue({
    id: "synthetic-xtab-asset-job-688", workspaceId: active!.workspaceId, jobType: "social.generate", status: "Succeeded",
  });
  await settle(() => { form.dispatchEvent({ type: "submit", bubbles: true, cancelable: true }); });
  await flushAutosave();
  const calls = harness.api.createSocialGenerationJob.mock.calls;
  if (calls.length === 0) throw new Error("the studio did not submit a generation request");
  return calls[calls.length - 1][0] as { prompt: string; assetIds: string[]; attachmentIds: string[] };
}

beforeEach(() => {
  tabs = new Map();
  active = null;
  harness.api.listProjects.mockReset().mockResolvedValue([]);
  harness.api.listFiles.mockReset().mockResolvedValue(sourceFileFixtures());
  harness.api.listAssets.mockReset().mockResolvedValue({ items: assetFixtures(), page: 1, pageSize: 100, totalCount: 1, totalPages: 1 });
  harness.api.getGenerationJob.mockReset().mockResolvedValue(null);
  harness.api.createSocialGenerationJob.mockReset();
  harness.api.cancelGenerationJob.mockReset();
  harness.auth.workspace = null;
  harness.auth.user = null;
});
afterEach(async () => { await leaveStudio(); vi.unstubAllGlobals(); });

describe("Social Studio selected file/asset identifiers across a cloned new tab (#688 criterion 2)", () => {
  // --- Isolation contract. ---
  it("renders Account B neither A's private copy nor A's selection in a tab cloned from A's tab", async () => {
    await openTab("tab-1", ACCOUNT_A, SHARED_WORKSPACE_ID);
    await composeAndSelectAsA();
    // tab-2 is created FROM tab-1 (its session storage is cloned) and B is the FIRST principal to
    // mount the studio there: A never signs out in tab-2, so no account-boundary scrub can be
    // credited for these results — isolation must come from the addressing itself.
    await openTab("tab-2", ACCOUNT_B, SHARED_WORKSPACE_ID, "tab-1");
    expect(promptValue()).toBe("");
    expect(disclosedCopy().trim()).toBe("");
    expect(selectedOptionNames()).toEqual([]);
    for (const text of A_PRIVATE_COPY) expect(disclosedCopy()).not.toContain(text);
    for (const id of A_SELECTED_IDS) {
      expect(selectedOptionNames().join(",")).not.toContain(id);
    }
  });

  it("hands Account B no selection after Account A signs out inside the cloned tab", async () => {
    await openTab("tab-1", ACCOUNT_A, SHARED_WORKSPACE_ID);
    await composeAndSelectAsA();
    await openTab("tab-2", ACCOUNT_A, SHARED_WORKSPACE_ID, "tab-1");
    await switchAccountInThisTab(ACCOUNT_B, SHARED_WORKSPACE_ID);
    expect(promptValue()).toBe("");
    expect(disclosedCopy().trim()).toBe("");
    expect(selectedOptionNames()).toEqual([]);
    // The re-attribution bug: on `main` the autosave that follows this switch writes the hydrated
    // foreign state BACK, so A's selection becomes B's own draft. B therefore composes something,
    // and B's OWN persisted draft — located by B's own text, never by a key format, so this stays
    // agnostic to how the remedy addresses entries — must carry none of A's identifiers. Whether
    // A's own still-owner-bound entry lingers in the partition is the sign-out-scrub question this
    // lane deliberately leaves to the auth-session lane (#279/#301), not a #688 disclosure.
    await typeInto(promptNode()!, PRIVATE_B.prompt);
    await flushAutosave();
    const bDrafts = draftPayloads().filter((payload) => payload.includes(PRIVATE_B.prompt));
    expect(bDrafts).toHaveLength(1);
    for (const id of A_SELECTED_IDS) expect(bDrafts[0]).not.toContain(id);
    for (const text of A_PRIVATE_COPY) expect(bDrafts[0]).not.toContain(text);
  });

  it("does not let A's selection survive B's own autosave and a reload of the cloned tab", async () => {
    await openTab("tab-1", ACCOUNT_A, SHARED_WORKSPACE_ID);
    await composeAndSelectAsA();
    await openTab("tab-2", ACCOUNT_B, SHARED_WORKSPACE_ID, "tab-1");
    await typeInto(promptNode()!, PRIVATE_B.prompt);
    await flushAutosave();
    await reloadStudio();
    expect(promptValue()).toBe(PRIVATE_B.prompt);
    expect(selectedOptionNames()).toEqual([]);
    for (const text of A_PRIVATE_COPY) expect(disclosedCopy()).not.toContain(text);
  });

  it("does not transmit A's selected file or asset identifiers on B's own generation", async () => {
    await openTab("tab-1", ACCOUNT_A, SHARED_WORKSPACE_ID);
    await composeAndSelectAsA();
    await openTab("tab-2", ACCOUNT_B, SHARED_WORKSPACE_ID, "tab-1");
    await typeInto(promptNode()!, PRIVATE_B.prompt);
    await flushAutosave();
    const payload = await submitGeneration();
    expect(payload.prompt).toBe(PRIVATE_B.prompt);
    // These ids are spent on a billable generation, so a foreign selection here is not a display
    // glitch: it reads A's private source material into B's job.
    expect(payload.attachmentIds).toEqual([]);
    expect(payload.assetIds).toEqual([]);
    for (const id of A_SELECTED_IDS) {
      expect(JSON.stringify(payload)).not.toContain(id);
    }
  });

  it("does not resurrect A's selection when B leaves to another workspace and returns", async () => {
    await openTab("tab-1", ACCOUNT_A, SHARED_WORKSPACE_ID);
    await composeAndSelectAsA();
    await openTab("tab-2", ACCOUNT_B, SHARED_WORKSPACE_ID, "tab-1");
    await navigateWorkspace(OTHER_WORKSPACE_ID);
    expect(selectedOptionNames()).toEqual([]);
    await navigateWorkspace(SHARED_WORKSPACE_ID);
    expect(promptValue()).toBe("");
    expect(selectedOptionNames()).toEqual([]);
  });

  // --- Retention and browser-semantics guards: correct today, and must stay correct. ---
  it("guard: Account A's authorized reload restores A's draft INCLUDING its selection", async () => {
    await openTab("tab-1", ACCOUNT_A, SHARED_WORKSPACE_ID);
    await composeAndSelectAsA();
    await reloadStudio();
    expect(promptValue()).toContain(PRIVATE_A.prompt);
    expect(advancedValue("social.audience")).toBe(PRIVATE_A.audience);
    expect(advancedValue("social.brandVoice")).toBe(PRIVATE_A.brandVoice);
    expect(advancedValue("social.callToAction")).toBe(PRIVATE_A.callToAction);
    const names = selectedOptionNames();
    expect(names).toHaveLength(2);
    expect(names.some((name) => name.includes(A_SOURCE_FILE_NAME))).toBe(true);
    expect(names.some((name) => name.includes(A_ASSET_NAME))).toBe(true);
  });

  it("guard: A's own duplicated tab still restores and spends A's selection", async () => {
    await openTab("tab-1", ACCOUNT_A, SHARED_WORKSPACE_ID);
    await composeAndSelectAsA();
    // Same identity across the clone: an authorized hand-off, so it must keep working. An
    // "isolating" remedy that blanks every restore would fail here instead of passing unnoticed.
    await openTab("tab-2", ACCOUNT_A, SHARED_WORKSPACE_ID, "tab-1");
    expect(promptValue()).toContain(PRIVATE_A.prompt);
    expect(selectedOptionNames()).toHaveLength(2);
    const payload = await submitGeneration();
    expect(payload.attachmentIds).toContain(A_SOURCE_FILE_ID);
    expect(payload.assetIds).toContain(A_ASSET_ID);
  });

  it("guard: work in the cloned tab never rewrites the original tab's partition", async () => {
    await openTab("tab-1", ACCOUNT_A, SHARED_WORKSPACE_ID);
    await composeAndSelectAsA();
    const tabOne = tabs.get("tab-1")!;
    const before = [...tabOne.storage.entries()];
    await openTab("tab-2", ACCOUNT_B, SHARED_WORKSPACE_ID, "tab-1");
    await typeInto(promptNode()!, PRIVATE_B.prompt);
    await flushAutosave();
    await submitGeneration();
    // Independent partitions: sessionStorage neither propagates nor fires a `storage` event. A
    // remedy that invents shared storage to "fix" the leak breaks this guard.
    expect([...tabOne.storage.entries()]).toEqual(before);
    activate(tabOne);
    await enterStudio();
    expect(promptValue()).toContain(PRIVATE_A.prompt);
    expect(promptValue()).not.toContain(PRIVATE_B.prompt);
    expect(selectedOptionNames()).toHaveLength(2);
  });

  it("guard: an independently opened tab hands the next identity nothing at all", async () => {
    await openTab("tab-1", ACCOUNT_A, SHARED_WORKSPACE_ID);
    await composeAndSelectAsA();
    // No cloneFrom: a tab opened directly (new window, bookmark, typed URL) starts with an empty
    // session storage. Green today — so the isolation cases cannot be satisfied by weakening
    // restore into a blanket no-op.
    await openTab("tab-2", ACCOUNT_B, SHARED_WORKSPACE_ID);
    expect(promptValue()).toBe("");
    expect(disclosedCopy().trim()).toBe("");
    expect(selectedOptionNames()).toEqual([]);
  });

  it("guard: synthetic fixtures stay non-overlapping so isolation cannot pass by accident", () => {
    const values = [...A_PRIVATE_COPY, ...A_SELECTED_IDS, A_SOURCE_FILE_NAME, A_ASSET_NAME, PRIVATE_B.prompt];
    expect(new Set(values).size).toBe(values.length);
    expect(ACCOUNT_A).not.toBe(ACCOUNT_B);
    expect(SHARED_WORKSPACE_ID).not.toBe(OTHER_WORKSPACE_ID);
    expect(A_SOURCE_FILE_ID).not.toBe(A_ASSET_ID);
    for (const value of values.slice(0, 6)) expect(PRIVATE_B.prompt).not.toContain(value);
  });
});
