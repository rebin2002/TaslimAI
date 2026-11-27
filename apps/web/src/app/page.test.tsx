import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Synthetic DOM harness, in the style established by ProjectContextEditor.test
// (#849) and reused by AssetsViewListing.test: no jsdom, no network, no
// provider - every input is fixed and every assertion is deterministic.
//
// The authenticated home dashboard fans one load out into four workspace-scoped
// requests (projects, conversations, activity, assets) and publishes whatever
// `Promise.all` returns. Before the fence added here, a load started for a
// workspace the user had already left still published: switching Personal ->
// Business while those four requests were open let the retired workspace
// replace the mounted dashboard, so the Continue rail, its activity status
// badge and the composer's project options showed records belonging to a
// workspace that was no longer mounted - and a late rejection from the old
// workspace raised a failure banner over a dashboard that had loaded fine.
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
  focus() { this.ownerDocument.activeElement = this; }
  blur() { if (this.ownerDocument.activeElement === this) this.ownerDocument.activeElement = this.ownerDocument.body; }
}
type TestEvent = { type: string; bubbles?: boolean; target?: TestNode; currentTarget?: TestNode; defaultPrevented?: boolean; preventDefault?: () => void; stopPropagation?: () => void; propagationStopped?: boolean; };
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
function findByClass(node: TestNode, className: string): TestNode[] {
  return [
    ...(node.getAttribute("class")?.split(/\s+/).includes(className) ? [node] : []),
    ...node.childNodes.flatMap((child) => findByClass(child, className)),
  ];
}
function findByTestId(node: TestNode, testId: string): TestNode | null {
  if (node.getAttribute("data-testid") === testId) return node;
  for (const child of node.childNodes) {
    const match = findByTestId(child, testId);
    if (match) return match;
  }
  return null;
}

type Deferred<T> = { promise: Promise<T>; resolve: (value: T) => void; reject: (error: unknown) => void };
function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}

// Stable identities: a fresh locale object handed back on every render would
// rebuild the component's useCallback and re-run the loader indefinitely.
const harness = vi.hoisted(() => ({
  locale: { t: (key: string) => key, locale: "en" },
  auth: { workspace: null as unknown, userId: "user-personal" },
  router: { push: vi.fn(), replace: vi.fn(), prefetch: vi.fn() },
  api: {
    listProjects: vi.fn(),
    listConversations: vi.fn(),
    listActivity: vi.fn(),
    listAssets: vi.fn(),
    createConversation: vi.fn(),
    uploadFile: vi.fn(),
    sendMessage: vi.fn(),
  },
}));

vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => ({ user: { id: harness.auth.userId, displayName: "Rebin" }, workspace: harness.auth.workspace, loading: false }),
}));
vi.mock("@/components/LocaleProvider", () => ({ useLocale: () => harness.locale }));
vi.mock("@/lib/api", () => ({ api: harness.api }));
vi.mock("@/components/ProtectedPage", () => ({
  ProtectedPage: ({ children }: { children: unknown }) => <>{children as never}</>,
}));
vi.mock("@/components/StudioChooser", () => ({ StudioChooser: () => <div className="studio-chooser" /> }));
vi.mock("@/components/HomeComposer", () => ({
  HomeComposer: (props: {
    value: string;
    busy: boolean;
    files: File[];
    projects: { id: string; name: string }[];
    onChange: (value: string) => void;
    onSubmit: () => void;
    onFilesChange: (files: File[]) => void;
    onProjectChange: (projectId: string) => void;
  }) => {
    const canCreate = !props.busy && (Boolean(props.value.trim()) || props.files.length > 0);
    return (
      <div className="home-composer-stub">
        {props.projects.map((project) => <span key={project.id}>{project.name}</span>)}
        {props.files.map((file, index) => <span key={`${file.name}-${index}`}>{file.name}</span>)}
        <button type="button" data-testid="set-prompt" onClick={() => props.onChange("Summarize the attached PDF")}>Set instructions</button>
        <button type="button" data-testid="attach-fixture" onClick={() => props.onFilesChange([new File([new Uint8Array([1, 2, 3])], "home-fixture.pdf", { type: "application/pdf" })])}>Attach local fixture</button>
        <button type="button" data-testid="attach-scoped-fixture" onClick={() => props.onFilesChange([new File([new Uint8Array([4, 5, 6])], `${props.projects[0]?.id ?? "unscoped"}-private.pdf`, { type: "application/pdf" })])}>Attach current-scope fixture</button>
        <button type="button" data-testid="select-project" onClick={() => { const project = props.projects[0]; if (project) props.onProjectChange(project.id); }}>Select current project</button>
        <button type="button" data-testid="home-create" disabled={!canCreate} onClick={props.onSubmit}>Create</button>
        <button type="button" data-testid="home-create-twice" disabled={!canCreate} onClick={() => { props.onSubmit(); props.onSubmit(); }}>Double-submit fixture</button>
      </div>
    );
  },
}));
vi.mock("next/link", () => ({
  default: (props: { href: string; className?: string; children?: unknown }) => <a href={props.href} className={props.className}>{props.children as never}</a>,
}));
vi.mock("next/navigation", () => ({
  useRouter: () => harness.router,
  usePathname: () => "/",
}));
vi.mock("lucide-react", () => {
  const Icon = (props: Record<string, unknown>) => <span {...props} />;
  return { ArrowRight: Icon, ArrowUpRight: Icon, FolderKanban: Icon, LibraryBig: Icon, LoaderCircle: Icon, MessageSquare: Icon, Sparkles: Icon };
});

