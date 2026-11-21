import { act, type ReactNode } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Project, ProjectOverview } from "@/lib/api";

// Synthetic DOM harness, in the style established by
// GenerationJobsView.workspaceFence.test.tsx and ActivityBell.badgeFence.test.tsx:
// no jsdom, no network, no provider, no real account or workspace. The real
// ProjectDetailView is mounted so the assertions describe what a member can
// actually see on /projects/{id} after their project role is revoked mid-session.
//
// This file owns ONE thing that the workspace-member-role-badge lane (#1044,
// which owns ProjectDetailView.tsx, ProjectDetailView.test.tsx,
// projectDetailAccessState.ts/.test.ts and e2e/workspace.spec.ts) does not:
// the ORDER between a definitive revocation answer and an overview response
// that lands afterwards. #1044 makes every failure path drop the snapshot, and
// pins that contract with pure-function tests, static source-text assertions and
// one Browser E2E journey in which the overview read has already settled before
// the revoking mutation is clicked. None of them covers the race the browser
// actually allows: a role refresh that is still in flight when the mutation
// answers 401/403/404, then resolves with the pre-revocation membership it read
// before the revocation committed (a stale cached project response, or a refresh
// interrupted by the revoke). The load path applies any resolved snapshot
// unconditionally - `if (active) setOverview(next)` - so that late answer
// re-pins the authorized role badge and the edit/archive affordances for a
// principal the server has already refused, and nothing re-checks afterwards.
//
// Every value below is a synthetic fixture. No production code is touched by
// this file; it is test-only and depends on #1044 being present.
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
  // React delegates its listeners to the root container, so a dispatched event has to
  // travel up the ancestor chain the same way a browser event would.
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
  focus() { /* the history list does not move focus in these cases */ }
  blur() { /* the history list does not move focus in these cases */ }
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
    // Two stable locale-context identities. The real LocaleProvider memoises a
    // new context value (and therefore a new `t`) whenever the language pack
    // changes (LocaleProvider.tsx:99-112), and ProjectDetailView's load effect
    // lists `t` in its dependency array (ProjectDetailView.tsx:58). Swapping the
    // identity here is exactly what a header language-pack switch does to the
    // mounted page: the overview - i.e. the role - is refreshed.
    packA: { locale: "en", t: translate },
    packB: { locale: "en", t: (key: string, variables?: Record<string, string>) => translate(key, variables) },
    active: null as { locale: string; t: (key: string, variables?: Record<string, string>) => string } | null,
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
  useParams: () => ({ projectId: "project-synthetic-1" }),
  useRouter: () => h.router,
}));
// `next/link` needs a mounted App Router context this provider-free harness
// deliberately does not provide; rendering it as its final anchor keeps every
// href and label the page asserts on.
vi.mock("next/link", async () => {
  const { createElement } = await import("react");
  type LinkProps = { href: string; className?: string; "aria-label"?: string; children?: ReactNode };
  return {
    default: ({ href, className, "aria-label": ariaLabel, children }: LinkProps) =>
      createElement("a", { href, className, "aria-label": ariaLabel }, children),
  };
});
vi.mock("@/components/LocaleProvider", () => ({ useLocale: () => h.active }));
// The three membership-scoped sections and the edit form are mounted below the
// badge and own their own requests; stubbing them keeps this file on the single
// ordering fence and off other lanes' paths.
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

// Synthetic fixtures only: no production record, no secret, no provider call.
function makeProject(overrides: Partial<Project> = {}): Project {
  return {
    id: "project-synthetic-1", workspaceId: "workspace-synthetic-1", name: "Revocation Race Project",
    description: "A synthetic project used only by this test.", instructions: null, contextNotes: null,
    type: "Marketing", status: "Active",
    createdAt: "2026-11-01T00:00:00Z", updatedAt: "2026-11-01T00:00:00Z", archivedAt: null, ...overrides,
  };
}
function makeOverview(role: string, project: Project = makeProject()): ProjectOverview {
  return {
    project,
    workspace: { id: "workspace-synthetic-1", name: "Synthetic Workspace", slug: "synthetic", type: "Business", role },
    counts: { files: 3, assets: 2, conversations: 1, activity: 4 },
    conversations: [],
    recentActivity: [],
  };
}
function apiError(status: number, message: string) {
  return Object.assign(new Error(message), { status });
}

let document: TestDocument;
let container: TestNode;
let root: Root | undefined;
let overviewRequests: Deferred<ProjectOverview>[];
let archiveRequests: Deferred<Project>[];

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
function buttonByText(label: string) {
  return findTag(container, "BUTTON").find((node) => (node.textContent ?? "").includes(label));
}
function drain() { return new Promise((resolve) => setTimeout(resolve, 0)); }

