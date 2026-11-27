import { readFileSync } from "node:fs";
import { act, type ReactNode } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { MovieCast, MovieCharacter, MovieWorldWorkspace } from "@/lib/api";

const harness = vi.hoisted(() => ({
  api: {
    getAsset: vi.fn(),
    getMovieCast: vi.fn(),
    getMovieWorld: vi.fn(),
    getMovieCharacterDetail: vi.fn(),
    updateMovieCharacter: vi.fn(),
    updateMovieLocation: vi.fn(),
    updateMovieProp: vi.fn(),
    lockMovieCharacterFact: vi.fn(),
    addMovieContinuityLock: vi.fn(),
  },
  locale: { t: (key: string) => key },
}));

vi.mock("@/components/LocaleProvider", () => ({ useLocale: () => harness.locale }));
vi.mock("@/lib/api", () => ({ api: harness.api }));
vi.mock("@/lib/apiBase", () => ({ assetFileUrl: (id: string) => `/assets/${id}` }));
vi.mock("next/link", () => ({ default: (props: { href: string; children?: ReactNode }) => <a href={props.href}>{props.children}</a> }));
vi.mock("lucide-react", () => {
  const Icon = (props: Record<string, unknown>) => <span {...props} />;
  return { AlertTriangle: Icon, ArrowUpRight: Icon, CheckCircle2: Icon, Image: Icon, LockKeyhole: Icon, MapPinned: Icon, Package: Icon, ShieldCheck: Icon, Sparkles: Icon, UsersRound: Icon };
});

import { kitRecordForTest, ProductionKitWorkspace } from "./ProductionKitWorkspace";

const recordCast = {
  project: {} as MovieCast["project"],
  characters: [{ id: "character-1", name: "Mara", role: "Lead", description: "A patient cartographer.", appearance: null, referenceAssetId: "asset-face", referenceAssetIds: ["asset-face"], referenceAssetCount: 1, stateCount: 1, latestState: null, relationshipCount: 0, relationshipTypes: [], lockedFactCount: 1, lockedFieldKeys: ["name"], updatedAt: "2026-09-30T00:00:00Z" }],
} as MovieCast;
const recordWorld = {
  movieProjectId: "movie-1", workspaceId: "workspace-1", projectId: null, status: "Draft", title: "North Star", description: "A film.", durationSeconds: 60, aspectRatio: "16:9", style: "cinematic", language: "en",
  world: { locations: [{ id: "location-1", name: "Harbor", description: "A cold harbor.", visualContinuityNotes: null, referenceAssetId: null }], sets: [], props: [{ id: "prop-1", name: "Compass", description: "A brass compass.", category: "Hero prop", continuityNotes: null, referenceAssetId: "asset-compass" }], references: [], usages: [], facts: [], locks: [{ id: "lock-1", entityType: "prop", entityId: "prop-1", fieldName: "name", lockedValue: "Compass", strength: "hard", reason: null, createdAt: "2026-09-30T00:00:00Z", releasedAt: null }] },
  usageDetails: [], assets: [],
} as MovieWorldWorkspace;

