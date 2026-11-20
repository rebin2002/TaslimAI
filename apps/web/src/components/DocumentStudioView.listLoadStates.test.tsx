import { act } from "react";
import * as React from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Asset, AssetList, Project, StoredFile } from "@/lib/api";

// Reproduction harness for the Document Studio documents-list audit.
//
// The compose view loads its two documents lists - the selectable source
// documents and the recent saved documents - through one `Promise.allSettled`
// call. On current main a rejected `listFiles` or `listAssets` request is
// collapsed into an empty array, so the rail falls through to its genuine
// zero-item copy: "No saved documents yet." / "Upload and process a supported
// document first." A member whose request failed (a revoked session, a 500, a
// dropped network) is told their own documents do not exist, with no error,
// no announcement of the failure, and no way to recover except a full reload.
//
// These cases pin the three states apart: loading, failed, and truly empty.
//
// Synthetic DOM harness in the style established by ProjectContextEditor.test
// (#849) and reused by AssetsViewListing.test and AssetDetail.historyFocus:
// no jsdom, no network, no provider call. Every fetch is a fixed promise and
// every assertion is deterministic.
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
  get textContent(): string { return this.childNodes.map((child) => child.textContent ?? "").join(""); }
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
  override get textContent(): string { return this.nodeValue; }
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

// The studio reads its lists through the api module only; nothing here reaches
// a provider, a network, or a charging path.
const h = vi.hoisted(() => ({
  listProjects: vi.fn(),
  listFiles: vi.fn(),
  listAssets: vi.fn(),
  getGenerationJob: vi.fn(),
  cancelGenerationJob: vi.fn(),
  createDocumentGenerationJob: vi.fn(),
  downloadAssetRepresentation: vi.fn(),
}));

vi.mock("@/lib/api", () => ({
  ApiError: class ApiError extends Error {
    status: number;
    constructor(message: string, status: number) { super(message); this.status = status; }
  },
  api: {
    listProjects: (workspaceId: string, status?: string) => h.listProjects(workspaceId, status),
    listFiles: (workspaceId: string) => h.listFiles(workspaceId),
    listAssets: (workspaceId: string, filters?: unknown) => h.listAssets(workspaceId, filters),
    getGenerationJob: (jobId: string) => h.getGenerationJob(jobId),
    cancelGenerationJob: (jobId: string) => h.cancelGenerationJob(jobId),
    createDocumentGenerationJob: (input: unknown) => h.createDocumentGenerationJob(input),
    downloadAssetRepresentation: (assetId: string, representationId: string) => h.downloadAssetRepresentation(assetId, representationId),
  },
}));

vi.mock("next/navigation", () => ({
  useSearchParams: () => new URLSearchParams(),
}));

// `next/link` needs a mounted App Router context that this harness deliberately
// does not provide. Rendering it as its final anchor keeps every href and label
// the rails assert on without importing router state into a provider-free test.
vi.mock("next/link", async () => {
  const { createElement } = await import("react");
  type LinkProps = { href: string; className?: string; "aria-label"?: string; children?: React.ReactNode };
  return {
    default: ({ href, className, "aria-label": ariaLabel, children }: LinkProps) =>
      createElement("a", { href, className, "aria-label": ariaLabel }, children),
  };
});

// Fixed identities for the two contexts the studio reads. A fresh object per
// call would re-fire the workspace-keyed load effect on every render, so the
// mount would never settle.
const stable = vi.hoisted(() => ({
  user: { id: "synthetic-test-user" },
  workspace: { id: "workspace-1", name: "Kurdistan Institute" },
  localeContext: { locale: "en", t: (key: string) => key },
}));

vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => ({ user: stable.user, workspace: stable.workspace }),
}));

vi.mock("@/components/LocaleProvider", () => ({
  useLocale: () => stable.localeContext,
}));

import { DocumentStudioView } from "./DocumentStudioView";

function project(id: string, name: string): Project {
  return {
    id, workspaceId: "workspace-1", name, description: null, instructions: null,
    contextNotes: null, type: "general", status: "Active",
    createdAt: "2026-11-01T00:00:00Z", updatedAt: "2026-11-01T00:00:00Z", archivedAt: null,
  };
}

function readyFile(overrides: Partial<StoredFile> = {}): StoredFile {
  return {
    id: "file-ready-1", workspaceId: "workspace-1", projectId: null, conversationId: null,
    originalFileName: "field-notes.pdf", contentType: "application/pdf", extension: ".pdf",
    sizeBytes: 20480, storageProvider: "s3", status: "Ready", textExtractionStatus: "Ready",
    extractedTextLength: 1200, createdAt: "2026-11-10T00:00:00Z", processedAt: "2026-11-10T00:00:00Z",
    ...overrides,
  };
}

