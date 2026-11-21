import { act, type ReactElement } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

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
  private find(predicate: (node: TestNode) => boolean): TestNode | null { const visit = (node: TestNode): TestNode | null => predicate(node) ? node : node.childNodes.map(visit).find(Boolean) ?? null; return visit(this); }
}

const harness = vi.hoisted(() => ({
  t: (key: string) => key,
  api: {
    getMovieStory: vi.fn(),
    updateMovieStoryDraft: vi.fn(),
    createMovieStoryRevision: vi.fn(),
    getMovieStoryRevision: vi.fn(),
  },
}));

vi.mock("@/components/LocaleProvider", () => ({ useLocale: () => ({ t: harness.t }) }));
vi.mock("@/lib/api", () => ({ api: harness.api }));
vi.mock("next/link", () => ({ default: ({ children }: { children?: unknown }) => children }));
vi.mock("lucide-react", () => {
  const Icon = () => null;
  return {
    AlertCircle: Icon, AlertTriangle: Icon, ArrowDown: Icon, ArrowLeft: Icon, ArrowUp: Icon,
    ArrowUpRight: Icon, Archive: Icon, AudioLines: Icon, BookOpen: Icon, Check: Icon,
    CheckCircle2: Icon, ChevronRight: Icon, CircleAlert: Icon, Clapperboard: Icon, Clock3: Icon,
    Film: Icon, Gauge: Icon, Image: Icon, ImageOff: Icon, Layers3: Icon, ListChecks: Icon,
    LockKeyhole: Icon, Map: Icon, Package: Icon, PencilRuler: Icon, Play: Icon, Plus: Icon,
    RefreshCw: Icon, Save: Icon, ShieldCheck: Icon, SlidersHorizontal: Icon, Sparkles: Icon,
    Target: Icon, Trash2: Icon, Users: Icon, Workflow: Icon, X: Icon,
  };
});
// Only the story editor's own module graph is exercised; sibling workspaces are stubbed.
vi.mock("@/components/MovieWorldWorkspace", () => ({ MovieWorldWorkspace: () => null }));
vi.mock("@/components/ShotDesigner", () => ({ ShotDesigner: () => null }));
vi.mock("@/components/MovieDirectorPanel", () => ({ MovieDirectorPanel: () => null }));
vi.mock("@/components/MovieProductionResolutionPanel", () => ({ MovieProductionResolutionPanel: () => null }));
vi.mock("@/components/MovieProductionWorkspace", () => ({ MovieProductionWorkspace: () => null }));
vi.mock("@/components/ProductionKitWorkspace", () => ({ ProductionKitWorkspace: () => null }));
vi.mock("@/components/MovieSelectsWorkspace", () => ({ MovieSelectsWorkspace: () => null }));
vi.mock("@/components/MovieAudioWorkspace", () => ({ MovieAudioWorkspace: () => null }));
vi.mock("@/components/MovieQualityWorkspace", () => ({ MovieQualityWorkspace: () => null }));
vi.mock("@/components/MovieExportsWorkspace", () => ({ MovieExportsWorkspace: () => null }));
vi.mock("@/components/MovieTeamWorkspace", () => ({ MovieTeamWorkspace: () => null }));

import { StoryModule } from "./FullMovieWorkspaceView";

type SceneFixture = {
  id: string;
  sceneIdentifier: string;
  actNumber: number;
  sequenceNumber: number;
  movieSceneId: string | null;
  slugline: string;
  synopsis: string;
  elements: { id: string; elementType: string; content: string; characterName: string | null; parenthetical: string | null }[];
};

type RevisionFixture = {
  id: string;
  movieStoryId: string;
  revisionNumber: number;
  status: string;
  authorship: string;
  premise: string;
  logline: string;
  synopsis: string;
  treatment: string;
  changeSummary: string;
  parentRevisionId: string | null;
  scenes: SceneFixture[];
};

type StoryFixture = {
  id: string;
  movieProjectId: string;
  workspaceId: string;
  premise: string;
  logline: string;
  synopsis: string;
  treatment: string;
  approvalState: string;
  currentRevisionId: string | null;
  approvedRevisionId: string | null;
  createdAt: string;
  updatedAt: string;
  currentRevision: RevisionFixture | null;
  approvedRevision: RevisionFixture | null;
  revisions: RevisionFixture[];
  canEdit: boolean;
  canApprove: boolean;
};

const ISO = "2026-01-01T00:00:00Z";

function scene(identifier: string, content: string): SceneFixture {
  return {
    id: `scene-${identifier}`,
    sceneIdentifier: identifier,
    actNumber: 1,
    sequenceNumber: 1,
    movieSceneId: null,
    slugline: `INT. TEST ROOM - DAY (${identifier})`,
    synopsis: "",
    elements: [{ id: `element-${identifier}`, elementType: "Action", content, characterName: null, parenthetical: null }],
  };
}

