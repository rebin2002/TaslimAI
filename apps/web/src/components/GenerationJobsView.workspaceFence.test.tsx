import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { GenerationJob } from "../lib/api";

// Synthetic DOM harness, in the style established by AssetsViewListing.test.tsx and
// ActivityBell.badgeFence.test.tsx: no jsdom, no network, no provider. The real
// GenerationJobsView is mounted so the assertions describe what a user can actually see
// at /account/generation-jobs, which the state-module tests in
// generationJobsState.test.ts cannot cover: those test the pure functions, while these
// own the render-time fence in the view itself.
//
// The defect: the session's personal workspace can be reassigned while this page stays
// mounted (AuthProvider.refresh() publishes a different workspace id). The view keeps the
// rows it loaded for the previous workspace in state, and each row carries a title, a job
// id and a resultJson payload. Rendering those rows, or the detail panel and cancel
// control built from them, discloses the retired workspace's records on a page the user
// now reads as another workspace.
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
  // React delegates its listeners to the root container, so a dispatched event has to
  // travel up the ancestor chain the same way a browser event would.
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
  focus() { /* the history list does not move focus in these cases */ }
  blur() { /* the history list does not move focus in these cases */ }
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
type TestEvent = {
  type: string;
  bubbles?: boolean;
  target?: TestNode;
  currentTarget?: TestNode;
  propagationStopped?: boolean;
  defaultPrevented?: boolean;
  preventDefault?: () => void;
  stopPropagation?: () => void;
};

const harness = vi.hoisted(() => ({
  auth: { workspace: null as { id: string } | null },
  api: {
    listGenerationJobs: vi.fn(),
    getGenerationJob: vi.fn(),
    createGenerationJob: vi.fn(),
    cancelGenerationJob: vi.fn(),
  },
  // A fresh `t` per useLocale() call would re-fire the workspace-keyed load effect on every
  // render, so the mount would never settle; the context identity has to be stable.
  localeContext: {
    t: (key: string, variables?: Record<string, string>) => Object.entries(variables ?? {}).reduce((value, [name, replacement]) => value.replace(`{${name}}`, replacement), key),
  },
}));

vi.mock("@/components/AuthProvider", () => ({ useAuth: () => ({ workspace: harness.auth.workspace }) }));
vi.mock("@/components/LocaleProvider", () => ({ useLocale: () => harness.localeContext }));
vi.mock("@/lib/api", () => ({ api: harness.api }));
vi.mock("next/link", () => ({ default: (p: { href: string; children?: unknown }) => <a href={p.href}>{p.children as never}</a> }));

import { GenerationJobsView } from "./GenerationJobsView";

const workspaceA = { id: "workspace-a" };
const workspaceB = { id: "workspace-b" };

type Deferred<T> = { promise: Promise<T>; resolve: (value: T) => void; reject: (error: unknown) => void };
function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}

// Synthetic fixtures only: no production record, no secret, no provider call, no charging path.
function makeJob(overrides: Partial<GenerationJob> = {}): GenerationJob {
  return {
    id: "job-a1", workspaceId: "workspace-a", projectId: null, jobType: "system.test", status: "Running",
    title: "Alpha workspace job", progressPercent: 30, resultJson: '{"note":"alpha-only"}',
    errorCode: null, errorMessage: null, cancellationRequested: false,
    createdAt: "2026-01-01T00:00:00Z", queuedAt: "2026-01-01T00:00:00Z", startedAt: null,
    completedAt: null, failedAt: null, cancelledAt: null, outputs: [], ...overrides,
  };
}

let document: TestDocument;
let root: Root;
let container: TestNode;
let timers: Map<number, () => void>;
let nextTimerId = 1;
let listRequests: Deferred<{ items: GenerationJob[]; page: number; pageSize: number; totalCount: number; totalPages: number }>[];
let detailRequests: Deferred<GenerationJob>[];

function drain() { return new Promise((resolve) => setTimeout(resolve, 0)); }

function findByClass(node: TestNode, className: string): TestNode[] {
  return [
    ...(node.getAttribute("class")?.split(/\s+/).includes(className) ? [node] : []),
    ...node.childNodes.flatMap((child) => findByClass(child, className)),
  ];
}

function findTag(node: TestNode, nodeName: string): TestNode[] {
  return [
    ...(node.nodeName === nodeName ? [node] : []),
    ...node.childNodes.flatMap((child) => findTag(child, nodeName)),
  ];
}

function renderedTitles() {
  // A row also renders its progress and status; the title is the strong element only.
  return findByClass(container, "generation-job-list-item").map((node) => findTag(node, "STRONG")[0]?.textContent ?? "");
}

function renderedText() { return container.textContent; }

