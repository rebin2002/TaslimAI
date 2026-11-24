// Behavioral tests for the route fence on the membership-scoped mutation
// continuations (archive/restore) added by the #1207 slice after the
// independent review found the unfenced paths.
//
// Division of labor, so no lane duplicates another: #1181's
// ProjectDetailView.routeTransitionBadgeFence.test.tsx owns the ARCHIVE-side
// transition cases and the effect-path pins; #1100's
// ProjectDetailView.accessEndedOrdering.test.tsx owns same-project ordering
// between a revocation and a late overview read. THIS file pins what neither
// covers: the RESTORE-side transition continuations and the same-project
// controls that keep the new fence from over-refusing legitimate updates.
//
// Synthetic DOM harness, in the style established by
// GenerationJobsView.workspaceFence.test.tsx, ActivityBell.badgeFence.test.tsx,
// ProjectDetailView.staleSnapshotRace.test.tsx and
// ProjectDetailView.routeTransitionBadgeFence.test.tsx: no jsdom, no network,
// no provider, no real account or workspace. The real ProjectDetailView is
// mounted; the only things that change are the route parameter and the order
// in which deferred answers land. No accounts, no protected operations.
import { act, type ReactNode } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Project, ProjectOverview } from "@/lib/api";

class TestNode {
  nodeType: number;
  nodeName: string;
  ownerDocument: TestDocument;
  parentNode: TestNode | null = null;
  childNodes: TestNode[] = [];
  attributes = new Map<string, string>();
  style: Record<string, string> = {};
  listeners = new Map<string, Set<(event: unknown) => void>>();
  constructor(nodeType: number, nodeName: string, ownerDocument: TestDocument) {
    this.nodeType = nodeType;
    this.nodeName = nodeName;
    this.ownerDocument = ownerDocument;
  }
  get firstChild() { return this.childNodes[0] ?? null; }
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
  focus() { /* these cases never move focus */ }
  blur() { /* these cases never move focus */ }
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
}
type TestEvent = {
  type: string;
  bubbles?: boolean;
  target?: TestNode;
  currentTarget?: TestNode;
  propagationStopped?: boolean;
  defaultPrevented?: boolean;
  preventDefault?: () => void;
  stopPropagation?: () => void;
};
const h = vi.hoisted(() => {
  const translate = (key: string, variables?: Record<string, string>) =>
    Object.entries(variables ?? {}).reduce((value, [name, replacement]) => value.replace(`{${name}}`, replacement), key);
  return {
    // One stable locale context; the only thing able to re-fire the load
    // effect is `params.projectId`, i.e. the route transition under test.
    pack: { locale: "en", t: translate },
    active: null as { locale: string; t: (key: string, variables?: Record<string, string>) => string } | null,
    projectId: "project-synthetic-a",
    api: {
      getProjectOverview: vi.fn(),
      archiveProject: vi.fn(),
      restoreProject: vi.fn(),
      updateProject: vi.fn(),
    },
    router: { push: vi.fn() },
    confirm: true,
  };
});
vi.mock("@/lib/api", () => ({
  ApiError: class ApiError extends Error {
    status: number;
    constructor(message: string, status: number) { super(message); this.status = status; }
  },
  api: h.api,
}));
vi.mock("next/navigation", () => ({
  useParams: () => ({ projectId: h.projectId }),
  useRouter: () => h.router,
}));
vi.mock("next/link", async () => {
  const { createElement } = await import("react");
  type LinkProps = { href: string; className?: string; "aria-label"?: string; children?: ReactNode };
  return {
    default: ({ href, className, "aria-label": ariaLabel, children }: LinkProps) =>
      createElement("a", { href, className, "aria-label": ariaLabel }, children),
  };
});
vi.mock("@/components/LocaleProvider", () => ({ useLocale: () => h.active }));
vi.mock("@/components/ProjectForm", () => ({ ProjectForm: () => null }));
vi.mock("@/components/ProjectContextEditor", () => ({ ProjectContextEditor: () => null }));
vi.mock("@/components/ProjectFilesSection", () => ({ ProjectFilesSection: () => null }));
vi.mock("@/components/ProjectAssetsSection", () => ({ ProjectAssetsSection: () => null }));
import { ProjectDetailView } from "./ProjectDetailView";

