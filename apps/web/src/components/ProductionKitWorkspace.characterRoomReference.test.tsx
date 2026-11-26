import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { MovieCast, MovieCharacterDetail, MovieWorldAsset, MovieWorldWorkspace } from "@/lib/api";
import { assetFileUrl } from "@/lib/apiBase";

// Mount the real Production Kit with independent world, character and Asset Library fixtures.
// The character models expose reference IDs. File readiness and visibility come from the
// authorized asset-detail request when the world has no row for the selected character.
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
const CHARACTER_ID = "char-1";
const FACE_ID = "asset-face";
const COMPASS_ID = "asset-compass";
const PROP_ID = "prop-compass";
const FACE_URL = assetFileUrl(FACE_ID, true);

// These synthetic download responses match AssetService and AssetsController for the
// fixture's visible files. A missing asset returns 404 and a non-Ready file returns 409.
type StoredFileStatus = "Uploading" | "Ready" | "Processing" | "Failed" | "Deleted";
type RouteAnswer = "200 bytes" | "409 ASSET_FILE_UNAVAILABLE" | "404 ASSET_NOT_FOUND";

// Absent key  -> the asset row itself is gone, so the download route has nothing to authorise.
// Null value  -> the asset exists but carries no stored-file row (or its id is dangling).
// Status      -> `StoredFile.Status`, of which only `Ready` serves.
const assetLibrary = new Map<string, StoredFileStatus | null>();

function routeAnswerFor(assetId: string): RouteAnswer {
  if (!assetLibrary.has(assetId)) return "404 ASSET_NOT_FOUND";
  return assetLibrary.get(assetId) === "Ready" ? "200 bytes" : "409 ASSET_FILE_UNAVAILABLE";
}

function makeWorld(assets: MovieWorldAsset[], locked: boolean): MovieWorldWorkspace {
  return {
    movieProjectId: PROJECT_ID, workspaceId: "workspace-1", projectId: null, status: "Draft", title: "North Star",
    description: "A film.", durationSeconds: 60, aspectRatio: "16:9", style: "cinematic", language: "en",
    world: {
      locations: [], sets: [],
      // A prop is only ever present in the cases that need one: its reference id is what puts an
      // asset row into the world projection in the first place.
      props: locked || assets.length ? [{ id: PROP_ID, name: "Compass", description: "A brass compass.", category: "Hero prop", continuityNotes: null, referenceAssetId: COMPASS_ID }] : [],
      references: [], usages: [], facts: [],
      locks: locked ? [{ id: "lock-1", entityType: "prop", entityId: PROP_ID, fieldName: "name", lockedValue: "Compass", strength: "hard", reason: null, createdAt: "2026-11-18T00:00:00Z", releasedAt: null }] : [],
    },
    usageDetails: [],
    assets,
  } as unknown as MovieWorldWorkspace;
}

// A character payload in exactly the shape `MovieCastCharacter` delivers. `lockedFactCount: 0`
// makes the record `needs-approval`, which is what enables the control this file drives its
// re-read through. Note what is *not* here: no `hasFile`, no `canPreview`, no media type, no
// stored-file status. The component must resolve readiness using the asset endpoint.
function makeCast(lockedFactCount: number, characters: Array<{ id: string; referenceAssetId: string | null }> = [{ id: CHARACTER_ID, referenceAssetId: FACE_ID }]): MovieCast {
  return {
    project: {},
    characters: characters.map((character) => ({
      ...character, name: "Mara", role: "Lead", description: "A patient cartographer.", appearance: null,
      referenceAssetIds: [character.referenceAssetId].filter((id): id is string => id !== null),
      referenceAssetCount: character.referenceAssetId ? 1 : 0, stateCount: 1, latestState: null,
      relationshipCount: 0, relationshipTypes: [], lockedFactCount, lockedFieldKeys: lockedFactCount ? ["name"] : [],
      updatedAt: "2026-11-18T00:00:00Z",
    })),
  } as unknown as MovieCast;
}