function mountView(workspace: { id: string } | null) {
  harness.auth.workspace = workspace;
  return act(async () => {
    root = createRoot(container as unknown as Element);
    root.render(<GenerationJobsView />);
    await drain();
  });
}

function switchWorkspace(workspace: { id: string } | null) {
  harness.auth.workspace = workspace;
  return act(async () => {
    root.render(<GenerationJobsView />);
    await drain();
  });
}

function settleList(index: number, items: GenerationJob[]) {
  return act(async () => { listRequests[index].resolve({ items, page: 1, pageSize: 20, totalCount: items.length, totalPages: 1 }); await drain(); });
}

function failList(index: number, message: string) {
  return act(async () => { listRequests[index].reject(new Error(message)); await drain(); });
}

function settleDetail(index: number, job: GenerationJob) {
  return act(async () => { detailRequests[index].resolve(job); await drain(); });
}

function runTimers() {
  return act(async () => {
    for (const tick of [...timers.values()]) tick();
    await drain();
  });
}

async function selectRow(title: string) {
  const row = findByClass(container, "generation-job-list-item").find((node) => node.textContent?.includes(title) ?? false);
  if (!row) throw new Error(`No rendered history row containing "${title}"`);
  await act(async () => {
    row.dispatchEvent({ type: "click", bubbles: true });
    await drain();
  });
}

beforeEach(() => {
  document = new TestDocument();
  container = document.createElement("div");
  document.body.appendChild(container);
  timers = new Map();
  nextTimerId = 1;
  listRequests = [];
  detailRequests = [];
  const window = {
    document,
    Element: TestNode,
    HTMLElement: TestNode,
    HTMLIFrameElement: TestNode,
    Node: TestNode,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    setInterval: (callback: () => void) => { const id = nextTimerId++; timers.set(id, callback); return id; },
    clearInterval: (id: number) => { timers.delete(id); },
    setTimeout: (callback: () => void) => { const id = nextTimerId++; timers.set(id, callback); return id; },
    clearTimeout: (id: number) => { timers.delete(id); },
  };
  document.defaultView = window;
  vi.stubGlobal("document", document);
  vi.stubGlobal("window", window);
  vi.stubGlobal("navigator", { userAgent: "test" });
  vi.stubGlobal("IS_REACT_ACT_ENVIRONMENT", true);
  harness.auth.workspace = null;
  harness.api.listGenerationJobs.mockReset().mockImplementation(() => {
    const request = deferred<{ items: GenerationJob[]; page: number; pageSize: number; totalCount: number; totalPages: number }>();
    listRequests.push(request);
    return request.promise;
  });
  harness.api.getGenerationJob.mockReset().mockImplementation(() => {
    const request = deferred<GenerationJob>();
    detailRequests.push(request);
    return request.promise;
  });
  harness.api.createGenerationJob.mockReset().mockResolvedValue(makeJob());
  harness.api.cancelGenerationJob.mockReset().mockResolvedValue(undefined);
});

afterEach(() => {
  act(() => root?.unmount());
  vi.unstubAllGlobals();
});