function revision(projectId: string, id: string, revisionNumber: number, scenes: SceneFixture[]): RevisionFixture {
  return {
    id,
    movieStoryId: `story-${projectId}`,
    revisionNumber,
    status: "Draft",
    authorship: "Human",
    premise: `Premise of ${projectId}`,
    logline: `Logline of ${projectId}`,
    synopsis: `Synopsis of ${projectId}`,
    treatment: `Treatment of ${projectId}`,
    changeSummary: `pass ${revisionNumber}`,
    parentRevisionId: null,
    scenes,
  };
}

function story(projectId: string, revisions: RevisionFixture[]): StoryFixture {
  const current = revisions[0] ?? null;
  return {
    id: `story-${projectId}`,
    movieProjectId: projectId,
    workspaceId: `workspace-${projectId}`,
    premise: "",
    logline: "",
    synopsis: "",
    treatment: "",
    approvalState: "Draft",
    currentRevisionId: current?.id ?? null,
    approvedRevisionId: null,
    createdAt: ISO,
    updatedAt: ISO,
    currentRevision: current,
    approvedRevision: null,
    revisions,
    canEdit: true,
    canApprove: false,
  };
}

let document: TestDocument;
let root: Root;
let container: TestNode;

beforeEach(() => {
  document = new TestDocument();
  container = document.createElement("div");
  document.body.appendChild(container);
  vi.stubGlobal("document", document);
  const window = {
    document,
    HTMLElement: TestNode,
    Element: TestNode,
    Node: TestNode,
    Document: TestDocument,
    HTMLDocument: TestDocument,
    DocumentFragment: TestNode,
    HTMLIFrameElement: TestNode,
    Window: function Window() { /* identity stub for React's iframe containment checks */ },
    setTimeout: (callback: () => void, ms: number) => setTimeout(callback, ms),
    clearTimeout: (timer: ReturnType<typeof setTimeout>) => clearTimeout(timer),
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  };
  document.defaultView = window;
  vi.stubGlobal("window", window);
  vi.stubGlobal("navigator", { userAgent: "test" });
  vi.stubGlobal("IS_REACT_ACT_ENVIRONMENT", true);
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "Date"] });
  harness.api.getMovieStory.mockReset();
  harness.api.updateMovieStoryDraft.mockReset();
  harness.api.createMovieStoryRevision.mockReset();
  harness.api.getMovieStoryRevision.mockReset();
});

afterEach(() => {
  act(() => root?.unmount());
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => { resolve = res; });
  return { promise, resolve };
}

/** Let queued microtasks (promise continuations) run inside act. */
function drain() {
  return act(async () => {
    vi.advanceTimersByTime(0);
    for (let tick = 0; tick < 8; tick += 1) await Promise.resolve();
  });
}

/** Move the fake clock so the autosave debounce fires, then flush continuations. */
function advance(ms: number) {
  return act(async () => {
    vi.advanceTimersByTime(ms);
    for (let tick = 0; tick < 8; tick += 1) await Promise.resolve();
  });
}

function click(node: TestNode) {
  return act(async () => {
    node.dispatchEvent({ type: "click", bubbles: true });
    for (let tick = 0; tick < 8; tick += 1) await Promise.resolve();
  });
}

function findByClass(node: TestNode, className: string): TestNode[] {
  return [
    ...(node.getAttribute("class")?.split(/\s+/).includes(className) ? [node] : []),
    ...node.childNodes.flatMap((child) => findByClass(child, className)),
  ];
}

function addSceneButton(): TestNode {
  const button = findByClass(container, "movie-add-screenplay-scene")[0];
  if (!button) throw new Error("Add scene button is not rendered");
  return button;
}

function historyItems(): TestNode[] {
  return findByClass(container, "movie-story-history-item");
}

function passesCount(): string {
  const badge = findByClass(container, "movie-section-count")[0];
  return badge?.textContent ?? "";
}

function revisionChip(): string {
  const chip = findByClass(container, "movie-story-revision-chip")[0];
  return chip?.textContent ?? "";
}

function firstSceneSlugline(): string {
  const input = findByClass(container, "movie-screenplay-slugline")[0];
  return input?.value ?? "";
}

function mount(module: ReactElement) {
  act(() => {
    root = createRoot(container as unknown as Element);
    root.render(module);
  });
}

