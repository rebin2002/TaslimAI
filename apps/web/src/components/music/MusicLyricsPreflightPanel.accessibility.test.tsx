/**
 * Accessibility and keyboard-semantics regression for the Music Studio lyric
 * preview panel.
 *
 * These tests pin the interactive contract of
 * `apps/web/src/components/music/MusicLyricsPreflightPanel.tsx`, the provider-free
 * lyrics preflight surface, for assistive technology and keyboard-only users:
 *
 * 1. A blocked handoff control must stay reachable. A natively `disabled`
 *    button leaves the tab order, so a keyboard user cannot reach the primary
 *    action of the preview at all and gets no explanation of why the draft is
 *    withheld. The standard contract is `aria-disabled="true"` plus activation
 *    handling that reports the reason.
 * 2. Activation of a blocked control must report the block. The panel
 *    documents `onBlocked` as "called with the full result when preflight
 *    withholds the payload"; while the control was natively disabled React
 *    never dispatched the click, so that documented callback was unreachable
 *    and the fail-closed path silently did nothing.
 * 3. The handoff state must be announced. The ready/blocked sentence is the
 *    only prose that explains the gate, so it is a polite live region and the
 *    control describes it, which is what a keyboard user hears on focus.
 * 4. Ids must be instance-unique. `aria-labelledby`/`aria-describedby` resolve
 *    document-wide, so a hardcoded id makes a second preview panel reference
 *    the first panel's heading instead of its own.
 *
 * The DOM is the same dependency-free test-only shim style used by
 * `ProjectContextEditor.test.tsx`, so this lane adds no test dependency and
 * touches no package manifest.
 */
import React from "react";
import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  defaultMusicLyricsPreflightLabels,
  musicLyricsPreflightGateIssueCode,
  MusicLyricsPreflightPanel,
  type MusicLyricsPreflightInput,
} from "./MusicLyricsPreflightPanel";

class TestNode {
  nodeType: number;
  nodeName: string;
  ownerDocument: TestDocument;
  parentNode: TestNode | null = null;
  childNodes: TestNode[] = [];
  attributes = new Map<string, string>();
  listeners = new Map<string, Set<(event: unknown) => void>>();
  disabled = false;
  protected textValue: string | null = null;

  constructor(nodeType: number, nodeName: string, ownerDocument: TestDocument) {
    this.nodeType = nodeType;
    this.nodeName = nodeName;
    this.ownerDocument = ownerDocument;
  }

  get firstChild(): TestNode | null { return this.childNodes[0] ?? null; }
  get lastChild(): TestNode | null { return this.childNodes.at(-1) ?? null; }
  get tagName(): string { return this.nodeName; }
  get textContent(): string {
    if (this.nodeType === 3) return this.textValue ?? "";
    return this.childNodes.map((child) => child.textContent).join("");
  }
  set textContent(value: string | null) {
    this.childNodes = value === null || value === "" ? [] : [new TestNodeText(value, this.ownerDocument)];
    for (const child of this.childNodes) child.parentNode = this;
    this.textValue = null;
  }
  get className(): string { return this.attributes.get("class") ?? ""; }
  set className(value: string) { this.attributes.set("class", value); }

