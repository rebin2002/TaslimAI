import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { MovieCast, MovieWorldAsset, MovieWorldWorkspace } from "@/lib/api";
import { assetFileUrl } from "@/lib/apiBase";

// Synthetic DOM harness, in the style established by AssetsViewListing.test.tsx and
// ActivityBell.badgeFence.test.tsx: no jsdom, no network, no provider call. The real
// ProductionKitWorkspace is mounted, so the assertions describe what a producer actually sees in
// the Movie Studio Production Kit. The existing ProductionKitWorkspace.test.tsx covers the pure
// record derivation (`kitRecordForTest`) and greps the source for structural markers; neither
// reaches the inspector, so the reference preview element itself has no owner today.
//
// The seam #1176/#1177 leave open. Their API fix makes `MovieStudioService.GetWorldAsync` report
// `hasFile`/`canPreview` from the stored file's status, so a retention-pruned reference asset
// (file `Deleted`, `Asset.StoredFileId` deliberately left in place, download answering
// `409 ASSET_FILE_UNAVAILABLE`) is no longer advertised. This consumer does not follow:
//
//   const imageAssets = world.assets.filter((asset) => asset.canPreview && ...);   // chooser only
//   {selectedAssetId ? <img src={assetFileUrl(selectedAssetId, true)} .../>        // preview: identity alone
//
// `selectedAsset` is resolved from the projection but consulted only for its name, so once #1177
// lands the chooser self-heals while the ALREADY-SELECTED asset keeps painting an <img> at a URL
// the read model has just told this component will fail. These cases drive that
// selected -> pruned transition in the rendered component. Case 4 pins the chooser filter as
// insufficient on its own; cases 5 and 6 pin the two over-corrections a fix must not introduce.
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
  defaultSelected = false;
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
  compareDocumentPosition(node: TestNode) { return this.contains(node) ? 20 : 35; }
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
  defaultView: Record<string, unknown>;
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

const harness = vi.hoisted(() => ({
  api: {
    getMovieCast: vi.fn(),
    getMovieWorld: vi.fn(),
    getMovieCharacterDetail: vi.fn(),
    updateMovieCharacter: vi.fn(),
    updateMovieLocation: vi.fn(),
    updateMovieProp: vi.fn(),
    lockMovieCharacterFact: vi.fn(),
    addMovieContinuityLock: vi.fn(),
  },
  // A fresh `t` per useLocale() call would re-fire the project-keyed load effect on every render,
  // so the mount would never settle; the context identity has to be stable.
  localeContext: {
    t: (key: string, variables?: Record<string, string>) => Object.entries(variables ?? {}).reduce((value, [name, replacement]) => value.replace(`{${name}}`, replacement), key),
  },
}));

vi.mock("@/components/LocaleProvider", () => ({ useLocale: () => harness.localeContext }));
vi.mock("@/lib/api", () => ({ api: harness.api }));
vi.mock("next/link", () => ({ default: (p: { href: string; children?: unknown }) => <a href={p.href}>{p.children as never}</a> }));

import { ProductionKitWorkspace } from "./ProductionKitWorkspace";

const PROJECT_ID = "movie-1";
const PROP_ID = "prop-compass";
const ASSET_ID = "asset-compass";
const FACE_ID = "asset-face";
// The exact URL whose route answers `409 ASSET_FILE_UNAVAILABLE` once the stored file is pruned.
// The defect is a network request, so every assertion below keys off the rendered `src`.
const PREVIEW_URL = assetFileUrl(ASSET_ID, true);
const FACE_URL = assetFileUrl(FACE_ID, true);

// Synthetic fixtures only: no production record, no secret, no provider call, no customer data.
function readyAsset(): MovieWorldAsset {
  return { id: ASSET_ID, name: "Compass reference", assetType: "image", mimeType: "image/png", hasFile: true, canPreview: true };
}
// The row a retention prune leaves behind: the asset identity and the record's `referenceAssetId`
// survive, the stored file no longer serves bytes. This is what #1177 makes the read model say.
function prunedAsset(): MovieWorldAsset {
  return { id: ASSET_ID, name: "Compass reference", assetType: "image", mimeType: "image/png", hasFile: false, canPreview: false };
}

function makeWorld(asset: MovieWorldAsset | null, locked: boolean): MovieWorldWorkspace {
  return {
    movieProjectId: PROJECT_ID, workspaceId: "workspace-1", projectId: null, status: "Draft", title: "North Star",
    description: "A film.", durationSeconds: 60, aspectRatio: "16:9", style: "cinematic", language: "en",
    world: {
      locations: [], sets: [],
      props: [{ id: PROP_ID, name: "Compass", description: "A brass compass.", category: "Hero prop", continuityNotes: null, referenceAssetId: ASSET_ID }],
      references: [], usages: [], facts: [],
      locks: locked ? [{ id: "lock-1", entityType: "prop", entityId: PROP_ID, fieldName: "name", lockedValue: "Compass", strength: "hard", reason: null, createdAt: "2026-11-18T00:00:00Z", releasedAt: null }] : [],
    },
    usageDetails: [],
    assets: asset ? [asset] : [],
  } as unknown as MovieWorldWorkspace;
}

