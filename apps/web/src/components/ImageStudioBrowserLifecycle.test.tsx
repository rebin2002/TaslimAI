import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { GenerationJob } from "@/lib/api";

// ---- synthetic DOM harness, in the style of ProjectContextEditor/AssetsViewListing ----
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
  auth: { user: null as { id: string } | null, workspace: null as { id: string } | null },
  api: {
    listProjects: vi.fn(), listAssets: vi.fn(), getGenerationJob: vi.fn(),
    createImageGenerationJob: vi.fn(), cancelGenerationJob: vi.fn(),
    assetFileUrl: vi.fn(() => "https://files.example/asset"),
  },
}));

vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => ({ user: harness.auth.user, workspace: harness.auth.workspace, loading: false }),
}));
const translate = (key: string) => key;
const localeValue = { locale: "en", t: translate };
vi.mock("@/components/LocaleProvider", () => ({ useLocale: () => localeValue }));
vi.mock("next/navigation", () => ({ useSearchParams: () => new URLSearchParams() }));
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
vi.mock("@/components/AssetDetail", () => ({ AssetDetail: () => <div className="asset-detail" /> }));
vi.mock("lucide-react", () => {
  const Icon = (p: Record<string, unknown>) => <span {...p} />;
  return { ArrowUpRight: Icon, Check: Icon, CheckCircle2: Icon, ChevronDown: Icon, Download: Icon,
    Image: Icon, LoaderCircle: Icon, Paperclip: Icon, Palette: Icon, RefreshCw: Icon, Sparkles: Icon, XCircle: Icon };
});

import {
  clearImageActiveJobId,
  persistImageActiveJobId,
  readImageActiveJobId,
  subscribeImageActiveJobChanges,
} from "@/lib/imageStudioState";
import { ImageStudioView } from "./ImageStudioView";

const user1 = { id: "user-1" };
const wsA = { id: "workspace-a" };
const wsB = { id: "workspace-b" };

type Deferred<T> = { promise: Promise<T>; resolve: (v: T) => void; reject: (e: unknown) => void };
function deferred<T>(): Deferred<T> {
  let resolve!: (v: T) => void; let reject!: (e: unknown) => void;
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}
function job(overrides: Partial<GenerationJob> = {}): GenerationJob {
  return {
    id: "job-a-1", workspaceId: wsA.id, projectId: null, jobType: "image.generate", status: "Running",
    title: "Synthetic", progressPercent: 10, resultJson: null, errorCode: null, errorMessage: null,
    cancellationRequested: false, createdAt: "2026-11-16T00:00:00Z", queuedAt: "2026-11-16T00:00:00Z",
    startedAt: "2026-11-16T00:00:01Z", completedAt: null, failedAt: null, cancelledAt: null, outputs: [],
    ...overrides,
  } as GenerationJob;
}

const storage = new Map<string, string>();
let timers: Map<number, () => void>;
let nextTimerId = 1;
let doc: TestDocument;
let container: TestNode;
let root: Root;

async function drain() { await new Promise((r) => setTimeout(r, 0)); }
function flushTimers() { const pending = [...timers.values()]; timers.clear(); for (const cb of pending) cb(); }
async function runTimers() { await act(async () => { flushTimers(); await drain(); }); }

function allNodes(node: TestNode): TestNode[] { return [node, ...node.childNodes.flatMap(allNodes)]; }
function findByClass(node: TestNode, cls: string) { return allNodes(node).filter((n) => (n.getAttribute("class")?.split(/\s+/) ?? []).includes(cls)); }
function findByTag(node: TestNode, tag: string) { return allNodes(node).filter((n) => n.nodeName === tag.toUpperCase()); }
function pointerKey(userId: string, workspaceId: string) {
  return `taslim:image-generation:${encodeURIComponent(userId)}:${encodeURIComponent(workspaceId)}`;
}

async function mount() {
  harness.auth.user = user1;
  harness.auth.workspace = wsA;
  await act(async () => { root = createRoot(container as unknown as Element); root.render(<ImageStudioView />); await drain(); });
}
async function render() { await act(async () => { root.render(<ImageStudioView />); await drain(); }); }
async function switchWorkspace(ws: { id: string }) { harness.auth.workspace = ws; await render(); }
async function settle(fn: () => void) { await act(async () => { fn(); await drain(); }); }
/** React keeps its own value tracker on the node, so write through the prototype setter
 *  and then emit the event React listens to for controlled text inputs. */
function progressPercent() {
  return findByClass(container, "image-progress-track")[0]?.getAttribute("aria-valuenow") ?? null;
}

