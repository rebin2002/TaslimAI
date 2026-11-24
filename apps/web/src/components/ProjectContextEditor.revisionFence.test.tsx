import React, { act } from "react";
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
  private currentValue = "";
  private currentChecked = false;
  private currentSelected = false;
  private currentType: string | null = null;
  disabled = false;
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
  // React decides whether a change on an element is a text edit from `node.type`,
  // and it writes the input's type during mount, so the property is readable
  // and writable. An input with no explicit attribute is a text field.
  get type() {
    if (this.currentType !== null) return this.currentType;
    const type = this.attributes.get("type");
    if (type) return type.toLowerCase();
    return this.nodeName === "INPUT" ? "text" : this.nodeName === "TEXTAREA" ? "textarea" : "";
  }
  set type(value: string) { this.currentType = String(value); }
  get value() { return this.currentValue; }
  set value(value: string) { this.currentValue = String(value); }
  get checked() { return this.currentChecked; }
  set checked(value: boolean) { this.currentChecked = Boolean(value); }
  get selected() { return this.currentSelected; }
  set selected(value: boolean) { this.currentSelected = Boolean(value); }
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
  defaultView: object;
  // React decides whether it can rely on native `input` events when react-dom is
  // first imported, by probing `"oninput" in document`. Declaring the handler here
  // makes the synthetic document answer like a browser, so a dispatched input
  // event reaches the editor's controlled onChange handlers.
  oninput: ((event: unknown) => void) | null = null;
  onchange: ((event: unknown) => void) | null = null;
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
  private find(predicate: (node: TestNode) => boolean): TestNode | null { const visit = (node: TestNode): TestNode | null => (predicate(node) ? node : node.childNodes.map(visit).find(Boolean) ?? null); return visit(this); }
}

/**
 * The browser side of the project revision fence, driven through the real
 * transport helper instead of a mocked client: the editor must capture the
 * server's `ETag` when a member starts typing, declare that captured revision
 * on every save, and - when the fence refuses it - keep the unsaved text, ask
 * which copy should win, and never quietly re-declare the newer revision.
 *
 * Before this lane the editor sent no `If-Match` at all, so two open tabs
 * always landed on the API's last-write-wins path and a stale draft silently
 * replaced a newer one. The harder case, pinned here too: a page that went stale
 * BEFORE the first keystroke must not be allowed to claim whatever token the row
 * happens to carry at that moment, because that token certifies content the member
 * never saw. So the editor hands the session the copy on screen, and a mismatch is
 * a conflict raised before any write is attempted.
 *
 * The stub below reproduces the published contract: the
 * seven-digit fractional stamp of the production store, a write that moves it
 * forward, a declared revision that must still hold the row, the wildcard that
 * applies over whatever the row holds, and the live revision carried on the
 * 409 answer.
 *
 * Provider-free and network-free: the component, `@/lib/api` and
 * `@/lib/projectRevision` are the real modules, `fetch` is the stub, and React's
 * event plumbing runs on the synthetic DOM established by
 * DocumentStudioView.draftRestore.test.tsx - which is also why the client
 * renderer and the editor are imported dynamically, after the globals are set.
 */
const harness = vi.hoisted(() => ({ t: (key: string) => key }));
vi.mock("@/components/LocaleProvider", () => ({ useLocale: () => ({ locale: "en", t: harness.t }) }));
vi.mock("lucide-react", () => {
  const Icon = (props: Record<string, unknown>) => <span {...props} />;
  return { Check: Icon, FileText: Icon };
});

const testDocument = new TestDocument();
const testContainer = testDocument.createElement("div");
testDocument.body.appendChild(testContainer);
const testWindow = {
  document: testDocument,
  HTMLElement: TestNode,
  HTMLIFrameElement: TestNode,
  addEventListener: vi.fn(),
  removeEventListener: vi.fn(),
  setTimeout: (callback: () => void) => globalThis.setTimeout(callback),
  clearTimeout: (id: ReturnType<typeof globalThis.setTimeout>) => globalThis.clearTimeout(id),
};
testDocument.defaultView = testWindow as unknown as TestDocument["defaultView"];
vi.stubGlobal("document", testDocument);
vi.stubGlobal("window", testWindow);
vi.stubGlobal("navigator", { userAgent: "test" });
vi.stubGlobal("IS_REACT_ACT_ENVIRONMENT", true);