describe("Movie Studio script autosave under slow networks", () => {
  it("carries edits typed while a save is in flight into a follow-up autosave", async () => {
    const storyA = story("project-a", [revision("project-a", "rev-a1", 1, [scene("SC-OLD", "The frame is held.")])]);
    harness.api.getMovieStory.mockResolvedValue(storyA);
    const saves: ReturnType<typeof deferred<RevisionFixture>>[] = [];
    harness.api.updateMovieStoryDraft.mockImplementation(() => {
      const pending = deferred<RevisionFixture>();
      saves.push(pending);
      return pending.promise;
    });

    mount(<StoryModule projectId="project-a" guideLocked />);
    await drain();

    // One edit arms the autosave; the debounce fires and the PATCH stays in flight.
    await click(addSceneButton());
    await advance(1800);
    expect(harness.api.updateMovieStoryDraft).toHaveBeenCalledTimes(1);
    const first = harness.api.updateMovieStoryDraft.mock.calls[0] as [string, string, { scenes: unknown[] }];
    expect(first[2].scenes).toHaveLength(2);

    // A second edit arrives while the slow save is still open.
    await click(addSceneButton());
    await advance(1800);
    expect(harness.api.updateMovieStoryDraft).toHaveBeenCalledTimes(1);

    // When the in-flight save finally confirms, the editor must not forget the
    // edit made after the sent snapshot: the debounce re-arms and carries it.
    saves[0].resolve(storyA.currentRevision!);
    await drain();
    await advance(1800);
    expect(harness.api.updateMovieStoryDraft).toHaveBeenCalledTimes(2);
    const followUp = harness.api.updateMovieStoryDraft.mock.calls[1] as [string, string, { scenes: unknown[] }];
    expect(followUp[0]).toBe("project-a");
    expect(followUp[1]).toBe("rev-a1");
    expect(followUp[2].scenes).toHaveLength(3);

    // Once the follow-up confirms the unchanged draft, autosave rests.
    const latest = story("project-a", [revision("project-a", "rev-a1", 1, [scene("SC-OLD", "saved"), scene("SCENE-2", "saved"), scene("SCENE-3", "saved")])]);
    harness.api.getMovieStory.mockResolvedValue(latest);
    saves[1].resolve(latest.currentRevision!);
    await drain();
    await advance(3600);
    expect(harness.api.updateMovieStoryDraft).toHaveBeenCalledTimes(2);
  });

  it("discards a save response that lands after the editor moved to another project", async () => {
    const storyA = story("project-a", [
      revision("project-a", "rev-a1", 3, [scene("SC-A", "First pass of A.")]),
      revision("project-a", "rev-a2", 4, [scene("SC-A2", "Second pass of A.")]),
    ]);
    const storyB = story("project-b", [revision("project-b", "rev-b1", 1, [scene("SC-B", "Only pass of B.")])]);
    harness.api.getMovieStory.mockImplementation((projectId: string) => Promise.resolve(projectId === "project-b" ? storyB : storyA));
    const saveA = deferred<RevisionFixture>();
    harness.api.updateMovieStoryDraft.mockReturnValue(saveA.promise);

    mount(<StoryModule projectId="project-a" guideLocked />);
    await drain();
    expect(passesCount()).toBe("2 passes");

    await click(addSceneButton());
    await advance(1800);
    expect(harness.api.updateMovieStoryDraft).toHaveBeenCalledWith("project-a", "rev-a1", expect.anything());

    // The project route changes while the slow save is still in flight.
    act(() => root.render(<StoryModule projectId="project-b" guideLocked />));
    await drain();
    expect(passesCount()).toBe("1 passes");

    // Project A's late response must not replay over B's freshly loaded editor.
    saveA.resolve(storyA.currentRevision!);
    await drain();
    expect(passesCount()).toBe("1 passes");
    expect(revisionChip()).toContain("Revision 1");
  });

  it("keeps the newest revision click when an older revision read resolves late", async () => {
    const revisionOne = revision("project-a", "rev-1", 1, [scene("SC-ONE", "Older pass.")]);
    const revisionTwo = revision("project-a", "rev-2", 2, [scene("SC-TWO", "Newer pass.")]);
    const storyA = story("project-a", [revisionOne, revisionTwo]);
    harness.api.getMovieStory.mockResolvedValue(storyA);
    const reads = [deferred<RevisionFixture>(), deferred<RevisionFixture>()];
    let readIndex = 0;
    harness.api.getMovieStoryRevision.mockImplementation(() => reads[readIndex++].promise);

    mount(<StoryModule projectId="project-a" guideLocked />);
    await drain();

    // Two history clicks: the first read is the slower one on a congested network.
    await click(historyItems()[0]);
    await click(historyItems()[1]);
    expect(harness.api.getMovieStoryRevision).toHaveBeenCalledTimes(2);

    // The newer click resolves first and takes over the editor.
    reads[1].resolve(revisionTwo);
    await drain();
    expect(revisionChip()).toContain("Revision 2");

    // The superseded read lands late and must be dropped, not replayed.
    reads[0].resolve(revisionOne);
    await drain();
    expect(revisionChip()).toContain("Revision 2");
    expect(firstSceneSlugline()).toBe("INT. TEST ROOM - DAY (SC-TWO)");
    expect(harness.api.getMovieStoryRevision).toHaveBeenCalledTimes(2);
  });
});