async function typePrompt(text: string) {
  const ta = findByTag(container, "TEXTAREA")[0];
  if (!ta) throw new Error("no textarea rendered");
  const descriptor = Object.getOwnPropertyDescriptor(TestNode.prototype, "value");
  await settle(() => { ta.dispatchEvent({ type: "focusin", bubbles: true }); });
  await settle(() => {
    descriptor!.set!.call(ta, text);
    ta.dispatchEvent({ type: "keyup", bubbles: true });
  });
}
async function submitForm() {
  const form = findByTag(container, "FORM")[0];
  if (!form) throw new Error("no form rendered");
  await settle(() => { form.dispatchEvent({ type: "submit", bubbles: true, cancelable: true }); });
}
function canvasCount() { return findByClass(container, "image-generating-canvas").length; }
function emptyCount() { return findByClass(container, "image-empty-canvas").length; }
function workingSpinner() { return findByClass(container, "image-button-spinner").length; }

beforeEach(() => {
  doc = new TestDocument();
  container = doc.createElement("div");
  doc.body.appendChild(container);
  timers = new Map();
  nextTimerId = 1;
  storage.clear();
  const windowStub = {
    document: doc, HTMLElement: TestNode, HTMLIFrameElement: TestNode, Element: TestNode, Node: TestNode,
    addEventListener: vi.fn(), removeEventListener: vi.fn(),
    setTimeout: (cb: () => void) => { const id = nextTimerId++; timers.set(id, cb); return id; },
    clearTimeout: (id: number) => { timers.delete(id); },
    sessionStorage: {
      getItem: (k: string) => storage.get(k) ?? null,
      setItem: (k: string, v: string) => { storage.set(k, v); },
      removeItem: (k: string) => { storage.delete(k); },
    },
  };
  doc.defaultView = windowStub;
  vi.stubGlobal("document", doc);
  vi.stubGlobal("window", windowStub);
  vi.stubGlobal("navigator", { userAgent: "test" });
  vi.stubGlobal("IS_REACT_ACT_ENVIRONMENT", true);
  harness.api.listProjects.mockReset().mockResolvedValue([]);
  harness.api.listAssets.mockReset().mockResolvedValue({ items: [], page: 1, pageSize: 6, totalCount: 0, totalPages: 1 });
  harness.api.getGenerationJob.mockReset();
  harness.api.createImageGenerationJob.mockReset();
  harness.api.cancelGenerationJob.mockReset().mockResolvedValue({ status: "Running" });
  harness.auth.user = null;
  harness.auth.workspace = null;
});
afterEach(() => { act(() => root?.unmount()); vi.unstubAllGlobals(); });