class TestNode {
  nodeType: number;
  nodeName: string;
  ownerDocument: TestDocument;
  parentNode: TestNode | null = null;
  childNodes: TestNode[] = [];
  attributes = new Map<string, string>();
  style: Record<string, string> = {};
  listeners = new Map<string, Set<(event: TestEvent) => void>>();
  disabled = false;
  selected = false;
  defaultSelected = false;
  hidden = false;
  private currentValue = "";
  constructor(nodeType: number, nodeName: string, ownerDocument: TestDocument) { this.nodeType = nodeType; this.nodeName = nodeName; this.ownerDocument = ownerDocument; }
  get firstChild() { return this.childNodes[0] ?? null; }
  get lastChild(): TestNode | null { return this.childNodes.at(-1) ?? null; }
  get options() { return this.childNodes; }
  get tagName() { return this.nodeName; }
  get textContent() { return this.childNodes.map((child) => child.textContent ?? "").join(""); }
  set textContent(value: string | null) { this.childNodes = value ? [new TestNodeText(value, this.ownerDocument)] : []; for (const child of this.childNodes) child.parentNode = this; }
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
  addEventListener(name: string, listener: (event: TestEvent) => void) { const listeners = this.listeners.get(name) ?? new Set(); listeners.add(listener); this.listeners.set(name, listeners); }
  removeEventListener(name: string, listener: (event: TestEvent) => void) { this.listeners.get(name)?.delete(listener); }
  dispatchEvent(event: TestEvent) {
    event.target ??= this;
    event.defaultPrevented ??= false;
    event.preventDefault ??= () => { event.defaultPrevented = true; };
    event.stopPropagation ??= () => { event.propagationStopped = true; };
    this.notify(event, this);
    return true;
  }
  private notify(event: TestEvent, node: TestNode) { event.currentTarget = node; for (const listener of node.listeners.get(event.type) ?? []) listener(event); if (!event.propagationStopped && node.parentNode) this.notify(event, node.parentNode); }
  contains(node: TestNode | null): boolean { return node === this || this.childNodes.some((child) => child.contains(node)); }
  compareDocumentPosition(node: TestNode) { return this.contains(node) ? 20 : 35; }
  focus() { this.ownerDocument.activeElement = this; }
  blur() { if (this.ownerDocument.activeElement === this) this.ownerDocument.activeElement = this.ownerDocument.body; }
}
type TestEvent = { type: string; bubbles?: boolean; target?: TestNode; currentTarget?: TestNode; defaultPrevented?: boolean; preventDefault?: () => void; stopPropagation?: () => void; propagationStopped?: boolean };
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
  defaultView: Record<string, unknown>;
  constructor() {
    super(9, "#document", null as unknown as TestDocument);
    this.ownerDocument = this;
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

type Deferred<T> = { promise: Promise<T>; resolve: (value: T) => void; reject: (error: unknown) => void };
function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}

