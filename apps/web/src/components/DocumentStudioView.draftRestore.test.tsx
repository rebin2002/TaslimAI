/**
 * Synthetic harness for the Document Studio editor's unsaved-draft lifecycle.
 *
 * Defect under test: the compose brief (description, title, guided choices)
 * lived only in component state, so switching tabs away from the document
 * editor and back - which unmounts and remounts the view - discarded work the
 * user had never submitted. These cases mount the real editor, type into it,
 * unmount for the tab switch, remount for the return, and assert the draft is
 * restored for its own workspace and never for another one.
 *
 * Provider-free and network-free: every API call is a stub, storage is an
 * in-memory Map, and React's event plumbing is exercised through the same
 * delegated-event DOM shim established by ProjectContextEditor.test (#849).
 */
import React, { act } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

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
  private currentChecked = false;
  private currentSelected = false;
  private currentType: string | null = null;
  disabled = false;
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
  // React decides whether a change on an element is a text edit from `node.type`,
  // and it writes the input's type during mount, so the property is readable
  // and writable. An input with no explicit attribute is a text field.
  get type() {
    if (this.currentType !== null) return this.currentType;
    const type = this.attributes.get("type");
    if (type) return type.toLowerCase();
    return this.nodeName === "INPUT" ? "text" : this.nodeName === "TEXTAREA" ? "textarea" : "";
  }
  set type(value: string) { this.currentType = String(value); }
  get value() { return this.currentValue; }
  set value(value: string) { this.currentValue = String(value); }
  get checked() { return this.currentChecked; }
  set checked(value: boolean) { this.currentChecked = Boolean(value); }
  get selected() { return this.currentSelected; }
  set selected(value: boolean) { this.currentSelected = Boolean(value); }
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
  // React decides whether it can rely on native `input` events when react-dom is
  // first imported, by probing `"oninput" in document`. Declaring the handler here
  // makes the synthetic document answer like a browser, so a dispatched input
  // event reaches the editor's controlled onChange handlers.
  oninput: ((event: unknown) => void) | null = null;
  onchange: ((event: unknown) => void) | null = null;
  constructor() {
    super(9, "#document", null as unknown as TestDocument);
    this.ownerDocument = this;
    this.defaultView = {};
    this.documentElement = new TestNode(1, "html", this);
    this.body = new TestNode(1, "body", this);
    this.documentElement.appendChild(this.body);
    this.appendChild(this.documentElement);
    this.activeElement = this.body;
  }
  createElement(name: string) { return new TestNode(1, name.toUpperCase(), this); }
  createElementNS(_namespace: string, name: string) { return this.createElement(name); }
  createTextNode(value: string) { return new TestNodeText(value, this); }
  getElementById(id: string) { return this.find((node) => node.getAttribute("id") === id); }
  private find(predicate: (node: TestNode) => boolean): TestNode | null { const visit = (node: TestNode): TestNode | null => (predicate(node) ? node : node.childNodes.map(visit).find(Boolean) ?? null); return visit(this); }
}

const harness = vi.hoisted(() => ({
  auth: { user: { id: "user-a" }, workspace: null as { id: string } | null },
  api: {
    listProjects: vi.fn(),
    listFiles: vi.fn(),
    listAssets: vi.fn(),
    createDocumentGenerationJob: vi.fn(),
    getGenerationJob: vi.fn(),
    cancelGenerationJob: vi.fn(),
    downloadAssetRepresentation: vi.fn(),
  },
  searchParams: new URLSearchParams(),
}));
vi.mock("@/components/AuthProvider", () => ({ useAuth: () => ({ user: harness.auth.user, workspace: harness.auth.workspace }) }));
vi.mock("@/components/LocaleProvider", () => ({ useLocale: () => ({ locale: "en", t: (key: string) => key }) }));
vi.mock("@/lib/api", () => ({
  ApiError: class ApiError extends Error {
    status: number;
    constructor(message: string, status: number) { super(message); this.status = status; }
  },
  api: harness.api,
}));
vi.mock("next/navigation", () => ({ useSearchParams: () => harness.searchParams }));
vi.mock("next/link", () => ({
  default: (props: { href: string; children?: unknown }) => <a href={props.href}>{props.children as never}</a>,
}));
vi.mock("lucide-react", () => {
  const Icon = (props: Record<string, unknown>) => <span {...props} />;
  return {
    ArrowLeft: Icon, Check: Icon, CheckCircle2: Icon, Clipboard: Icon, Download: Icon,
    FileText: Icon, FolderOpen: Icon, LoaderCircle: Icon, RefreshCw: Icon, Sparkles: Icon,
    WandSparkles: Icon, XCircle: Icon,
  };
});