const emptyCast = { project: {}, characters: [] } as unknown as MovieCast;

// The characters room is the deliberate boundary of this seam. `GetWorldAsync` builds `assets`
// from location, set, prop and world-reference ids only, so a *character* reference never has a
// row there: absence is not evidence of removal, and a fix that hid the preview whenever it could
// not see a row would take every character reference with it. Case 6 pins that.
const characterCast = {
  project: {},
  characters: [{
    id: "char-1", name: "Mara", role: "Lead", description: "A patient cartographer.", appearance: null,
    referenceAssetId: FACE_ID, referenceAssetIds: [FACE_ID], referenceAssetCount: 1, stateCount: 1,
    latestState: null, relationshipCount: 0, relationshipTypes: [], lockedFactCount: 1, lockedFieldKeys: ["name"],
    updatedAt: "2026-11-18T00:00:00Z",
  }],
} as unknown as MovieCast;

let document: TestDocument;
let container: TestNode;
let root: Root;
let worldReads: Deferred<MovieWorldWorkspace>[];

function drain() { return new Promise((resolve) => setTimeout(resolve, 0)); }

function findClass(node: TestNode, className: string): TestNode[] {
  return [
    ...(node.getAttribute("class")?.split(/\s+/).includes(className) ? [node] : []),
    ...node.childNodes.flatMap((child) => findClass(child, className)),
  ];
}

function findTag(node: TestNode, nodeName: string): TestNode[] {
  return [
    ...(node.nodeName === nodeName ? [node] : []),
    ...node.childNodes.flatMap((child) => findTag(child, nodeName)),
  ];
}

/** Every <img> src the workspace has asked the browser to fetch. An entry here is a GET. */
function previewRequests(): string[] {
  return findTag(container, "IMG").map((node) => node.getAttribute("src") ?? "");
}

function renderedText(): string { return container.textContent ?? ""; }

/** Mounts the Production Kit and settles its first world read. */
async function mountKit(asset: MovieWorldAsset | null, cast: MovieCast = emptyCast) {
  harness.api.getMovieCast.mockResolvedValue(cast);
  worldReads[0].resolve(makeWorld(asset, false));
  await act(async () => {
    root = createRoot(container as unknown as Element);
    root.render(<ProductionKitWorkspace projectId={PROJECT_ID} />);
    await drain();
    await drain();
  });
}

/** Opens the props room and selects the prop, making its reference the selected asset. */
async function selectPropRecord() {
  const propsTab = findTag(container, "BUTTON").find((node) => node.getAttribute("role") === "tab" && node.textContent?.includes("propsHint"));
  if (!propsTab) throw new Error("The props room tab never rendered.");
  await act(async () => { propsTab.dispatchEvent({ type: "click", bubbles: true }); await drain(); });
  const row = findClass(container, "movie-kit-record")[0];
  if (!row) throw new Error("No Production Kit record rendered in the props room.");
  await act(async () => { row.dispatchEvent({ type: "click", bubbles: true }); await drain(); });
}

/**
 * Drives the selected -> pruned transition through the rendered component. Approving the prop's
 * identity is a real user action on this card, and its handler ends with `load(room, selected.id)`,
 * so the *same live selection* is re-read and the second response is the one delivered here. No
 * unmount, no tab re-entry, no remount: the prune lands between two reads of one selection.
 */
async function reReadSelection(nextWorld: MovieWorldWorkspace) {
  const approve = findClass(container, "movie-kit-approve")[0];
  if (!approve) throw new Error("The approve-identity control never rendered, so the re-read cannot be driven.");
  await act(async () => {
    approve.dispatchEvent({ type: "click", bubbles: true });
    await drain();
  });
  worldReads[1].resolve(nextWorld);
  await act(async () => { await drain(); });
}