describe("Image Studio synthetic browser lifecycle", () => {
  // A submission and its job pointer belong to the browser session, not to one render of one
  // component. An identity change (a workspace or account switch, or leaving the studio and coming
  // back) replaces the whole instance while the POST is still in flight, and the request then
  // settles from a closure whose component is gone. The generation is queued and chargeable at that
  // moment, so the studio that is mounted for that same identity has to notice.
  it("adopts a generation whose submit settled after an identity remount replaced it", async () => {
    const post = deferred<GenerationJob>();
    harness.api.createImageGenerationJob.mockReturnValue(post.promise);
    harness.api.getGenerationJob
      .mockResolvedValueOnce(job({ id: "job-a-9", progressPercent: 45 }))
      .mockResolvedValue(job({ id: "job-a-9", progressPercent: 62 }));
    await mount();
    await typePrompt("a lighthouse at dawn, long exposure");
    await submitForm();
    expect(harness.api.createImageGenerationJob).toHaveBeenCalledTimes(1);

    // Leave the workspace and come back before the POST is answered.
    await switchWorkspace(wsB);
    await switchWorkspace(wsA);
    expect(emptyCount()).toBe(1);
    await settle(() => post.resolve(job({ id: "job-a-9", progressPercent: 40 })));

    // The job is persisted for the workspace that created it, and the studio now showing that
    // workspace adopts it instead of announcing nothing about work it is paying for.
    expect(storage.get(pointerKey("user-1", "workspace-a"))).toBe("job-a-9");
    expect(canvasCount()).toBe(1);
    expect(emptyCount()).toBe(0);
    expect(harness.api.getGenerationJob).toHaveBeenCalledTimes(1);
    expect(progressPercent()).toBe("45");

    // Adoption also restarts the progress loop, so the canvas is not merely a snapshot.
    await runTimers();
    expect(harness.api.getGenerationJob).toHaveBeenCalledTimes(2);
    expect(progressPercent()).toBe("62");
  });

  // Adopting late must not turn the announcement into a cross-workspace leak: the pointer names a
  // job owned by one workspace, and only a studio mounted on that workspace may read it.
  it("keeps a stranded submission invisible to the workspace that did not create it", async () => {
    const post = deferred<GenerationJob>();
    harness.api.createImageGenerationJob.mockReturnValue(post.promise);
    harness.api.getGenerationJob.mockResolvedValue(job({ id: "job-a-7" }));
    await mount();
    await typePrompt("a ceramic coffee cup on a linen cloth");
    await submitForm();

    await switchWorkspace(wsB);
    await settle(() => post.resolve(job({ id: "job-a-7" })));

    expect(storage.get(pointerKey("user-1", "workspace-a"))).toBe("job-a-7");
    expect(storage.has(pointerKey("user-1", "workspace-b"))).toBe(false);
    // The new workspace shows neither the previous workspace's job nor a busy state for a
    // submission it never made.
    expect(canvasCount()).toBe(0);
    expect(emptyCount()).toBe(1);
    expect(workingSpinner()).toBe(0);
    // ...and it never spends an authenticated read on the other workspace's job id.
    expect(harness.api.getGenerationJob).not.toHaveBeenCalled();
  });

  // The same rule covers a progress read that is still in flight when the identity changes: the
  // frame belongs to the workspace that asked for it and must not be adopted, kept, or retried by
  // the workspace that replaced it.
  it("discards a progress frame that settles after the workspace changed", async () => {
    storage.set(pointerKey("user-1", "workspace-a"), "job-a-1");
    const frame = deferred<GenerationJob>();
    harness.api.getGenerationJob.mockReturnValue(frame.promise);
    await mount();
    await runTimers();
    expect(harness.api.getGenerationJob).toHaveBeenCalledTimes(1);

    await switchWorkspace(wsB);
    await settle(() => frame.resolve(job({ progressPercent: 90 })));

    expect(canvasCount()).toBe(0);
    expect(emptyCount()).toBe(1);
    // The abandoned pointer still belongs to workspace A and is not consumed by anything in B.
    expect(storage.get(pointerKey("user-1", "workspace-a"))).toBe("job-a-1");
    expect(storage.has(pointerKey("user-1", "workspace-b"))).toBe(false);
    // A dropped frame must not leave a timer behind that keeps reading the old job.
    await runTimers();
    expect(harness.api.getGenerationJob).toHaveBeenCalledTimes(1);
  });

  // The fast path must stay free of redundant private reads: when the mounted studio is the one
  // that submitted, its own response is already the state, so the pointer announcement may not
  // start a second adoption pass for the job it just adopted.
  it("does not re-read a generation that the mounted studio submitted itself", async () => {
    const post = deferred<GenerationJob>();
    harness.api.createImageGenerationJob.mockReturnValue(post.promise);
    harness.api.getGenerationJob.mockResolvedValue(job({ id: "job-a-1", progressPercent: 30 }));
    await mount();
    await typePrompt("a moss-covered stone stair in the rain");
    await submitForm();
    await settle(() => post.resolve(job({ id: "job-a-1", progressPercent: 15 })));

    // The response the studio is holding already describes this job, so announcing its pointer
    // must not spend an authenticated read on it.
    expect(canvasCount()).toBe(1);
    expect(progressPercent()).toBe("15");
    expect(storage.get(pointerKey("user-1", "workspace-a"))).toBe("job-a-1");
    expect(harness.api.getGenerationJob).not.toHaveBeenCalled();

    // Only the ordinary progress loop reads the job from here.
    await runTimers();
    expect(harness.api.getGenerationJob).toHaveBeenCalledTimes(1);
    expect(progressPercent()).toBe("30");
  });

  // The announcement channel is the narrow contract the component relies on: it fires for the
  // identity that owns the pointer, on both write and clear, and stops when unsubscribed.
  it("announces pointer changes only to live subscribers", () => {
    const seen: string[] = [];
    const unsubscribe = subscribeImageActiveJobChanges((userId, workspaceId) => {
      seen.push(`${userId}/${workspaceId}`);
    });
    persistImageActiveJobId("user-1", "workspace-a", "job-a-1");
    expect(readImageActiveJobId("user-1", "workspace-a")).toBe("job-a-1");
    clearImageActiveJobId("user-1", "workspace-a");
    expect(readImageActiveJobId("user-1", "workspace-a")).toBeNull();
    unsubscribe();
    persistImageActiveJobId("user-1", "workspace-a", "job-a-2");
    expect(seen).toEqual(["user-1/workspace-a", "user-1/workspace-a"]);
  });
});