// The synthetic document has to exist before react-dom computes its native
// input-event support, so the client renderer and the editor are imported
// dynamically, after the globals below are stubbed.
const testDocument = new TestDocument();
const testContainer = testDocument.createElement("div");
testDocument.body.appendChild(testContainer);
const stored = new Map<string, string>();
const draftWrites: Array<{ key: string; value: string }> = [];
function makeWindow(document: TestDocument) {
  return {
    document,
    HTMLElement: TestNode,
    HTMLIFrameElement: TestNode,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    setTimeout: (callback: () => void) => globalThis.setTimeout(callback),
    clearTimeout: (id: ReturnType<typeof globalThis.setTimeout>) => globalThis.clearTimeout(id),
    sessionStorage: {
      getItem: (key: string) => stored.get(key) ?? null,
      setItem: (key: string, value: string) => { draftWrites.push({ key, value }); stored.set(key, value); },
      removeItem: (key: string) => { stored.delete(key); },
    },
  };
}
const testWindow = makeWindow(testDocument);
testDocument.defaultView = testWindow;
vi.stubGlobal("document", testDocument);
vi.stubGlobal("window", testWindow);
vi.stubGlobal("navigator", { userAgent: "test" });
vi.stubGlobal("IS_REACT_ACT_ENVIRONMENT", true);

const { createRoot } = await import("react-dom/client");
const { DocumentStudioView } = await import("@/components/DocumentStudioView");
const { documentDraftStorageKey } = await import("@/lib/documentStudioDraftState");

const workspaceA = { id: "workspace-aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa" };
const workspaceB = { id: "workspace-bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb" };
const briefText = "A concise launch brief for the retail pilot, written but never submitted.";

let root: ReturnType<typeof createRoot> | null = null;

async function drain() {
  await new Promise((resolve) => setTimeout(resolve, 0));
}

async function mountStudio(workspace: { id: string }) {
  harness.auth.workspace = workspace;
  await act(async () => {
    root = createRoot(testContainer as unknown as Element);
    root.render(<DocumentStudioView />);
    await drain();
  });
}

async function switchTabAwayAndBack(workspace: { id: string }) {
  await act(async () => {
    root?.unmount();
    root = null;
    await drain();
  });
  await mountStudio(workspace);
}

function elementById(id: string): TestNode {
  const node = testDocument.getElementById(id);
  if (!node) throw new Error(`missing element #${id}`);
  return node;
}
/** Same walk as findByAttribute, for the cases where the element must be
 * absent rather than present. */
function hasAttribute(name: string, value: string): boolean {
  const visit = (node: TestNode): boolean => node.getAttribute(name) === value || node.childNodes.some(visit);
  return visit(testDocument);
}
function findByAttribute(name: string, value: string): TestNode {
  const visit = (node: TestNode): TestNode | null => (node.getAttribute(name) === value ? node : node.childNodes.map(visit).find(Boolean) ?? null);
  const found = visit(testDocument);
  if (!found) throw new Error(`missing element [${name}="${value}"]`);
  return found;
}
function findByClass(node: TestNode, className: string): TestNode[] {
  return [
    ...(node.getAttribute("class")?.split(/\s+/).includes(className) ? [node] : []),
    ...node.childNodes.flatMap((child) => findByClass(child, className)),
  ];
}

