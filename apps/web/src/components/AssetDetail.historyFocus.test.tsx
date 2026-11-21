import { act } from "react";
import * as React from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Asset } from "@/lib/api";

// Reproduction harness for the Image Studio generation-history audit.
//
// The recent-generation strip on the Image Studio canvas hands every activated
// history entry to `AssetDetail`. On current main the dialog announces and
// traps nothing for a keyboard user: opening a history item leaves focus on
// the trigger (which is now hidden behind an `aria-modal` surface), Escape
// does not close the dialog, Tab walks straight out of the dialog into the
// page behind it, and closing never returns focus to the history item, so the
// screen reader's place in the list is lost with no announcement at all.
//
// Synthetic DOM harness in the style established by ProjectContextEditor.test
// (#849) and reused by AssetsViewListing.test: no jsdom, no network, no
// provider — every input is fixed and every assertion is deterministic.
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
  get children(): TestNode[] { return this.childNodes.filter((child) => child.nodeType === 1); }
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
  key?: string;
  shiftKey?: boolean;
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
    this.documentElement = new TestNode(1, "html", this);
    this.body = new TestNode(1, "body", this);
    this.documentElement.appendChild(this.body);
    this.appendChild(this.documentElement);
    this.activeElement = this.body;
    this.defaultView = {};
  }
  createElement(name: string) { return new TestNode(1, name.toUpperCase(), this); }
  createElementNS(_namespace: string, name: string) { return this.createElement(name); }
  createTextNode(value: string) { return new TestNodeText(value, this.ownerDocument ?? this); }
  getElementById(id: string) { return this.find((node) => node.getAttribute("id") === id); }
  private find(predicate: (node: TestNode) => boolean): TestNode | null { const visit = (node: TestNode): TestNode | null => (predicate(node) ? node : node.childNodes.map(visit).find(Boolean) ?? null); return visit(this); }
}

// The dialog is rendered by Image Studio without the studio's data layer; mock
// the providers it reads and the media URLs it composes, never a provider call.
vi.mock("../lib/apiBase", () => ({
  assetFileUrl: (assetId: string, inline = false) => `/api/assets/${assetId}/download${inline ? "?inline=true" : ""}`,
  assetRepresentationUrl: (assetId: string, representationId: string) => `/api/assets/${assetId}/representations/${representationId}/download`,
}));

import { AssetDetail, type AssetDetailLabels } from "./AssetDetail";

function historyAsset(): Asset {
  return {
    id: "asset-history-1",
    workspaceId: "workspace-1",
    projectId: "project-1",
    projectName: "Autumn campaign",
    name: "Bottle hero shot",
    description: null,
    assetType: "image",
    mimeType: "image/png",
    status: "Active",
    hasFile: true,
    canPreview: false,
    fileSizeBytes: 1024,
    sourceStudio: "image",
    sourceJobTitle: null,
    createdAt: "2026-11-16T00:00:00Z",
    updatedAt: "2026-11-16T00:00:00Z",
    archivedAt: null,
    representations: [],
  };
}

const labels: AssetDetailLabels = {
  detailEyebrow: "Asset detail", close: "Close", structuredTitle: "Structured asset",
  structuredDescription: "Structured content", previewLabel: "Preview", summaryLabel: "Summary",
  download: "Download", open: "Open", edit: "Rename", type: "Type", typeValue: "Image",
  created: "Created", project: "Project", noProject: "Workspace level", file: "File",
  notAvailable: "n/a", source: "Source", manual: "Manual upload", description: "Description",
  representations: "Representations", studios: { image: "Image Studio" },
};

let document: TestDocument;
let root: Root;
let container: TestNode;
let closeCalls: number;
// Mirrors the wiring Image Studio uses: a keyboard-activated history button
// mounts the detail dialog, and closing unmounts it (`selectedAsset && ...`).
function StudioHistoryHost() {
  const [selected, setSelected] = React.useState<Asset | null>(null);
  return <div className="image-studio-page">
    <div className="image-recent-list">
      <button className="image-recent-item" type="button" onClick={() => setSelected(historyAsset())}>Bottle hero shot</button>
    </div>
    <button className="page-behind-control" type="button">Generate</button>
    {selected && <AssetDetail
      asset={selected}
      locale="en"
      labels={labels}
      onClose={() => { closeCalls += 1; setSelected(null); }}
      onEdit={() => setSelected(null)}
    />}
  </div>;
}

async function drain() {
  await new Promise((resolve) => setTimeout(resolve, 0));
}

function findByClass(node: TestNode, className: string): TestNode[] {
  return [
    ...(node.getAttribute("class")?.split(/\s+/).includes(className) ? [node] : []),
    ...node.childNodes.flatMap((child) => findByClass(child, className)),
  ];
}

