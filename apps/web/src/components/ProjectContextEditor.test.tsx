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
  t: (key: string) => key,
  api: { updateProject: vi.fn() },
}));

vi.mock("@/components/LocaleProvider", () => ({ useLocale: () => ({ t: harness.t }) }));
vi.mock("@/lib/api", () => ({ api: harness.api }));
vi.mock("lucide-react", () => {
  const Icon = (props: Record<string, unknown>) => <span {...props} />;
  return { Check: Icon, FileText: Icon };
});

import { ProjectContextEditor } from "./ProjectContextEditor";
import type { Project } from "@/lib/api";

type ProjectFixture = {
  id: string;
  workspaceId: string;
  name: string;
  description: string | null;
  type: string;
  status: "Active";
  instructions: string | null;
  contextNotes: string | null;
  createdAt: string;
  updatedAt: string;
  archivedAt: string | null;
};

function project(id: string, instructions: string, contextNotes: string, workspaceId = "workspace-1"): ProjectFixture {
  return { id, workspaceId, name: id, description: null, type: "General", status: "Active", instructions, contextNotes, createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z", archivedAt: null };
}

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

/** The two context fields in authored order: instructions then notes. */
function contextValues(): string[] {
  return findNodes(container, "TEXTAREA").map((node) => node.value);
}

function saveButton(): TestNode {
  return findNodes(container, "BUTTON")[0];
}

/** React emits boolean attributes as an empty string and removes them when false. */
function saveButtonDisabled(): boolean {
  const button = saveButton();
  return button.disabled === true || button.getAttribute("disabled") !== null;
}

function submitContext(): Promise<void> {
  return act(async () => {
    findNodes(container, "FORM")[0].dispatchEvent({ type: "submit", bubbles: true });
    await Promise.resolve();
  });
}

function mountedProject(): Promise<void> {
  return act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 0));
  });
}

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
  harness.api.updateProject.mockReset();
});

afterEach(() => {
  act(() => root?.unmount());
  vi.unstubAllGlobals();
});

function mount(initial: ProjectFixture, onSaved = vi.fn()) {
  act(() => {
    root = createRoot(container as unknown as Element);
    root.render(<ProjectContextEditor project={initial} onSaved={onSaved} />);
  });
  return onSaved;
}

function switchTo(next: ProjectFixture, onSaved: (project: Project) => void) {
  act(() => {
    root.render(<ProjectContextEditor project={next} onSaved={onSaved} />);
  });
}

describe("ProjectContextEditor project freshness", () => {
  it("replaces local context fields when the mounted project changes", async () => {
    const onSaved = vi.fn();
    await act(async () => {
      root = createRoot(container as unknown as Element);
      root.render(<ProjectContextEditor project={project("project-a", "A instructions", "A notes")} onSaved={onSaved} />);
      await new Promise((resolve) => setTimeout(resolve, 0));
    });

    const initialTextareas = findNodes(container, "TEXTAREA");
    initialTextareas[0].value = "A edited instructions";
    initialTextareas[1].value = "A edited notes";

    await act(async () => {
      root.render(<ProjectContextEditor project={project("project-b", "B instructions", "B notes")} onSaved={onSaved} />);
      await new Promise((resolve) => setTimeout(resolve, 0));
    });

    const switchedTextareas = findNodes(container, "TEXTAREA");
    expect(switchedTextareas.map((node) => node.value)).toEqual(["B instructions", "B notes"]);
  });

  it("drops an unconfirmed 'saved' state when the project switches", async () => {
    const onSaved = mount(project("project-a", "A instructions", "A notes"));
    harness.api.updateProject.mockResolvedValue(project("project-a", "A instructions", "A notes"));

    await submitContext();
    await mountedProject();
    expect(findByClass(container, "form-success")).toHaveLength(1);

    switchTo(project("project-b", "B instructions", "B notes"), onSaved);

    expect(findByClass(container, "form-success")).toHaveLength(0);
    expect(contextValues()).toEqual(["B instructions", "B notes"]);
  });

  it("drops a failure message that belonged to the previous project", async () => {
    const onSaved = mount(project("project-a", "A instructions", "A notes"));
    harness.api.updateProject.mockRejectedValue(new Error("project A rejected"));

    await submitContext();
    await mountedProject();
    expect(findByClass(container, "form-error").map((node) => node.textContent)).toEqual(["project A rejected"]);

    harness.api.updateProject.mockResolvedValue(project("project-b", "B instructions", "B notes"));
    switchTo(project("project-b", "B instructions", "B notes"), onSaved);

    expect(findByClass(container, "form-error")).toHaveLength(0);
    // The stale error must not be replayed against the newly mounted project.
    await submitContext();
    await mountedProject();
    expect(findByClass(container, "form-error")).toHaveLength(0);
  });

  it("writes the mounted project's own identity when saving after a switch", async () => {
    const onSaved = mount(project("project-a", "A instructions", "A notes"));
    harness.api.updateProject.mockResolvedValue(project("project-b", "B instructions", "B notes"));

    switchTo(project("project-b", "B instructions", "B notes", "workspace-2"), onSaved);
    await submitContext();
    await mountedProject();

    expect(harness.api.updateProject).toHaveBeenCalledTimes(1);
    const [calledId, calledInput] = harness.api.updateProject.mock.calls[0] as [string, Record<string, unknown>];
    expect(calledId).toBe("project-b");
    expect(calledInput).toEqual({ name: "project-b", description: undefined, type: "General", instructions: "B instructions", contextNotes: "B notes" });
  });

  it("discards a save that resolves after the view moved to another workspace", async () => {
    let settleSave: ((value: ProjectFixture) => void) | undefined;
    harness.api.updateProject.mockImplementation(() => new Promise<ProjectFixture>((resolve) => { settleSave = resolve; }));
    const onSaved = mount(project("project-a", "A instructions", "A notes", "workspace-alpha"));

    await submitContext();
    expect(saveButtonDisabled()).toBe(true);
    expect(harness.api.updateProject).toHaveBeenCalledWith("project-a", expect.anything());

    switchTo(project("project-b", "B instructions", "B notes", "workspace-beta"), onSaved);
    settleSave?.(project("project-a", "A instructions", "A leaked note", "workspace-alpha"));
    await mountedProject();

    // Project A's response must neither reach the parent overview nor be shown as
    // a confirmation for project B, and B must not stay locked in A's saving state.
    expect(onSaved).not.toHaveBeenCalled();
    expect(findByClass(container, "form-success")).toHaveLength(0);
    expect(contextValues()).toEqual(["B instructions", "B notes"]);
    expect(saveButtonDisabled()).toBe(false);
  });

  it("restores the server context when returning to a previously visited project", async () => {
    const onSaved = mount(project("project-a", "A instructions", "A notes", "workspace-alpha"));
    switchTo(project("project-b", "B instructions", "B notes", "workspace-beta"), onSaved);
    expect(contextValues()).toEqual(["B instructions", "B notes"]);

    switchTo(project("project-a", "A instructions", "A notes", "workspace-alpha"), onSaved);

    // No in-memory residue of project B survives the round trip back to project A.
    expect(contextValues()).toEqual(["A instructions", "A notes"]);
    expect(findByClass(container, "form-success")).toHaveLength(0);
    expect(findByClass(container, "form-error")).toHaveLength(0);
  });
});
