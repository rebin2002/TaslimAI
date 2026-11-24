import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { GenerationJob } from "@/lib/api";

// ---- synthetic DOM harness, in the style of ImageStudioBrowserLifecycle/ProjectContextEditor ----
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
    id: "job-a-1", workspaceId: wsA.id, projectId: null, jobType: "social.generate", status: "Running",
    title: "Synthetic launch brief", progressPercent: 10, resultJson: null, errorCode: null, errorMessage: null,
    cancellationRequested: false, createdAt: "2026-11-17T00:00:00Z", queuedAt: "2026-11-17T00:00:00Z",
    startedAt: "2026-11-17T00:00:01Z", completedAt: null, failedAt: null, cancelledAt: null, outputs: [],
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
async function settle(fn: () => void) { await act(async () => { fn(); await drain(); }); }

function allNodes(node: TestNode): TestNode[] { return [node, ...node.childNodes.flatMap(allNodes)]; }
function findByClass(node: TestNode, cls: string) { return allNodes(node).filter((n) => (n.getAttribute("class")?.split(/\s+/) ?? []).includes(cls)); }
function findByTag(node: TestNode, tag: string) { return allNodes(node).filter((n) => n.nodeName === tag.toUpperCase()); }
/** The pointer a Social Studio session keeps for the job it is watching. */
function pointerKey(workspaceId: string) { return `taslim:social-generation:${workspaceId}`; }
function progressPercent() {
  return findByClass(container, "generation-progress-track")[0]?.getAttribute("aria-valuenow") ?? null;
}
function generateButtonLabel() {
  const button = findByClass(container, "social-generate-button")[0];
  return button ? button.textContent : null;
}
function clickCancelButton() {
  const button = findByClass(container, "generation-cancel-button")[0];
  if (!button) throw new Error("no cancel button rendered");
  return settle(() => { button.dispatchEvent({ type: "click", bubbles: true }); });
}
async function typePrompt(text: string) {
  const ta = findByTag(container, "TEXTAREA")[0];
  if (!ta) throw new Error("no textarea rendered");
  const descriptor = Object.getOwnPropertyDescriptor(TestNode.prototype, "value");
  await settle(() => { ta.dispatchEvent({ type: "focusin", bubbles: true }); });
  await settle(() => {
    descriptor!.set!.call(ta, text);
    ta.dispatchEvent({ type: "input", bubbles: true });
    ta.dispatchEvent({ type: "keyup", bubbles: true });
  });
}
async function submitForm() {
  const form = findByTag(container, "FORM")[0];
  if (!form) throw new Error("no form rendered");
  await settle(() => { form.dispatchEvent({ type: "submit", bubbles: true, cancelable: true }); });
}

async function mount() {
  harness.auth.workspace = wsA;
  harness.auth.user = { id: "user-a" };
  await act(async () => { root = createRoot(container as unknown as Element); root.render(<SocialStudioView />); await drain(); });
}
async function render() { await act(async () => { root.render(<SocialStudioView />); await drain(); }); }
async function switchWorkspace(ws: { id: string }) { harness.auth.workspace = ws; await render(); }

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
  harness.api.listFiles.mockReset().mockResolvedValue([]);
  harness.api.listAssets.mockReset().mockResolvedValue({ items: [], page: 1, pageSize: 100, totalCount: 0, totalPages: 1 });
  harness.api.getGenerationJob.mockReset();
  harness.api.createSocialGenerationJob.mockReset();
  harness.api.cancelGenerationJob.mockReset().mockResolvedValue({ status: "Running" });
  harness.auth.workspace = null;
  harness.auth.user = null;
});
afterEach(() => { act(() => root?.unmount()); vi.unstubAllGlobals(); });