function makeDetail(lockedFactCount: number, referenceAssetId: string | null = FACE_ID): MovieCharacterDetail {
  return {
    project: {},
    character: {
      id: CHARACTER_ID, name: "Mara", role: "Lead", description: "A patient cartographer.",
      referenceAssetId, referenceAssetIds: referenceAssetId ? [referenceAssetId] : [],
      continuityLocks: lockedFactCount ? [{ id: "lock-1", fieldKey: "name", lockedValue: "Mara" }] : [],
    },
  } as unknown as MovieCharacterDetail;
}

let document: TestDocument;
let container: TestNode;
let root: Root;
let castReads: MovieCast[];
let worldReads: MovieWorldWorkspace[];
let detailReads: MovieCharacterDetail[];
let readCursor = 0;

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

/** Every `<img>` src the workspace is binding into the DOM. Each one is a GET the browser makes. */
function previewRequests(): string[] {
  return findTag(container, "IMG").map((node) => node.getAttribute("src") ?? "");
}

/** The subset of those requests whose URL the download route does not serve. */
function doomedPreviewRequests(): string[] {
  return previewRequests().filter((src) => {
    const assetId = /\/api\/assets\/([^/]+)\/download/.exec(src)?.[1];
    return assetId === undefined || routeAnswerFor(assetId) !== "200 bytes";
  });
}

function renderedText(): string { return container.textContent ?? ""; }

/**
 * Mounts the Production Kit on the characters room, which is its default, and settles the first
 * cast/world/detail reads. `load()` auto-selects the first record, so no click is needed to reach
 * the inspector.
 */
async function mountKit() {
  await act(async () => {
    root = createRoot(container as unknown as Element);
    root.render(<ProductionKitWorkspace projectId={PROJECT_ID} />);
    await drain();
    await drain();
  });
}

/**
 * Approving the character's identity is a real user action on this card, and `approveIdentity`
 * ends with `load(room, selected.id)`, so the *same live selection* is re-read through the second
 * payload set. Nothing is unmounted between the two reads: the backing file changes status while
 * the component is looking at it.
 */
async function reReadCharacterSelection() {
  readCursor = 1;
  const approve = findClass(container, "movie-kit-approve")[0];
  if (!approve) throw new Error("The approve-identity control never rendered, so the re-read cannot be driven.");
  expect(approve.disabled, "the character record must be needs-approval for its identity control to be usable").toBe(false);
  await act(async () => {
    approve.dispatchEvent({ type: "click", bubbles: true });
    await drain();
    await drain();
  });
}

beforeEach(() => {
  document = new TestDocument();
  container = document.createElement("div");
  document.body.appendChild(container);
  castReads = [];
  worldReads = [];
  detailReads = [];
  readCursor = 0;
  assetLibrary.clear();
  const window = {
    document, Element: TestNode, HTMLElement: TestNode, HTMLIFrameElement: TestNode, Node: TestNode,
    addEventListener: vi.fn(), removeEventListener: vi.fn(),
    setInterval: () => 0, clearInterval: () => {}, setTimeout: () => 0, clearTimeout: () => {},
  };
  // React resolves the container's ownerDocument.defaultView and tests the active element against
  document.defaultView = window;
  vi.stubGlobal("document", document);
  vi.stubGlobal("window", window);
  vi.stubGlobal("navigator", { userAgent: "test" });
  vi.stubGlobal("IS_REACT_ACT_ENVIRONMENT", true);
  harness.api.getAsset.mockReset().mockImplementation(async (id: string) => {
    if (!assetLibrary.has(id)) throw new Error("Asset not found");
    const hasFile = assetLibrary.get(id) === "Ready";
    return { id, hasFile, canPreview: hasFile };
  });
  harness.api.getMovieCast.mockReset().mockImplementation(() => Promise.resolve(castReads[readCursor]));
  harness.api.getMovieWorld.mockReset().mockImplementation(() => Promise.resolve(worldReads[readCursor]));
  harness.api.getMovieCharacterDetail.mockReset().mockImplementation(() => Promise.resolve(detailReads[readCursor]));
  harness.api.lockMovieCharacterFact.mockReset().mockResolvedValue(undefined);
  harness.api.addMovieContinuityLock.mockReset().mockResolvedValue(undefined);
  harness.api.updateMovieCharacter.mockReset().mockResolvedValue(undefined);
  harness.api.updateMovieProp.mockReset().mockResolvedValue(undefined);
  harness.api.updateMovieLocation.mockReset().mockResolvedValue(undefined);
});

