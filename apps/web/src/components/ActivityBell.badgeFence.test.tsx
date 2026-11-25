import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Synthetic DOM harness, in the style established by AssetsViewListing.test.tsx
// (#849 lineage) and ProjectContextEditor.test.tsx. Only the header badge hook is
// mounted: it reads the live unread count, subscribes to same-workspace read events,
// and polls. These cases own the fence between an in-flight, workspace-scoped
// unread-count poll and a newer locally published count. Every value below is a
// synthetic fixture; no real account, workspace, or provider data is used.
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
  get tagName() { return this.nodeName; }
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
  focus() { /* the badge probe is not focusable */ }
  blur() { /* the badge probe is not focusable */ }
}
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
}

type TestEventListener = (event: { type: string; detail?: unknown }) => void;
type UnreadResponse = { unreadCount: number };
type Deferred<T> = { promise: Promise<T>; resolve: (value: T) => void; reject: (error: unknown) => void };

class TestCustomEvent {
  type: string;
  detail: unknown;
  defaultPrevented = false;
  constructor(type: string, init: { detail?: unknown } = {}) { this.type = type; this.detail = init.detail; }
  preventDefault() { this.defaultPrevented = true; }
  stopPropagation() { /* window-scoped events do not bubble in this harness */ }
}

function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}

const harness = vi.hoisted(() => ({
  auth: { workspace: null as { id: string; name: string } | null },
  api: {
    getNotificationUnreadCount: vi.fn(),
    listNotifications: vi.fn(),
    markNotificationRead: vi.fn(),
    markAllNotificationsRead: vi.fn(),
  },
}));

vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => ({ workspace: harness.auth.workspace }),
}));
vi.mock("@/lib/api", () => ({ api: harness.api }));

import { publishNotificationUnreadCount, useNotificationUnreadCount } from "./ActivityBell";

function BadgeProbe() {
  const count = useNotificationUnreadCount();
  return <span className="badge-probe">{count}</span>;
}

const workspaceA = { id: "workspace-a", name: "Workspace A" };
const workspaceB = { id: "workspace-b", name: "Workspace B" };

let document: TestDocument;
let root: Root;
let container: TestNode;
let windowListeners: Map<string, Set<TestEventListener>>;
let intervals: Map<number, () => void>;
let nextTimerId = 1;
let unreadRequests: Deferred<UnreadResponse>[];

function drain() { return new Promise((resolve) => setTimeout(resolve, 0)); }
function mountProbe(workspace: typeof workspaceA) {
  harness.auth.workspace = workspace;
  return act(async () => {
    root = createRoot(container as unknown as Element);
    root.render(<BadgeProbe />);
    await drain();
  });
}
function switchWorkspace(workspace: typeof workspaceA) {
  harness.auth.workspace = workspace;
  return act(async () => {
    root.render(<BadgeProbe />);
    await drain();
  });
}
function settleRequest(index: number, value: UnreadResponse) {
  return act(async () => { unreadRequests[index].resolve(value); await drain(); });
}
function publishCount(workspaceId: string, unreadCount: number) {
  return act(async () => {
    publishNotificationUnreadCount(workspaceId, unreadCount);
    await drain();
  });
}
function failRequest(index: number, error: unknown) {
  return act(async () => { unreadRequests[index].reject(error); await drain(); });
}
function runInterval() {
  return act(async () => {
    for (const tick of [...intervals.values()]) tick();
    await drain();
  });
}
function badgeCount() {
  const visit = (node: TestNode): TestNode | null =>
    (node.getAttribute("class")?.split(/\s+/).includes("badge-probe") ? node : null)
    ?? node.childNodes.map(visit).find(Boolean) ?? null;
  const probe = visit(container);
  if (!probe) throw new Error("badge probe not rendered");
  return probe.textContent;
}

