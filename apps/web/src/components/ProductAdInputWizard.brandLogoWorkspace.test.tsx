import { act } from "react";
import { flushSync } from "react-dom";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  applyProductAdDraftEdit,
  persistProductAdDraft,
  productAdDraftStorageKey,
  productAdPendingAssetNoticeKind,
  readProductAdDraft,
  type ProductAdDraftStore,
} from "@/lib/productAdDraftState";
import { ProductAdInputWizard } from "./ProductAdInputWizard";

/**
 * The optional brand logo across a WORKSPACE / scope change.
 *
 * The logo is the one draft asset whose expiry never blocks the user:
 * `productAdPendingAssetNoticeKind` deliberately reports "none" for a stale logo
 * sitting behind a re-attached photo, so the only thing that can still tell the
 * user the logo is gone is the restored-draft live region. PR #937 wrote the rule
 * that region has to obey into `ProductAdInputWizard.tsx` - "a live region that is
 * inserted together with its text is not announced" - and a scope change is the
 * moment that rule is hardest to keep, because the wizard masks its own tree until
 * the post-commit restore has read the incoming workspace's draft.
 *
 * Sibling suites stay out of scope: `brandLogoOptional.test.tsx` (#785) owns logo
 * survival across a photo replacement, `logoRemovalStaleReference.test.tsx` (#798)
 * owns removal before continue, and the account-switch isolation suite owns the
 * API boundary. Nothing here re-pins those three.
 *
 * Synthetic and deterministic: a hand-built DOM in the style of
 * `ImageStudioBrowserLifecycle.test.tsx`, an in-memory session store, stubbed
 * `URL.createObjectURL`/`revokeObjectURL` and a frozen clock. No provider call, no
 * network, no real file and no real user data.
 */

// ---- synthetic DOM harness, in the style of ProjectContextEditor / ImageStudioBrowserLifecycle ----
class TestNode {
  nodeType: number;
  nodeName: string;
  ownerDocument: TestDocument;
  parentNode: TestNode | null = null;
  childNodes: TestNode[] = [];
  attributes = new Map<string, string>();
  style: Record<string, string> = {};
  listeners = new Map<string, Set<(event: unknown) => void>>();
  files: unknown[] | null = null;
  private innerValue = "";
  constructor(nodeType: number, nodeName: string, ownerDocument: TestDocument) {
    this.nodeType = nodeType;
    this.nodeName = nodeName;
    this.ownerDocument = ownerDocument;
  }
  get firstChild() { return this.childNodes[0] ?? null; }
  get options() { return this.childNodes; }
  get tagName() { return this.nodeName; }
  get textContent() { return this.childNodes.map((child) => child.textContent ?? "").join(""); }
  set textContent(value: string | null) {
    this.childNodes = value ? [new TestNodeText(value, this.ownerDocument)] : [];
    for (const child of this.childNodes) child.parentNode = this;
  }
  get className() { return this.attributes.get("class") ?? ""; }
  set className(value: string) { this.attributes.set("class", value); }
  get value() { return this.innerValue; }
  set value(next: string) { this.innerValue = String(next); }
  appendChild<T extends TestNode>(child: T) { if (child.parentNode) child.parentNode.removeChild(child); child.parentNode = this; this.childNodes.push(child); return child; }
  insertBefore<T extends TestNode>(child: T, before: TestNode | null) {
    if (!before) return this.appendChild(child);
    const index = this.childNodes.indexOf(before);
    if (index < 0) return this.appendChild(child);
    if (child.parentNode) child.parentNode.removeChild(child);
    child.parentNode = this;
    this.childNodes.splice(index, 0, child);
    return child;
  }
  removeChild<T extends TestNode>(child: T) { const index = this.childNodes.indexOf(child); if (index >= 0) this.childNodes.splice(index, 1); child.parentNode = null; return child; }
  setAttribute(name: string, value: string) { this.attributes.set(name, String(value)); }
  getAttribute(name: string) { return this.attributes.get(name) ?? null; }
  removeAttribute(name: string) { this.attributes.delete(name); }
  addEventListener(name: string, listener: (event: unknown) => void) { const set = this.listeners.get(name) ?? new Set(); set.add(listener); this.listeners.set(name, set); }
  removeEventListener(name: string, listener: (event: unknown) => void) { this.listeners.get(name)?.delete(listener); }
  dispatchEvent(event: TestEvent) {
    event.target ??= this;
    event.defaultPrevented ??= false;
    event.preventDefault ??= () => { event.defaultPrevented = true; };
    event.stopPropagation ??= () => { event.propagationStopped = true; };
    this.notify(event, this);
    return true;
  }
  private notify(event: TestEvent, node: TestNode) {
    event.currentTarget = node;
    for (const listener of node.listeners.get(event.type) ?? []) listener(event);
    if (!event.propagationStopped && node.parentNode) this.notify(event, node.parentNode);
  }
  contains(node: TestNode | null): boolean { return node === this || this.childNodes.some((child) => child.contains(node)); }
  focus() { this.ownerDocument.activeElement = this; }
  blur() { if (this.ownerDocument.activeElement === this) this.ownerDocument.activeElement = this.ownerDocument.body; }
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
  createElementNS(_namespace: string, name: string) { return this.createElement(name); }
  createTextNode(value: string) { return new TestNodeText(value, this); }
  getElementById(id: string) { return this.find((node) => node.getAttribute("id") === id); }
  private find(predicate: (node: TestNode) => boolean): TestNode | null {
    const visit = (node: TestNode): TestNode | null => (predicate(node) ? node : node.childNodes.map(visit).find(Boolean) ?? null);
    return visit(this);
  }
}

