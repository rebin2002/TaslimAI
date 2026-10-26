import { readFileSync } from "node:fs";
import React from "react";
import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { MovieCast, MovieCharacter, MovieWorldWorkspace } from "@/lib/api";
import { kitRecordForTest } from "./ProductionKitWorkspace";

const recordCast = { project: {} as MovieCast["project"], characters: [{ id: "character-1", name: "Mara", role: "Lead", description: "A patient cartographer.", appearance: null, referenceAssetId: "asset-face", referenceAssetIds: ["asset-face"], referenceAssetCount: 1, stateCount: 1, latestState: null, relationshipCount: 0, relationshipTypes: [], lockedFactCount: 1, lockedFieldKeys: ["name"], updatedAt: "2026-09-30T00:00:00Z" }] } as MovieCast;
const recordWorld = { movieProjectId: "movie-1", workspaceId: "workspace-1", projectId: null, status: "Draft", title: "North Star", description: "A film.", durationSeconds: 60, aspectRatio: "16:9", style: "cinematic", language: "en", world: { locations: [{ id: "location-1", name: "Harbor", description: "A cold harbor.", visualContinuityNotes: null, referenceAssetId: null }], sets: [], props: [{ id: "prop-1", name: "Compass", description: "A brass compass.", category: "Hero prop", continuityNotes: null, referenceAssetId: "asset-compass" }], references: [], usages: [], facts: [], locks: [{ id: "lock-1", entityType: "prop", entityId: "prop-1", fieldName: "name", lockedValue: "Compass", strength: "hard", reason: null, createdAt: "2026-09-30T00:00:00Z", releasedAt: null }] }, usageDetails: [], assets: [] } as MovieWorldWorkspace;

class TestNode {
  nodeType: number;
  nodeName: string;
  ownerDocument: TestDocument;
  parentNode: TestNode | null = null;
  childNodes: TestNode[] = [];
  attributes = new Map<string, string>();
  style: Record<string, string> = {};
  listeners = new Map<string, Set<(event: unknown) => void>>();
  value = "";
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

  appendChild<T extends TestNode>(child: T) { if (child.parentNode) child.parentNode.removeChild(child); child.parentNode = this; this.childNodes.push(child); return child; }
  insertBefore<T extends TestNode>(child: T, before: TestNode | null) { if (!before) return this.appendChild(child); const index = this.childNodes.indexOf(before); if (index < 0) return this.appendChild(child); if (child.parentNode) child.parentNode.removeChild(child); child.parentNode = this; this.childNodes.splice(index, 0, child); return child; }
  removeChild<T extends TestNode>(child: T) { const index = this.childNodes.indexOf(child); if (index >= 0) this.childNodes.splice(index, 1); child.parentNode = null; return child; }
  setAttribute(name: string, value: string) { this.attributes.set(name, String(value)); }
  getAttribute(name: string) { return this.attributes.get(name) ?? null; }
  removeAttribute(name: string) { this.attributes.delete(name); }
  addEventListener(name: string, listener: (event: unknown) => void) { const listeners = this.listeners.get(name) ?? new Set(); listeners.add(listener); this.listeners.set(name, listeners); }
  removeEventListener(name: string, listener: (event: unknown) => void) { this.listeners.get(name)?.delete(listener); }
  dispatchEvent(event: { type: string }) { for (const listener of this.listeners.get(event.type) ?? []) listener(event); return true; }
  contains(node: TestNode | null): boolean { return node === this || this.childNodes.some((child) => child.contains(node)); }
  focus() { this.ownerDocument.activeElement = this; }
  blur() { if (this.ownerDocument.activeElement === this) this.ownerDocument.activeElement = this.ownerDocument.body; }
}

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
}));

vi.mock("@/components/LocaleProvider", () => ({ useLocale: () => ({ t: (key: string) => key }) }));
vi.mock("@/lib/api", () => ({ api: harness.api }));
vi.mock("@/lib/apiBase", () => ({ assetFileUrl: (id: string) => `/assets/${id}` }));
vi.mock("next/link", () => ({ default: ({ href, children, ...props }: { href: string; children: React.ReactNode }) => <a href={href} {...props}>{children}</a> }));
vi.mock("lucide-react", () => {
  const Icon = (props: Record<string, unknown>) => <span {...props} />;
  return { AlertTriangle: Icon, ArrowUpRight: Icon, CheckCircle2: Icon, Image: Icon, LockKeyhole: Icon, MapPinned: Icon, Package: Icon, ShieldCheck: Icon, Sparkles: Icon, UsersRound: Icon };
});