async function mountView() {
  await act(async () => {
    root = createRoot(container as unknown as Element);
    root.render(<ProjectDetailView />);
    await drain();
  });
}
async function switchLanguagePack() {
  // A header language-pack switch while the page stays mounted: re-render with
  // the other locale-context identity, then flush the re-fired load effect.
  h.active = h.packB;
  await act(async () => {
    root?.render(<ProjectDetailView />);
    await drain();
  });
}
async function settleOverview(index: number, overview: ProjectOverview) {
  await act(async () => { overviewRequests[index].resolve(overview); await drain(); });
}
async function failOverview(index: number, error: unknown) {
  await act(async () => { overviewRequests[index].reject(error); await drain(); });
}
async function failArchive(index: number, error: unknown) {
  await act(async () => { archiveRequests[index].reject(error); await drain(); });
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
  h.confirm = true;
  h.active = h.packA;
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
  h.api.getProjectOverview.mockReset().mockImplementation(() => {
    const request = deferred<ProjectOverview>();
    overviewRequests.push(request);
    return request.promise;
  });
  h.api.archiveProject.mockReset().mockImplementation(() => {
    const request = deferred<Project>();
    archiveRequests.push(request);
    return request.promise;
  });
  h.api.restoreProject.mockReset().mockResolvedValue(makeProject({ status: "Active" }));
  h.api.updateProject.mockReset().mockResolvedValue(makeProject());
});

afterEach(() => {
  act(() => root?.unmount());
  vi.unstubAllGlobals();
});

describe("project detail role badge revocation ordering", () => {
  it("drops the role badge and the edit affordance when a mutation answers that access ended", async () => {
    // The mounted-DOM half of #1044: the badge and the membership-scoped edit
    // controls render from one overview snapshot, and a definitive 403 must take
    // all of them down in the same frame.
    await mountView();
    await settleOverview(0, makeOverview("Owner"));
    expect(badgeNodes().map((node) => node.textContent)).toEqual(["projects.role.owner"]);
    expect(buttonByText("projects.edit")).toBeTruthy();

    await clickButton("projects.archive");
    expect(archiveRequests.length).toBe(1);
    await failArchive(0, apiError(403, "Workspace access has ended."));

    expect(badgeNodes()).toHaveLength(0);
    expect(buttonByText("projects.edit")).toBeUndefined();
    // The page also repeats the role in the metadata card; no authorized role
    // claim may survive anywhere once the server has refused this principal.
    expect(container.textContent ?? "").not.toContain("projects.role.owner");
    expect(alertNodes().map((node) => node.textContent).join(" ")).toContain("Workspace access has ended.");
  });

  it("keeps the role badge down when the revoked membership is replayed by an overview response that lands after the 403", async () => {
    // The race this file exists for. The member's role is revoked while
    // ProjectDetailView is mounted. Their Archive click is answered 403, so the
    // page fails closed; a language-pack switch (the role refresh) had already
    // put a second overview read in flight, and that read carries the
    // pre-revocation membership the server captured before the revoke
    // committed - the stale cached project response. The late 200 must not be
    // allowed to re-pin the authorized role badge, or the edit and archive
    // affordances, for a principal the server has just refused.
    const authorized = makeOverview("Owner");
    await mountView();
    await settleOverview(0, authorized);

    await clickButton("projects.archive");
    expect(archiveRequests.length).toBe(1);
    await switchLanguagePack();
    expect(overviewRequests.length).toBe(2); // the refresh is in flight when the answer arrives

    await failArchive(0, apiError(403, "Workspace access has ended."));
    expect(badgeNodes()).toHaveLength(0);
    await settleOverview(1, authorized); // ...and then resolves with the stale Owner snapshot

    expect(badgeNodes()).toHaveLength(0);
    expect(buttonByText("projects.edit")).toBeUndefined();
    expect(buttonByText("projects.archive")).toBeUndefined();
    expect(container.textContent ?? "").not.toContain("projects.role.owner");
    expect(alertNodes().map((node) => node.textContent).join(" ")).toContain("Workspace access has ended.");
  });

  it("shows the server's current, downgraded role when a retry after the revocation is answered fresh", async () => {
    // The other side of the same fence, and the constraint on any fix: a
    // response fetched AFTER the definitive answer is authoritative. #1044's E2E
    // pins re-admission in the browser; this pins the downgraded-role variant at
    // the mounted page so the ordering guard cannot simply suppress every later
    // overview response.
    await mountView();
    await settleOverview(0, makeOverview("Owner"));
    await clickButton("projects.archive");
    await failArchive(0, apiError(403, "Workspace access has ended."));
    expect(badgeNodes()).toHaveLength(0);

    await clickButton("error.retry");
    expect(overviewRequests.length).toBe(2);
    await settleOverview(1, makeOverview("Viewer"));

    expect(badgeNodes().map((node) => node.textContent)).toEqual(["projects.role.viewer"]);
  });

  it("keeps the role badge down when the refresh after the revocation is interrupted by a transport failure", async () => {
    // An interrupted refresh has no access answer at all, so the disposition
    // stays "keep" and the already-invalidated snapshot must stay off screen.
    await mountView();
    await settleOverview(0, makeOverview("Owner"));
    await clickButton("projects.archive");
    await failArchive(0, apiError(403, "Workspace access has ended."));

    await clickButton("error.retry");
    await failOverview(1, new TypeError("Failed to fetch"));

    expect(badgeNodes()).toHaveLength(0);
    expect(buttonByText("projects.edit")).toBeUndefined();
    expect(alertNodes().map((node) => node.textContent).join(" ")).toContain("Failed to fetch");
  });
});