import HomePage from "./page";

const PERSONAL = { id: "workspace-personal", name: "Personal", type: "Personal", role: "Owner", slug: "personal" };
const BUSINESS = { id: "workspace-business", name: "Business", type: "Business", role: "Admin", slug: "business" };
type WorkspaceFixture = typeof PERSONAL;

const projectRow = (workspaceId: string, label: string) => ({
  id: `project-${label}`, workspaceId, name: `${label}-plan`, description: null, instructions: null,
  contextNotes: null, type: "Marketing", status: "Active" as const,
  createdAt: "2026-09-01T00:00:00.000Z", updatedAt: "2026-09-25T00:00:00.000Z", archivedAt: null,
});
const conversationRow = (workspaceId: string, label: string) => ({
  id: `conversation-${label}`, workspaceId, projectId: null, title: `${label}-thread`, status: "Active" as const,
  createdAt: "2026-09-01T00:00:00.000Z", updatedAt: "2026-09-21T00:00:00.000Z", lastMessageAt: "2026-09-23T00:00:00.000Z",
});
const activityRow = (workspaceId: string, label: string) => ({
  jobId: `job-${label}`, workspaceId, projectId: null, jobType: "image" as const, title: `${label}-hero-render`,
  status: "Running" as const, progressPercent: 66, createdAt: "2026-09-24T00:00:00.000Z", completedAt: null,
  isRead: false, safeFailureMessage: null, assetId: null,
});
const assetRow = (workspaceId: string, label: string) => ({
  id: `asset-${label}`, workspaceId, projectId: null, projectName: null, name: `${label}-overview`, description: null,
  assetType: "document" as const, mimeType: "application/pdf", status: "Active" as const, hasFile: true, canPreview: true,
  fileSizeBytes: null, sourceStudio: null, sourceJobTitle: null, createdAt: "2026-09-19T00:00:00.000Z",
  updatedAt: "2026-09-22T00:00:00.000Z", archivedAt: null, representations: [],
});

function rowsFor(workspaceId: string, label: string) {
  return {
    projects: [projectRow(workspaceId, label)],
    conversations: [conversationRow(workspaceId, label)],
    items: [activityRow(workspaceId, label)],
    assets: [assetRow(workspaceId, label)],
  };
}
type Rows = ReturnType<typeof rowsFor>;
type Bag = { projects: Deferred<unknown[]>; conversations: Deferred<unknown[]>; activity: Deferred<unknown>; assets: Deferred<unknown> };
const openBag = (): Bag => ({ projects: deferred(), conversations: deferred(), activity: deferred(), assets: deferred() });
function resolveBag(bag: Bag, rows: Rows) {
  bag.projects.resolve(rows.projects);
  bag.conversations.resolve(rows.conversations);
  bag.activity.resolve({ items: rows.items, page: 1, pageSize: 12, totalCount: 1, totalPages: 1, unreadCount: 1 });
  bag.assets.resolve({ items: rows.assets, page: 1, pageSize: 12, totalCount: 1, totalPages: 1, unreadCount: 0 });
}
function wireBags(bags: { personal: Bag; business: Bag }) {
  const route = (personal: Promise<unknown>, business: Promise<unknown>) => (workspaceId: string) => (workspaceId === PERSONAL.id ? personal : business);
  harness.api.listProjects.mockImplementation(route(bags.personal.projects.promise, bags.business.projects.promise));
  harness.api.listConversations.mockImplementation(route(bags.personal.conversations.promise, bags.business.conversations.promise));
  harness.api.listActivity.mockImplementation(route(bags.personal.activity.promise, bags.business.activity.promise));
  harness.api.listAssets.mockImplementation(route(bags.personal.assets.promise, bags.business.assets.promise));
}