afterEach(() => {
  act(() => root?.unmount());
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

describe("Production Kit characters-room reference against the download route", () => {
  it("serves bytes for a Ready reference and nothing else, which is what every case below keys off", () => {
    assetLibrary.set("probe", "Ready");
    expect(routeAnswerFor("probe")).toBe("200 bytes");
    for (const status of ["Uploading", "Processing", "Failed", "Deleted"] satisfies StoredFileStatus[]) {
      assetLibrary.set("probe", status);
      expect(routeAnswerFor("probe"), `${status} must not serve`).toBe("409 ASSET_FILE_UNAVAILABLE");
    }
    assetLibrary.set("probe", null);
    expect(routeAnswerFor("probe")).toBe("409 ASSET_FILE_UNAVAILABLE");
    assetLibrary.delete("probe");
    expect(routeAnswerFor("probe")).toBe("404 ASSET_NOT_FOUND");
  });

  it("paints a genuinely Ready character reference that the world projection never describes", async () => {
    assetLibrary.set(FACE_ID, "Ready");
    castReads = [makeCast(0)];
    worldReads = [makeWorld([], false)];
    detailReads = [makeDetail(0)];
    await mountKit();

    expect(previewRequests()).toContain(FACE_URL);
    expect(routeAnswerFor(FACE_ID)).toBe("200 bytes");
    expect(findClass(container, "movie-kit-reference-preview")).toHaveLength(1);
    expect(worldReads[0].assets.map((asset) => asset.id), "the projection must not be relied on to describe a character reference").not.toContain(FACE_ID);
  });

  it("uses the Asset Library when the character and world carry no availability flags", async () => {
    assetLibrary.set(FACE_ID, "Deleted");
    castReads = [makeCast(0)];
    worldReads = [makeWorld([], false)];
    detailReads = [makeDetail(0)];
    await mountKit();

    expect(harness.api.getAsset).toHaveBeenCalledWith(FACE_ID);
    const castKeys = Object.keys(castReads[0].characters[0] as object);
    const detailKeys = Object.keys((detailReads[0].character as unknown as object));
    expect(castKeys).toContain("referenceAssetId");
    expect(detailKeys).toContain("referenceAssetId");
    for (const key of ["hasFile", "canPreview", "storedFileStatus", "mimeType"]) {
      expect(castKeys, `the cast fixture has no ${key} field`).not.toContain(key);
      expect(detailKeys, `the character detail payload must not grow a ${key} field`).not.toContain(key);
    }
    expect(worldReads[0].assets).toHaveLength(0);
  });

  it("stops binding the character preview once the same live selection reports its file pruned", async () => {
    castReads = [makeCast(0), makeCast(1)];
    worldReads = [makeWorld([], false), makeWorld([], true)];
    detailReads = [makeDetail(0), makeDetail(1)];
    assetLibrary.set(FACE_ID, "Ready");
    await mountKit();
    expect(previewRequests()).toContain(FACE_URL);
    expect(doomedPreviewRequests()).toEqual([]);

    assetLibrary.set(FACE_ID, "Deleted");
    await reReadCharacterSelection();

    expect(doomedPreviewRequests(), `the characters room still binds ${FACE_URL}, which the download route answers ${routeAnswerFor(FACE_ID)}`).toEqual([]);
    // Withholding must not cost the producer the room: record, chooser and identity control stay.
    expect(findClass(container, "movie-kit-approve")).toHaveLength(1);
    expect(renderedText()).toContain("assets.fileUnavailable");
  });

  it("never binds a character preview whose file was already pruned before the first read", async () => {
    assetLibrary.set(FACE_ID, "Deleted");
    castReads = [makeCast(1)];
    worldReads = [makeWorld([], true)];
    detailReads = [makeDetail(1)];
    await mountKit();

    expect(doomedPreviewRequests(), `a pruned character reference is bound as ${FACE_URL} on first paint, answering ${routeAnswerFor(FACE_ID)}`).toEqual([]);
    expect(findClass(container, "movie-kit-reference-empty")).toHaveLength(1);
  });

  it("never binds a character preview whose stored-file row is missing", async () => {
    assetLibrary.set(FACE_ID, null);
    castReads = [makeCast(1)];
    worldReads = [makeWorld([], true)];
    detailReads = [makeDetail(1)];
    await mountKit();

    expect(routeAnswerFor(FACE_ID)).toBe("409 ASSET_FILE_UNAVAILABLE");
    expect(doomedPreviewRequests(), `a character reference with no stored-file row is bound as ${FACE_URL}`).toEqual([]);
  });

  it("never binds a character preview for a reference asset that has been deleted outright", async () => {
    assetLibrary.set(FACE_ID, "Ready");
    assetLibrary.delete(FACE_ID);
    castReads = [makeCast(1)];
    worldReads = [makeWorld([], true)];
    detailReads = [makeDetail(1)];
    await mountKit();

    expect(routeAnswerFor(FACE_ID)).toBe("404 ASSET_NOT_FOUND");
    expect(doomedPreviewRequests(), `a deleted reference asset is still bound into the card as ${FACE_URL}`).toEqual([]);
  });

  it("removes a preview when the file becomes unreadable after the metadata check", async () => {
    assetLibrary.set(FACE_ID, "Ready");
    castReads = [makeCast(1)];
    worldReads = [makeWorld([], true)];
    detailReads = [makeDetail(1)];
    await mountKit();

    const image = findTag(container, "IMG")[0];
    expect(image.getAttribute("src")).toBe(FACE_URL);
    await act(async () => { image.dispatchEvent({ type: "error", bubbles: false }); });
    expect(previewRequests()).toEqual([]);
    expect(renderedText()).toContain("assets.fileUnavailable");
  });

  it("ignores a late Ready answer after a newer read marks the same reference unavailable", async () => {
    let finishOldRead!: (asset: { id: string; hasFile: boolean; canPreview: boolean }) => void;
    harness.api.getAsset.mockImplementationOnce(() => new Promise(resolve => { finishOldRead = resolve; }));
    assetLibrary.set(FACE_ID, "Deleted");
    castReads = [makeCast(0), makeCast(1)];
    worldReads = [makeWorld([], false), makeWorld([], true)];
    detailReads = [makeDetail(0), makeDetail(1)];
    await mountKit();
    expect(previewRequests()).toEqual([]);
    await reReadCharacterSelection();
    await act(async () => { finishOldRead({ id: FACE_ID, hasFile: true, canPreview: true }); });
    expect(harness.api.getAsset).toHaveBeenCalledTimes(2);
    expect(previewRequests()).toEqual([]);
    expect(renderedText()).toContain("assets.fileUnavailable");
  });

  it("withholds the very same pruned reference when an unrelated world record shares its asset id", async () => {
    assetLibrary.set(FACE_ID, "Deleted");
    const sharedRow: MovieWorldAsset = { id: FACE_ID, name: "Mara face plate", assetType: "image", mimeType: "image/png", hasFile: false, canPreview: false };
    castReads = [makeCast(1)];
    worldReads = [makeWorld([sharedRow], true)];
    detailReads = [makeDetail(1)];
    await mountKit();

    expect(harness.api.getAsset).not.toHaveBeenCalled();
    expect(doomedPreviewRequests(), "the world row suppresses the unavailable URL").toEqual([]);
    expect(findClass(container, "movie-kit-reference-empty")).toHaveLength(1);
    expect(renderedText()).toContain("assets.fileUnavailable");
  });

  it("keeps a Ready character reference previewing beside an unrelated pruned world asset", async () => {
    assetLibrary.set(FACE_ID, "Ready");
    assetLibrary.set(COMPASS_ID, "Deleted");
    const prunedSibling: MovieWorldAsset = { id: COMPASS_ID, name: "Compass reference", assetType: "image", mimeType: "image/png", hasFile: false, canPreview: false };
    castReads = [makeCast(1)];
    worldReads = [makeWorld([prunedSibling], true)];
    detailReads = [makeDetail(1)];
    await mountKit();

    expect(previewRequests()).toContain(FACE_URL);
    expect(findClass(container, "movie-kit-reference-preview")).toHaveLength(1);
  });
});