function documentAsset(overrides: Partial<Asset> = {}): Asset {
  return {
    id: "asset-doc-1", workspaceId: "workspace-1", projectId: null, projectName: null,
    name: "Q4 programme brief", description: "Saved on 16 November", assetType: "document",
    mimeType: "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
    status: "Active", hasFile: true, canPreview: true, fileSizeBytes: 4096,
    sourceStudio: "document", sourceJobTitle: null,
    createdAt: "2026-11-16T00:00:00Z", updatedAt: "2026-11-16T00:00:00Z", archivedAt: null,
    representations: [], ...overrides,
  };
}

function assetList(items: Asset[]): AssetList {
  return { items, page: 1, pageSize: 4, totalCount: items.length, totalPages: 1 };
}

type ListOutcome<T> = { kind: "pending" } | { kind: "ok"; value: T } | { kind: "fail"; error: Error };

const pending: ListOutcome<never> = { kind: "pending" };
const ok = <T,>(value: T): ListOutcome<T> => ({ kind: "ok", value });
const fail = <T,>(message: string): ListOutcome<T> => ({ kind: "fail", error: new Error(message) });

/** Wire the three list reads to fixed outcomes; never to a live endpoint. */
function seed(lists: {
  projects?: ListOutcome<Project[]>;
  files?: ListOutcome<StoredFile[]>;
  assets?: ListOutcome<AssetList>;
}) {
  const settle = <T,>(outcome: ListOutcome<T> | undefined, empty: T) => {
    const value = outcome ?? ok(empty);
    if (value.kind === "fail") return Promise.reject(value.error);
    if (value.kind === "pending") return new Promise<T>(() => undefined);
    return Promise.resolve(value.value);
  };
  h.listProjects.mockImplementation((_workspaceId: string, status?: string) =>
    settle(lists.projects, status === "Archived" ? [] : [project("project-1", "Autumn campaign")]));
  h.listFiles.mockImplementation(() => settle(lists.files, []));
  h.listAssets.mockImplementation(() => settle(lists.assets, assetList([])));
}

let document_: TestDocument;
let root: Root;
let container: TestNode;

beforeEach(() => {
  document_ = new TestDocument();
  container = document_.createElement("div");
  document_.body.appendChild(container);
  vi.stubGlobal("document", document_);
  const window = {
    document: document_,
    HTMLElement: TestNode,
    HTMLIFrameElement: TestNode,
    setTimeout: (handler: () => void) => setTimeout(handler, 0),
    clearTimeout: (id: ReturnType<typeof setTimeout>) => clearTimeout(id),
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  };
  document_.defaultView = window;
  vi.stubGlobal("window", window);
  vi.stubGlobal("navigator", { userAgent: "test" });
  vi.stubGlobal("IS_REACT_ACT_ENVIRONMENT", true);
  Object.values(h).forEach((fn) => fn.mockReset());
});

afterEach(() => {
  act(() => root?.unmount());
  vi.unstubAllGlobals();
});

async function drain() {
  await new Promise((resolve) => setTimeout(resolve, 0));
}

async function mount() {
  await act(async () => {
    root = createRoot(container as unknown as Element);
    root.render(<DocumentStudioView />);
    await drain();
  });
}

function findByClass(node: TestNode, className: string): TestNode[] {
  return [
    ...(node.getAttribute("class")?.split(/\s+/).includes(className) ? [node] : []),
    ...node.childNodes.flatMap((child) => findByClass(child, className)),
  ];
}

/** The visible copy of one rail, including any nested action labels. */
function railText(className: "document-recent-card" | "document-context-card"): string {
  const [rail] = findByClass(container, className);
  if (!rail) throw new Error(`The ${className} rail is not mounted`);
  return rail.textContent;
}

function railHasButton(className: "document-recent-card" | "document-context-card", label: string): boolean {
  const [rail] = findByClass(container, className);
  if (!rail) throw new Error(`The ${className} rail is not mounted`);
  const visit = (node: TestNode): boolean =>
    (node.nodeName === "BUTTON" && (node.textContent ?? "").includes(label)) ||
    node.childNodes.some(visit);
  return visit(rail);
}

function findRailButton(className: "document-recent-card" | "document-context-card", label: string): TestNode {
  const [rail] = findByClass(container, className);
  if (!rail) throw new Error(`The ${className} rail is not mounted`);
  const visit = (node: TestNode): TestNode | null => {
    if (node.nodeName === "BUTTON" && (node.textContent ?? "").includes(label)) return node;
    return node.childNodes.map(visit).find(Boolean) ?? null;
  };
  const match = visit(rail);
  if (!match) throw new Error(`No button containing "${label}" inside ${className}`);
  return match;
}

async function click(node: TestNode) {
  await act(async () => {
    node.dispatchEvent({ type: "click", bubbles: true });
    await drain();
  });
}

const recentKeys = {
  error: "document.recentLoadError",
  errorHint: "document.recentLoadErrorHint",
  retry: "document.retryRecent",
  empty: "document.recentEmpty",
  loading: "assets.loading",
} as const;