function workspaceCast(workspaceId: string, lockedFactCount = 1): MovieCast {
  return {
    project: { id: `${workspaceId}-project`, workspaceId, projectId: null, mode: "Full", status: "Draft", title: `${workspaceId} film`, description: "A film.", durationSeconds: 60, aspectRatio: "16:9", style: "cinematic", language: "en", createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z" },
    characters: [{ id: `${workspaceId}-character`, name: `${workspaceId} character`, role: "Lead", description: `${workspaceId} description`, appearance: null, referenceAssetId: `${workspaceId}-asset`, referenceAssetIds: [`${workspaceId}-asset`], referenceAssetCount: 1, stateCount: 1, latestState: null, relationshipCount: 0, relationshipTypes: [], lockedFactCount, lockedFieldKeys: lockedFactCount ? ["name"] : [], updatedAt: "2026-01-01T00:00:00Z" }],
  } as MovieCast;
}
function workspaceDetail(workspaceId: string): MovieCharacter {
  return { id: `${workspaceId}-character`, name: `${workspaceId} character`, role: "Lead", description: `${workspaceId} description`, appearance: null, physicalDescription: null, wardrobe: null, voiceReference: null, personalityAndStoryNotes: null, voiceAndPerformance: null, continuityNotes: null, referenceAssetId: `${workspaceId}-asset`, referenceAssetIds: [`${workspaceId}-asset`], states: [], relationships: [], continuityLocks: [] } as unknown as MovieCharacter;
}
function workspaceWorld(workspaceId: string): MovieWorldWorkspace {
  return {
    movieProjectId: `${workspaceId}-project`, workspaceId, projectId: null, status: "Draft", title: `${workspaceId} film`, description: "A film.", durationSeconds: 60, aspectRatio: "16:9", style: "cinematic", language: "en",
    world: { locations: [{ id: `${workspaceId}-location-1`, name: "Harbor", description: "A cold harbor.", visualContinuityNotes: null, referenceAssetId: null }, { id: `${workspaceId}-location-2`, name: "Cliffside", description: "A quiet cliffside.", visualContinuityNotes: null, referenceAssetId: null }], sets: [], props: [], references: [], usages: [], facts: [], locks: [] },
    usageDetails: [], assets: [],
  } as MovieWorldWorkspace;
}

function workspaceCastWithTwoCharacters(workspaceId: string): MovieCast {
  const base = workspaceCast(workspaceId, 0);
  const template = base.characters[0];
  return {
    ...base,
    characters: [
      { ...template, id: `${workspaceId}-character-a`, name: "Alpha", description: "Alpha summary.", referenceAssetId: `${workspaceId}-asset-a`, referenceAssetIds: [`${workspaceId}-asset-a`], lockedFactCount: 0, lockedFieldKeys: [] },
      { ...template, id: `${workspaceId}-character-b`, name: "Bravo", description: "Bravo summary.", referenceAssetId: `${workspaceId}-asset-b`, referenceAssetIds: [`${workspaceId}-asset-b`], lockedFactCount: 0, lockedFieldKeys: [] },
    ],
  } as MovieCast;
}

function workspaceCharacterDetail(workspaceId: string, characterId: string, name: string, assetId: string): MovieCharacter {
  return { ...workspaceDetail(workspaceId), id: characterId, name, referenceAssetId: assetId, referenceAssetIds: [assetId] } as MovieCharacter;
}

function workspaceWorldWithImageAsset(workspaceId: string): MovieWorldWorkspace {
  return {
    ...workspaceWorld(workspaceId),
    assets: [{ id: "library-image", name: "Library portrait", assetType: "image", mimeType: "image/png", hasFile: true, canPreview: true } as MovieWorldWorkspace["assets"][number]],
  };
}

function workspaceWorldWithUnavailableReference(workspaceId: string): MovieWorldWorkspace {
  const base = workspaceWorld(workspaceId);
  const [firstLocation, ...otherLocations] = base.world.locations;
  return {
    ...base,
    world: { ...base.world, locations: [{ ...firstLocation, referenceAssetId: "pruned-private-image" }, ...otherLocations] },
    assets: [{ id: "pruned-private-image", name: "Pruned private portrait", assetType: "image", mimeType: "image/png", hasFile: false, canPreview: false } as MovieWorldWorkspace["assets"][number]],
  };
}

let testDocument: TestDocument;
let container: TestNode;
let root: Root | undefined;
function drain() { return new Promise((resolve) => setTimeout(resolve, 0)); }
function findClass(node: TestNode, className: string): TestNode[] { return [...(node.getAttribute("class")?.split(/\s+/).includes(className) ? [node] : []), ...node.childNodes.flatMap((child) => findClass(child, className))]; }
function findButtons(node: TestNode): TestNode[] { return [...(node.nodeName === "BUTTON" ? [node] : []), ...node.childNodes.flatMap(findButtons)]; }
function findSelects(node: TestNode): TestNode[] { return [...(node.nodeName === "SELECT" ? [node] : []), ...node.childNodes.flatMap(findSelects)]; }
function findImages(node: TestNode): TestNode[] { return [...(node.nodeName === "IMG" ? [node] : []), ...node.childNodes.flatMap(findImages)]; }
function selectedRecordName() { return findClass(container, "movie-kit-record").find((node) => node.getAttribute("class")?.includes("is-selected"))?.textContent ?? ""; }
function click(node: TestNode) { node.dispatchEvent({ type: "click", bubbles: true }); }
function choose(node: TestNode, value: string) { node.value = value; node.dispatchEvent({ type: "change", bubbles: true }); }
function locationTab() { return findButtons(container).find((node) => node.getAttribute("role") === "tab" && node.textContent?.includes("movieBody.world.locations")); }

beforeEach(() => {
  testDocument = new TestDocument();
  container = testDocument.createElement("div");
  testDocument.body.appendChild(container);
  root = undefined;
  const window = { document: testDocument, Element: TestNode, HTMLElement: TestNode, HTMLIFrameElement: TestNode, Node: TestNode, addEventListener: vi.fn(), removeEventListener: vi.fn(), setInterval: () => 0, clearInterval: () => {}, setTimeout: () => 0, clearTimeout: () => {} };
  testDocument.defaultView = window;
  vi.stubGlobal("document", testDocument);
  vi.stubGlobal("window", window);
  vi.stubGlobal("navigator", { userAgent: "test" });
  vi.stubGlobal("IS_REACT_ACT_ENVIRONMENT", true);
  Object.values(harness.api).forEach((mock) => mock.mockReset());
  harness.api.getAsset.mockImplementation(async (id: string) => ({ id, hasFile: false, canPreview: false }));
  harness.api.getMovieCast.mockResolvedValue(workspaceCast("default"));
  harness.api.getMovieWorld.mockResolvedValue(workspaceWorld("default"));
  harness.api.getMovieCharacterDetail.mockImplementation((id: string) => Promise.resolve({ character: workspaceDetail(id.replace(/-character$/, "")) }));
  harness.api.updateMovieCharacter.mockResolvedValue(undefined);
  harness.api.updateMovieLocation.mockResolvedValue(undefined);
  harness.api.updateMovieProp.mockResolvedValue(undefined);
  harness.api.lockMovieCharacterFact.mockResolvedValue(undefined);
  harness.api.addMovieContinuityLock.mockResolvedValue(undefined);
});

afterEach(() => {
  if (root) act(() => root?.unmount());
  root = undefined;
  vi.unstubAllGlobals();
});

async function mount(projectId: string) {
  await act(async () => {
    root = createRoot(container as unknown as Element);
    root.render(<ProductionKitWorkspace projectId={projectId} />);
    await drain();
    await drain();
  });
}

describe("Production Kit workspace", () => {
  it("derives readiness from references and persisted locks", () => {
    expect(kitRecordForTest("characters", recordCast, recordWorld)[0].state).toBe("ready");
    expect(kitRecordForTest("locations", recordCast, recordWorld)[0].state).toBe("needs-reference");
    expect(kitRecordForTest("props", recordCast, recordWorld)[0].state).toBe("ready");
  });

  it("keeps the three-room hierarchy and safe user-facing boundaries explicit", () => {
    const source = readFileSync(new URL("./ProductionKitWorkspace.tsx", import.meta.url), "utf8");
    expect(source).toContain('id: "characters"');
    expect(source).toContain('id: "locations"');
    expect(source).toContain('id: "props"');
    expect(source).toContain("Needs reference");
    expect(source).toContain("movieBody.kit.approveIdentity");
    expect(source).toContain("movieBody.world.openLibrary");
    expect(source).not.toContain("providerName");
    expect(source).not.toContain("modelName");
    expect(source).not.toContain("promptName");
  });

  it("does not preview a private world reference after its file is unavailable", async () => {
    const workspaceId = "workspace-private-preview";
    harness.api.getMovieCast.mockResolvedValue(workspaceCast(workspaceId));
    harness.api.getMovieWorld.mockResolvedValue(workspaceWorldWithUnavailableReference(workspaceId));

    await mount(workspaceId);
    const tab = locationTab();
    expect(tab).toBeDefined();
    await act(async () => { click(tab!); await drain(); });

    expect(findImages(container)).toHaveLength(0);
    expect(container.textContent).toContain("assets.fileUnavailable");
  });

  it("does not let a delayed previous-project load overwrite the newly selected project", async () => {
    const castA = deferred<MovieCast>();
    const worldA = deferred<MovieWorldWorkspace>();
    const castB = deferred<MovieCast>();
    const worldB = deferred<MovieWorldWorkspace>();
    harness.api.getMovieCast.mockImplementation((id: string) => id === "workspace-a" ? castA.promise : castB.promise);
    harness.api.getMovieWorld.mockImplementation((id: string) => id === "workspace-a" ? worldA.promise : worldB.promise);

    await act(async () => { root = createRoot(container as unknown as Element); root.render(<ProductionKitWorkspace projectId="workspace-a" />); await drain(); });
    await act(async () => { root?.render(<ProductionKitWorkspace projectId="workspace-b" />); await drain(); });
    expect(harness.api.getMovieCast).toHaveBeenCalledWith("workspace-a");
    expect(harness.api.getMovieCast).toHaveBeenCalledWith("workspace-b");

    await act(async () => { castB.resolve(workspaceCast("workspace-b")); worldB.resolve(workspaceWorld("workspace-b")); await drain(); await drain(); });
    expect(container.textContent).toContain("workspace-b character");
    await act(async () => { castA.resolve(workspaceCast("workspace-a")); worldA.resolve(workspaceWorld("workspace-a")); await drain(); await drain(); });

    expect(container.textContent).toContain("workspace-b character");
    expect(container.textContent).not.toContain("workspace-a character");
  });

  it("hides a loaded project immediately and keeps it hidden when the next project's load fails", async () => {
    const castB = deferred<MovieCast>();
    const worldB = deferred<MovieWorldWorkspace>();
    harness.api.getMovieCast.mockImplementation((id: string) => id === "workspace-b" ? castB.promise : Promise.resolve(workspaceCast(id)));
    harness.api.getMovieWorld.mockImplementation((id: string) => id === "workspace-b" ? worldB.promise : Promise.resolve(workspaceWorld(id)));

    await mount("workspace-a");
    expect(container.textContent).toContain("workspace-a character");
    await act(async () => { root?.render(<ProductionKitWorkspace projectId="workspace-b" />); await drain(); });
    expect(container.textContent).not.toContain("workspace-a");
    expect(container.textContent).toContain("movieBody.loading");

    castB.reject(new Error("Project B could not be loaded."));
    worldB.resolve(workspaceWorld("workspace-b"));
    await act(async () => { await drain(); await drain(); });
    expect(container.textContent).not.toContain("workspace-a");
    expect(container.textContent).toContain("Project B could not be loaded.");
    expect(findClass(container, "movie-kit-approve")).toHaveLength(0);
  });

  it("approves the selected character rather than reusing the previous character's detail", async () => {
    const workspaceId = "workspace-selection";
    const characterAId = `${workspaceId}-character-a`;
    const characterBId = `${workspaceId}-character-b`;
    const detailB = deferred<{ character: MovieCharacter }>();
    harness.api.getMovieCast.mockResolvedValue(workspaceCastWithTwoCharacters(workspaceId));
    harness.api.getMovieWorld.mockResolvedValue(workspaceWorld(workspaceId));
    harness.api.getMovieCharacterDetail.mockImplementation((id: string) => id === characterAId
      ? Promise.resolve({ character: workspaceCharacterDetail(workspaceId, characterAId, "Alpha canonical", `${workspaceId}-asset-a`) })
      : detailB.promise);

    await mount(workspaceId);
    const bravo = findClass(container, "movie-kit-record").find((node) => node.textContent?.includes("Bravo"));
    expect(bravo).toBeDefined();
    await act(async () => { click(bravo!); await drain(); });
    await act(async () => { click(findClass(container, "movie-kit-approve")[0]); await drain(); });
    expect(selectedRecordName()).toContain("Bravo");

    detailB.resolve({ character: workspaceCharacterDetail(workspaceId, characterBId, "Bravo canonical", `${workspaceId}-asset-b`) });
    await act(async () => { await drain(); await drain(); await drain(); });
    expect(harness.api.lockMovieCharacterFact).toHaveBeenCalledWith(characterBId, { fieldKey: "name", lockedValue: "Bravo canonical" });
    expect(harness.api.lockMovieCharacterFact).not.toHaveBeenCalledWith(characterAId, expect.anything());
  });

  it("attaches a reference to the selected character rather than a previously loaded character", async () => {
    const workspaceId = "workspace-reference-selection";
    const characterAId = `${workspaceId}-character-a`;
    const characterBId = `${workspaceId}-character-b`;
    const detailB = deferred<{ character: MovieCharacter }>();
    harness.api.getMovieCast.mockResolvedValue(workspaceCastWithTwoCharacters(workspaceId));
    harness.api.getMovieWorld.mockResolvedValue(workspaceWorldWithImageAsset(workspaceId));
    harness.api.getMovieCharacterDetail.mockImplementation((id: string) => id === characterAId
      ? Promise.resolve({ character: workspaceCharacterDetail(workspaceId, characterAId, "Alpha canonical", `${workspaceId}-asset-a`) })
      : detailB.promise);

    await mount(workspaceId);
    const bravo = findClass(container, "movie-kit-record").find((node) => node.textContent?.includes("Bravo"));
    expect(bravo).toBeDefined();
    await act(async () => { click(bravo!); await drain(); });
    const imagePicker = findSelects(container)[0];
    expect(imagePicker).toBeDefined();
    await act(async () => { choose(imagePicker, "library-image"); await drain(); });

    detailB.resolve({ character: workspaceCharacterDetail(workspaceId, characterBId, "Bravo canonical", `${workspaceId}-asset-b`) });
    await act(async () => { await drain(); await drain(); await drain(); });
    expect(harness.api.updateMovieCharacter).toHaveBeenCalledWith(characterBId, expect.objectContaining({ name: "Bravo canonical", referenceAssetIds: [`${workspaceId}-asset-b`, "library-image"] }));
    expect(harness.api.updateMovieCharacter).not.toHaveBeenCalledWith(characterAId, expect.anything());
  });

  it("does not let a failed write response from the old project overwrite the new project", async () => {
    const write = deferred<void>();
    harness.api.getMovieCast.mockImplementation((id: string) => Promise.resolve(workspaceCast(id, 0)));
    harness.api.getMovieWorld.mockImplementation((id: string) => Promise.resolve(workspaceWorld(id)));
    harness.api.getMovieCharacterDetail.mockImplementation((id: string) => Promise.resolve({ character: workspaceDetail(id.replace(/-character$/, "")) }));
    harness.api.lockMovieCharacterFact.mockReturnValue(write.promise);

    await mount("workspace-write-a");
    await act(async () => { click(findClass(container, "movie-kit-approve")[0]); await drain(); });
    expect(harness.api.lockMovieCharacterFact).toHaveBeenCalledWith("workspace-write-a-character", expect.anything());
    await act(async () => { root?.render(<ProductionKitWorkspace projectId="workspace-write-b" />); await drain(); await drain(); });
    expect(container.textContent).toContain("workspace-write-b character");

    write.reject(new Error("Stale project write failed."));
    await act(async () => { await drain(); await drain(); });
    expect(container.textContent).toContain("workspace-write-b character");
    expect(container.textContent).not.toContain("Stale project write failed.");
  });

  it("keeps the selected location when a slower refresh from the previous room finishes", async () => {
    const initialCast = workspaceCast("workspace-room", 0);
    const initialWorld = workspaceWorld("workspace-room");
    const refreshCast = deferred<MovieCast>();
    const refreshWorld = deferred<MovieWorldWorkspace>();
    harness.api.getMovieCast.mockResolvedValueOnce(initialCast).mockReturnValueOnce(refreshCast.promise);
    harness.api.getMovieWorld.mockResolvedValueOnce(initialWorld).mockReturnValueOnce(refreshWorld.promise);

    await mount("workspace-room");
    expect(findClass(container, "movie-kit-approve")).toHaveLength(1);
    const approve = findClass(container, "movie-kit-approve")[0];
    await act(async () => { click(approve); await drain(); await drain(); });
    expect(harness.api.getMovieWorld).toHaveBeenCalledTimes(2);

    const tab = locationTab();
    expect(tab).toBeDefined();
    await act(async () => { click(tab!); await drain(); });
    const secondLocation = findClass(container, "movie-kit-record").find((node) => node.textContent?.includes("Cliffside"));
    expect(secondLocation).toBeDefined();
    await act(async () => { click(secondLocation!); await drain(); });
    expect(selectedRecordName()).toContain("Cliffside");

    await act(async () => { refreshCast.resolve(initialCast); refreshWorld.resolve(initialWorld); await drain(); await drain(); });
    expect(selectedRecordName()).toContain("Cliffside");
  });

  it.each(["write", "refresh"] as const)("keeps a completed approval when switching rooms during the %s", async (pending) => {
    const projectId = "workspace-approval";
    const write = deferred<void>();
    const refreshedCast = deferred<MovieCast>();
    harness.api.getMovieCast.mockResolvedValueOnce(workspaceCast(projectId, 0)).mockReturnValueOnce(refreshedCast.promise);
    harness.api.getMovieWorld.mockResolvedValue(workspaceWorld(projectId));
    harness.api.lockMovieCharacterFact.mockReturnValue(write.promise);

    await mount(projectId);
    const approve = findClass(container, "movie-kit-approve")[0];
    expect(approve.getAttribute("disabled")).toBeNull();
    await act(async () => { click(approve); await drain(); });
    expect(harness.api.lockMovieCharacterFact).toHaveBeenCalledTimes(1);
    if (pending === "refresh") {
      await act(async () => { write.resolve(); await drain(); });
      expect(harness.api.getMovieCast).toHaveBeenCalledTimes(2);
    }
    await act(async () => { click(locationTab()!); await drain(); });
    if (pending === "write") await act(async () => { write.resolve(); await drain(); });
    await act(async () => { refreshedCast.resolve(workspaceCast(projectId, 1)); await drain(); });
    expect(selectedRecordName()).toContain("Harbor");

    const characters = findButtons(container).find((node) => node.getAttribute("role") === "tab" && node.textContent?.includes("movieModule.cast.label"));
    expect(characters).toBeDefined();
    await act(async () => { click(characters!); await drain(); });
    expect(findClass(container, "movie-kit-approve")[0].getAttribute("disabled")).not.toBeNull();
  });
});