beforeEach(() => {
  document = new TestDocument();
  container = document.createElement("div");
  document.body.appendChild(container);
  worldReads = [deferred<MovieWorldWorkspace>(), deferred<MovieWorldWorkspace>(), deferred<MovieWorldWorkspace>()];
  const window = {
    document, Element: TestNode, HTMLElement: TestNode, HTMLIFrameElement: TestNode, Node: TestNode,
    addEventListener: vi.fn(), removeEventListener: vi.fn(),
    setInterval: () => 0, clearInterval: () => {}, setTimeout: () => 0, clearTimeout: () => {},
  };
  // React resolves the container's ownerDocument.defaultView and tests the active element against
  // `HTMLIFrameElement` there, so the window object has to be wired back into this document.
  document.defaultView = window;
  vi.stubGlobal("document", document);
  vi.stubGlobal("window", window);
  vi.stubGlobal("navigator", { userAgent: "test" });
  vi.stubGlobal("IS_REACT_ACT_ENVIRONMENT", true);

  let read = 0;
  harness.api.getMovieCast.mockReset().mockResolvedValue(emptyCast);
  harness.api.getMovieWorld.mockReset().mockImplementation(() => worldReads[read++].promise);
  harness.api.getMovieCharacterDetail.mockReset().mockResolvedValue({
    project: {},
    character: { id: "char-1", name: "Mara", role: "Lead", description: "A patient cartographer.", referenceAssetId: FACE_ID, referenceAssetIds: [FACE_ID], continuityLocks: [] },
  });
  harness.api.addMovieContinuityLock.mockReset().mockResolvedValue(undefined);
  harness.api.updateMovieProp.mockReset().mockResolvedValue(undefined);
});

afterEach(() => {
  act(() => root?.unmount());
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

describe("Production Kit reference preview after a retention prune", () => {
  it("paints the selected reference while the world read model still advertises it", async () => {
    // Baseline pin: the fixture really reaches the preview element, so a failure below cannot be
    // read as a harness artifact.
    await mountKit(readyAsset());
    await selectPropRecord();

    expect(previewRequests()).toContain(PREVIEW_URL);
    expect(findClass(container, "movie-kit-reference-preview")).toHaveLength(1);
    expect(renderedText()).toContain("Compass reference");
  });

  it("stops requesting the already-selected preview once the same read reports it pruned", async () => {
    await mountKit(readyAsset());
    await selectPropRecord();
    expect(previewRequests()).toContain(PREVIEW_URL);

    await reReadSelection(makeWorld(prunedAsset(), true));

    expect(previewRequests(), `the workspace still fetches ${PREVIEW_URL}, which the download route answers with 409 ASSET_FILE_UNAVAILABLE`).not.toContain(PREVIEW_URL);
    expect(findClass(container, "movie-kit-reference-preview")).toHaveLength(0);
    // The room stays usable: the record, its identity control and the chooser all remain mounted.
    expect(renderedText()).toContain("Compass");
    expect(findClass(container, "movie-kit-approve")).toHaveLength(1);
    expect(findClass(container, "movie-kit-reference-actions")).toHaveLength(1);
  });

  it("withholds the preview once the world room stops describing the selected reference at all", async () => {
    // The same dead-end in its stronger shape: no row in the projection describes the asset that
    // the selected prop still points at.
    await mountKit(readyAsset());
    await selectPropRecord();
    expect(previewRequests()).toContain(PREVIEW_URL);

    await reReadSelection(makeWorld(null, true));

    expect(previewRequests()).not.toContain(PREVIEW_URL);
    expect(findClass(container, "movie-kit-reference-preview")).toHaveLength(0);
  });

  it("does not treat the chooser filter as sufficient", async () => {
    // The chooser already omits an unavailable asset, because `imageAssets` filters on
    // `canPreview`. That is #1177's API change reaching the option list, and it is pinned here as
    // NOT the fix: a reference that is already attached never passes through that filter on its
    // way back onto the screen.
    await mountKit(prunedAsset());
    await selectPropRecord();

    const offered = findTag(container, "SELECT").flatMap((select) => select.childNodes.map((option) => option.getAttribute("value") ?? ""));
    expect(offered, "an unavailable asset must not be offered as a new reference").not.toContain(ASSET_ID);
    expect(previewRequests(), "filtering the chooser does not stop the selected preview from being fetched").not.toContain(PREVIEW_URL);
  });

  it("keeps a ready reference previewable when only another asset was pruned", async () => {
    // Over-correction guard one: the selected asset is advertised Ready, so its inline URL is a
    // valid request and must still be made even though the same read prunes a sibling asset.
    const world = makeWorld(readyAsset(), true);
    world.assets = [readyAsset(), { ...prunedAsset(), id: "asset-other", name: "Harbor plate" }];
    await mountKit(readyAsset());
    await selectPropRecord();
    expect(previewRequests()).toContain(PREVIEW_URL);

    await reReadSelection(world);

    expect(previewRequests()).toContain(PREVIEW_URL);
    expect(findClass(container, "movie-kit-reference-preview")).toHaveLength(1);
  });

  it("leaves the characters room previewing a reference the world projection does not carry", async () => {
    // Over-correction guard two: no availability signal exists for character references at all,
    // so this must keep working exactly as it does today. The guard is "the read model says it is
    // gone in a room it describes", never "the read model is silent".
    await mountKit(null, characterCast);

    expect(previewRequests()).toContain(FACE_URL);
    expect(findClass(container, "movie-kit-reference-preview")).toHaveLength(1);
  });
});
