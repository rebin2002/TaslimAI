import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { MovieLocation, MovieWorldReference, MovieWorldWorkspace as WorldData } from "@/lib/api";
import { MovieWorldWorkspace } from "./MovieWorldWorkspace";

// Synthetic DOM harness, in the style established by AssetsViewListing.test (#849) and
// AdminOperationsView.selectedRangeReload.test.tsx. Independent review of carrier head 5df3254f
// found that while the in-flight getMovieWorld load was fenced on project switch, the save() and
// saveReference() continuations were not: a create started in movie project A that resolved after
// the user switched to project B appended A's returned record into B's world and moved B's
// selection/edit state, and a delayed failure painted an error onto B. These cases hold the real
// component against deferred mutation and GET promises and pin the fence on both success and
// failure arms. Every value below is a synthetic fixture; no real project data is used.

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
  hidden = false;
  private currentType: string | null = null;
  private currentChecked = false;
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
  // React's isTextInputElement reads node.type; without it an INPUT never counts as text-like
  // and its onChange would stay silent (ProjectContextEditor.revisionFence harness fields).
  get type() { return this.currentType ?? (this.nodeName === "INPUT" ? "text" : this.nodeName === "TEXTAREA" ? "textarea" : this.nodeName === "BUTTON" ? "submit" : ""); }
  set type(value: string) { this.currentType = String(value); }
  get checked() { return this.currentChecked; }
  set checked(value: boolean) { this.currentChecked = Boolean(value); }
  appendChild<T extends TestNode>(child: T) { if (child.parentNode) child.parentNode.removeChild(child); child.parentNode = this; this.childNodes.push(child); return child; }
  insertBefore<T extends TestNode>(child: T, before: TestNode | null) { if (!before) return this.appendChild(child); const index = this.childNodes.indexOf(before); if (index < 0) return this.appendChild(child); if (child.parentNode) child.parentNode.removeChild(child); child.parentNode = this; this.childNodes.splice(index, 0, child); return child; }
  removeChild<T extends TestNode>(child: T) { const index = this.childNodes.indexOf(child); if (index >= 0) this.childNodes.splice(index, 1); child.parentNode = null; return child; }
  setAttribute(name: string, value: string) { this.attributes.set(name, String(value)); }
  getAttribute(name: string) { return this.attributes.get(name) ?? null; }
  removeAttribute(name: string) { this.attributes.delete(name); }
  addEventListener(name: string, listener: (event: unknown) => void) { const listeners = this.listeners.get(name) ?? new Set(); listeners.add(listener); this.listeners.set(name, listeners); }
  removeEventListener(name: string, listener: (event: unknown) => void) { this.listeners.get(name)?.delete(listener); }
  // React delegates its listeners to the root container, so a dispatched event has to travel up
  // the ancestor chain the same way a browser event would.
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
  // React falls back to its input-event polyfill outside a real browser, where value changes are
  // watched through the legacy onpropertychange hook rather than the native input event.
  attachEvent() { return true; }
  detachEvent() { return true; }
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
  createTextNode(value: string) { return new TestNodeText(value, this.ownerDocument); }
  getElementById(id: string) { return this.find((node) => node.getAttribute("id") === id); }
  private find(predicate: (node: TestNode) => boolean): TestNode | null { const visit = (node: TestNode): TestNode | null => (predicate(node) ? node : node.childNodes.map(visit).find(Boolean) ?? null); return visit(this); }
}

type Deferred<T> = { promise: Promise<T>; resolve: (value: T) => void; reject: (error: unknown) => void };

const harness = vi.hoisted(() => ({
  api: {
    getMovieWorld: vi.fn(),
    addMovieLocation: vi.fn(),
    updateMovieLocation: vi.fn(),
    addMovieSet: vi.fn(),
    updateMovieSet: vi.fn(),
    addMovieProp: vi.fn(),
    updateMovieProp: vi.fn(),
    addMovieWorldReference: vi.fn(),
    addMovieSetVariation: vi.fn(),
    upsertMovieLocationGeographySheet: vi.fn(),
    approveMovieLocationGeographySheet: vi.fn(),
    addMovieLocationGeographyVariant: vi.fn(),
    approveMovieLocationGeographyVariant: vi.fn(),
  },
}));