describe("generation history workspace isolation in the rendered page", () => {
  it("never renders a row belonging to another workspace while the current one is mounted", async () => {
    await mountView(workspaceA);
    // A list response that carries a foreign row - the server fence is the list's own guard,
    // the view must not render what does not belong to the workspace it is reading.
    await settleList(0, [makeJob(), makeJob({ id: "job-b1", workspaceId: "workspace-b", title: "Beta workspace job", resultJson: '{"note":"beta-only"}' })]);

    expect(renderedTitles()).toEqual(["Alpha workspace job"]);
    expect(renderedText()).not.toContain("Beta workspace job");
    expect(renderedText()).not.toContain("beta-only");
    expect(renderedText()).not.toContain("job-b1");
  });

  it("drops the previous workspace's rows at the switch render, before the new list has arrived", async () => {
    await mountView(workspaceA);
    await settleList(0, [makeJob()]);
    expect(renderedTitles()).toEqual(["Alpha workspace job"]);

    // Reassignment: the new workspace's list request is still in flight, so the only data the
    // view holds is the retired workspace's array. It must not paint for even one render.
    await switchWorkspace(workspaceB);
    expect(listRequests).toHaveLength(2);
    expect(renderedTitles()).toEqual([]);
    expect(renderedText()).not.toContain("Alpha workspace job");
    expect(renderedText()).not.toContain("alpha-only");
    // The panel falls back to its own loading placeholder, never to the retired rows.
    expect(findByClass(container, "generation-empty").map((node) => node.textContent)).toContain("jobs.loading");

    // The current workspace's own rows then render normally.
    await settleList(1, [makeJob({ id: "job-b1", workspaceId: "workspace-b", title: "Beta workspace job" })]);
    expect(renderedTitles()).toEqual(["Beta workspace job"]);
  });

  it("keeps another workspace's rows out of the list when the workspace being read fails to load", async () => {
    await mountView(workspaceA);
    await settleList(0, [makeJob({ id: "job-a1", workspaceId: "workspace-a", title: "Alpha workspace job" })]);
    await switchWorkspace(workspaceB);
    await settleList(1, [makeJob({ id: "job-b1", workspaceId: "workspace-b", title: "Beta workspace job", resultJson: '{"note":"beta-only"}' })]);
    expect(renderedTitles()).toEqual(["Beta workspace job"]);

    // Back to A, whose list request fails: the load has settled (no loading placeholder), so the
    // only array the view holds is workspace B's. That array must not render as A's history.
    await switchWorkspace(workspaceA);
    await failList(2, "The history could not be loaded.");
    expect(renderedTitles()).toEqual([]);
    expect(renderedText()).not.toContain("Beta workspace job");
    expect(renderedText()).not.toContain("beta-only");
    expect(findByClass(container, "generation-retry-button")).toHaveLength(1);
  });

  it("ignores a late list response from the workspace the user has left", async () => {
    await mountView(workspaceA);
    await switchWorkspace(workspaceB);
    // A's request was still open when the session moved on; its rows belong to a retired read.
    await settleList(0, [makeJob({ id: "job-a1", workspaceId: "workspace-a", title: "Alpha workspace job", resultJson: '{"note":"alpha-only"}' })]);
    expect(renderedTitles()).toEqual([]);
    expect(renderedText()).not.toContain("Alpha workspace job");

    await settleList(1, [makeJob({ id: "job-b1", workspaceId: "workspace-b", title: "Beta workspace job" })]);
    expect(renderedTitles()).toEqual(["Beta workspace job"]);
  });

  it("does not leave the detail panel or cancel control armed for the previous workspace's job", async () => {
    await mountView(workspaceA);
    await settleList(0, [makeJob()]);
    await selectRow("Alpha workspace job");
    expect(findByClass(container, "generation-job-detail")).toHaveLength(1);
    expect(findByClass(container, "generation-cancel-button")).toHaveLength(1);
    expect(renderedText()).toContain("alpha-only");

    await switchWorkspace(workspaceB);
    expect(findByClass(container, "generation-job-detail")).toHaveLength(0);
    expect(findByClass(container, "generation-cancel-button")).toHaveLength(0);
    expect(renderedText()).not.toContain("alpha-only");
    expect(renderedText()).not.toContain("job-a1");
  });

  it("ignores a poll response that describes a workspace other than the one being read", async () => {
    await mountView(workspaceA);
    await settleList(0, [makeJob()]);
    await selectRow("Alpha workspace job");

    await runTimers();
    expect(detailRequests).toHaveLength(1);
    // The record answers with a different workspace than the page is reading.
    await settleDetail(0, makeJob({ workspaceId: "workspace-b", progressPercent: 90, resultJson: '{"note":"moved-out"}' }));

    expect(renderedText()).not.toContain("moved-out");
    expect(findByClass(container, "generation-job-list-item")).toHaveLength(1);
    expect(renderedTitles()).toEqual(["Alpha workspace job"]);
  });

  it("does not let a superseded load of the same workspace rewind the rendered history", async () => {
    // Two loads of one workspace cannot be told apart by the workspace fence - the records belong
    // to the workspace being read - so the request generation alone decides which response wins.
    // Sequence: load A, move to B, come back to A, let the newest A load land, then deliver the
    // first, older A page. The older page predates the new job and must not erase it.
    const oldRow = makeJob({ id: "job-a-old", workspaceId: "workspace-a", title: "Alpha older job" });
    const newRow = makeJob({ id: "job-a-new", workspaceId: "workspace-a", title: "Alpha newest job" });
    await mountView(workspaceA);
    await switchWorkspace(workspaceB);
    await switchWorkspace(workspaceA);
    expect(listRequests).toHaveLength(3);

    await settleList(2, [newRow, oldRow]);
    expect(renderedTitles()).toEqual(["Alpha newest job", "Alpha older job"]);

    // The first page lands late. It is older than the settled load, so it must not rewind the list.
    await settleList(0, [oldRow]);
    expect(renderedTitles()).toEqual(["Alpha newest job", "Alpha older job"]);
  });

  it("renders no private history once the session has no workspace", async () => {
    await mountView(workspaceA);
    await settleList(0, [makeJob()]);
    expect(renderedTitles()).toEqual(["Alpha workspace job"]);

    await switchWorkspace(null);
    expect(renderedTitles()).toEqual([]);
    expect(renderedText()).not.toContain("Alpha workspace job");
    expect(renderedText()).not.toContain("alpha-only");
    expect(findByClass(container, "generation-empty").map((node) => node.textContent)).toContain("jobs.empty");
  });
});
