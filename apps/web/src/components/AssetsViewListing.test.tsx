import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Asset, AssetList } from "@/lib/api";

// Synthetic DOM harness, in the style established by ProjectContextEditor.test
// (#849). The asset library listing is read-only UI state; these cases own the
// fence between an in-flight workspace-scoped list request and the workspace
// that is actually mounted when the response arrives.
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
  defaultSelected = false;
  hidden = false;
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
  appendChild<T extends TestNode>(child: T) { if (child.parentNode) child.parentNode.removeChild(child); child.parentNode = this; this.childNodes.push(child); return child; }
  insertBefore<T extends TestNode>(child: T, before: TestNode | null) { if (!before) return this.appendChild(child); const index = this.childNodes.indexOf(before); if (index < 0) return this.appendChild(child); if (child.parentNode) child.parentNode.removeChild(child); child.parentNode = this; this.childNodes.splice(index, 0, child); return child; }
  removeChild<T extends TestNode>(child: T) { const index = this.childNodes.indexOf(child); if (index >= 0) this.childNodes.splice(index, 1); child.parentNode = null; return child; }
  setAttribute(name: string, value: string) { this.attributes.set(name, String(value)); }
  getAttribute(name: string) { return this.attributes.get(name) ?? null; }
  removeAttribute(name: string) { this.attributes.delete(name); }
  addEventListener(name: string, listener: (event: unknown) => void) { const listeners = this.listeners.get(name) ?? new Set(); listeners.add(listener); this.listeners.set(name, listeners); }
  removeEventListener(name: string, listener: (event: unknown) => void) { this.listeners.get(name)?.delete(listener); }
  // React delegates its listeners to the root container, so a dispatched event has
  // to travel up the ancestor chain the same way a browser event would.
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
  type: string;
  bubbles?: boolean;
  target?: TestNode;
  currentTarget?: TestNode;
  defaultPrevented?: boolean;
  preventDefault?: () => void;
  stopPropagation?: () => void;
  propagationStopped?: boolean;
};
class TestNodeText extends TestNode {
  nodeValue: string;
  constructor(value: string, ownerDocument: TestDocument) { super(3, "#text", ownerDocument); this.nodeValue = value; }
  override get textContent() { return this.nodeValue; }
  override set textContent(value: string | null) { this.nodeValue = value ?? ""; }
}
class TestDocument extends TestNode {
  body: TestNode;
  documentElement: TestNode;
  activeElement: TestNode;
  defaultView: object;
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
  private find(predicate: (node: TestNode) => boolean): TestNode | null { const visit = (node: TestNode): TestNode | null => (predicate(node) ? node : node.childNodes.map(visit).find(Boolean) ?? null); return visit(this); }
}

type WorkspaceFixture = { id: string; name: string };
type Deferred<T> = { promise: Promise<T>; resolve: (value: T) => void; reject: (error: unknown) => void };

const harness = vi.hoisted(() => ({
  auth: { workspace: null as { id: string; name: string } | null },
  api: {
    listAssets: vi.fn(),
    listProjects: vi.fn(),
    getAsset: vi.fn(),
    updateAsset: vi.fn(),
    archiveAsset: vi.fn(),
    restoreAsset: vi.fn(),
  },
}));

vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => ({ workspace: harness.auth.workspace }),
}));
vi.mock("@/components/LocaleProvider", () => ({
  useLocale: () => ({ t: (key: string) => key, locale: "en" }),
}));
vi.mock("@/lib/api", () => ({ api: harness.api }));
vi.mock("next/link", () => ({
  default: (props: { href: string; children?: unknown }) => <a href={props.href}>{props.children as never}</a>,
}));
vi.mock("lucide-react", () => {
  const Icon = (props: Record<string, unknown>) => <span {...props} />;
  return {
    ArrowDownUp: Icon, Filter: Icon, Grid2X2: Icon, LayoutList: Icon, LibraryBig: Icon,
    Plus: Icon, Search: Icon, SlidersHorizontal: Icon, X: Icon,
  };
});
vi.mock("@/components/AssetCard", () => ({
  AssetCard: (props: { asset: { name: string } }) => <span className="asset-card">{props.asset.name}</span>,
}));
vi.mock("@/components/AssetDetail", () => ({
  AssetDetail: () => <div className="asset-detail" />,
}));

import { AssetsView } from "./AssetsView";