// ---- fixed synthetic input --------------------------------------------------
const SAVED_NOW = Date.UTC(2026, 10, 17, 3, 0, 0);
type Scope = { userId: string; workspaceId: string };
const scopeA: Scope = { userId: "user-1", workspaceId: "workspace-a" };
const scopeB: Scope = { userId: "user-1", workspaceId: "workspace-b" };
const photoFile = { name: "cedar-silk-photo.jpg", type: "image/jpeg", size: 240_000 };
const logoFile = { name: "cedar-silk-logo.png", type: "image/png", size: 18_400 };

let entries: Map<string, string>;
let store: ProductAdDraftStore & { writes: string[] };
let created: string[];
let revoked: string[];
let doc: TestDocument;
let container: TestNode;
let root: Root | null = null;
let urlSeq = 0;

/** A draft as the previous visit to this scope left it: names kept, bytes gone. */
function expiredUploadDraft() {
  return applyProductAdDraftEdit(null, {
    productName: "Cedar & Silk hand cream",
    benefit: "Calm, soft skin in one minute",
    channel: "instagram",
    format: "square",
    productPhoto: photoFile,
    brandLogo: logoFile,
  }, SAVED_NOW);
}
function seedExpiredUploads(scope: Scope) {
  persistProductAdDraft(store, scope, expiredUploadDraft());
  store.writes.length = 0;
}

const allNodes = (node: TestNode): TestNode[] => [node, ...node.childNodes.flatMap(allNodes)];
const byIdSuffix = (suffix: string) => allNodes(container).find((node) => (node.getAttribute("id") ?? "").endsWith(suffix)) ?? null;
const documentText = () => allNodes(container).map((node) => node.textContent).join("|");
const logoPreviewSrcs = () => allNodes(container)
  .filter((node) => node.getAttribute("data-preview-asset") === "brand-logo")
  .map((node) => node.getAttribute("src"));

/**
 * One observation of the rendered wizard. `region` is the live <p> itself, so a
 * test can tell "the same region gained a sentence" from "a new region was
 * inserted already holding it" - the distinction the whole notice depends on.
 */
type View = {
  masked: boolean;
  region: TestNode | null;
  regionText: string;
  regionAttrs: (string | null)[];
  text: string;
  logoSrcs: (string | null)[];
};
function view(): View {
  const region = byIdSuffix("-draft-notice");
  return {
    masked: documentText().includes("Restoring saved product-ad draft"),
    region,
    regionText: region?.textContent ?? "\u0000absent",
    regionAttrs: region ? ["role", "aria-live", "aria-atomic"].map((name) => region.getAttribute(name)) : [],
    text: documentText(),
    logoSrcs: logoPreviewSrcs(),
  };
}
const drain = () => new Promise((resolve) => setTimeout(resolve, 0));
/** The brand-logo descriptor the wizard last committed for this scope, if any. */
function storedDraftLogo(scope: Scope) {
  return readProductAdDraft(store, scope)?.brandLogo ?? null;
}

/**
 * The first committed render of `scope`: the mask window, before the queued
 * session restore has run.
 */
function firstCommit(scope: Scope | null) {
  // `flushSync` ends the commit that the render scheduled, so the effects have
  // run and the restore is queued as a microtask - but the microtask cannot have
  // run yet, because the synchronous step has not ended. That is the mask window.
  flushSync(() => { root!.render(<ProductAdInputWizard principal={scope} />); });
  return view();
}
/**
 * Mount, snapshot the mask window, then let the queued restore land and snapshot
 * again - all inside one `act`, so the restore's own state update is observed as
 * part of the same controlled commit sequence.
 */
