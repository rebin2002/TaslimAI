import { act, type ReactNode } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Project, ProjectOverview } from "@/lib/api";

// Synthetic DOM harness, in the style established by ActivityBell.badgeFence.test.tsx
// and GenerationJobsView.workspaceFence.test.tsx: no jsdom, no network, no provider,
// no real account or workspace. The real ProjectDetailView is mounted, because the
// defect this file pins is a commit order inside that mounted page.
//
// SCOPE - the ordering fence only. #1044 owns the question "what does a failed
// project-detail request do to the snapshot that is already on screen" and answers
// it with resolveProjectDetailSnapshotAfterFailure. This file owns the question
// "what does a definitive access answer do to the snapshots that were already in
// flight when it arrived". Those two are different: a response carries the
// membership the server captured BEFORE the revoke committed (a cached project
// response, or a role refresh issued a moment earlier), and applying it after the
// page has failed closed re-pins the workspace member role badge, the metadata role
// claim and the edit/archive affordances for a principal the server has just
// refused. Every fixture below is synthetic.
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
  focus() { /* these cases do not move focus */ }
  blur() { /* these cases do not move focus */ }
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
  createTextNode(value: string) { return new TestNodeText(value, this.ownerDocument); }
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
type Deferred<T> = { promise: Promise<T>; resolve: (value: T) => void; reject: (error: unknown) => void };
function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}