let document: TestDocument;
let root: Root;
let container: TestNode;
const text = () => container.textContent;

beforeEach(() => {
  document = new TestDocument();
  container = document.createElement("div");
  document.body.appendChild(container);
  vi.stubGlobal("document", document);
  const window = { document, HTMLElement: TestNode, HTMLIFrameElement: TestNode, location: { search: "" }, addEventListener: vi.fn(), removeEventListener: vi.fn() };
  document.defaultView = window;
  vi.stubGlobal("window", window);
  vi.stubGlobal("navigator", { userAgent: "test" });
  vi.stubGlobal("IS_REACT_ACT_ENVIRONMENT", true);
  harness.auth.workspace = PERSONAL;
  harness.auth.userId = "user-personal";
  harness.locale = { t: (key: string) => key, locale: "en" };
  for (const fn of Object.values(harness.api)) fn.mockReset();
  harness.api.listProjects.mockResolvedValue([]);
  harness.api.listConversations.mockResolvedValue([]);
  harness.api.listActivity.mockResolvedValue({ items: [] });
  harness.api.listAssets.mockResolvedValue({ items: [] });
  harness.api.createConversation.mockResolvedValue({ id: "conversation-fixture" });
  harness.api.uploadFile.mockImplementation(async (_workspaceId: string, file: File) => ({ id: `uploaded-${file.name}` }));
  harness.api.sendMessage.mockResolvedValue(undefined);
  harness.router.push.mockReset();
  harness.router.replace.mockReset();
  harness.router.prefetch.mockReset();
});

afterEach(() => {
  act(() => root?.unmount());
  vi.unstubAllGlobals();
});

async function flush(rounds = 8) {
  await act(async () => {
    for (let round = 0; round < rounds; round += 1) await Promise.resolve();
  });
}

function mount() {
  act(() => {
    root = createRoot(container as unknown as Element);
    root.render(<HomePage />);
  });
}

function switchWorkspace(next: WorkspaceFixture) {
  harness.auth.workspace = next;
  act(() => { root.render(<HomePage />); });
}

function switchUser(userId: string) {
  harness.auth.userId = userId;
  act(() => { root.render(<HomePage />); });
}

function clickTestId(testId: string): boolean {
  const target = findByTestId(container, testId);
  if (!target || target.disabled || target.getAttribute("disabled") !== null) return false;
  act(() => { target.dispatchEvent({ type: "click", bubbles: true }); });
  return true;
}

function mockResolvedDashboardForWorkspaces() {
  const labelFor = (workspaceId: string) => workspaceId === PERSONAL.id ? "personal" : "business";
  harness.api.listProjects.mockImplementation((workspaceId: string) => Promise.resolve([projectRow(workspaceId, labelFor(workspaceId))]));
  harness.api.listConversations.mockImplementation((workspaceId: string) => Promise.resolve([conversationRow(workspaceId, labelFor(workspaceId))]));
  harness.api.listActivity.mockImplementation((workspaceId: string) => Promise.resolve({ items: [activityRow(workspaceId, labelFor(workspaceId))] }));
  harness.api.listAssets.mockImplementation((workspaceId: string) => Promise.resolve({ items: [assetRow(workspaceId, labelFor(workspaceId))] }));
}