  appendChild<T extends TestNode>(child: T): T {
    if (child.parentNode) child.parentNode.removeChild(child);
    child.parentNode = this;
    this.childNodes.push(child);
    return child;
  }
  insertBefore<T extends TestNode>(child: T, before: TestNode | null): T {
    if (!before) return this.appendChild(child);
    const index = this.childNodes.indexOf(before);
    if (index < 0) return this.appendChild(child);
    if (child.parentNode) child.parentNode.removeChild(child);
    child.parentNode = this;
    this.childNodes.splice(index, 0, child);
    return child;
  }
  removeChild<T extends TestNode>(child: T): T {
    const index = this.childNodes.indexOf(child);
    if (index >= 0) this.childNodes.splice(index, 1);
    child.parentNode = null;
    return child;
  }
  setAttribute(name: string, value: string) { this.attributes.set(name, String(value)); }
  getAttribute(name: string): string | null { return this.attributes.get(name) ?? null; }
  removeAttribute(name: string) { this.attributes.delete(name); }
  hasAttribute(name: string) { return this.attributes.has(name); }
  addEventListener(name: string, listener: (event: unknown) => void) {
    const listeners = this.listeners.get(name) ?? new Set();
    listeners.add(listener);
    this.listeners.set(name, listeners);
  }
  removeEventListener(name: string, listener: (event: unknown) => void) {
    this.listeners.get(name)?.delete(listener);
  }
  // React delegates its listeners to the root container, so a dispatched event
  // has to travel up the ancestor chain the same way a browser event would.
  dispatchEvent(event: TestEvent): boolean {
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

class TestNodeText extends TestNode {
  constructor(value: string, ownerDocument: TestDocument) {
    super(3, "#text", ownerDocument);
    this.textValue = value;
  }
  override get textContent(): string { return this.textValue ?? ""; }
  override set textContent(value: string | null) { this.textValue = value ?? ""; }
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
  private find(predicate: (node: TestNode) => boolean): TestNode | null {
    const visit = (node: TestNode): TestNode | null =>
      predicate(node) ? node : node.childNodes.map(visit).find(Boolean) ?? null;
    return visit(this);
  }
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

/** Every element node below `node`, in document order. */
function descendants(node: TestNode): TestNode[] {
  return node.childNodes.flatMap((child) => (child.nodeType === 1 ? [child, ...descendants(child)] : descendants(child)));
}

const soraniVerse = "بەینەوتنی شةوعیەوە دەست پێکات\nدڵم بە خۆشەویستی پڕە";
const soraniChorus = "ئەی ڕۆژهەڵات، ئەی نیشتمانی ئازیز";

function cleanInput(): MusicLyricsPreflightInput {
  return {
    sections: [
      { id: "s1", type: "verse", text: soraniVerse },
      { id: "s2", type: "chorus", text: soraniChorus },
    ],
    vocalLanguage: "ku",
    lyricsRightsConfirmed: true,
  };
}

/** Blocked by the visible-glyph gate: controls-only text has nothing to sing. */
function invisibleOnlyInput(): MusicLyricsPreflightInput {
  return {
    sections: [{ id: "ghost", type: "chorus", text: "\u200F\u200E\u061C\u202A\u200B" }],
    vocalLanguage: "ku",
    lyricsRightsConfirmed: true,
  };
}

/** Blocked by the mirrored contract before the gate is even consulted. */
function rightsMissingInput(): MusicLyricsPreflightInput {
  return { ...cleanInput(), lyricsRightsConfirmed: false };
}

let document: TestDocument;
let root: Root;
let container: TestNode;

beforeEach(() => {
  document = new TestDocument();
  container = document.createElement("div");
  document.body.appendChild(container);
  vi.stubGlobal("document", document);
  const window = {
    document,
    Node: TestNode,
    Element: TestNode,
    HTMLElement: TestNode,
    HTMLIFrameElement: TestNode,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  };
  document.defaultView = window;
  vi.stubGlobal("window", window);
  vi.stubGlobal("navigator", { userAgent: "test" });
  vi.stubGlobal("IS_REACT_ACT_ENVIRONMENT", true);
});

afterEach(() => {
  act(() => root?.unmount());
  vi.unstubAllGlobals();
});

function mount(element: React.ReactElement) {
  act(() => {
    root = createRoot(container as unknown as Element);
    root.render(element);
  });
}

/** The single handoff control the panel exposes. */
function handoffControl(): TestNode {
  const [button] = findNodes(container, "BUTTON");
  expect(button, "the lyric preview renders one handoff control").toBeTruthy();
  return button;
}

function statusRegion(): TestNode {
  const [status] = findByClass(container, "music-lyrics-preflight-status");
  expect(status, "the lyric preview renders its handoff status").toBeTruthy();
  return status;
}

/** Keyboard activation: Enter/Space on a focused button dispatches a click. */
function activate(node: TestNode) {
  act(() => {
    node.dispatchEvent({ type: "click", bubbles: true });
  });
}

describe("Music lyric preview keyboard reachability", () => {
  it("keeps the handoff control in the tab order while preflight withholds it", () => {
    for (const blocked of [invisibleOnlyInput(), rightsMissingInput()]) {
      mount(<MusicLyricsPreflightPanel input={blocked} onHandoff={vi.fn()} onBlocked={vi.fn()} />);
      const button = handoffControl();
      // A natively disabled control is removed from the sequential focus
      // order, so the reason it is blocked can never be reached by keyboard.
      expect(button.disabled, "the blocked control stays focusable").toBe(false);
      expect(button.getAttribute("disabled"), "no native disabled attribute").toBeNull();
      expect(button.getAttribute("aria-disabled"), "the block is expressed to AT").toBe("true");
    }
  });

  it("leaves a clean preflight as an ordinary enabled control", () => {
    mount(<MusicLyricsPreflightPanel input={cleanInput()} onHandoff={vi.fn()} onBlocked={vi.fn()} />);
    const button = handoffControl();
    expect(button.disabled).toBe(false);
    expect(button.getAttribute("disabled")).toBeNull();
    // Absent means the default `false`; no aria-disabled="false" noise.
    expect(button.getAttribute("aria-disabled")).toBeNull();
  });
});

describe("Music lyric preview blocked activation", () => {
  it("reports the withheld draft to the host instead of swallowing the keypress", () => {
    const onHandoff = vi.fn();
    const onBlocked = vi.fn();
    mount(<MusicLyricsPreflightPanel input={invisibleOnlyInput()} onHandoff={onHandoff} onBlocked={onBlocked} />);

    activate(handoffControl());

    expect(onHandoff, "a withheld draft is never handed to the submit path").not.toHaveBeenCalled();
    expect(onBlocked, "the documented blocked callback is reachable").toHaveBeenCalledTimes(1);
    const [result] = onBlocked.mock.calls[0];
    expect(result.admissible).toBe(false);
    expect(result.payload).toBeNull();
    expect(result.blockedReason).toBe(musicLyricsPreflightGateIssueCode);
  });

  it("still refuses the handoff when the contract, not the gate, blocks the draft", () => {
    const onHandoff = vi.fn();
    const onBlocked = vi.fn();
    mount(<MusicLyricsPreflightPanel input={rightsMissingInput()} onHandoff={onHandoff} onBlocked={onBlocked} />);

    activate(handoffControl());

    expect(onHandoff).not.toHaveBeenCalled();
    expect(onBlocked).toHaveBeenCalledTimes(1);
    expect(onBlocked.mock.calls[0][0].blockedReason).toBe("lyrics_rights_required");
  });

  it("hands off a clean preflight exactly once and never reports it blocked", () => {
    const onHandoff = vi.fn();
    const onBlocked = vi.fn();
    mount(<MusicLyricsPreflightPanel input={cleanInput()} onHandoff={onHandoff} onBlocked={onBlocked} />);

    activate(handoffControl());

    expect(onBlocked).not.toHaveBeenCalled();
    expect(onHandoff).toHaveBeenCalledTimes(1);
    expect(onHandoff.mock.calls[0][0].lyricsLanguage).toBe("ku");
  });
});

describe("Music lyric preview announcements", () => {
  it("announces the handoff state in a live region the control describes", () => {
    mount(<MusicLyricsPreflightPanel input={rightsMissingInput()} onHandoff={vi.fn()} onBlocked={vi.fn()} />);
    const status = statusRegion();
    expect(status.getAttribute("role"), "state prose is announced politely").toBe("status");
    expect(status.getAttribute("aria-live")).toBe("polite");
    expect(status.textContent).toContain(defaultMusicLyricsPreflightLabels.blockedNotice);

    const describedBy = handoffControl().getAttribute("aria-describedby");
    expect(describedBy, "the control names the status it is described by").toBeTruthy();
    const referenced = status.getAttribute("id");
    expect(referenced, "the status is addressable").toBeTruthy();
    expect(describedBy).toContain(referenced as string);
    expect(document.getElementById(referenced as string), "the reference resolves").toBe(status);
  });

  it("binds the accessible name of the preview to its own heading", () => {
    mount(<MusicLyricsPreflightPanel input={cleanInput()} />);
    const [section] = findByClass(container, "music-lyrics-preflight");
    const heading = findNodes(section, "H2")[0];
    const labelledBy = section.getAttribute("aria-labelledby");
    expect(labelledBy).toBeTruthy();
    expect(heading.getAttribute("id")).toBe(labelledBy as string);
    expect(heading.textContent).toContain(defaultMusicLyricsPreflightLabels.heading);
  });

  it("gives each preview instance its own ids so two panels do not cross-talk", () => {
    mount(
      <>
        <MusicLyricsPreflightPanel input={cleanInput()} />
        <MusicLyricsPreflightPanel input={rightsMissingInput()} />
      </>,
    );
    const sections = findByClass(container, "music-lyrics-preflight");
    expect(sections).toHaveLength(2);

    const headingIds = sections.map((section) => section.getAttribute("aria-labelledby"));
    expect(new Set(headingIds).size, "each panel is named by its own heading").toBe(2);
    for (const [index, section] of sections.entries()) {
      const own = document.getElementById(headingIds[index] as string);
      expect(own, "the reference resolves inside this panel").toBeTruthy();
      expect(section.contains(own as TestNode)).toBe(true);
    }

    const statusIds = findByClass(container, "music-lyrics-preflight-status").map((node) => node.getAttribute("id"));
    expect(new Set(statusIds).size, "each status is addressable on its own").toBe(2);
    const described = findNodes(container, "BUTTON").map((button) => button.getAttribute("aria-describedby"));
    expect(described).toEqual(statusIds);
  });

  it("keeps the visible issue list as the announced region without re-reading its heading", () => {
    mount(<MusicLyricsPreflightPanel input={rightsMissingInput()} />);
    const [issues] = findByClass(container, "music-lyrics-preflight-issues");
    expect(issues, "the panel renders its blocking issues").toBeTruthy();

    const live = descendants(issues).filter((node) => node.getAttribute("aria-live") === "polite");
    expect(live.length, "the issues are announced").toBeGreaterThan(0);
    for (const node of live) {
      // The block title is static prose; inside a live region it is re-read on
      // every keystroke, burying the violation that actually changed.
      expect(findNodes(node, "H3"), "the heading is not inside the live region").toHaveLength(0);
      expect(node.textContent).not.toContain(defaultMusicLyricsPreflightLabels.issuesHeading);
    }
    expect(live[0].textContent, "the announced region carries the violation").toContain("lyrics_rights_required");
  });
});

describe("Music lyric preview privacy boundary", () => {
  it("announces state without echoing authored lyric text", () => {
    mount(<MusicLyricsPreflightPanel input={cleanInput()} />);
    expect(handoffControl().textContent).toContain(defaultMusicLyricsPreflightLabels.submitLabel);
    const announced = `${statusRegion().textContent}`;
    expect(announced).not.toContain(soraniVerse);
    expect(announced).not.toContain(soraniChorus);
  });
});