const h = vi.hoisted(() => {
  const translate = (key: string, variables?: Record<string, string>) =>
    Object.entries(variables ?? {}).reduce((value, [name, replacement]) => value.replace(`{${name}}`, replacement), key);
  return {
    // Two locale-context identities. LocaleProvider memoises a new context value
    // (and therefore a new `t`) per installed language pack, and the detail page
    // lists `t` in its load-effect dependencies, so swapping the identity here is
    // what a header language-pack switch does to the mounted page: the role is
    // refreshed while the page stays mounted.
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
// The three membership-scoped sections and the edit form render below the badge
// and own their own requests; stubbing them keeps this file on the ordering fence
// and off other lanes' paths.
vi.mock("@/components/ProjectForm", () => ({ ProjectForm: () => null }));
vi.mock("@/components/ProjectContextEditor", () => ({ ProjectContextEditor: () => null }));
vi.mock("@/components/ProjectFilesSection", () => ({ ProjectFilesSection: () => null }));
vi.mock("@/components/ProjectAssetsSection", () => ({ ProjectAssetsSection: () => null }));

import { ProjectDetailView } from "./ProjectDetailView";

function makeProject(overrides: Partial<Project> = {}): Project {
  return {
    id: "project-synthetic-1", workspaceId: "workspace-synthetic-1", name: "Ordering Fence Project",
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
let restoreRequests: Deferred<Project>[];

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
  // A header language-pack switch with the page still mounted: re-render under the
  // other locale-context identity and flush the re-fired load effect.
  h.active = h.active === h.packA ? h.packB : h.packA;
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
async function failRestore(index: number, error: unknown) {
  await act(async () => { restoreRequests[index].reject(error); await drain(); });
}
async function clickButton(label: string) {
  const button = buttonByText(label);
  if (!button) throw new Error(`No rendered control containing "${label}"`);
  await act(async () => {
    button.dispatchEvent({ type: "click", bubbles: true });
    await drain();
  });
}
function expectAuthorizedUi() {
  expect(badgeNodes().map((node) => node.textContent)).toEqual(["projects.role.owner"]);
  expect(buttonByText("projects.edit")).toBeTruthy();
}
function expectNoAuthorizedUi(message: string) {
  expect(badgeNodes()).toHaveLength(0);
  expect(buttonByText("projects.edit")).toBeUndefined();
  expect(buttonByText("projects.archive")).toBeUndefined();
  expect(buttonByText("projects.restore")).toBeUndefined();
  expect(container.textContent ?? "").not.toContain("projects.role.owner");
  expect(message.length).toBeGreaterThan(0);
}

beforeEach(() => {
  document = new TestDocument();
  container = document.createElement("div");
  document.body.appendChild(container);
  overviewRequests = [];
  archiveRequests = [];
  restoreRequests = [];
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
  h.api.restoreProject.mockReset().mockImplementation(() => {
    const request = deferred<Project>();
    restoreRequests.push(request);
    return request.promise;
  });
  h.api.updateProject.mockReset().mockResolvedValue(makeProject());
});

afterEach(() => {
  act(() => root?.unmount());
  vi.unstubAllGlobals();
});

describe("project detail overview responses against a confirmed revocation", () => {
  it("pins the baseline: an authorized snapshot shows the badge and both controls", async () => {
    // The behavior the fence must not damage: with no revocation in play, an
    // overview response paints the role and the membership-scoped controls.
    await mountView();
    await settleOverview(0, makeOverview("Owner"));
    expectAuthorizedUi();
    expect(overviewRequests).toHaveLength(1);
  });

  it("does not let a role refresh issued before the revocation resurrect the badge after an archive answers 403", async () => {
    // The defect. The member's role is revoked while the page is mounted. Their
    // archive click is answered 403, so the page fails closed. A language-pack
    // switch had already put a second overview read on the wire, and that read
    // carries the membership the server captured before the revoke committed.
    // The late 200 must not re-pin the authorized badge, the metadata role claim
    // or the edit/archive affordances for a principal the server just refused.
    const authorized = makeOverview("Owner");
    await mountView();
    await settleOverview(0, authorized);
    expectAuthorizedUi();

    await clickButton("projects.archive");
    expect(archiveRequests).toHaveLength(1);
    await switchLanguagePack();
    expect(overviewRequests).toHaveLength(2); // in flight when the answer arrives

    await failArchive(0, apiError(403, "Workspace access has ended."));
    expect(badgeNodes()).toHaveLength(0);

    await settleOverview(1, authorized); // ...and it resolves with the stale Owner snapshot

    expectNoAuthorizedUi(alertNodes().map((node) => node.textContent).join(" "));
    expect(alertNodes().map((node) => node.textContent).join(" ")).toContain("Workspace access has ended.");
  });

  it("does not let a pre-revocation refresh resurrect the badge when the 404 mutation path confirms the removal", async () => {
    // Same ordering, other answers: the restore route folds "no membership" and
    // "gone project" into one 404, and it is reached from the archived state.
    const authorized = makeOverview("Owner", makeProject({ status: "Archived", archivedAt: "2026-11-02T00:00:00Z" }));
    await mountView();
    await settleOverview(0, authorized);
    expect(buttonByText("projects.restore")).toBeTruthy();

    await clickButton("projects.restore");
    expect(restoreRequests).toHaveLength(1);
    await switchLanguagePack();
    expect(overviewRequests).toHaveLength(2);

    await failRestore(0, apiError(404, "PROJECT_NOT_FOUND"));
    await settleOverview(1, authorized);

    expect(badgeNodes()).toHaveLength(0);
    expect(container.textContent ?? "").not.toContain("projects.role.owner");
    expect(alertNodes().map((node) => node.textContent).join(" ")).toContain("PROJECT_NOT_FOUND");
  });

  it("drops a session-expired overview read and any older read still on the wire", async () => {
    // 401 is the third definitive answer, and the one that arrives on a
    // mutation: the session ended while a role refresh was already on the wire.
    // The membership that refresh captured belonged to a session the server has
    // now closed, so its late 200 must not be able to re-authorize the page.
    const authorized = makeOverview("Owner");
    await mountView();
    await settleOverview(0, authorized);
    expectAuthorizedUi();

    await clickButton("projects.archive");
    await switchLanguagePack();
    expect(overviewRequests).toHaveLength(2);

    await failArchive(0, apiError(401, "Session has ended."));
    expect(badgeNodes()).toHaveLength(0);
    await settleOverview(1, authorized);

    expectNoAuthorizedUi(alertNodes().map((node) => node.textContent).join(" "));
    expect(alertNodes().map((node) => node.textContent).join(" ")).toContain("Session has ended.");
  });

  it("fails closed when the revocation is discovered by the overview read itself, then recovers on a fresh read", async () => {
    // The read path can be the definitive answer too (GET overview -> 403 from
    // GetMembershipAsync). Nothing is in flight here, so the fence must not
    // latch: the next read issued after the answer is authoritative again.
    await mountView();
    await settleOverview(0, makeOverview("Owner"));
    expectAuthorizedUi();

    await switchLanguagePack();
    expect(overviewRequests).toHaveLength(2);
    await failOverview(1, apiError(403, "Workspace access has ended."));

    expect(badgeNodes()).toHaveLength(0);
    expect(container.textContent ?? "").not.toContain("projects.role.owner");

    await clickButton("error.retry");
    expect(overviewRequests).toHaveLength(3);
    await settleOverview(2, makeOverview("Viewer"));
    expect(badgeNodes().map((node) => node.textContent)).toEqual(["projects.role.viewer"]);
  });

  it("paints the server's current downgraded role for a read issued after the revocation", async () => {
    // The constraint on any fix, and the false-positive guard: the fence retires
    // snapshots issued BEFORE the definitive answer, not every later response.
    // A retry started after it is authoritative and must paint.
    await mountView();
    await settleOverview(0, makeOverview("Owner"));
    await clickButton("projects.archive");
    await failArchive(0, apiError(403, "Workspace access has ended."));
    expect(badgeNodes()).toHaveLength(0);

    await clickButton("error.retry");
    expect(overviewRequests).toHaveLength(2);
    await settleOverview(1, makeOverview("Viewer"));

    expect(badgeNodes().map((node) => node.textContent)).toEqual(["projects.role.viewer"]);
  });

  it("paints a re-admitted role again after a revocation, without needing a remount", async () => {
    // Second false-positive guard, the opposite direction of the same fence: the
    // epoch is compared, not latched. Remove, re-add, read again - the page shows
    // Owner again while the very same mount is still on screen.
    await mountView();
    await settleOverview(0, makeOverview("Owner"));
    await clickButton("projects.archive");
    await failArchive(0, apiError(403, "Workspace access has ended."));

    await clickButton("error.retry");
    await settleOverview(1, makeOverview("Viewer"));
    expect(badgeNodes().map((node) => node.textContent)).toEqual(["projects.role.viewer"]);

    await clickButton("projects.archive");
    await failArchive(1, apiError(403, "Workspace access has ended."));
    expect(badgeNodes()).toHaveLength(0);

    await clickButton("error.retry");
    await settleOverview(2, makeOverview("Owner"));
    expectAuthorizedUi();
  });

  it("keeps #1044's transient contract: a 5xx refresh does not retire anything and the snapshot stays up", async () => {
    // The fence must fire on definitive answers only. A transient failure keeps
    // the last observed snapshot - the same contract the notification unread
    // badge follows - and must not silently invalidate later reads.
    const authorized = makeOverview("Owner");
    await mountView();
    await settleOverview(0, authorized);

    await switchLanguagePack();
    expect(overviewRequests).toHaveLength(2);
    await failOverview(1, apiError(503, "Upstream is busy."));

    expectAuthorizedUi();
  });

  it("still applies a read that was in flight behind a TRANSIENT mutation failure", async () => {
    // The mirror image of the defect case, and the guard against a fence that
    // bumps on every rejection: a 502 on the mutation says nothing about this
    // principal's access, so it must leave the membership snapshot up AND must
    // not retire the read that is still on the wire. Fixing the race by
    // invalidating on any failure turns this case red. While that read is in
    // flight the page is on its loading state, so the assertion lands once the
    // server's current answer arrives.
    const authorized = makeOverview("Owner");
    const viewer = makeOverview("Viewer");
    await mountView();
    await settleOverview(0, authorized);

    await clickButton("projects.archive");
    expect(archiveRequests).toHaveLength(1);
    await switchLanguagePack();
    expect(overviewRequests).toHaveLength(2);

    await failArchive(0, apiError(502, "Upstream gateway error."));
    // A transient answer keeps the snapshot (#1044) and retires nothing, so the
    // read issued before it is still the freshest answer the page can get.
    await settleOverview(1, viewer);
    expect(badgeNodes().map((node) => node.textContent)).toEqual(["projects.role.viewer"]);
  });

  it("drops the pre-revocation response even when a later refresh re-issues from the request cache", async () => {
    // The hole a per-render guard would leave. The load effect keeps a
    // single-slot cache keyed on project:loadAttempt, so a second language-pack
    // switch while the first refresh is still on the wire re-uses that same
    // response instead of issuing a new read; `loadAttempt` has not moved, so
    // there is nothing to tell the two refreshes apart. Reading the
    // access-ended marker at effect time would therefore hand the stale answer
    // a fresh epoch and paint it. The epoch is stored WITH the request, so this
    // response still carries the epoch it was actually issued under - and is
    // dropped.
    const authorized = makeOverview("Owner");
    await mountView();
    await settleOverview(0, authorized);

    await clickButton("projects.archive");
    await switchLanguagePack(); // read 1 issued before the revocation
    expect(overviewRequests).toHaveLength(2);
    await failArchive(0, apiError(403, "Workspace access has ended."));

    await switchLanguagePack(); // re-fires the effect: same key, same cached response
    expect(overviewRequests).toHaveLength(2);

    await settleOverview(1, authorized);
    expect(badgeNodes()).toHaveLength(0);
    expect(buttonByText("projects.edit")).toBeUndefined();
    expect(container.textContent ?? "").not.toContain("projects.role.owner");
    expect(alertNodes().map((node) => node.textContent).join(" ")).toContain("Workspace access has ended.");

    // And the fresh read that follows it is still believed.
    await clickButton("error.retry");
    expect(overviewRequests).toHaveLength(3);
    await settleOverview(2, makeOverview("Viewer"));
    expect(badgeNodes().map((node) => node.textContent)).toEqual(["projects.role.viewer"]);
  });
});
