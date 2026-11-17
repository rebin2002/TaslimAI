import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { GenerationJob } from "@/lib/api";

// Synthetic DOM harness, in the style established by ImageStudioBrowserLifecycle and
// AssetsViewListing. Scope: the disabled/enabled state of the Document Studio draft-export
// actions (the DOCX/PDF representation buttons) and the busy flag they share with the compose
// form. Everything is provider-free: fabricated job payloads, stubbed api calls, and a
// hand-built DOM. No network, no storage identity, no real translation catalog, no user data.
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
  // The export success path builds an <a download> and clicks it; the stub records intent.
  click() { this.clicked = true; }
  clicked = false;
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
  workspace: null as { id: string } | null,
  api: {
    listProjects: vi.fn(),
    listFiles: vi.fn(),
    listAssets: vi.fn(),
    createDocumentGenerationJob: vi.fn(),
    getGenerationJob: vi.fn(),
    cancelGenerationJob: vi.fn(),
    downloadAssetRepresentation: vi.fn(),
  },
}));

vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => ({ workspace: harness.workspace }),
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
vi.mock("lucide-react", () => {
  const Icon = (p: Record<string, unknown>) => <span {...p} />;
  return { ArrowLeft: Icon, Check: Icon, CheckCircle2: Icon, Clipboard: Icon, Download: Icon,
    FileText: Icon, FolderOpen: Icon, LoaderCircle: Icon, RefreshCw: Icon, Sparkles: Icon,
    WandSparkles: Icon, XCircle: Icon };
});

import { DocumentStudioView } from "./DocumentStudioView";

const WS = { id: "workspace-1" };
const DOCX_CONTENT_TYPE = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
const PDF_CONTENT_TYPE = "application/pdf";
const ASSET_ID = "00000000-0000-4000-8000-000000000010";
const DOCX_ID = "00000000-0000-4000-8000-000000000011";
const PDF_ID = "00000000-0000-4000-8000-000000000012";
const JOB_ID = "00000000-0000-4000-8000-000000000001";
const pinnedInstant = "2026-11-16T12:00:00Z";

function succeededDocumentJob(): GenerationJob {
  return {
    id: JOB_ID, workspaceId: WS.id, projectId: null, jobType: "document.generate", status: "Succeeded",
    title: "Synthetic brief", progressPercent: 100, errorCode: null, errorMessage: null,
    cancellationRequested: false, createdAt: pinnedInstant, queuedAt: pinnedInstant,
    startedAt: pinnedInstant, completedAt: pinnedInstant, failedAt: null, cancelledAt: null, outputs: [],
    resultJson: JSON.stringify({
      assetId: ASSET_ID,
      documentType: "document",
      title: "Synthetic brief",
      language: "en",
      summary: "Synthetic summary",
      sections: [],
      representations: [
        { id: DOCX_ID, type: "docx", fileName: "synthetic-brief.docx", contentType: DOCX_CONTENT_TYPE },
        { id: PDF_ID, type: "pdf", fileName: "synthetic-brief.pdf", contentType: PDF_CONTENT_TYPE },
      ],
    }),
  } as GenerationJob;
}

type Deferred<T> = { promise: Promise<T>; resolve: (v: T) => void; reject: (e: unknown) => void };
function deferred<T>(): Deferred<T> {
  let resolve!: (v: T) => void; let reject!: (e: unknown) => void;
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}

const storage = new Map<string, string>();
let timers: Map<number, () => void>;
let nextTimerId = 1;
let doc: TestDocument;
let container: TestNode;
let root: Root;

async function drain() { await new Promise((r) => setTimeout(r, 0)); }
async function settle(fn: () => void) { await act(async () => { fn(); await drain(); }); }