vi.mock("@/components/LocaleProvider", () => ({
  useLocale: () => ({ locale: "en", t: (key: string) => key }),
}));
vi.mock("@/lib/api", () => ({ api: harness.api }));
vi.mock("next/link", () => ({
  default: (props: { href: string; children?: unknown }) => <a href={props.href}>{props.children as never}</a>,
}));
vi.mock("lucide-react", () => {
  const Icon = () => <span />;
  return {
    ArrowUpRight: Icon, Check: Icon, Compass: Icon, DoorOpen: Icon, Image: Icon,
    LockKeyhole: Icon, MapPin: Icon, Package: Icon, Pencil: Icon, Plus: Icon,
    Route: Icon, Save: Icon, ShieldCheck: Icon, Sparkles: Icon, Sun: Icon, Theater: Icon,
  };
});

const PROJECT_A = "movie-A";
const PROJECT_B = "movie-B";

function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}
async function drain() { await new Promise((resolve) => setTimeout(resolve, 0)); }
async function settle(run: () => void) { await act(async () => { run(); await drain(); }); }
async function flush() { await act(async () => { await drain(); }); }

function worldFor(projectId: string): WorldData {
  return {
    movieProjectId: projectId,
    workspaceId: `workspace-${projectId}`,
    projectId: `parent-${projectId}`,
    status: "Planning",
    title: `World ${projectId}`,
    description: "Synthetic fixture world",
    durationSeconds: 60,
    aspectRatio: "16:9",
    style: "neo-noir",
    language: "en",
    world: {
      locations: [{ id: `loc-${projectId}`, name: `Alpha ${projectId}`, description: "Anchor location", visualContinuityNotes: null, referenceAssetId: null }],
      sets: [],
      props: [],
      references: [],
      usages: [],
      facts: [],
      locks: [],
    },
    usageDetails: [],
    assets: [],
  };
}

let document: TestDocument;
let root: Root;
let container: TestNode;
let loads: { projectId: string; deferred: Deferred<WorldData>; settled: boolean }[];
let creates: Deferred<MovieLocation>[];
let referenceAdds: Deferred<MovieWorldReference>[];

async function mountWorld(projectId: string) {
  await act(async () => {
    root = createRoot(container as unknown as Element);
    root.render(<MovieWorldWorkspace projectId={projectId} />);
    await drain();
  });
  await settleLoad(loads.length - 1);
}
async function switchToProject(projectId: string) {
  await act(async () => {
    root.render(<MovieWorldWorkspace projectId={projectId} />);
    await drain();
  });
  await settleLoad(loads.length - 1);
}
async function settleLoad(index: number) {
  const entry = loads[index];
  if (!entry) throw new Error(`no world load #${index + 1}`);
  await settle(() => { if (!entry.settled) { entry.settled = true; entry.deferred.resolve(worldFor(entry.projectId)); } });
  await flush();
}

function findByClass(node: TestNode, className: string): TestNode[] {
  return [
    ...(node.getAttribute("class")?.split(/\s+/).includes(className) ? [node] : []),
    ...node.childNodes.flatMap((child) => findByClass(child, className)),
  ];
}
function findByTag(node: TestNode, tagName: string): TestNode[] {
  return [
    ...(node.nodeName === tagName ? [node] : []),
    ...node.childNodes.flatMap((child) => findByTag(child, tagName)),
  ];
}
function textOf(node: TestNode) { return node.textContent ?? ""; }
function requireNode(node: TestNode | undefined, what: string): TestNode {
  if (!node) throw new Error(`no ${what} rendered`);
  return node;
}
async function click(node: TestNode) { await settle(() => node.dispatchEvent({ type: "click", bubbles: true })); }
/**
 * Types into a controlled field the way a browser does. TEXTAREA fields take the plain input
 * event (ProjectContextEditor.revisionFence idiom); INPUT fields are additionally checked by
 * React's keyup value-poll (AdminOperationsView.selectedRangeReload idiom). The value tracker
 * deduplicates, so firing both arms against either element is safe.
 */