const workspaceA: WorkspaceFixture = { id: "workspace-a", name: "Workspace A" };
const workspaceB: WorkspaceFixture = { id: "workspace-b", name: "Workspace B" };

function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}

function asset(id: string, name: string, workspaceId: string): Asset {
  return {
    id, workspaceId, projectId: null, projectName: null, name, description: null,
    assetType: "image", mimeType: "image/png", status: "Active", hasFile: true, canPreview: false,
    fileSizeBytes: 100, sourceStudio: "image", sourceJobTitle: null,
    createdAt: "2026-11-01T00:00:00Z", updatedAt: "2026-11-01T00:00:00Z", archivedAt: null, representations: [],
  };
}

function assetList(items: Asset[], overrides: Partial<AssetList> = {}): AssetList {
  return { items, page: 1, pageSize: 12, totalCount: items.length, totalPages: 1, ...overrides };
}

let document: TestDocument;
let root: Root;
let container: TestNode;
let timers: Map<number, () => void>;
let nextTimerId = 1;
let assetRequests: Deferred<AssetList>[];

function flushDebounce() {
  const pending = [...timers.values()];
  timers.clear();
  for (const callback of pending) callback();
}

async function drain() {
  await new Promise((resolve) => setTimeout(resolve, 0));
}

async function mountAssetsView(workspace: WorkspaceFixture) {
  harness.auth.workspace = workspace;
  await act(async () => {
    root = createRoot(container as unknown as Element);
    root.render(<AssetsView />);
    await drain();
  });
}

async function switchWorkspace(workspace: WorkspaceFixture) {
  harness.auth.workspace = workspace;
  await act(async () => {
    root.render(<AssetsView />);
    await drain();
  });
}

async function flushLoad() {
  await act(async () => {
    flushDebounce();
    await drain();
  });
}

async function settleRequest(index: number, value: AssetList) {
  await act(async () => {
    assetRequests[index].resolve(value);
    await drain();
  });
}

async function failRequest(index: number, error: unknown) {
  await act(async () => {
    assetRequests[index].reject(error);
    await drain();
  });
}

function findByClass(node: TestNode, className: string): TestNode[] {
  return [
    ...(node.getAttribute("class")?.split(/\s+/).includes(className) ? [node] : []),
    ...node.childNodes.flatMap((child) => findByClass(child, className)),
  ];
}

function findButtons(node: TestNode): TestNode[] {
  return [
    ...(node.nodeName === "BUTTON" ? [node] : []),
    ...node.childNodes.flatMap((child) => findButtons(child)),
  ];
}

function renderedAssetNames() {
  return findByClass(container, "asset-card").map((node) => node.textContent);
}

function buttonByText(text: string): TestNode {
  const match = findButtons(container).find((node) => node.textContent === text);
  if (!match) throw new Error(`No button with text "${text}"`);
  return match;
}

async function click(node: TestNode) {
  await act(async () => {
    node.dispatchEvent({ type: "click", bubbles: true });
    await drain();
  });
}

beforeEach(() => {
  document = new TestDocument();
  container = document.createElement("div");
  document.body.appendChild(container);
  vi.stubGlobal("document", document);
  timers = new Map();
  nextTimerId = 1;
  const window = {
    document,
    HTMLElement: TestNode,
    HTMLIFrameElement: TestNode,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    setTimeout: (callback: () => void) => { const id = nextTimerId++; timers.set(id, callback); return id; },
    clearTimeout: (id: number) => { timers.delete(id); },
  };
  document.defaultView = window;
  vi.stubGlobal("window", window);
  vi.stubGlobal("navigator", { userAgent: "test" });
  vi.stubGlobal("IS_REACT_ACT_ENVIRONMENT", true);
  assetRequests = [];
  harness.auth.workspace = null;
  harness.api.listAssets.mockReset().mockImplementation(() => {
    const request = deferred<AssetList>();
    assetRequests.push(request);
    return request.promise;
  });
  harness.api.listProjects.mockReset().mockResolvedValue([]);
  harness.api.getAsset.mockReset().mockResolvedValue(asset("unused", "unused", "unused"));
  harness.api.updateAsset.mockReset().mockResolvedValue(asset("unused", "unused", "unused"));
  harness.api.archiveAsset.mockReset().mockResolvedValue(asset("unused", "unused", "unused"));
  harness.api.restoreAsset.mockReset().mockResolvedValue(asset("unused", "unused", "unused"));
});