function findButtonByText(text: string): TestNode {
  const visit = (node: TestNode): TestNode | null => {
    if (node.nodeName === "BUTTON" && (node.textContent ?? "").includes(text)) return node;
    return node.childNodes.map(visit).find(Boolean) ?? null;
  };
  const match = visit(container);
  if (!match) throw new Error(`No button containing "${text}"`);
  return match;
}

function dialogPanel(): TestNode {
  const [panel] = findByClass(container, "asset-detail-panel");
  if (!panel) throw new Error("The asset detail dialog is not mounted");
  return panel;
}

async function click(node: TestNode) {
  await act(async () => {
    node.dispatchEvent({ type: "click", bubbles: true });
    await drain();
  });
}

async function pressKey(node: TestNode, key: string, shiftKey = false) {
  let handled = false;
  await act(async () => {
    const event: TestEvent = { type: "keydown", bubbles: true, key, shiftKey };
    node.dispatchEvent(event);
    handled = event.defaultPrevented === true;
    await drain();
  });
  return handled;
}

beforeEach(() => {
  document = new TestDocument();
  container = document.createElement("div");
  document.body.appendChild(container);
  vi.stubGlobal("document", document);
  const window = {
    document,
    HTMLElement: TestNode,
    HTMLIFrameElement: TestNode,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  };
  document.defaultView = window;
  vi.stubGlobal("window", window);
  vi.stubGlobal("navigator", { userAgent: "test" });
  vi.stubGlobal("IS_REACT_ACT_ENVIRONMENT", true);
  closeCalls = 0;
});

afterEach(() => {
  act(() => root?.unmount());
  vi.unstubAllGlobals();
});

async function mount() {
  await act(async () => {
    root = createRoot(container as unknown as Element);
    root.render(<StudioHistoryHost />);
    await drain();
  });
}

describe("Image Studio history detail dialog keyboard contract", () => {
  it("moves focus into the named dialog when a history item is activated", async () => {
    await mount();
    const trigger = findButtonByText("Bottle hero shot");
    trigger.focus();
    await click(trigger);

    const panel = dialogPanel();
    // The dialog must be programmatically focusable and actually hold focus, so
    // a screen reader announces role "dialog" with the asset name on open.
    expect(panel.getAttribute("role")).toBe("dialog");
    expect(panel.getAttribute("aria-modal")).toBe("true");
    const labelId = panel.getAttribute("aria-labelledby");
    expect(labelId, "the dialog must be named by an element in the document").toBeTruthy();
    expect(document.getElementById(labelId as string)?.textContent).toBe("Bottle hero shot");
    expect(panel.getAttribute("tabindex"), "the panel must accept focus without joining the tab order").toBe("-1");
    expect(document.activeElement).toBe(panel);
  });

  it("closes on Escape and returns focus to the history item that opened it", async () => {
    await mount();
    const trigger = findButtonByText("Bottle hero shot");
    trigger.focus();
    await click(trigger);
    expect(findByClass(container, "asset-detail-panel")).toHaveLength(1);

    const prevented = await pressKey(dialogPanel(), "Escape");
    expect(prevented, "the dialog owns Escape while it is open").toBe(true);
    expect(closeCalls).toBe(1);
    expect(findByClass(container, "asset-detail-panel")).toHaveLength(0);
    // Without the restore the reader's position in the recent-generation list
    // is lost: activeElement would fall to the body with no announcement.
    expect(document.activeElement).toBe(trigger);
  });

  it("wraps the tab ring at both ends instead of walking into the page behind the dialog", async () => {
    await mount();
    const trigger = findButtonByText("Bottle hero shot");
    trigger.focus();
    await click(trigger);

    const panel = dialogPanel();
    // Document order inside the panel: close, download, open (links), rename.
    const close = findByClass(panel, "modal-close")[0];
    const rename = findButtonByText("Rename");
    expect(close, "the dialog must expose its close control").toBeTruthy();
    expect(rename, "the dialog must expose its edit control").toBeTruthy();

    // Tabbing from the last control must land on the first dialog control.
    rename.focus();
    await pressKey(rename, "Tab");
    expect(document.activeElement).toBe(close);

    // Shift+Tabbing from the first control must land on the last dialog control.
    await pressKey(close, "Tab", true);
    expect(document.activeElement).toBe(rename);

    // The control behind the modal must never receive focus while it is open.
    const behind = findButtonByText("Generate");
    rename.focus();
    await pressKey(rename, "Tab");
    expect(document.activeElement).not.toBe(behind);
  });

  it("restores focus to the trigger when the close control unmounts the dialog", async () => {
    await mount();
    const trigger = findButtonByText("Bottle hero shot");
    trigger.focus();
    await click(trigger);
    const close = findByClass(dialogPanel(), "modal-close")[0];
    await click(close);
    expect(closeCalls).toBe(1);
    expect(findByClass(container, "asset-detail-panel")).toHaveLength(0);
    expect(document.activeElement).toBe(trigger);
  });
});