describe("Home creation API flow with local fixtures", () => {
  it("sends explicit text plus the selected file within the current workspace and project", async () => {
    mockResolvedDashboardForWorkspaces();
    harness.api.createConversation.mockResolvedValue({ id: "conversation-text-file" });
    harness.api.uploadFile.mockResolvedValue({ id: "attachment-text-file" });
    mount();
    await flush();

    expect(clickTestId("set-prompt")).toBe(true);
    expect(clickTestId("attach-fixture")).toBe(true);
    expect(clickTestId("select-project")).toBe(true);
    expect(text()).toContain("home-fixture.pdf");
    expect(clickTestId("home-create")).toBe(true);
    await flush(16);

    expect(harness.api.createConversation).toHaveBeenCalledTimes(1);
    expect(harness.api.createConversation).toHaveBeenCalledWith(PERSONAL.id, { projectId: "project-personal" });
    expect(harness.api.uploadFile).toHaveBeenCalledTimes(1);
    expect(harness.api.uploadFile).toHaveBeenCalledWith(PERSONAL.id, expect.objectContaining({ name: "home-fixture.pdf" }), {
      conversationId: "conversation-text-file",
      projectId: "project-personal",
    });
    expect(harness.api.sendMessage).toHaveBeenCalledTimes(1);
    expect(harness.api.sendMessage).toHaveBeenCalledWith("conversation-text-file", "Summarize the attached PDF", undefined, ["attachment-text-file"]);
    expect(harness.router.push).toHaveBeenCalledWith("/chat/conversation-text-file");
  });

  it("sends the deterministic attachment-only prompt exactly once for a selected local file", async () => {
    mount();
    await flush();
    expect(clickTestId("attach-fixture")).toBe(true);
    expect(clickTestId("home-create-twice")).toBe(true);
    await flush(16);

    expect(harness.api.createConversation).toHaveBeenCalledTimes(1);
    expect(harness.api.uploadFile).toHaveBeenCalledTimes(1);
    expect(harness.api.sendMessage).toHaveBeenCalledTimes(1);
    expect(harness.api.sendMessage).toHaveBeenCalledWith("conversation-fixture", "Please review the attached files.", undefined, ["uploaded-home-fixture.pdf"]);
    expect(harness.router.push).toHaveBeenCalledWith("/chat/conversation-fixture");
  });

  it("drops a prior workspace's local attachment and project before a new-scope request", async () => {
    mockResolvedDashboardForWorkspaces();
    mount();
    await flush();
    expect(clickTestId("attach-fixture")).toBe(true);
    expect(clickTestId("select-project")).toBe(true);
    expect(text()).toContain("home-fixture.pdf");
    expect(text()).toContain("personal-plan");

    switchWorkspace(BUSINESS);
    expect(text()).not.toContain("home-fixture.pdf");
    expect(text()).not.toContain("personal-plan");
    await flush();
    expect(text()).toContain("business-plan");
    expect(findByTestId(container, "home-create")?.getAttribute("disabled")).not.toBeNull();
    expect(clickTestId("home-create")).toBe(false);
    expect(harness.api.createConversation).not.toHaveBeenCalled();
    expect(harness.api.uploadFile).not.toHaveBeenCalled();
    expect(harness.api.sendMessage).not.toHaveBeenCalled();

    expect(clickTestId("select-project")).toBe(true);
    expect(clickTestId("attach-scoped-fixture")).toBe(true);
    expect(text()).toContain("project-business-private.pdf");
    expect(clickTestId("home-create")).toBe(true);
    await flush(16);
    expect(harness.api.createConversation).toHaveBeenCalledTimes(1);
    expect(harness.api.createConversation).toHaveBeenCalledWith(BUSINESS.id, { projectId: "project-business" });
    expect(harness.api.uploadFile).toHaveBeenCalledTimes(1);
    expect(harness.api.uploadFile).toHaveBeenCalledWith(BUSINESS.id, expect.objectContaining({ name: "project-business-private.pdf" }), {
      conversationId: "conversation-fixture",
      projectId: "project-business",
    });
    expect(harness.api.sendMessage).toHaveBeenCalledTimes(1);
    expect(harness.api.sendMessage).toHaveBeenCalledWith("conversation-fixture", "Please review the attached files.", undefined, ["uploaded-project-business-private.pdf"]);
    expect(harness.router.push).toHaveBeenCalledWith("/chat/conversation-fixture");
  });

  it("drops unsent local attachment state when the authenticated user changes in the same mounted workspace", async () => {
    mockResolvedDashboardForWorkspaces();
    mount();
    await flush();
    expect(clickTestId("attach-fixture")).toBe(true);
    expect(text()).toContain("home-fixture.pdf");

    switchUser("user-next");
    expect(harness.auth.workspace).toBe(PERSONAL);
    expect(text()).not.toContain("home-fixture.pdf");
    expect(findByTestId(container, "home-create")?.getAttribute("disabled")).not.toBeNull();
    expect(harness.api.createConversation).not.toHaveBeenCalled();
    expect(harness.api.uploadFile).not.toHaveBeenCalled();
    expect(harness.api.sendMessage).not.toHaveBeenCalled();
    await flush();
  });
});