/** Types into a controlled field the way a browser does: set the value on the
 * platform descriptor React could not replace, then dispatch the input event. */
async function type(field: TestNode, value: string) {
  const descriptor = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(field), "value");
  descriptor?.set?.call(field, value);
  await act(async () => {
    field.dispatchEvent({ type: "input", bubbles: true });
    await drain();
  });
}

/** Chooses a select option, which React delivers as a change event. */
async function choose(select: TestNode, value: string) {
  const descriptor = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(select), "value");
  descriptor?.set?.call(select, value);
  await act(async () => {
    select.dispatchEvent({ type: "change", bubbles: true });
    await drain();
  });
}
/** Submits the compose form, the way the Generate button does. */
async function submitBrief() {
  const form = findByClass(testContainer, "document-brief-card")[0];
  if (!form) throw new Error("missing compose form");
  await act(async () => {
    form.dispatchEvent({ type: "submit", bubbles: true });
    await drain();
  });
}

describe("Document Studio unsaved draft across a tab switch", () => {
  beforeEach(() => {
    stored.clear();
    draftWrites.length = 0;
    harness.auth.workspace = null;
    harness.auth.user = { id: "user-a" };
    harness.api.listProjects.mockReset().mockResolvedValue([]);
    harness.api.listFiles.mockReset().mockResolvedValue([]);
    harness.api.listAssets.mockReset().mockResolvedValue({ items: [] });
    harness.api.getGenerationJob.mockReset();
    harness.api.createDocumentGenerationJob.mockReset();
  });

  afterEach(() => {
    act(() => {
      root?.unmount();
      root = null;
    });
  });

  it("restores the unsaved brief when the editor is mounted again", async () => {
    await mountStudio(workspaceA);
    await type(elementById("document-brief"), briefText);

    // The user has not generated anything yet: the work exists only as an
    // unsaved draft, so it must survive the unmount that a tab switch causes.
    await switchTabAwayAndBack(workspaceA);

    expect(elementById("document-brief").value).toBe(briefText);
  });

  it("restores the guided choices the user left unset on the server", async () => {
    await mountStudio(workspaceA);
    await type(elementById("document-brief"), briefText);
    await type(findByAttribute("placeholder", "document.titlePlaceholder"), "Pilot launch brief");
    await choose(elementById("document-output-format"), "pdf");

    await switchTabAwayAndBack(workspaceA);

    expect(elementById("document-brief").value).toBe(briefText);
    expect(findByAttribute("placeholder", "document.titlePlaceholder").value).toBe("Pilot launch brief");

    // The restored fields have to be the fields that get sent: a brief that
    // only looks restored in the DOM but not in state is still lost work.
    harness.api.createDocumentGenerationJob.mockReset().mockResolvedValue({
      id: "00000000-0000-4000-8000-000000000001",
      workspaceId: workspaceA.id,
      projectId: null,
      jobType: "document.generate",
      status: "Pending",
      title: "Pilot launch brief",
      progressPercent: 0,
      createdAt: "2026-11-17T00:00:00Z",
      queuedAt: null,
      startedAt: null,
      completedAt: null,
      failedAt: null,
      cancelledAt: null,
      cancellationRequested: false,
      resultJson: null,
      errorCode: null,
      errorMessage: null,
      outputs: [],
    });
    await submitBrief();
    expect(harness.api.createDocumentGenerationJob).toHaveBeenCalledTimes(1);
    expect(harness.api.createDocumentGenerationJob.mock.calls[0]?.[0]).toMatchObject({
      workspaceId: workspaceA.id,
      description: briefText,
      title: "Pilot launch brief",
      outputFormat: "pdf",
      documentType: "auto",
      length: "standard",
      tone: "professional",
    });
  });

  it("keeps one workspace's unsaved brief out of another workspace", async () => {
    await mountStudio(workspaceA);
    await type(elementById("document-brief"), briefText);

    // Same browser, different workspace: the draft belongs to workspace A only.
    await switchTabAwayAndBack(workspaceB);
    expect(elementById("document-brief").value).toBe("");
    expect(stored.has(documentDraftStorageKey(harness.auth.user.id, workspaceB.id))).toBe(false);

    await switchTabAwayAndBack(workspaceA);
    expect(elementById("document-brief").value).toBe(briefText);
  });

  it("does not expose a prior signed-in account draft in the same workspace", async () => {
    await mountStudio(workspaceA);
    await type(elementById("document-brief"), briefText);
    harness.auth.user = { id: "user-b" };
    await switchTabAwayAndBack(workspaceA);
    expect(elementById("document-brief").value).toBe("");
    await type(elementById("document-brief"), "Second account only");
    harness.auth.user = { id: "user-a" };
    await switchTabAwayAndBack(workspaceA);
    expect(elementById("document-brief").value).toBe(briefText);
  });

  it("fences an account switch on the already-mounted editor", async () => {
    await mountStudio(workspaceA);
    await type(elementById("document-brief"), briefText);
    harness.auth.user = { id: "user-b" };
    await act(async () => { root!.render(<DocumentStudioView />); await drain(); });
    expect(elementById("document-brief").value).toBe("");
    // No transient write of user A's text may land in user B's empty draft.
    const incomingKey = documentDraftStorageKey("user-b", workspaceA.id);
    expect(draftWrites.filter((entry) => entry.key === incomingKey)).toEqual([]);
  });

  it("starts from a blank brief when nothing was left unsent", async () => {
    await mountStudio(workspaceA);
    expect(elementById("document-brief").value).toBe("");
    expect(stored.has(documentDraftStorageKey(harness.auth.user.id, workspaceA.id))).toBe(false);
  });

  it("discards a stored brief that is not a readable draft", async () => {
    stored.set(documentDraftStorageKey(harness.auth.user.id, workspaceA.id), "{not-json");

    await mountStudio(workspaceA);

    expect(elementById("document-brief").value).toBe("");
    expect(stored.has(documentDraftStorageKey(harness.auth.user.id, workspaceA.id))).toBe(false);
  });

  it("names the unsent brief as restored when it comes back", async () => {
    await mountStudio(workspaceA);
    await type(elementById("document-brief"), briefText);

    await switchTabAwayAndBack(workspaceA);

    // The text is back; the notice tells the user it came from their own
    // unsent draft instead of leaving the restoration as an unexplained
    // surprise. `t` is the identity mock, so the key names the catalog entry
    // whose EN/AR/Sorani copies are pinned by i18nParity and the browser
    // acceptance suite.
    expect(hasAttribute("data-document-draft-restored", "true")).toBe(true);
    const notice = findByAttribute("data-document-draft-restored", "true");
    expect(notice.getAttribute("role")).toBe("status");
    expect(notice.getAttribute("aria-live")).toBe("polite");
    expect(notice.textContent).toContain("document.draftRestored");
  });

  it("stays silent when no unsent brief was restored", async () => {
    await mountStudio(workspaceA);
    expect(hasAttribute("data-document-draft-restored", "true")).toBe(false);

    // A different workspace of the same browser session has no draft of its
    // own: a blank editor must not be described as a restoration.
    await type(elementById("document-brief"), briefText);
    await switchTabAwayAndBack(workspaceB);
    expect(elementById("document-brief").value).toBe("");
    expect(hasAttribute("data-document-draft-restored", "true")).toBe(false);

    // An unreadable stored value restores nothing, so nothing is announced.
    stored.set(documentDraftStorageKey(harness.auth.user.id, workspaceB.id), "{not-json");
    await switchTabAwayAndBack(workspaceB);
    expect(hasAttribute("data-document-draft-restored", "true")).toBe(false);
  });
});