afterEach(() => {
  act(() => root?.unmount());
  vi.unstubAllGlobals();
});

describe("AssetsView workspace-scoped listing", () => {
  it("renders the mounted workspace asset list and clears the loading state", async () => {
    await mountAssetsView(workspaceA);
    expect(findByClass(container, "assets-loading")).toHaveLength(1);

    await flushLoad();
    expect(harness.api.listAssets.mock.calls[0]?.[0]).toBe("workspace-a");
    await settleRequest(0, assetList([asset("asset-a1", "Campaign visual A", "workspace-a")]));

    expect(renderedAssetNames()).toEqual(["Campaign visual A"]);
    expect(findByClass(container, "assets-loading")).toHaveLength(0);
    expect(findByClass(container, "inline-error")).toHaveLength(0);
  });

  it("drops a late list reply from the previous workspace after a workspace switch", async () => {
    await mountAssetsView(workspaceA);
    await flushLoad();
    expect(assetRequests).toHaveLength(1);

    await switchWorkspace(workspaceB);
    await flushLoad();
    expect(assetRequests).toHaveLength(2);
    expect(harness.api.listAssets.mock.calls[1]?.[0]).toBe("workspace-b");
    await settleRequest(1, assetList([asset("asset-b1", "Storyboard frame B", "workspace-b")]));
    expect(renderedAssetNames()).toEqual(["Storyboard frame B"]);

    // The workspace A request now wins the race in wall-clock time. A member of
    // B must never see A's listing rendered under B.
    await settleRequest(0, assetList([
      asset("asset-a1", "Campaign visual A", "workspace-a"),
      asset("asset-a2", "Private research A", "workspace-a"),
    ]));

    expect(renderedAssetNames()).toEqual(["Storyboard frame B"]);
    expect(container.textContent).not.toContain("Campaign visual A");
    expect(container.textContent).not.toContain("Private research A");
  });

  it("drops a delayed failure from the previous workspace after a workspace switch", async () => {
    await mountAssetsView(workspaceA);
    await flushLoad();

    await switchWorkspace(workspaceB);
    await flushLoad();
    await settleRequest(1, assetList([asset("asset-b1", "Storyboard frame B", "workspace-b")]));
    expect(findByClass(container, "inline-error")).toHaveLength(0);

    // The abandoned workspace A request fails after B has loaded (for example a
    // 500 on the previous workspace's listing); its error must not surface as a
    // phantom failure on workspace B's healthy list.
    await failRequest(0, new Error("Asset list failed with status 500."));

    expect(findByClass(container, "inline-error")).toHaveLength(0);
    expect(renderedAssetNames()).toEqual(["Storyboard frame B"]);
  });

  it("keeps the newest page when an earlier page request resolves out of order", async () => {
    const firstPage = assetList(
      [asset("asset-a1", "Alpha A1", "workspace-a"), asset("asset-a2", "Alpha A2", "workspace-a")],
      { page: 1, totalCount: 3, totalPages: 2 },
    );
    await mountAssetsView(workspaceA);
    await flushLoad();
    await settleRequest(0, firstPage);
    expect(renderedAssetNames()).toEqual(["Alpha A1", "Alpha A2"]);

    // Navigate to page 2, then immediately back to page 1 before the page 2
    // response arrives. If page 2 lands last it must not displace page 1, or
    // the library re-renders a page the user already left and re-clicking Next
    // shows the same items twice.
    await click(buttonByText("assets.next"));
    await flushLoad();
    expect(assetRequests).toHaveLength(2);
    expect(harness.api.listAssets.mock.calls[1]?.[1]).toMatchObject({ page: 2 });
    await click(buttonByText("assets.previous"));
    await flushLoad();
    expect(assetRequests).toHaveLength(3);
    expect(harness.api.listAssets.mock.calls[2]?.[1]).toMatchObject({ page: 1 });

    await settleRequest(2, firstPage);
    await settleRequest(1, assetList([asset("asset-a3", "Beta A3", "workspace-a")], { page: 2, totalCount: 3, totalPages: 2 }));

    expect(renderedAssetNames()).toEqual(["Alpha A1", "Alpha A2"]);
    expect(container.textContent).not.toContain("Beta A3");
  });
});