import { ProductionKitWorkspace } from "./ProductionKitWorkspace";

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((next) => { resolve = next; });
  return { promise, resolve };
}

function workspaceCast(workspaceId: string): MovieCast {
  return {
    project: { id: `${workspaceId}-project`, workspaceId, projectId: null, mode: "Full", status: "Draft", title: `${workspaceId} film`, description: "A film.", durationSeconds: 60, aspectRatio: "16:9", style: "cinematic", language: "en", createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z" },
    characters: [{ id: `${workspaceId}-character`, name: `${workspaceId} character`, role: "Lead", description: `${workspaceId} description`, appearance: null, referenceAssetId: `${workspaceId}-asset`, referenceAssetIds: [`${workspaceId}-asset`], referenceAssetCount: 1, stateCount: 1, latestState: null, relationshipCount: 0, relationshipTypes: [], lockedFactCount: 1, lockedFieldKeys: ["name"], updatedAt: "2026-01-01T00:00:00Z" }],
  } as MovieCast;
}

function workspaceDetail(workspaceId: string): MovieCharacter {
  return { id: `${workspaceId}-character`, name: `${workspaceId} character`, role: "Lead", description: `${workspaceId} description`, appearance: null, physicalDescription: null, wardrobe: null, voiceReference: null, personalityAndStoryNotes: null, voiceAndPerformance: null, continuityNotes: null, referenceAssetId: `${workspaceId}-asset`, referenceAssetIds: [`${workspaceId}-asset`], states: [], relationships: [], continuityLocks: [] };
}

function workspaceWorld(workspaceId: string): MovieWorldWorkspace {
  return { movieProjectId: `${workspaceId}-project`, workspaceId, projectId: null, status: "Draft", title: `${workspaceId} film`, description: "A film.", durationSeconds: 60, aspectRatio: "16:9", style: "cinematic", language: "en", world: { locations: [], sets: [], props: [], references: [], usages: [], facts: [], locks: [] }, usageDetails: [], assets: [] };
}

let document: TestDocument;
let root: Root | undefined;
let container: TestNode;

beforeEach(() => {
  document = new TestDocument();
  container = document.createElement("div");
  document.body.appendChild(container);
  vi.stubGlobal("document", document);
  const window = { document, HTMLElement: TestNode, HTMLIFrameElement: TestNode, addEventListener: vi.fn(), removeEventListener: vi.fn() };
  document.defaultView = window;
  vi.stubGlobal("window", window);
  vi.stubGlobal("navigator", { userAgent: "test" });
  vi.stubGlobal("IS_REACT_ACT_ENVIRONMENT", true);
  Object.values(harness.api).forEach((mock) => mock.mockReset());
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

  it("does not render a late response from the previous project after switching projects", async () => {
    const castA = deferred<MovieCast>();
    const worldA = deferred<MovieWorldWorkspace>();
    const castB = deferred<MovieCast>();
    const worldB = deferred<MovieWorldWorkspace>();
    harness.api.getMovieCast.mockReturnValueOnce(castA.promise).mockReturnValueOnce(castB.promise);
    harness.api.getMovieWorld.mockReturnValueOnce(worldA.promise).mockReturnValueOnce(worldB.promise);
    harness.api.getMovieCharacterDetail.mockImplementation((id: string) => Promise.resolve(workspaceDetail(id.replace(/-character$/, ""))));

    await act(async () => {
      root = createRoot(container as unknown as Element);
      root.render(<ProductionKitWorkspace projectId="workspace-a" />);
      await Promise.resolve();
    });
    expect(harness.api.getMovieCast).toHaveBeenCalledWith("workspace-a");

    await act(async () => {
      root?.render(<ProductionKitWorkspace projectId="workspace-b" />);
      await Promise.resolve();
    });
    expect(harness.api.getMovieCast).toHaveBeenCalledWith("workspace-b");

    castB.resolve(workspaceCast("workspace-b"));
    worldB.resolve(workspaceWorld("workspace-b"));
    await act(async () => { await new Promise((resolve) => setTimeout(resolve, 0)); await Promise.resolve(); await Promise.resolve(); });
    const afterWorkspaceB = container.textContent;
    expect(afterWorkspaceB).toContain("workspace-b character");

    castA.resolve(workspaceCast("workspace-a"));
    worldA.resolve(workspaceWorld("workspace-a"));
    await act(async () => { await new Promise((resolve) => setTimeout(resolve, 0)); await Promise.resolve(); await Promise.resolve(); });

    expect(container.textContent).toContain("workspace-b character");
    expect(container.textContent).not.toContain("workspace-a character");
  });
});