function allNodes(node: TestNode): TestNode[] { return [node, ...node.childNodes.flatMap(allNodes)]; }
function findByClass(node: TestNode, cls: string) { return allNodes(node).filter((n) => (n.getAttribute("class")?.split(/\s+/) ?? []).includes(cls)); }
function findByTag(node: TestNode, tag: string) { return allNodes(node).filter((n) => n.nodeName === tag.toUpperCase()); }
/** React emits boolean attributes as an empty string and removes them when false. */
function isDisabled(node: TestNode): boolean { return node.disabled === true || node.getAttribute("disabled") !== null; }
function actionButtonByLabel(label: string): TestNode {
  const match = findByClass(container, "document-action-button")
    .find((node) => (node.textContent ?? "").trim().includes(label));
  if (!match) throw new Error(`No document action button with label "${label}"`);
  return match;
}
function generateButton(): TestNode {
  const match = findByClass(container, "document-generate-button")[0];
  if (!match) throw new Error("No generate button rendered");
  return match;
}

async function mount() {
  harness.workspace = WS;
  await act(async () => { root = createRoot(container as unknown as Element); root.render(<DocumentStudioView />); await drain(); });
}
async function reachResultView() {
  await mount();
  // Type a brief and submit: the compose form becomes the result workspace when the
  // (stubbed) generation job resolves as Succeeded with docx + pdf representations.
  const textarea = findByTag(container, "TEXTAREA")[0];
  const descriptor = Object.getOwnPropertyDescriptor(TestNode.prototype, "value");
  await settle(() => { textarea.dispatchEvent({ type: "focusin", bubbles: true }); });
  await settle(() => {
    descriptor!.set!.call(textarea, "quarterly synthesis brief");
    textarea.dispatchEvent({ type: "keyup", bubbles: true });
  });
  const form = findByTag(container, "FORM")[0];
  await settle(() => { form.dispatchEvent({ type: "submit", bubbles: true, cancelable: true }); });
  expect(findByClass(container, "document-workspace")).toHaveLength(1);
}

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
  harness.api.listAssets.mockReset().mockResolvedValue({ items: [], page: 1, pageSize: 4, totalCount: 0, totalPages: 1 });
  harness.api.createDocumentGenerationJob.mockReset().mockResolvedValue(succeededDocumentJob());
  harness.api.getGenerationJob.mockReset().mockResolvedValue(succeededDocumentJob());
  harness.api.cancelGenerationJob.mockReset().mockResolvedValue({ status: "Cancelled" });
  harness.api.downloadAssetRepresentation.mockReset();
  harness.workspace = null;
});
afterEach(() => { act(() => root?.unmount()); vi.unstubAllGlobals(); });