async function mountAndSettle(scope: Scope | null) {
  let mask!: View;
  await act(async () => {
    root = createRoot(container as unknown as Element);
    mask = firstCommit(scope);
    await drain();
  });
  return { mask, settled: view() };
}
/** The same two snapshots for a scope change on an already-mounted wizard. */
async function switchAndSettle(scope: Scope | null) {
  let mask!: View;
  await act(async () => {
    mask = firstCommit(scope);
    await drain();
  });
  return { mask, settled: view() };
}
async function settle(action: () => void) {
  await act(async () => { action(); await drain(); });
}
async function pickFile(idSuffix: string, file: { name: string; type: string; size: number } | null) {
  const input = byIdSuffix(idSuffix);
  if (!input) throw new Error(`no ${idSuffix} input rendered`);
  await settle(() => { input.files = file ? [file] : []; input.dispatchEvent({ type: "change", bubbles: true }); });
}
async function buildPreview() {
  const form = allNodes(container).find((node) => node.nodeName === "FORM");
  if (!form) throw new Error("no form rendered");
  await settle(() => { form.dispatchEvent({ type: "submit", bubbles: true, cancelable: true }); });
}

beforeEach(() => {
  entries = new Map();
  created = [];
  revoked = [];
  urlSeq = 0;
  const writes: string[] = [];
  store = {
    writes,
    getItem: (key) => entries.get(key) ?? null,
    setItem: (key, value) => { writes.push(key); entries.set(key, value); },
    removeItem: (key) => { entries.delete(key); },
  };
  doc = new TestDocument();
  container = doc.createElement("div");
  doc.body.appendChild(container);
  const windowStub = {
    document: doc, HTMLElement: TestNode, HTMLIFrameElement: TestNode, Element: TestNode, Node: TestNode,
    addEventListener: vi.fn(), removeEventListener: vi.fn(),
    sessionStorage: store,
  };
  doc.defaultView = windowStub;
  vi.stubGlobal("document", doc);
  vi.stubGlobal("window", windowStub);
  vi.stubGlobal("navigator", { userAgent: "synthetic-test" });
  vi.stubGlobal("IS_REACT_ACT_ENVIRONMENT", true);
  const url = globalThis.URL as unknown as Record<string, unknown>;
  url.createObjectURL = vi.fn(() => { const next = `blob:synthetic-${++urlSeq}`; created.push(next); return next; });
  url.revokeObjectURL = vi.fn((objectUrl: string) => { revoked.push(objectUrl); });
  vi.spyOn(Date, "now").mockReturnValue(SAVED_NOW);
});
afterEach(() => {
  act(() => { root?.unmount(); });
  root = null;
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("Product Ad optional brand logo across workspace scope changes", () => {
  it("mounts the shared notice region empty on a scope's first committed render", async () => {
    seedExpiredUploads(scopeA);
    const { mask, settled } = await mountAndSettle(scopeA);

    // Every authenticated render starts masked: the draft is identity-bound, so it
    // can only be read after the commit. If the mask swapped the whole tree, the
    // region carrying the expired-logo sentence would be born holding that
    // sentence - the one shape assistive technology never announces.
    expect(mask.masked).toBe(true);
    expect(mask.region, "the scope-change mask unmounted the draft-notice live region").not.toBeNull();
    expect(mask.regionAttrs).toEqual(["status", "polite", "true"]);
    // Silent on the first commit, and holding nothing from the scope being replaced.
    expect(mask.regionText).toBe("");
    // And it is filled, not replaced, once the restore has run.
    expect(settled.region).toBe(mask.region);
    expect(settled.masked).toBe(false);
  });

  it("fills the very same region with the brand-logo retry once the restore commits", async () => {
    seedExpiredUploads(scopeA);
    const { mask, settled } = await mountAndSettle(scopeA);

    // One host element, not a replacement: the retry has to arrive as a text change
    // inside a region the accessibility tree already owns.
    expect(settled.region).toBe(mask.region);
    expect(settled.regionText).toContain("Draft restored: Cedar & Silk hand cream.");
    // The optional logo is named inside the retry it shares with the required photo.
    expect(settled.regionText).toMatch(/brand logo/i);
    expect(settled.regionText).toMatch(/Re-attach/i);
    // What is being retried is the logo's stored descriptor, never a painted preview:
    // a restored draft has no bytes behind it, so no object URL may exist yet.
    expect(settled.logoSrcs).toEqual([]);
    expect(created).toEqual([]);
    expect(settled.text).toContain("Brand logo (optional)");
  });

  it("hands the live region from the outgoing workspace to the incoming one", async () => {
    // Workspace A is filled in by hand, with a live brand-logo preview on screen.
    await mountAndSettle(scopeA);
    await pickFile("-product-photo", photoFile);
    await pickFile("-brand-logo", logoFile);
    await buildPreview();
    const filled = view();
    expect(filled.logoSrcs).toHaveLength(1);
    expect(filled.text).toContain(logoFile.name);
    seedExpiredUploads(scopeB);

    // The mask window for the incoming workspace: committed, not yet restored.
    const { mask, settled } = await switchAndSettle(scopeB);
    expect(mask.masked).toBe(true);
    expect(mask.region, "the region must survive the workspace switch").not.toBeNull();
    expect(mask.region).toBe(filled.region);
    // It carries neither the outgoing logo's name nor its live object URL, and the
    // incoming workspace's logo has not been adopted yet either.
    expect(mask.regionText).toBe("");
    expect(mask.text).not.toContain(logoFile.name);
    expect(mask.logoSrcs).toEqual([]);

    // The incoming workspace's own expired logo is announced in that same region.
    expect(settled.region).toBe(filled.region);
    expect(settled.regionText).toMatch(/brand logo/i);
    expect(settled.masked).toBe(false);
  });

  it("never paints a preview for a logo whose bytes did not come back", async () => {
    // A draft whose only surviving asset is the optional brand logo.
    const logoOnly = applyProductAdDraftEdit(null, {
      productName: "Cedar & Silk hand cream",
      benefit: "",
      channel: "instagram",
      format: "square",
      productPhoto: null,
      brandLogo: logoFile,
    }, SAVED_NOW);
    persistProductAdDraft(store, scopeA, logoOnly);
    store.writes.length = 0;

    const { settled } = await mountAndSettle(scopeA);
    // The bytes are gone, so there is nothing to preview and nothing to revoke.
    expect(settled.logoSrcs).toEqual([]);
    expect(created).toEqual([]);
    expect(revoked).toEqual([]);
    // The optional logo is remembered, so its name survives in the stored draft.
    expect(storedDraftLogo(scopeA)).toEqual({ name: logoFile.name, mime: "image/png", bytes: logoFile.size });
    // And the documented logo-optional contract holds: the draft is named back, but
    // no re-attach is demanded for the optional logo, so it never blocks a retry.
    expect(productAdPendingAssetNoticeKind(logoOnly, { productPhoto: false, brandLogo: false })).toBe("none");
    expect(settled.regionText).toBe("Draft restored: Cedar & Silk hand cream.");
    expect(settled.regionText).not.toMatch(/re-attach/i);
  });

  it("keeps a foreign workspace's brand logo out of the restored draft and purges it", async () => {
    // An envelope stored under B's identity-bound key that claims workspace A.
    const foreignKey = productAdDraftStorageKey(scopeB);
    if (!foreignKey) throw new Error("expected an identity-bound key");
    entries.set(foreignKey, JSON.stringify({
      ...expiredUploadDraft(),
      brandLogo: { name: "other-team-logo.png", mime: "image/png", bytes: 999 },
      savedByUser: scopeB.userId,
      savedByWorkspace: scopeA.workspaceId,
    }));

    expect(readProductAdDraft(store, scopeB)).toBeNull();
    // Purged on the owner mismatch, so it cannot be probed a second time.
    expect(entries.has(foreignKey)).toBe(false);

    const { settled } = await mountAndSettle(scopeB);
    expect(settled.text).not.toContain("other-team-logo.png");
    expect(settled.regionText).toBe("");
    expect(settled.logoSrcs).toEqual([]);
  });

  it("revokes every brand-logo object URL exactly once across a workspace switch", async () => {
    await mountAndSettle(scopeA);
    await pickFile("-product-photo", photoFile);
    await pickFile("-brand-logo", logoFile);
    await buildPreview();
    const liveLogoUrl = view().logoSrcs[0];
    expect(liveLogoUrl).toBeDefined();
    expect(new Set(created).size).toBe(created.length);

    // Straight into the mask window: the outgoing URL must not survive it painted.
    const { mask } = await switchAndSettle(scopeB);
    expect(mask.logoSrcs).toEqual([]);

    await settle(() => { root?.unmount(); });
    root = null;

    // Every created URL - photo and logo - was revoked, and none of them twice.
    expect(new Set(revoked).size).toBe(revoked.length);
    expect(new Set(revoked)).toEqual(new Set(created));
    expect(revoked).toContain(liveLogoUrl);
  });
});
