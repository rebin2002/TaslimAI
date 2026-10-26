import React from "react";
import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
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
  workspace: { id: "workspace-a" },
  t: (key: string) => key,
  api: {
    listProjects: vi.fn(),
    listAssets: vi.fn(),
    assetFileUrl: vi.fn((id: string) => `/api/assets/${id}/download`),
    createMusicGenerationJob: vi.fn(),
    getGenerationJob: vi.fn(),
    cancelGenerationJob: vi.fn(),
  },
}));

vi.mock("@/components/AuthProvider", () => ({ useAuth: () => ({ workspace: harness.workspace }) }));
vi.mock("@/components/LocaleProvider", () => ({ useLocale: () => ({ locale: "en", t: harness.t }) }));
vi.mock("next/navigation", () => ({ useSearchParams: () => ({ get: () => null }) }));
vi.mock("next/link", () => ({ default: ({ href, children, ...props }: { href: string; children: React.ReactNode }) => <a href={href} {...props}>{children}</a> }));
vi.mock("@/components/AccessibleProgressBar", () => ({ AccessibleProgressBar: () => <div /> }));
vi.mock("lucide-react", () => {
  const Icon = (props: Record<string, unknown>) => <span {...props} />;
  return { AlertCircle: Icon, AudioLines: Icon, CheckCircle2: Icon, Clock3: Icon, Download: Icon, FolderOpen: Icon, Headphones: Icon, LoaderCircle: Icon, Music2: Icon, Pause: Icon, Play: Icon, RefreshCw: Icon, Sparkles: Icon, WandSparkles: Icon, XCircle: Icon };
});
vi.mock("@/lib/api", () => ({ api: harness.api }));

import { MusicStudioView } from "./MusicStudioView";

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((next) => { resolve = next; });
  return { promise, resolve };
}

function projects(name: string, suffix: string) { return [{ id: `${name}-${suffix}-project`, workspaceId: name, name, description: null, type: "General", status: "Active", instructions: null, contextNotes: null, createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z" }]; }
function assets(name: string) { return { items: [{ id: `${name}-asset`, workspaceId: name, projectId: null, projectName: null, name, description: null, assetType: "music", mimeType: "audio/mpeg", status: "Active", hasFile: true, canPreview: true, fileSizeBytes: 10, sourceStudio: "music", sourceJobTitle: name, createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z", archivedAt: null, representations: [] }], totalCount: 1, page: 1, pageSize: 5, totalPages: 1 }; }

let document: TestDocument;
let root: Root;
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
  harness.workspace = { id: "workspace-a" };
  harness.api.listProjects.mockReset();
  harness.api.listAssets.mockReset();
});

afterEach(() => {
  act(() => root?.unmount());
  vi.unstubAllGlobals();
});

describe("MusicStudioView workspace freshness", () => {
  it("does not render late project or music responses from a previous workspace", async () => {
    const projectsAActive = deferred<ReturnType<typeof projects>>();
    const projectsAArchived = deferred<ReturnType<typeof projects>>();
    const assetsA = deferred<ReturnType<typeof assets>>();
    const projectsBActive = deferred<ReturnType<typeof projects>>();
    const projectsBArchived = deferred<ReturnType<typeof projects>>();
    const assetsB = deferred<ReturnType<typeof assets>>();
    harness.api.listProjects
      .mockReturnValueOnce(projectsAActive.promise)
      .mockReturnValueOnce(projectsAArchived.promise)
      .mockReturnValueOnce(projectsBActive.promise)
      .mockReturnValueOnce(projectsBArchived.promise);
    harness.api.listAssets.mockReturnValueOnce(assetsA.promise).mockReturnValueOnce(assetsB.promise);

    await act(async () => { root = createRoot(container as unknown as Element); root.render(<MusicStudioView />); await new Promise((resolve) => setTimeout(resolve, 0)); });
    expect(harness.api.listProjects).toHaveBeenCalledTimes(2);
    expect(harness.api.listAssets).toHaveBeenCalledTimes(1);
    harness.workspace = { id: "workspace-b" };
    await act(async () => { root.render(<MusicStudioView />); await new Promise((resolve) => setTimeout(resolve, 0)); });
    expect(harness.api.listProjects).toHaveBeenCalledTimes(4);
    expect(harness.api.listAssets).toHaveBeenCalledTimes(2);
    expect(harness.api.listAssets.mock.calls.map((call: unknown[]) => call[0])).toEqual(["workspace-a", "workspace-b"]);
    projectsBActive.resolve(projects("workspace-b", "active"));
    projectsBArchived.resolve(projects("workspace-b", "archived"));
    assetsB.resolve(assets("workspace-b music"));
    await act(async () => { await assetsB.promise; await Promise.resolve(); await Promise.resolve(); });
    projectsAActive.resolve(projects("workspace-a", "active"));
    projectsAArchived.resolve(projects("workspace-a", "archived"));
    assetsA.resolve(assets("workspace-a music"));
    await act(async () => { await Promise.resolve(); await Promise.resolve(); });

    expect(container.textContent).not.toContain("workspace-a music");
  });
});
