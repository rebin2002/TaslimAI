import { act, type ReactElement } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
// ---- synthetic DOM harness, in the style of FullMovieWorkspaceView.storyAutosave ----
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
    getMovieCast: vi.fn(),
    getMovieCharacterDetail: vi.fn(),
    listAssets: vi.fn(),
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
// Only the cast room's own module graph is exercised; sibling workspaces are stubbed.
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

import { CastModule } from "./FullMovieWorkspaceView";

const ISO = "2026-11-17T00:00:00.000Z";

type CastEntryFixture = {
  id: string;
  name: string;
  role: string | null;
  description: string;
  appearance: string | null;
  referenceAssetId: string | null;
  referenceAssetIds: string[];
  referenceAssetCount: number;
  stateCount: number;
  latestState: { id: string; key: string; label: string | null; wardrobe: string | null; ageOrTimeState: string | null; appearance: string | null; injuryOrCondition: string | null; locationOrStoryState: string | null; continuityNotes: string | null; createdAt: string; updatedAt: string } | null;
  relationshipCount: number;
  relationshipTypes: string[];
  lockedFactCount: number;
  lockedFieldKeys: string[];
  updatedAt: string;
};

function castEntry(id: string, name: string, role: string): CastEntryFixture {
  return {
    id,
    name,
    role,
    description: `${name} story function`,
    appearance: null,
    referenceAssetId: null,
    referenceAssetIds: [],
    referenceAssetCount: 0,
    stateCount: 1,
    latestState: { id: `state-${id}`, key: "current", label: `${name} current`, wardrobe: null, ageOrTimeState: null, appearance: null, injuryOrCondition: null, locationOrStoryState: null, continuityNotes: null, createdAt: ISO, updatedAt: ISO },
    relationshipCount: 0,
    relationshipTypes: [],
    lockedFactCount: 0,
    lockedFieldKeys: [],
    updatedAt: ISO,
  };
}

function castFixture(entries: CastEntryFixture[]) {
  return {
    project: {
      id: "project-a",
      workspaceId: "workspace-a",
      projectId: null,
      mode: "Full",
      status: "Active",
      title: "Test feature",
      description: "",
      durationSeconds: 120,
      aspectRatio: "16:9",
      style: "live action",
      language: "en",
      createdAt: ISO,
      updatedAt: ISO,
    },
    characters: entries,
  };
}

function characterDetail(id: string, name: string) {
  return {
    project: castFixture([]).project,
    character: {
      id,
      name,
      role: "Protagonist",
      description: `${name} story function`,
      appearance: null,
      physicalDescription: null,
      wardrobe: null,
      voiceReference: null,
      personalityAndStoryNotes: null,
      voiceAndPerformance: null,
      continuityNotes: null,
      referenceAssetId: null,
      referenceAssetIds: [],
      states: [],
      relationships: [],
      continuityLocks: [],
      productionSheet: null,
    },
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
  harness.api.getMovieCast.mockReset();
  harness.api.getMovieCharacterDetail.mockReset();
  harness.api.listAssets.mockReset();
  harness.api.listAssets.mockResolvedValue({ items: [] });
});

afterEach(() => {
  act(() => root?.unmount());
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

/** Let queued microtasks (promise continuations) run inside act. */
function drain() {
  return act(async () => {
    vi.advanceTimersByTime(0);
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

function mount(module: ReactElement) {
  act(() => {
    root = createRoot(container as unknown as Element);
    root.render(module);
  });
}

function rosterGrid(): TestNode {
  const grid = findByClass(container, "movie-cast-card-grid")[0];
  if (!grid) throw new Error("Roster grid is not rendered");
  return grid;
}

function rosterCards(): TestNode[] {
  return findByClass(rosterGrid(), "movie-cast-card");
}

describe("Movie Studio cast roster selection semantics", () => {
  async function mountRoster() {
    harness.api.getMovieCast.mockResolvedValue(castFixture([castEntry("char-a", "Aria", "Protagonist"), castEntry("char-b", "Bram", "Antagonist")]));
    harness.api.getMovieCharacterDetail.mockImplementation(async (id: string) => {
      const names: Record<string, string> = { "char-a": "Aria", "char-b": "Bram" };
      return characterDetail(id, names[id] ?? id);
    });
    mount(<CastModule projectId="project-a" />);
    await drain();
  }

  it("keeps every roster entry a native button so Tab and Enter/Space drive selection", async () => {
    await mountRoster();
    const cards = rosterCards();
    expect(cards.map((card) => card.nodeName)).toEqual(["BUTTON", "BUTTON"]);
    // A button element is natively focusable and keyboard-activatable; anything
    // downgraded to a div/click target would break the keyboard path.
    expect(cards.map((card) => card.getAttribute("type"))).toEqual(["button", "button"]);
  });

  it("labels the roster group so the card set is announced as one control context", async () => {
    await mountRoster();
    const grid = rosterGrid();
    expect(grid.getAttribute("role")).toBe("group");
    expect(grid.getAttribute("aria-label")).toBeTruthy();
  });

  it("exposes which roster entry is selected through aria-pressed, not colour alone", async () => {
    await mountRoster();
    const cards = rosterCards();
    expect(cards[0]?.getAttribute("aria-pressed")).toBe("true");
    expect(cards[1]?.getAttribute("aria-pressed")).toBe("false");
    expect(cards[0]?.className).toContain("is-selected");
  });

  it("moves the announced selection when the keyboard activates another roster entry", async () => {
    await mountRoster();
    const cards = rosterCards();
    await click(cards[1]!);
    const after = rosterCards();
    expect(after[1]?.getAttribute("aria-pressed")).toBe("true");
    expect(after[0]?.getAttribute("aria-pressed")).toBe("false");
    expect(after[1]?.className).toContain("is-selected");
    expect(after[0]?.className).not.toContain("is-selected");
    // The visual selection and the announced selection must not diverge: the
    // detail pane loaded for the same character the card reports as pressed.
    expect(harness.api.getMovieCharacterDetail).toHaveBeenCalledWith("char-b");
  });
});