async function typeText(field: TestNode, value: string) {
  const descriptor = Object.getOwnPropertyDescriptor(TestNode.prototype, "value");
  await settle(() => { field.dispatchEvent({ type: "focusin", bubbles: true }); });
  await act(async () => {
    descriptor?.set?.call(field, value);
    field.dispatchEvent({ type: "input", bubbles: true });
    field.dispatchEvent({ type: "keyup", bubbles: true });
    await drain();
  });
}
function editorSaveButton() {
  const editor = requireNode(findByClass(container, "movie-world-editor")[0], "world editor");
  const button = findByClass(editor, "movie-world-save").flatMap((node) => findByTag(node, "BUTTON"))[0]
    ?? findByTag(editor, "BUTTON").find((node) => (node.getAttribute("class") ?? "").includes("movie-world-save"));
  return requireNode(button, "editor save button");
}
function referenceForm() {
  return requireNode(findByClass(container, "movie-world-reference-form")[0], "reference form");
}
function selectedListEntryName(): string | null {
  const item = findByClass(container, "movie-world-list-item").find((node) => textOf(node) && (node.getAttribute("class") ?? "").includes("is-selected"));
  return item ? textOf(item) : null;
}

async function startCreateInA() {
  await click(requireNode(findByClass(container, "movie-world-add")[0], "new record button"));
  const editor = requireNode(findByClass(container, "movie-world-editor")[0], "world editor");
  await typeText(requireNode(findByTag(editor, "INPUT")[0], "name input"), "Late arrival");
  await typeText(requireNode(findByTag(editor, "TEXTAREA")[0], "description field"), "lands after the switch");
  await click(editorSaveButton());
  if (creates.length !== 1) throw new Error(`expected one pending create, saw ${creates.length}`);
}

beforeEach(() => {
  document = new TestDocument();
  container = document.createElement("div");
  document.body.appendChild(container);
  vi.stubGlobal("document", document);
  const window = {
    document,
    HTMLElement: TestNode,
    HTMLIFrameElement: TestNode,
    Node: TestNode,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    setTimeout: (callback: () => void) => setTimeout(callback, 0),
    clearTimeout: (id: number) => clearTimeout(id),
  };
  document.defaultView = window;
  vi.stubGlobal("window", window);
  vi.stubGlobal("navigator", { userAgent: "test" });
  vi.stubGlobal("IS_REACT_ACT_ENVIRONMENT", true);
  loads = [];
  creates = [];
  referenceAdds = [];
  harness.api.getMovieWorld.mockReset().mockImplementation((projectId: string) => {
    const entry = { projectId, deferred: deferred<WorldData>(), settled: false };
    loads.push(entry);
    return entry.deferred.promise;
  });
  harness.api.addMovieLocation.mockReset().mockImplementation(() => {
    const entry = deferred<MovieLocation>();
    creates.push(entry);
    return entry.promise;
  });
  harness.api.addMovieWorldReference.mockReset().mockImplementation(() => {
    const entry = deferred<MovieWorldReference>();
    referenceAdds.push(entry);
    return entry.promise;
  });
});

afterEach(() => {
  act(() => root?.unmount());
  vi.unstubAllGlobals();
});