const sourceKeys = {
  error: "document.sourcesLoadError",
  errorHint: "document.sourcesLoadErrorHint",
  retry: "document.retrySources",
  empty: "document.noSources",
  loading: "document.loadingSources",
} as const;

describe("Document Studio documents-list load states", () => {
  it("announces a failed recent-document read as an error with a retry instead of claiming nothing is saved", async () => {
    seed({ assets: fail("asset list unavailable") });
    await mount();

    const text = railText("document-recent-card");
    expect(text).toContain(recentKeys.error);
    expect(text).toContain(recentKeys.errorHint);
    expect(text).toContain(recentKeys.retry);
    expect(railHasButton("document-recent-card", recentKeys.retry), "the failure must offer recovery").toBe(true);
    // The defect: a rejected request must never be reported as a true zero-item library.
    expect(text).not.toContain(recentKeys.empty);
  });

  it("announces a failed source-document read as an error instead of telling the member to upload first", async () => {
    seed({ files: fail("file list unavailable") });
    await mount();

    const text = railText("document-context-card");
    expect(text).toContain(sourceKeys.error);
    expect(text).toContain(sourceKeys.errorHint);
    expect(text).toContain(sourceKeys.retry);
    expect(railHasButton("document-context-card", sourceKeys.retry), "the failure must offer recovery").toBe(true);
    expect(text).not.toContain(sourceKeys.empty);
  });

  it("keeps the genuine empty copy for a list that loaded successfully with no items", async () => {
    seed({ assets: ok(assetList([])), files: ok([]) });
    await mount();

    expect(railText("document-recent-card")).toContain(recentKeys.empty);
    expect(railText("document-recent-card")).not.toContain(recentKeys.error);
    expect(railText("document-recent-card")).not.toContain(recentKeys.retry);
    expect(railText("document-context-card")).toContain(sourceKeys.empty);
    expect(railText("document-context-card")).not.toContain(sourceKeys.error);
  });

  it("reports loading rather than a premature empty or error claim while the reads are in flight", async () => {
    seed({ assets: pending, files: pending });
    await mount();

    expect(railText("document-recent-card")).toContain(recentKeys.loading);
    expect(railText("document-recent-card")).not.toContain(recentKeys.empty);
    expect(railText("document-recent-card")).not.toContain(recentKeys.error);
    expect(railText("document-context-card")).toContain(sourceKeys.loading);
    expect(railText("document-context-card")).not.toContain(sourceKeys.empty);
    expect(railText("document-context-card")).not.toContain(sourceKeys.error);
  });

  it("renders the saved documents once the list has loaded", async () => {
    seed({ assets: ok(assetList([documentAsset()])), files: ok([readyFile()]) });
    await mount();

    const text = railText("document-recent-card");
    expect(text).toContain("Q4 programme brief");
    expect(text).not.toContain(recentKeys.empty);
    expect(text).not.toContain(recentKeys.error);
    expect(railText("document-context-card")).toContain("field-notes.pdf");
  });

  it("recovers through the retry control without a reload once the failing read succeeds", async () => {
    seed({ assets: fail("asset list unavailable") });
    await mount();
    expect(railText("document-recent-card")).toContain(recentKeys.error);

    const retry = findRailButton("document-recent-card", recentKeys.retry);
    // A fixed second read: the retry must re-issue the list request the view skipped over.
    h.listAssets.mockImplementation(() => Promise.resolve(assetList([documentAsset({ name: "Board summary" })])));
    await click(retry);

    expect(h.listAssets.mock.calls.length, "retry re-requests the recent document list").toBeGreaterThanOrEqual(2);
    const text = railText("document-recent-card");
    expect(text).toContain("Board summary");
    expect(text).not.toContain(recentKeys.error);
    expect(text).not.toContain(recentKeys.empty);
  });

  it("keeps each rail's state independent when only one list read fails", async () => {
    seed({ files: fail("file list unavailable"), assets: ok(assetList([documentAsset()])) });
    await mount();

    expect(railText("document-context-card")).toContain(sourceKeys.error);
    // The recent rail loaded documents, so it must show them and stay silent about failure.
    const recent = railText("document-recent-card");
    expect(recent).toContain("Q4 programme brief");
    expect(recent).not.toContain(recentKeys.error);
    expect(recent).not.toContain(recentKeys.empty);
  });

  it("announces the changed state to assistive technology on both rails", async () => {
    seed({ files: fail("file list unavailable"), assets: fail("asset list unavailable") });
    await mount();

    for (const className of ["document-recent-card", "document-context-card"] as const) {
      const [rail] = findByClass(container, className);
      const visit = (node: TestNode): TestNode | null => {
        if (node.getAttribute("role") === "status") return node;
        return node.childNodes.map(visit).find(Boolean) ?? null;
      };
      expect(visit(rail), `${className} must announce its load state`).not.toBeNull();
    }
  });
});