const { createRoot } = await import("react-dom/client");
const { ProjectContextEditor } = await import("@/components/ProjectContextEditor");
const { makeProject, PROJECT_TOKEN, fence } = await import("@/lib/projectRevisionFenceFixtures");
const { ProjectRevisionSession } = await import("@/lib/projectRevision");
const { api } = await import("@/lib/api");

let root: ReturnType<typeof createRoot> | null = null;
let onSavedSpy = vi.fn();
let server: ReturnType<typeof fence>;
let fetchMock: ReturnType<typeof vi.fn>;

async function drain() {
  await new Promise((resolve) => setTimeout(resolve, 0));
  await new Promise((resolve) => setTimeout(resolve, 0));
}

beforeEach(() => {
  // The synthetic document, window and navigator are installed once, at module
  // scope: react-dom reads them when it computes input-event support. Only
  // `fetch` and the fence state are per test.
  server = fence([
    { project: makeProject("project-a", "A instructions", "A notes"), revision: PROJECT_TOKEN },
    { project: makeProject("project-b", "B instructions", "B notes"), revision: '"2026-11-16T09:00:00.0000002Z"' },
  ]);
  fetchMock = vi.fn(server.handler);
  vi.stubGlobal("fetch", fetchMock);
  onSavedSpy = vi.fn();
});

afterEach(() => {
  act(() => root?.unmount());
  root = null;
});

function findNodes(node: TestNode, nodeName: string): TestNode[] {
  return [
    ...(node.nodeName === nodeName ? [node] : []),
    ...node.childNodes.flatMap((child) => findNodes(child, nodeName)),
  ];
}

function findByClass(node: TestNode, className: string): TestNode[] {
  return [
    ...(node.getAttribute("class")?.split(/\s+/).includes(className) ? [node] : []),
    ...node.childNodes.flatMap((child) => findByClass(child, className)),
  ];
}

function contextValues(): string[] {
  return findNodes(testContainer, "TEXTAREA").map((node) => node.value);
}

function conflictBlock(): TestNode[] {
  return findByClass(testContainer, "revision-conflict");
}

function patchCalls() {
  return server.calls.filter((call) => call.method === "PATCH");
}

async function mount(id = "project-a", instructions = "A instructions", notes = "A notes") {
  await act(async () => {
    root = createRoot(testContainer as unknown as Element);
    root.render(<ProjectContextEditor project={makeProject(id, instructions, notes)} onSaved={onSavedSpy} />);
    await drain();
  });
}

async function switchTo(id: string, instructions: string, notes: string) {
  await act(async () => {
    root!.render(<ProjectContextEditor project={makeProject(id, instructions, notes)} onSaved={onSavedSpy} />);
    await drain();
  });
}

/**
 * The parent re-renders the SAME project with a newer copy - what happens when a
 * sibling form saves, or an archive lands and the view folds the answer into its
 * overview. The mounted draft's fields do not move with it, which is the whole
 * point: the fence must judge the row against what the member is looking at, not
 * against the copy the parent happens to hold now.
 */
async function reparent(session?: InstanceType<typeof ProjectRevisionSession>) {
  const latest = server.row("project-a").project;
  await act(async () => {
    root!.render(<ProjectContextEditor project={{ ...latest }} onSaved={onSavedSpy} {...(session ? { revision: session } : {})} />);
    await drain();
  });
}

/** Types into a controlled field the way a browser does. */
async function type(field: TestNode, value: string) {
  const descriptor = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(field), "value");
  descriptor?.set?.call(field, value);
  await act(async () => {
    field.dispatchEvent({ type: "input", bubbles: true });
    await drain();
  });
}

async function submit(): Promise<void> {
  await act(async () => {
    findNodes(testContainer, "FORM")[0].dispatchEvent({ type: "submit", bubbles: true });
    await drain();
  });
}

async function clickByLabel(label: string) {
  const button = findNodes(testContainer, "BUTTON").find((node) => (node.textContent ?? "").includes(label));
  if (!button) throw new Error(`expected a control labelled ${label}`);
  await act(async () => {
    button.dispatchEvent({ type: "click", bubbles: true });
    await drain();
    await drain();
  });
}

async function typeInstructions(value: string) {
  await type(findNodes(testContainer, "TEXTAREA")[0]!, value);
}