describe("Document Studio draft-export action states", () => {
  // While an export download is in flight, the DOCX and PDF actions must be disabled so the
  // same representation cannot be fetched twice concurrently, and the press must announce busy.
  it("disables the export actions while an export download is in flight", async () => {
    const pending = deferred<Blob>();
    harness.api.downloadAssetRepresentation.mockReturnValue(pending.promise);
    await reachResultView();

    await settle(() => { actionButtonByLabel("DOCX").dispatchEvent({ type: "click", bubbles: true }); });

    expect(harness.api.downloadAssetRepresentation).toHaveBeenCalledTimes(1);
    expect(harness.api.downloadAssetRepresentation).toHaveBeenCalledWith(ASSET_ID, DOCX_ID);
    for (const label of ["DOCX", "PDF"]) {
      expect(isDisabled(actionButtonByLabel(label))).toBe(true);
      expect(actionButtonByLabel(label).getAttribute("aria-busy")).toBe("true");
    }
  });

  // DEFECT (red on current main): the export download shares its busy flag with the compose
  // form. "Create another" abandons the result view (and the download's write-back fences),
  // but never releases that busy claim. The in-flight download can no longer clear the flag
  // either: its finally-block guard compares against refs the abandon path just nulled. The
  // compose form then renders its Generate action permanently disabled (and aria-busy), with
  // no way to start a new document other than reloading the page.
  it("releases the compose Generate action when the studio leaves an in-flight export", async () => {
    const pending = deferred<Blob>();
    harness.api.downloadAssetRepresentation.mockReturnValue(pending.promise);
    await reachResultView();

    await settle(() => { actionButtonByLabel("DOCX").dispatchEvent({ type: "click", bubbles: true }); });
    // Leaving the result view mid-export is allowed on purpose: only the two export
    // actions themselves carry the busy state.
    await settle(() => { actionButtonByLabel("document.regenerate").dispatchEvent({ type: "click", bubbles: true }); });

    expect(findByClass(container, "document-compose-layout")).toHaveLength(1);
    expect(isDisabled(generateButton())).toBe(false);
    expect(generateButton().getAttribute("aria-busy")).toBe("false");

    // The abandoned export settling (either way) must not re-arm the stuck busy state.
    await settle(() => { pending.resolve(new Blob(["synthetic"], { type: PDF_CONTENT_TYPE })); });
    expect(isDisabled(generateButton())).toBe(false);
  });

  // DEFECT (red on current main): the same stuck-busy state through the Refine handoff, which
  // routes through the same abandon path. Its deferred focus reset must still land on the brief.
  it("releases the compose Generate action when Refine interrupts an in-flight export", async () => {
    const pending = deferred<Blob>();
    harness.api.downloadAssetRepresentation.mockReturnValue(pending.promise);
    await reachResultView();

    await settle(() => { actionButtonByLabel("PDF").dispatchEvent({ type: "click", bubbles: true }); });
    await settle(() => { actionButtonByLabel("document.refine").dispatchEvent({ type: "click", bubbles: true }); });

    expect(findByClass(container, "document-compose-layout")).toHaveLength(1);
    expect(isDisabled(generateButton())).toBe(false);

    // A failed export that settles after being abandoned is reported nowhere.
    await settle(() => { pending.reject(new Error("synthetic transport failure")); });
    expect(isDisabled(generateButton())).toBe(false);
    expect(findByClass(container, "form-error")
      .some((node) => (node.textContent ?? "").includes("document.downloadError"))).toBe(false);
  });

  // The failure path that stays on the result view must return the export actions to enabled
  // and surface the localized download error: the busy claim is only allowed to outlive the
  // view when the view itself is abandoned.
  it("re-enables the export actions when a download fails without leaving the result view", async () => {
    const pending = deferred<Blob>();
    harness.api.downloadAssetRepresentation.mockReturnValue(pending.promise);
    await reachResultView();

    await settle(() => { actionButtonByLabel("DOCX").dispatchEvent({ type: "click", bubbles: true }); });
    expect(isDisabled(actionButtonByLabel("PDF"))).toBe(true);

    await settle(() => { pending.reject(new Error("synthetic transport failure")); });

    for (const label of ["DOCX", "PDF"]) {
      expect(isDisabled(actionButtonByLabel(label))).toBe(false);
      expect(actionButtonByLabel(label).getAttribute("aria-busy")).not.toBe("true");
    }
    expect(findByClass(container, "form-error")
      .some((node) => (node.textContent ?? "").includes("document.downloadError"))).toBe(true);
  });

  // After the export completes normally, the actions return to enabled as well; a full
  // abandon-then-export round trip must not leak busy state either way.
  it("re-enables the export actions when the download completes in view", async () => {
    harness.api.downloadAssetRepresentation.mockResolvedValue(new Blob(["synthetic"], { type: DOCX_CONTENT_TYPE }));
    await reachResultView();

    await settle(() => { actionButtonByLabel("DOCX").dispatchEvent({ type: "click", bubbles: true }); });

    expect(findByClass(container, "document-workspace")).toHaveLength(1);
    for (const label of ["DOCX", "PDF"]) {
      expect(isDisabled(actionButtonByLabel(label))).toBe(false);
      expect(actionButtonByLabel(label).getAttribute("aria-busy")).not.toBe("true");
    }
  });
});
