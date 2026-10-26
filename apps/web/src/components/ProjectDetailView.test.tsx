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
  constructor(nodeType: number, nodeName: string, ownerDocument: TestDocument) { this.nodeType = nodeType; this.nodeName = nodeName; this.ownerDocument = ownerDocument; }
  get firstChild() { return this.childNodes[0] ?? null; }
  get lastChild(): TestNode | null { return this.childNodes.at(-1) ?? null; }
  get options() { return this.childNodes; }
  get tagName() { return this.nodeName; }
  get textContent() { return this.childNodes.map((child) => child.textContent ?? "").join(""); }
  set textContent(value: string | null) { this.childNodes = value ? [new TestNodeText(value, this.ownerDocument)] : []; for (const child of this.childNodes) child.parentNode = this; }
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
  dispatchEvent(event: { type: string; bubbles?: boolean; target?: TestNode }) { const dispatched = { ...event, target: event.target ?? this, currentTarget: this }; for (const listener of this.listeners.get(event.type) ?? []) listener(dispatched); if (event.bubbles !== false && this.parentNode) this.parentNode.dispatchEvent(dispatched); return true; }
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
  defaultView: Record<string, unknown>;
  constructor() { super(9, "#document", null as unknown as TestDocument); this.ownerDocument = this; this.defaultView = {}; this.documentElement = new TestNode(1, "html", this); this.body = new TestNode(1, "body", this); this.documentElement.appendChild(this.body); this.appendChild(this.documentElement); this.activeElement = this.body; }
  createElement(name: string) { return new TestNode(1, name.toUpperCase(), this); }
  createElementNS(_namespace: string, name: string) { return this.createElement(name); }
  createTextNode(value: string) { return new TestNodeText(value, this); }
}

const harness = vi.hoisted(() => ({
  routeProjectId: "project-a",
  t: (key: string) => key,
  api: { getProjectOverview: vi.fn(), archiveProject: vi.fn(), restoreProject: vi.fn(), updateProject: vi.fn() },
}));

vi.mock("@/lib/api", () => ({ api: harness.api }));
vi.mock("@/lib/i18n", () => ({ localeTag: () => "en-US" }));
vi.mock("@/components/LocaleProvider", () => ({ useLocale: () => ({ locale: "en", t: harness.t }) }));
vi.mock("next/navigation", () => ({ useParams: () => ({ projectId: harness.routeProjectId }), useRouter: () => ({ push: vi.fn() }) }));
vi.mock("next/link", () => ({ default: ({ href, children, ...props }: { href: string; children: React.ReactNode }) => <a href={href} {...props}>{children}</a> }));
vi.mock("@/components/ProjectForm", () => ({ ProjectForm: () => <div /> }));
vi.mock("@/components/ProjectContextEditor", () => ({ ProjectContextEditor: () => <div /> }));
vi.mock("@/components/ProjectFilesSection", () => ({ ProjectFilesSection: () => <div /> }));
vi.mock("@/components/ProjectAssetsSection", () => ({ ProjectAssetsSection: () => <div /> }));
vi.mock("lucide-react", () => { const Icon = (props: Record<string, unknown>) => <span {...props} />; return { Activity: Icon, Archive: Icon, ArrowLeft: Icon, ArrowUpRight: Icon, BarChart3: Icon, CalendarDays: Icon, FileText: Icon, FolderOpen: Icon, LibraryBig: Icon, MessageSquare: Icon, Pencil: Icon, Plus: Icon, RotateCcw: Icon, Sparkles: Icon }; });

import { ProjectDetailView } from "./ProjectDetailView";

function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>((next) => { resolve = next; }); return { promise, resolve }; }
function overview(projectId: string, name: string, status: "Active" | "Archived" = "Active") { return { project: { id: projectId, workspaceId: "workspace-1", name, description: null, instructions: null, contextNotes: null, type: "General", status, createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z", archivedAt: null }, workspace: { id: "workspace-1", name: "Personal", slug: "personal", type: "Personal", role: "Owner" }, counts: { files: 0, assets: 0, conversations: 0, activity: 0 }, conversations: [], recentActivity: [] }; }
function findButton(node: TestNode, label: string): TestNode | null { if (node.nodeName === "BUTTON" && node.textContent?.includes(label)) return node; for (const child of node.childNodes) { const match = findButton(child, label); if (match) return match; } return null; }

let document: TestDocument;
let root: Root;
let container: TestNode;
beforeEach(() => { document = new TestDocument(); container = document.createElement("div"); document.body.appendChild(container); vi.stubGlobal("document", document); const window = { document, HTMLElement: TestNode, HTMLIFrameElement: TestNode, addEventListener: vi.fn(), removeEventListener: vi.fn(), confirm: vi.fn(() => true) }; document.defaultView = window; vi.stubGlobal("window", window); vi.stubGlobal("navigator", { userAgent: "test" }); vi.stubGlobal("IS_REACT_ACT_ENVIRONMENT", true); harness.routeProjectId = "project-a"; harness.api.getProjectOverview.mockReset(); harness.api.archiveProject.mockReset(); harness.api.restoreProject.mockReset(); harness.api.updateProject.mockReset(); });
afterEach(() => { act(() => root?.unmount()); vi.unstubAllGlobals(); });

describe("ProjectDetailView route freshness", () => {
  it("does not let a late archive response from the prior project replace the current project", async () => {
    const projectA = deferred<ReturnType<typeof overview>>();
    const projectB = deferred<ReturnType<typeof overview>>();
    const archivedA = deferred<ReturnType<typeof overview>["project"]>();
    harness.api.getProjectOverview.mockReturnValueOnce(projectA.promise).mockReturnValueOnce(projectB.promise);
    harness.api.archiveProject.mockReturnValueOnce(archivedA.promise);
    await act(async () => { root = createRoot(container as unknown as Element); root.render(<ProjectDetailView />); await Promise.resolve(); });
    projectA.resolve(overview("project-a", "Project A"));
    await act(async () => { await projectA.promise; await Promise.resolve(); await Promise.resolve(); });
    const archiveButton = findButton(container, "projects.archive");
    expect(archiveButton).not.toBeNull();
    await act(async () => { archiveButton?.dispatchEvent({ type: "click" }); await Promise.resolve(); });
    expect(harness.api.archiveProject).toHaveBeenCalledWith("project-a");
    harness.routeProjectId = "project-b";
    await act(async () => { root.render(<ProjectDetailView />); await Promise.resolve(); });
    projectB.resolve(overview("project-b", "Project B"));
    await act(async () => { await projectB.promise; await Promise.resolve(); await Promise.resolve(); });
    expect(container.textContent).toContain("Project B");
    archivedA.resolve(overview("project-a", "Project A", "Archived").project);
    await act(async () => { await archivedA.promise; await Promise.resolve(); await Promise.resolve(); });
    expect(container.textContent).toContain("Project B");
    expect(container.textContent).not.toContain("Project A");
  });
});