describe("ProjectContextEditor revision fence", () => {
  it("captures the server revision when the member starts typing", async () => {
    await mount();
    await typeInstructions("My new instructions");
    expect(contextValues()[0]).toBe("My new instructions");

    await submit();

    const call = patchCalls()[0]!;
    expect(call.ifMatch).toBe(PROJECT_TOKEN);
    expect((call.body as { instructions?: string }).instructions).toBe("My new instructions");
  });

  it("keeps the draft and asks which copy should win when another tab saved first", async () => {
    await mount();
    await typeInstructions("Draft typed in tab A");
    const otherTab = server.commitFromElsewhere("project-a", { instructions: "Saved in tab B" });

    await submit();

    // Nothing was written and the text the member typed is still on screen.
    expect(contextValues()[0]).toBe("Draft typed in tab A");
    expect(server.row("project-a").project.instructions).toBe("Saved in tab B");
    // The fence's answer is what is shown, with both choices.
    expect(conflictBlock()).toHaveLength(1);
    expect(conflictBlock()[0]!.textContent).toContain("projects.revisionConflict");
    expect(findByClass(testContainer, "revision-conflict-actions")[0]!.childNodes.length).toBeGreaterThan(1);
    // The expired token is never swapped for the live one behind their back:
    // the single PATCH still declares the revision the draft was built on.
    expect(patchCalls()).toHaveLength(1);
    expect(patchCalls()[0]!.ifMatch).toBe(PROJECT_TOKEN);
    expect(otherTab).not.toBe(PROJECT_TOKEN);
  });

  it("writes nothing of the stale draft while the conflict is left unresolved", async () => {
    await mount();
    await typeInstructions("Draft typed in tab A");
    server.commitFromElsewhere("project-a", { instructions: "Saved in tab B" });
    await submit();

    // Leaving the choice unanswered is not an implicit acceptance of either side.
    expect(patchCalls()).toHaveLength(1);
    expect(server.row("project-a").project.instructions).toBe("Saved in tab B");
  });

  it("loads the newer copy only when the member asks, then re-bases the draft", async () => {
    await mount();
    await typeInstructions("Draft typed in tab A");
    server.commitFromElsewhere("project-a", { instructions: "Saved in tab B" });
    await submit();

    await clickByLabel("projects.revisionReloadAction");

    expect(contextValues()[0]).toBe("Saved in tab B");
    expect(conflictBlock()).toHaveLength(0);
    expect(onSavedSpy).toHaveBeenCalled();

    // The session holds the revision it re-read, so the next save is accepted.
    await typeInstructions("Edit after reload");
    await submit();
    expect(patchCalls().slice(-1)[0]!.ifMatch).not.toBe(PROJECT_TOKEN);
    expect(server.row("project-a").project.instructions).toBe("Edit after reload");
  });

  it("keeps the member's draft over the newer revision only on the explicit choice, declaring the wildcard", async () => {
    await mount();
    await typeInstructions("Draft typed in tab A");
    server.commitFromElsewhere("project-a", { instructions: "Saved in tab B" });
    await submit();

    await clickByLabel("projects.revisionKeepMineAction");

    const last = patchCalls().slice(-1)[0]!;
    expect(last.ifMatch).toBe("*");
    expect((last.body as { instructions?: string }).instructions).toBe("Draft typed in tab A");
    expect(server.row("project-a").project.instructions).toBe("Draft typed in tab A");
    expect(conflictBlock()).toHaveLength(0);
    expect(findByClass(testContainer, "form-success")).toHaveLength(1);
  });

  it("never sends an unfenced save when the keystroke capture was skipped", async () => {
    await mount();
    // No typing: the submitted draft is the server copy, and it is still fenced.
    await submit();
    expect(patchCalls()[0]!.ifMatch).toBe(PROJECT_TOKEN);
  });

  it("drops the previous project's revision when the route switches", async () => {
    await mount("project-a", "A instructions", "A notes");
    await typeInstructions("Draft for A");

    await switchTo("project-b", "B instructions", "B notes");
    expect(contextValues()).toEqual(["B instructions", "B notes"]);

    await submit();
    const call = patchCalls()[0]!;
    expect(call.path).toBe("/api/projects/project-b");
    // A's token belongs to A: it may not certify B's row, and nothing of A's
    // draft is written into the project the member navigated to.
    expect(call.ifMatch).not.toBe(PROJECT_TOKEN);
    expect((call.body as { instructions?: string }).instructions).toBe("B instructions");
  });

  it("refuses to certify a page whose row moved before the first keystroke", async () => {
    // The reported race: the overview rendered an older copy, another tab saved,
    // and only then did the member start typing. Adopting the token that read
    // returns at that moment would stamp the stale draft as current and silently
    // overwrite the other tab's edit - the lost update the fence exists to stop.
    await mount();
    server.commitFromElsewhere("project-a", { instructions: "Saved in tab B", name: "Renamed in tab B" });
    await typeInstructions("Draft typed in tab A");

    // The divergence is caught while capturing, before any PATCH is attempted.
    expect(patchCalls()).toHaveLength(0);
    expect(conflictBlock()).toHaveLength(1);
    expect(conflictBlock()[0]!.textContent).toContain("projects.revisionDiverged");
    expect(findByClass(testContainer, "form-error")).toHaveLength(0);

    // Saving now still writes nothing; the draft stays on screen for the choice.
    await submit();
    expect(patchCalls()).toHaveLength(0);
    expect(contextValues()[0]).toBe("Draft typed in tab A");
    expect(server.row("project-a").project.instructions).toBe("Saved in tab B");
    expect(server.row("project-a").project.name).toBe("Renamed in tab B");
  });

  it("refuses a save with no typing when the row moved while the page was open", async () => {
    // Clicking Save without editing used to send the displayed copy against a
    // freshly read token, reverting the other tab's change with no keystroke and
    // no conflict to notice.
    await mount();
    server.commitFromElsewhere("project-a", { description: "Tab B's synopsis" });
    await submit();

    expect(patchCalls()).toHaveLength(0);
    expect(conflictBlock()[0]!.textContent).toContain("projects.revisionDiverged");
    expect(server.row("project-a").project.description).toBe("Tab B's synopsis");
  });

  it("recovers from a pre-save divergence by reloading, then saves honestly", async () => {
    await mount();
    server.commitFromElsewhere("project-a", { instructions: "Saved in tab B" });
    await typeInstructions("Draft typed in tab A");
    expect(conflictBlock()).toHaveLength(1);

    await clickByLabel("projects.revisionReloadAction");
    // Re-reading the row is not a write.
    expect(patchCalls()).toHaveLength(0);
    expect(contextValues()[0]).toBe("Saved in tab B");
    expect(conflictBlock()).toHaveLength(0);

    await typeInstructions("Edit after reload");
    await submit();
    expect(patchCalls()).toHaveLength(1);
    // The token declared is the one the reload read, so the write lands.
    expect(patchCalls()[0]!.ifMatch).not.toBe(PROJECT_TOKEN);
    expect(server.row("project-a").project.instructions).toBe("Edit after reload");
  });

  it("runs only one conflict action at a time", async () => {
    await mount();
    await typeInstructions("Draft typed in tab A");
    server.commitFromElsewhere("project-a", { instructions: "Saved in tab B" });
    await submit();
    expect(conflictBlock()).toHaveLength(1);
    const before = patchCalls().length;

    // Hold the reload's read open and click keep-mine meanwhile: a wildcard that
    // fired now would race the reload and could overwrite the copy it was about
    // to put on screen, which is the decision the member has not made yet.
    let release: (() => void) | undefined;
    const gate = new Promise<void>((resolve) => { release = resolve; });
    const handler = server.handler;
    fetchMock.mockImplementation(async (input: string, init?: RequestInit) => {
      if ((init?.method ?? "GET").toUpperCase() === "GET" && String(input).endsWith("/api/projects/project-a")) {
        await gate;
      }
      return handler(input, init);
    });

    const reload = findNodes(testContainer, "BUTTON")
      .find((node) => (node.textContent ?? "").includes("projects.revisionReloadAction"))!;

    await act(async () => {
      reload.dispatchEvent({ type: "click", bubbles: true });
      await drain();
    });
    // Re-queried after the re-render: both actions are inert while one is running.
    const isDisabled = (node: TestNode) => node.disabled || node.getAttribute("disabled") !== null;
    const running = findNodes(testContainer, "BUTTON");
    expect(isDisabled(running.find((node) => (node.textContent ?? "").includes("projects.revisionReloadAction"))!)).toBe(true);
    expect(isDisabled(running.find((node) => (node.textContent ?? "").includes("projects.revisionKeepMineAction"))!)).toBe(true);

    await act(async () => {
      findNodes(testContainer, "BUTTON")
        .find((node) => (node.textContent ?? "").includes("projects.revisionKeepMineAction"))!
        .dispatchEvent({ type: "click", bubbles: true });
      await drain();
    });
    expect(patchCalls()).toHaveLength(before);

    release?.();
    await act(async () => {
      await drain();
      await drain();
    });
    // The reload finished and still no wildcard write happened.
    expect(patchCalls()).toHaveLength(before);
    expect(contextValues()[0]).toBe("Saved in tab B");
    expect(conflictBlock()).toHaveLength(0);
  });

  it("reports a save failure that is not a conflict as an ordinary error", async () => {
    await mount();
    await typeInstructions("Draft typed in tab A");
    fetchMock.mockImplementationOnce(async () =>
      new Response(JSON.stringify({ error: { code: "VALIDATION_FAILED", message: "Name is required." } }), { status: 400, headers: { "Content-Type": "application/json" } }));

    await submit();

    expect(conflictBlock()).toHaveLength(0);
    expect(findByClass(testContainer, "form-error").map((node) => node.textContent)).toEqual(["Name is required."]);
    expect(contextValues()[0]).toBe("Draft typed in tab A");
  });

  it("refuses to certify a draft the parent moved past while it sat untouched", async () => {
    // The sibling-form race, on one page: another form saved, the view folded that
    // answer into its own copy, and this editor - still displaying the older
    // content, now with a NEWER prop - must not have its fields certified by it.
    await mount();
    server.commitFromElsewhere("project-a", { instructions: "Saved by the details form" });
    await reparent();
    expect(contextValues()[0]).toBe("A instructions");

    await typeInstructions("Draft typed in the context form");
    await submit();

    expect(patchCalls()).toHaveLength(0);
    expect(conflictBlock()[0]!.textContent).toContain("projects.revisionDiverged");
    expect(contextValues()[0]).toBe("Draft typed in the context form");
    expect(server.row("project-a").project.instructions).toBe("Saved by the details form");
  });

  it("refuses a save with no typing when the prop alone was refreshed", async () => {
    // Same page, no keystroke at all: the textarea still holds what it was seeded
    // with, so writing it would revert the other form's change behind a token this
    // draft never earned.
    await mount();
    server.commitFromElsewhere("project-a", { instructions: "Saved by the details form" });
    await reparent();

    await submit();

    expect(patchCalls()).toHaveLength(0);
    expect(conflictBlock()[0]!.textContent).toContain("projects.revisionDiverged");
    expect(server.row("project-a").project.instructions).toBe("Saved by the details form");
  });

  it("does not let a status write certify a draft whose row moved before its first keystroke", async () => {
    // Archive FIRST, typing after: the session is unbound, the archive answer
    // carries newer content, and adopting it used to bind that token to a draft
    // nobody had compared. The save must refuse and the other tab must survive.
    const session = new ProjectRevisionSession("project-a", "context");
    await mount();
    await act(async () => {
      root!.render(<ProjectContextEditor project={makeProject("project-a", "A instructions", "A notes")} onSaved={onSavedSpy} revision={session} />);
      await drain();
    });
    server.commitFromElsewhere("project-a", { instructions: "Saved in tab B" });
    const archived = await api.archiveProjectWithRevision("project-a");
    await act(async () => { session.adopt(archived); await drain(); });

    await typeInstructions("Draft typed after archiving");
    await submit();

    expect(patchCalls()).toHaveLength(0);
    expect(conflictBlock()[0]!.textContent).toContain("projects.revisionDiverged");
    expect(contextValues()[0]).toBe("Draft typed after archiving");
    expect(server.row("project-a").project.instructions).toBe("Saved in tab B");
  });

  it("keeps a draft savable across a status write that changed nothing it displays", async () => {
    // The other side of the same rule, so the fix does not over-refuse: archiving
    // moves the revision but not the content, and a draft showing that content
    // should simply advance to the new token.
    const session = new ProjectRevisionSession("project-a", "context");
    await mount();
    await act(async () => {
      root!.render(<ProjectContextEditor project={makeProject("project-a", "A instructions", "A notes")} onSaved={onSavedSpy} revision={session} />);
      await drain();
    });
    const archived = await api.archiveProjectWithRevision("project-a");
    await act(async () => { session.adopt(archived); await drain(); });

    await typeInstructions("Draft typed after archiving");
    await submit();

    expect(conflictBlock()).toHaveLength(0);
    expect(patchCalls()).toHaveLength(1);
    expect(patchCalls()[0]!.ifMatch).toBe(archived.revision);
    expect(server.row("project-a").project.instructions).toBe("Draft typed after archiving");
  });
});