describe("Social Studio cancellation session semantics", () => {
  it("keeps a cancellation that settles after a workspace switch from releasing the new session's work lock", async () => {
    // Session A is watching a running Social generation and asks for it to stop.
    storage.set(pointerKey(wsA.id), "job-a-1");
    harness.api.getGenerationJob.mockResolvedValue(job({ progressPercent: 10 }));
    await mount();
    await runTimers();
    expect(progressPercent()).toBe("10");

    const cancelPost = deferred<{ status: string }>();
    harness.api.cancelGenerationJob.mockReturnValue(cancelPost.promise);
    await clickCancelButton();
    expect(harness.api.cancelGenerationJob).toHaveBeenCalledTimes(1);

    // The account changes before the cancellation is answered. The new session submits its own
    // generation, so its create POST is what the busy lock now protects.
    await switchWorkspace(wsB);
    const createPost = deferred<GenerationJob>();
    harness.api.createSocialGenerationJob.mockReturnValue(createPost.promise);
    await typePrompt("spring launch brief for the workspace pilot");
    await submitForm();
    expect(harness.api.createSocialGenerationJob).toHaveBeenCalledTimes(1);
    expect(generateButtonLabel()).toContain("social.working");

    // The superseded cancellation settles. It must not be able to unlock the form the new session
    // is still submitting: releasing the lock re-enables the generate control while a create POST is
    // in flight, which invites a duplicate, separately billable generation.
    await settle(() => cancelPost.resolve({ status: "Running" }));
    expect(generateButtonLabel()).toContain("social.working");

    // The new session's own submission still completes normally and reclaims the lock itself.
    await settle(() => createPost.resolve(job({ id: "job-b-1", workspaceId: wsB.id, progressPercent: 5 })));
    expect(progressPercent()).toBe("5");
  });

  it("discards a cancellation response that settles after the session was replaced and restored", async () => {
    // Session A asks to stop job-a-1 while it is at 10%.
    storage.set(pointerKey(wsA.id), "job-a-1");
    let frame = 0;
    const restoreA = job({ progressPercent: 10 });
    const authoritative = job({ progressPercent: 55 });
    const stale = job({ progressPercent: 10 });
    harness.api.getGenerationJob.mockImplementation(async () => {
      const next = [restoreA, authoritative, stale][Math.min(frame, 2)];
      frame += 1;
      return next;
    });
    await mount();
    await runTimers();
    expect(progressPercent()).toBe("10");

    const cancelPost = deferred<{ status: string }>();
    harness.api.cancelGenerationJob.mockReturnValue(cancelPost.promise);
    await clickCancelButton();

    // Leaving and returning reuses the same workspace id, so an id comparison alone cannot tell the
    // superseded session from the restored one. The restored session reads the newer frame first.
    await switchWorkspace(wsB);
    await switchWorkspace(wsA);
    await runTimers();
    expect(progressPercent()).toBe("55");

    // The cancellation from the replaced session now settles with its older frame. Installing it
    // would rewind the job the restored session is showing and hide the authoritative status.
    await settle(() => cancelPost.resolve({ status: "Running" }));
    expect(progressPercent()).toBe("55");
  });
  it("keeps a cancellation from an old account from unlocking a new account in the same workspace", async () => {
    storage.set(pointerKey(wsA.id), "job-a-1");
    harness.api.getGenerationJob.mockResolvedValue(job({ progressPercent: 10 }));
    await mount();
    await runTimers();
    const oldCancellation = deferred<{ status: string }>();
    harness.api.cancelGenerationJob.mockReturnValue(oldCancellation.promise);
    await clickCancelButton();
    expect(harness.api.cancelGenerationJob).toHaveBeenCalledTimes(1);

    // Account B has access to the same workspace id. Workspace-only fencing must not
    // mistake this principal for the account that started the cancellation.
    harness.auth.user = { id: "user-b" };
    await render();
    const newGeneration = deferred<GenerationJob>();
    harness.api.createSocialGenerationJob.mockReturnValue(newGeneration.promise);
    await typePrompt("shared team campaign created by the second account");
    await submitForm();
    expect(harness.api.createSocialGenerationJob).toHaveBeenCalledTimes(1);
    expect(generateButtonLabel()).toContain("social.working");

    await settle(() => oldCancellation.resolve({ status: "Running" }));
    expect(generateButtonLabel()).toContain("social.working");
    await settle(() => newGeneration.resolve(job({ id: "job-b-2", workspaceId: wsA.id, progressPercent: 5 })));
    expect(progressPercent()).toBe("5");
  });

});