beforeEach(() => {
  document = new TestDocument();
  container = document.createElement("div");
  document.body.appendChild(container);
  windowListeners = new Map();
  intervals = new Map();
  nextTimerId = 1;
  const window = {
    document,
    Element: TestNode,
    HTMLElement: TestNode,
    HTMLIFrameElement: TestNode,
    Node: TestNode,
    addEventListener: (type: string, listener: TestEventListener) => {
      const listeners = windowListeners.get(type) ?? new Set<TestEventListener>();
      listeners.add(listener);
      windowListeners.set(type, listeners);
    },
    removeEventListener: (type: string, listener: TestEventListener) => { windowListeners.get(type)?.delete(listener); },
    dispatchEvent: (event: { type: string }) => {
      for (const listener of [...(windowListeners.get(event.type) ?? [])]) listener(event);
      return true;
    },
    setInterval: (callback: () => void) => { const id = nextTimerId++; intervals.set(id, callback); return id; },
    clearInterval: (id: number) => { intervals.delete(id); },
    setTimeout: (callback: () => void) => { const id = nextTimerId++; intervals.set(id, callback); return id; },
    clearTimeout: (id: number) => { intervals.delete(id); },
  };
  document.defaultView = window;
  vi.stubGlobal("document", document);
  vi.stubGlobal("window", window);
  vi.stubGlobal("CustomEvent", TestCustomEvent);
  vi.stubGlobal("navigator", { userAgent: "test" });
  vi.stubGlobal("IS_REACT_ACT_ENVIRONMENT", true);
  unreadRequests = [];
  harness.auth.workspace = null;
  harness.api.getNotificationUnreadCount.mockReset().mockImplementation(() => {
    const request = deferred<UnreadResponse>();
    unreadRequests.push(request);
    return request.promise;
  });
  harness.api.listNotifications.mockReset().mockResolvedValue({ items: [], page: 1, pageSize: 6, totalCount: 0, totalPages: 0, unreadCount: 0 });
  harness.api.markNotificationRead.mockReset().mockResolvedValue(undefined);
  harness.api.markAllNotificationsRead.mockReset().mockResolvedValue(undefined);
});

afterEach(() => {
  act(() => root?.unmount());
  vi.unstubAllGlobals();
});

describe("header badge count freshness", () => {
  it("does not let an in-flight unread poll repaint the badge over a newer local read", async () => {
    await mountProbe(workspaceA);
    // The mount poll is still in flight and the snapshot behind it was taken while
    // all five notifications were unread.
    expect(unreadRequests).toHaveLength(1);
    // Marking one notification read publishes the newer, locally derived count.
    await publishCount(workspaceA.id, 4);
    expect(badgeCount()).toBe("4");
    // The pre-read snapshot lands late. It is older than the published read, so it
    // must not resurrect the five-unread badge.
    await settleRequest(0, { unreadCount: 5 });
    expect(badgeCount()).toBe("4");
  });

  it("applies a poll issued after the local read", async () => {
    await mountProbe(workspaceA);
    await publishCount(workspaceA.id, 4);
    await settleRequest(0, { unreadCount: 5 });
    expect(badgeCount()).toBe("4");
    // The next scheduled poll sees the read, so its newer snapshot is adopted.
    await runInterval();
    expect(unreadRequests).toHaveLength(2);
    await settleRequest(1, { unreadCount: 3 });
    expect(badgeCount()).toBe("3");
  });

  it("ignores a late count published by another workspace", async () => {
    await mountProbe(workspaceA);
    await settleRequest(0, { unreadCount: 5 });
    expect(badgeCount()).toBe("5");
    await switchWorkspace(workspaceB);
    await publishCount(workspaceA.id, 0);
    expect(badgeCount()).toBe("5");
    expect(unreadRequests).toHaveLength(2);
    await settleRequest(1, { unreadCount: 2 });
    expect(badgeCount()).toBe("2");
  });

  it("keeps the newest local count across a transient poll failure", async () => {
    await mountProbe(workspaceA);
    await publishCount(workspaceA.id, 4);
    await failRequest(0, new Error("network"));
    expect(badgeCount()).toBe("4");
  });
});