type Deferred<T> = { promise: Promise<T>; resolve: (value: T) => void; reject: (error: unknown) => void };
function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}
// Synthetic fixtures only. Two distinct memberships, so a leaked one is always identifiable.
function makeProject(overrides: Partial<Project> = {}): Project {
  return {
    id: "project-synthetic-a", workspaceId: "workspace-synthetic-a", name: "Alpha Fence Project",
    description: "A synthetic project used only by this test.", instructions: null, contextNotes: null,
    type: "Marketing", status: "Active",
    createdAt: "2026-11-01T00:00:00Z", updatedAt: "2026-11-01T00:00:00Z", archivedAt: null, ...overrides,
  };
}
function makeOverview(role: string, project: Project): ProjectOverview {
  return {
    project,
    workspace: { id: `workspace-of-${project.id}`, name: `Workspace of ${project.name}`, slug: project.id, type: "Business", role },
    counts: { files: 3, assets: 2, conversations: 1, activity: 4 },
    conversations: [],
    recentActivity: [],
  };
}
const projectA = makeProject();
const projectAArchived = makeProject({ status: "Archived", archivedAt: "2026-11-19T00:00:00Z" });
const projectAActiveAgain = makeProject({ status: "Active", archivedAt: null });
const projectB = makeProject({ id: "project-synthetic-b", workspaceId: "workspace-synthetic-b", name: "Beta Fence Project" });
const overviewA = makeOverview("Owner", projectA);
const overviewAArchived = makeOverview("Owner", projectAArchived);
const memberB = makeOverview("Editor", projectB);
function apiError(status: number, message: string) {
  return Object.assign(new Error(message), { status });
}
let document: TestDocument;
let container: TestNode;
let root: Root | undefined;
let overviewRequests: { projectId: string; request: Deferred<ProjectOverview> }[];
let archiveRequests: { projectId: string; request: Deferred<Project> }[];
let restoreRequests: { projectId: string; request: Deferred<Project> }[];
function findByClass(node: TestNode, className: string): TestNode[] {
  return [
    ...(node.getAttribute("class")?.split(/\s+/).includes(className) ? [node] : []),
    ...node.childNodes.flatMap((child) => findByClass(child, className)),
  ];
}
function findTag(node: TestNode, nodeName: string): TestNode[] {
  return [
    ...(node.nodeName === nodeName ? [node] : []),
    ...node.childNodes.flatMap((child) => findTag(child, nodeName)),
  ];
}
function badgeNodes() { return findByClass(container, "workspace-role"); }
function alertNodes() { return findByClass(container, "inline-error").filter((node) => node.getAttribute("role") === "alert"); }
function headingText() { return findTag(container, "H1")[0]?.textContent ?? ""; }
function buttonByText(label: string) {
  return findTag(container, "BUTTON").find((node) => (node.textContent ?? "").includes(label));
}
function linkProjectIds() {
  return findTag(container, "A")
    .map((node) => node.getAttribute("href") ?? "")
    .filter((href) => /projectId=/.test(href))
    .map((href) => /projectId=([^&]+)/.exec(href)?.[1] ?? "");
}
function drain() { return new Promise((resolve) => setTimeout(resolve, 0)); }
async function renderView() {
  await act(async () => {
    root?.render(<ProjectDetailView />);
    await drain();
  });
}
async function mountView() {
  await act(async () => {
    root = createRoot(container as unknown as Element);
    root.render(<ProjectDetailView />);
    await drain();
  });
}
async function navigateTo(projectId: string) {
  h.projectId = projectId;
  await renderView();
}
async function settleOverview(index: number, overview: ProjectOverview) {
  await act(async () => { overviewRequests[index].request.resolve(overview); await drain(); });
}
async function settleArchive(index: number, project: Project) {
  await act(async () => { archiveRequests[index].request.resolve(project); await drain(); });
}
async function failArchive(index: number, error: unknown) {
  await act(async () => { archiveRequests[index].request.reject(error); await drain(); });
}
async function settleRestore(index: number, project: Project) {
  await act(async () => { restoreRequests[index].request.resolve(project); await drain(); });
}
async function failRestore(index: number, error: unknown) {
  await act(async () => { restoreRequests[index].request.reject(error); await drain(); });
}
async function clickButton(label: string) {
  const button = buttonByText(label);
  if (!button) throw new Error(`No rendered control containing "${label}"`);
  await act(async () => {
    button.dispatchEvent({ type: "click", bubbles: true });
    await drain();
  });
}
beforeEach(() => {
  document = new TestDocument();
  container = document.createElement("div");
  document.body.appendChild(container);
  overviewRequests = [];
  archiveRequests = [];
  restoreRequests = [];
  h.confirm = true;
  h.active = h.pack;
  h.projectId = projectA.id;
  const window = {
    document,
    Element: TestNode,
    HTMLElement: TestNode,
    HTMLIFrameElement: TestNode,
    Node: TestNode,
    confirm: () => h.confirm,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  };
  document.defaultView = window;
  vi.stubGlobal("document", document);
  vi.stubGlobal("window", window);
  vi.stubGlobal("navigator", { userAgent: "test" });
  vi.stubGlobal("IS_REACT_ACT_ENVIRONMENT", true);
  h.api.getProjectOverview.mockReset().mockImplementation((projectId: string) => {
    const request = deferred<ProjectOverview>();
    overviewRequests.push({ projectId, request });
    return request.promise;
  });
  h.api.archiveProject.mockReset().mockImplementation((projectId: string) => {
    const request = deferred<Project>();
    archiveRequests.push({ projectId, request });
    return request.promise;
  });
  h.api.restoreProject.mockReset().mockImplementation((projectId: string) => {
    const request = deferred<Project>();
    restoreRequests.push({ projectId, request });
    return request.promise;
  });
  h.api.updateProject.mockReset().mockResolvedValue(projectA);
});
afterEach(() => {
  act(() => root?.unmount());
  vi.unstubAllGlobals();
});
describe("project detail mutation route fence", () => {
  it("still applies a same-project archive success to the displayed record", async () => {
    // Over-refusal control: the fence retires only answers whose subject is
    // no longer the route. The normal archive flow must keep working,
    // including the affordance flip from Restore back to Archive.
    await mountView();
    await settleOverview(0, overviewA);
    expect(buttonByText("projects.archive")).toBeTruthy();
    await clickButton("projects.archive");
    expect(archiveRequests.map((entry) => entry.projectId)).toEqual([projectA.id]);
    await settleArchive(0, projectAArchived);
    expect(headingText()).toBe("Alpha Fence Project");
    expect(buttonByText("projects.restore")).toBeTruthy();
  });
  it("still applies a same-project restore success to the displayed record", async () => {
    await mountView();
    await settleOverview(0, overviewAArchived);
    expect(buttonByText("projects.restore")).toBeTruthy();
    await clickButton("projects.restore");
    expect(restoreRequests.map((entry) => entry.projectId)).toEqual([projectA.id]);
    await settleRestore(0, projectAActiveAgain);
    expect(headingText()).toBe("Alpha Fence Project");
    expect(buttonByText("projects.archive")).toBeTruthy();
  });
  it("keeps the page operating on the project the member navigated to when a restore success for the project they left lands late", async () => {
    // The restore-side counterpart of #1181 case 3. replaceProject patches
    // whatever snapshot is displayed, so an unfenced A record would rewrite
    // B's page to act on A while the route says B.
    await mountView();
    await settleOverview(0, overviewAArchived);
    await clickButton("projects.restore");
    expect(restoreRequests.map((entry) => entry.projectId)).toEqual([projectA.id]);
    await navigateTo(projectB.id);
    await settleOverview(1, memberB);
    expect(headingText()).toBe("Beta Fence Project");
    await settleRestore(0, projectAActiveAgain);
    expect(headingText()).toBe("Beta Fence Project");
    expect(badgeNodes().map((node) => node.textContent)).toEqual(["projects.role.editor"]);
    expect(container.textContent ?? "").not.toContain("Alpha Fence Project");
    expect(linkProjectIds()).not.toContain(projectA.id);
  });
  it("does not let a revocation answer for a restore on the project the member left drop the current project's badge", async () => {
    // The restore-side counterpart of #1181 case 4. A's 403 is truthful
    // about A; acting on it while the route says B would clear a snapshot
    // the member is entitled to look at and plant A's message in B's page.
    await mountView();
    await settleOverview(0, overviewAArchived);
    await clickButton("projects.restore");
    await navigateTo(projectB.id);
    await settleOverview(1, memberB);
    expect(badgeNodes().map((node) => node.textContent)).toEqual(["projects.role.editor"]);
    await failRestore(0, apiError(403, "Workspace access has ended."));
    expect(badgeNodes().map((node) => node.textContent)).toEqual(["projects.role.editor"]);
    expect(headingText()).toBe("Beta Fence Project");
    expect(alertNodes().map((node) => node.textContent).join(" ")).not.toContain("Workspace access has ended.");
  });
  it("keeps the #1044 disposition on a same-project revocation answer while fenced answers cannot trigger it", async () => {
    // Fence/regression interplay: with the route unchanged, the archive 403
    // must still fail the badge closed and leave the retry affordance,
    // exactly the behavior the badge lane shipped. The fence only retires
    // cross-project answers, never the same-project definitive path.
    await mountView();
    await settleOverview(0, overviewA);
    await clickButton("projects.archive");
    await failArchive(0, apiError(403, "Workspace access has ended."));
    expect(badgeNodes()).toHaveLength(0);
    expect(alertNodes().map((node) => node.textContent).join(" ")).toContain("Workspace access has ended.");
    expect(buttonByText("error.retry")).toBeTruthy();
  });
});
