import { act, type ReactNode } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Project, ProjectOverview } from "@/lib/api";

// Synthetic DOM harness, in the style established by
// GenerationJobsView.workspaceFence.test.tsx, ActivityBell.badgeFence.test.tsx and
// ProjectDetailView.staleSnapshotRace.test.tsx: no jsdom, no network, no provider,
// no real account or workspace. The real ProjectDetailView is mounted, and the only
// thing that changes between the two projects below is the route parameter, which is
// exactly what a member clicking from /projects/A to /projects/B does.
//
// THIS FILE OWNS ONE THING AND ONE THING ONLY: the transition of the page from one
// project to another while project A's answer is still on the wire.
//
// The workspace-member-role-badge lane (#1044) owns the decision itself - the pure
// `resolveProjectDetailSnapshotAfterFailure` contract and its unit tests, the static
// source pins in ProjectDetailView.test.tsx, and the E2E journey in
// e2e/workspace.spec.ts - and the ordering lane (#1098/#1100) owns the ORDER between
// a confirmed revocation and a late overview response FOR THE SAME PROJECT. Every one
// of those settles, or fails, the read of the project the member is looking at. None
// of them moves the member to a different project first.
//
// That leaves the route-transition invariant unpinned: a member who loads project A -
// a membership they may have just lost - and then navigates to project B, where they
// are still a member, must never end up displaying, or acting on, A's
// membership-scoped role or A's project record while the route says B. The load effect
// fences its own two continuations with a per-run `active` flag
// (ProjectDetailView.tsx:35,57 - `if (active) setOverview(next)` and the early return
// in its catch), and the in-flight cache is keyed by `${projectId}:${loadAttempt}`
// (:36) so a read issued for A can never be handed to B. The two membership-scoped
// MUTATION handlers (archive :64, restore :75) have no such fence: they resolve into
// `replaceProject`, which patches whatever snapshot is currently displayed.
//
// Cases 1 and 2 pin the fence the page has. Cases 3 and 4 pin the same invariant on
// the mutation continuations, which is where it is not yet held; they are RED by
// design and are the gap this file reports. No production path is edited here, and no
// path owned by #1044, #1098 or #1100 is touched.

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
    // ONE stable locale context for the whole file. The ordering lane (#1098) swaps
    // the `t` identity to make the mounted page re-read the role; here the pack is
    // deliberately constant so that the only thing able to re-fire the load effect is
    // `params.projectId`, i.e. the route transition under test.
    pack: { locale: "en", t: translate },
    active: null as { locale: string; t: (key: string, variables?: Record<string, string>) => string } | null,
    // The route parameter. This is the whole point of the file: it changes mid-session.
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
// `next/link` needs an App Router context this provider-free harness does not
// provide; a plain anchor keeps every href the assertions below read.
vi.mock("next/link", async () => {
  const { createElement } = await import("react");
  type LinkProps = { href: string; className?: string; "aria-label"?: string; children?: ReactNode };
  return {
    default: ({ href, className, "aria-label": ariaLabel, children }: LinkProps) =>
      createElement("a", { href, className, "aria-label": ariaLabel }, children),
  };
});
vi.mock("@/components/LocaleProvider", () => ({ useLocale: () => h.active }));
// The membership-scoped sections and the edit form own their own requests; stubbing
// them keeps this file on the route-transition fence and off other lanes' paths.
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

// Synthetic fixtures only: no production record, no secret, no provider call, no real
// workspace. Two distinct memberships, so a leaked one is always identifiable.
function makeProject(overrides: Partial<Project> = {}): Project {
  return {
    id: "project-synthetic-a", workspaceId: "workspace-synthetic-a", name: "Alpha Transition Project",
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
const projectB = makeProject({ id: "project-synthetic-b", workspaceId: "workspace-synthetic-b", name: "Beta Transition Project" });
// The membership A's in-flight read carries: the pre-revocation claim the server had
// already captured. B's is the member's current, still-valid, LOWER claim.
const authorizedA = makeOverview("Owner", projectA);
const memberB = makeOverview("Editor", projectB);
function apiError(status: number, message: string) {
  return Object.assign(new Error(message), { status });
}

let document: TestDocument;
let container: TestNode;
let root: Root | undefined;
let overviewRequests: { projectId: string; request: Deferred<ProjectOverview> }[];
let archiveRequests: { projectId: string; request: Deferred<Project> }[];

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
  // The client-side half of clicking a different project in the list: the page stays
  // mounted, the route parameter changes, and the load effect re-runs for the new
  // project while the previous project's answer is still on the wire.
  h.projectId = projectId;
  await renderView();
}
async function settleOverview(index: number, overview: ProjectOverview) {
  await act(async () => { overviewRequests[index].request.resolve(overview); await drain(); });
}
async function failOverview(index: number, error: unknown) {
  await act(async () => { overviewRequests[index].request.reject(error); await drain(); });
}
async function settleArchive(index: number, project: Project) {
  await act(async () => { archiveRequests[index].request.resolve(project); await drain(); });
}
async function failArchive(index: number, error: unknown) {
  await act(async () => { archiveRequests[index].request.reject(error); await drain(); });
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
  h.api.restoreProject.mockReset().mockResolvedValue(projectA);
  h.api.updateProject.mockReset().mockResolvedValue(projectA);
});

