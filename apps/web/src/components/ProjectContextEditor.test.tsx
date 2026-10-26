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

function project(id: string, instructions: string, contextNotes: string): ProjectFixture {
  return { id, workspaceId: "workspace-1", name: id, description: null, type: "General", status: "Active", instructions, contextNotes, createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z", archivedAt: null };
}

function findNodes(node: TestNode, nodeName: string): TestNode[] {
  return [
    ...(node.nodeName === nodeName ? [node] : []),
    ...node.childNodes.flatMap((child) => findNodes(child, nodeName)),
  ];
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
});

afterEach(() => {
  act(() => root?.unmount());
  vi.unstubAllGlobals();
});

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
});