describe("home dashboard workspace response fence", () => {
  it("publishes the mounted workspace's own dashboard", async () => {
    const bags = { personal: openBag(), business: openBag() };
    wireBags(bags);
    mount();
    await flush();
    expect(harness.api.listProjects).toHaveBeenCalledWith(PERSONAL.id, "Active");
    resolveBag(bags.personal, rowsFor(PERSONAL.id, "personal"));
    await flush();
    expect(text()).toContain("personal-plan");
    expect(text()).toContain("personal-hero-render");
    expect(text()).not.toContain("business-plan");
  });

  it("discards a previous workspace snapshot that resolves after the mounted one", async () => {
    const bags = { personal: openBag(), business: openBag() };
    wireBags(bags);
    mount();
    await flush();

    switchWorkspace(BUSINESS);
    await flush();
    expect(harness.api.listProjects).toHaveBeenCalledWith(BUSINESS.id, "Active");

    resolveBag(bags.business, rowsFor(BUSINESS.id, "business"));
    await flush();
    expect(text()).toContain("business-plan");

    // The Personal load is still open. When it lands it must not replace the
    // Business dashboard the user is now looking at.
    resolveBag(bags.personal, rowsFor(PERSONAL.id, "personal"));
    await flush();

    expect(text()).not.toContain("personal-plan");
    expect(text()).not.toContain("personal-hero-render");
    expect(text()).toContain("business-plan");
    expect(text()).toContain("business-hero-render");
  });

  it("discards a previous workspace snapshot that resolves before the mounted one", async () => {
    const bags = { personal: openBag(), business: openBag() };
    wireBags(bags);
    mount();
    await flush();
    switchWorkspace(BUSINESS);
    await flush();

    // Out-of-order arrival: the retired load answers first. Publishing it would
    // win the later race, so the fence has to reject it by request identity
    // rather than by which response happens to land last.
    resolveBag(bags.personal, rowsFor(PERSONAL.id, "personal"));
    await flush();
    expect(text()).not.toContain("personal-plan");

    resolveBag(bags.business, rowsFor(BUSINESS.id, "business"));
    await flush();
    expect(text()).toContain("business-plan");
  });

  it("discards a previous workspace failure that lands after the mounted one", async () => {
    const bags = { personal: openBag(), business: openBag() };
    wireBags(bags);
    mount();
    await flush();
    switchWorkspace(BUSINESS);
    await flush();
    resolveBag(bags.business, rowsFor(BUSINESS.id, "business"));
    await flush();
    expect(text()).toContain("business-plan");

    bags.personal.conversations.reject(new Error("personal workspace load failed"));
    await flush();

    // A retired load cannot raise an alert over a dashboard that loaded.
    expect(findByClass(container, "inline-error")).toHaveLength(0);
    expect(text()).toContain("business-plan");
  });

  it("stops showing the previous workspace's records the moment it is left", async () => {
    const bags = { personal: openBag(), business: openBag() };
    wireBags(bags);
    mount();
    await flush();
    resolveBag(bags.personal, rowsFor(PERSONAL.id, "personal"));
    await flush();
    expect(text()).toContain("personal-plan");

    switchWorkspace(BUSINESS);
    // Nothing has resolved for Business yet, so the retired Personal projects,
    // activity badge and continuation rows must already be off the screen.
    expect(text()).not.toContain("personal-plan");
    expect(text()).not.toContain("personal-hero-render");
    expect(text()).toContain("home.loadingWork");
  });

  it("reloads for the same workspace without losing the fence", async () => {
    const bags = { personal: openBag(), business: openBag() };
    wireBags(bags);
    mount();
    await flush();
    resolveBag(bags.personal, rowsFor(PERSONAL.id, "personal"));
    await flush();
    expect(text()).toContain("personal-plan");

    // A locale change rebuilds the loader for the workspace that is still
    // mounted: its own response must still be allowed to publish.
    const reloaded = openBag();
    harness.api.listProjects.mockReturnValue(reloaded.projects.promise);
    harness.api.listConversations.mockReturnValue(reloaded.conversations.promise);
    harness.api.listActivity.mockReturnValue(reloaded.activity.promise);
    harness.api.listAssets.mockReturnValue(reloaded.assets.promise);
    harness.locale = { t: (key: string) => key, locale: "ar" };
    switchWorkspace(PERSONAL);
    await flush();
    resolveBag(reloaded, rowsFor(PERSONAL.id, "personal"));
    await flush();
    expect(text()).toContain("personal-plan");
    expect(text()).toContain("personal-hero-render");
  });
});