afterEach(() => {
  act(() => root?.unmount());
  vi.unstubAllGlobals();
});

describe("project detail role badge route-transition fence", () => {
  it("does not let the project the member navigated away from repopulate the role badge of the project they arrived on", async () => {
    // The invariant in its plainest form. The member opens /projects/A; the overview
    // read - which carries A's membership-scoped role - is on the wire. Before it
    // lands they click through to /projects/B, where they are an Editor, and B's own
    // read settles first. A's answer is the pre-revocation Owner claim, captured by
    // the server before the revoke committed, and it is the exact value an
    // unfenced `.then` would pin over a page whose route says B.
    await mountView();
    expect(overviewRequests.map((entry) => entry.projectId)).toEqual([projectA.id]);

    await navigateTo(projectB.id);
    expect(overviewRequests.map((entry) => entry.projectId)).toEqual([projectA.id, projectB.id]);
    await settleOverview(1, memberB);
    expect(badgeNodes().map((node) => node.textContent)).toEqual(["projects.role.editor"]);
    expect(headingText()).toBe("Beta Transition Project");

    // A's stale read lands, one tick after the member already lives on B.
    await settleOverview(0, authorizedA);

    expect(badgeNodes().map((node) => node.textContent)).toEqual(["projects.role.editor"]);
    expect(headingText()).toBe("Beta Transition Project");
    expect(container.textContent ?? "").not.toContain("projects.role.owner");
    expect(container.textContent ?? "").not.toContain("Alpha Transition Project");
    // The in-flight cache is keyed by `projectId:loadAttempt`, so answering A late
    // must not re-fire, or reuse, anything for B.
    expect(overviewRequests.map((entry) => entry.projectId)).toEqual([projectA.id, projectB.id]);
  });

  it("does not let a definitive access answer for the project the member left take the current project's badge down", async () => {
    // The same fence seen from the failure side, and the half no revocation test can
    // reach because it never leaves the project. A's read is answered 403 after the
    // member has moved to B: for A that answer is correct, and #1044's disposition
    // invalidates A's snapshot. It must not invalidate B's, which the server has
    // never refused - a revoked membership in one workspace cannot be allowed to log
    // the member out of another one on screen.
    await mountView();
    await navigateTo(projectB.id);
    await settleOverview(1, memberB);
    expect(badgeNodes().map((node) => node.textContent)).toEqual(["projects.role.editor"]);

    await failOverview(0, apiError(403, "Workspace access has ended."));

    expect(badgeNodes().map((node) => node.textContent)).toEqual(["projects.role.editor"]);
    expect(headingText()).toBe("Beta Transition Project");
    // No A-scoped error may surface on B either.
    expect(alertNodes().map((node) => node.textContent).join(" ")).not.toContain("Workspace access has ended.");
  });

  it("keeps the page operating on the project the member navigated to when a mutation answer for the project they left lands late", async () => {
    // The mutation continuation has to hold the same invariant. The member clicks
    // Archive on A, and navigates to B while that POST is on the wire. A's answer is
    // A's archived Project record; `replaceProject` patches it into whatever snapshot
    // is displayed, so an unfenced answer hands B's page A's name, A's status and
    // A's project id - the affordances then act on A while the route says B, which is
    // precisely the stale, membership-scoped privilege this lane is about.
    await mountView();
    await settleOverview(0, authorizedA);
    await clickButton("projects.archive");
    expect(archiveRequests.map((entry) => entry.projectId)).toEqual([projectA.id]);

    await navigateTo(projectB.id);
    await settleOverview(1, memberB);
    expect(headingText()).toBe("Beta Transition Project");

    await settleArchive(0, makeProject({ id: projectA.id, status: "Archived", archivedAt: "2026-11-19T00:00:00Z" }));

    expect(headingText()).toBe("Beta Transition Project");
    expect(badgeNodes().map((node) => node.textContent)).toEqual(["projects.role.editor"]);
    // Nothing on screen may have been rewritten to point back at A.
    expect(container.textContent ?? "").not.toContain("Alpha Transition Project");
    expect(linkProjectIds()).not.toContain(projectA.id);
  });

  it("does not let a revocation answer for a mutation on the project the member left drop the current project's badge", async () => {
    // The failure continuation of the same unfenced path. A's POST is answered 403 -
    // truthfully, about A - after the member has navigated to B. The disposition is
    // "invalidate", and with no route fence on this path it clears the snapshot the
    // member is entitled to look at on B and puts A's access message inside B's page.
    await mountView();
    await settleOverview(0, authorizedA);
    await clickButton("projects.archive");

    await navigateTo(projectB.id);
    await settleOverview(1, memberB);
    expect(badgeNodes().map((node) => node.textContent)).toEqual(["projects.role.editor"]);

    await failArchive(0, apiError(403, "Workspace access has ended."));

    expect(badgeNodes().map((node) => node.textContent)).toEqual(["projects.role.editor"]);
    expect(headingText()).toBe("Beta Transition Project");
    expect(alertNodes().map((node) => node.textContent).join(" ")).not.toContain("Workspace access has ended.");
  });
});