describe("MovieWorldWorkspace project-switch continuation fence", () => {
  it("drops a create success that resolves after switching to another project", async () => {
    await mountWorld(PROJECT_A);
    await startCreateInA();

    await switchToProject(PROJECT_B);
    expect(selectedListEntryName()).toContain("Alpha movie-B");

    // Project A's create is answered while project B is the active world.
    await settle(() => creates[0]!.resolve({
      id: "loc-a-late", name: "Late arrival", description: "lands after the switch",
      visualContinuityNotes: null, referenceAssetId: null,
    }));
    await flush();

    // The durable write was made against A - that is correct and unchanged by the fence.
    expect(harness.api.addMovieLocation).toHaveBeenCalledTimes(1);
    expect(harness.api.addMovieLocation.mock.calls[0]![0]).toBe(PROJECT_A);

    // But nothing of A's response may touch B's view: no appended record, selection untouched.
    expect(textOf(container)).not.toContain("Late arrival");
    expect(findByClass(container, "movie-world-list-item")).toHaveLength(1);
    expect(selectedListEntryName()).toContain("Alpha movie-B");
    expect(textOf(container)).toContain("World movie-B");
  });

  it("drops a create failure that resolves after switching to another project", async () => {
    await mountWorld(PROJECT_A);
    await startCreateInA();

    await switchToProject(PROJECT_B);
    expect(findByClass(container, "movie-workspace-error-inline")).toHaveLength(0);

    await settle(() => creates[0]!.reject(new Error("A-only failure must not reach B")));
    await flush();

    expect(textOf(container)).not.toContain("A-only failure must not reach B");
    expect(findByClass(container, "movie-workspace-error-inline")).toHaveLength(0);
    expect(textOf(container)).toContain("World movie-B");
  });

  it("drops a reference success that resolves after switching to another project", async () => {
    await mountWorld(PROJECT_A);
    const bottomGrid = requireNode(findByClass(container, "movie-world-bottom-grid")[0], "reference library card");
    await click(requireNode(findByClass(bottomGrid, "movie-world-inline-add")[0], "register reference button"));
    const form = referenceForm();
    await typeText(requireNode(findByTag(form, "INPUT")[0], "reference name input"), "Ghost reference");
    await click(requireNode(findByTag(form, "BUTTON").find((node) => (node.getAttribute("class") ?? "").includes("movie-world-save")), "reference save button"));
    if (referenceAdds.length !== 1) throw new Error(`expected one pending reference add, saw ${referenceAdds.length}`);

    await switchToProject(PROJECT_B);

    await settle(() => referenceAdds[0]!.resolve({ id: "ref-a-late", name: "Ghost reference", kind: "moodboard", description: null, tagsJson: null, assetId: null }));
    await flush();

    expect(harness.api.addMovieWorldReference).toHaveBeenCalledTimes(1);
    expect(harness.api.addMovieWorldReference.mock.calls[0]![0]).toBe(PROJECT_A);
    expect(textOf(container)).not.toContain("Ghost reference");
    expect(textOf(container)).toContain("World movie-B");
  });

  it("drops a reference failure that resolves after switching to another project", async () => {
    await mountWorld(PROJECT_A);
    const bottomGrid = requireNode(findByClass(container, "movie-world-bottom-grid")[0], "reference library card");
    await click(requireNode(findByClass(bottomGrid, "movie-world-inline-add")[0], "register reference button"));
    const form = referenceForm();
    await typeText(requireNode(findByTag(form, "INPUT")[0], "reference name input"), "Failing reference");
    await click(requireNode(findByTag(form, "BUTTON").find((node) => (node.getAttribute("class") ?? "").includes("movie-world-save")), "reference save button"));

    await switchToProject(PROJECT_B);
    await settle(() => referenceAdds[0]!.reject(new Error("register refused under A")));
    await flush();

    expect(textOf(container)).not.toContain("register refused under A");
    expect(findByClass(container, "movie-workspace-error-inline")).toHaveLength(0);
  });

  it("still applies a create that resolves while its own project is active", async () => {
    await mountWorld(PROJECT_A);
    await startCreateInA();

    await settle(() => creates[0]!.resolve({
      id: "loc-a-on-time", name: "Late arrival", description: "same project answer",
      visualContinuityNotes: null, referenceAssetId: null,
    }));
    await flush();

    // No project switch happened - the fence must not swallow the normal flow.
    expect(textOf(container)).toContain("Late arrival");
    expect(findByClass(container, "movie-world-list-item")).toHaveLength(2);
    expect(selectedListEntryName()).toContain("Late arrival");
    expect(findByClass(container, "movie-world-editor")).toHaveLength(0);
  });
});
