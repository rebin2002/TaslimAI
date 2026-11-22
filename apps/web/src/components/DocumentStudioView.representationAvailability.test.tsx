import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { translate, type Locale } from "@/lib/i18n";
import type { GenerationJob } from "@/lib/api";

/**
 * #1049 / #1117 consumer-lane regression for the Document Studio result view.
 *
 * Synthetic DOM harness in the style established by
 * `DocumentStudioView.exportActionState.test.tsx`: fabricated job payloads,
 * stubbed api calls, a hand-built DOM, and the real EN/AR/KU translation
 * catalog for the download label. No network, no providers, no storage
 * identity, no user data.
 *
 * The scenario the review (#5969683567) demands the client half to pin:
 * a completed job advertising a ready DOCX and a retention-pruned PDF must
 * render the DOCX control only when the API delivers the companion
 * `representationDownloads` facts - and must render both controls, exactly
 * as today, when the field is absent (old read models, byte-identical
 * legacy payloads).
 */

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
  clicked = false;
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
  addEventListener(name: string, listener: (event: unknown) => void) { const set = this.listeners.get(name) ?? new Set(); set.add(listener); this.listeners.set(name, set); }
  removeEventListener(name: string, listener: (event: unknown) => void) { this.listeners.get(name)?.delete(listener); }
  dispatchEvent(evt: TestEvent) {
    evt.target ??= this;
    evt.preventDefault ??= () => { evt.defaultPrevented = true; };
    evt.stopPropagation ??= () => { evt.propagationStopped = true; };
    this.notify(evt, this);
    return true;
  }
  private notify(evt: TestEvent, node: TestNode) {
    evt.currentTarget = node;
    for (const listener of node.listeners.get(evt.type) ?? []) listener(evt);
    if (!evt.propagationStopped && node.parentNode) this.notify(evt, node.parentNode);
  }
  contains(node: TestNode | null): boolean { return node === this || this.childNodes.some((child) => child.contains(node)); }
  attachEvent() { return true; }
  detachEvent() { return true; }
  focus() { this.ownerDocument.activeElement = this; }
  click() { this.clicked = true; }
}
type TestEvent = {
  type: string; bubbles?: boolean; cancelable?: boolean; target?: TestNode; currentTarget?: TestNode;
  defaultPrevented?: boolean; preventDefault?: () => void; stopPropagation?: () => void; propagationStopped?: boolean;
};
class TestNodeText extends TestNode {
  nodeValue: string;
  constructor(value: string, ownerDocument: TestDocument) { super(3, "#text", ownerDocument); this.nodeValue = value; }
  override get textContent() { return this.nodeValue; }
  override set textContent(value: string | null) { this.nodeValue = value ?? ""; }
}
class TestDocument extends TestNode {
  body: TestNode; documentElement: TestNode; activeElement: TestNode; defaultView: object;
  visibilityState = "visible";
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
  createElementNS(_ns: string, name: string) { return this.createElement(name); }
  createTextNode(value: string) { return new TestNodeText(value, this); }
  getElementById(id: string) { return this.find((n) => n.getAttribute("id") === id); }
  private find(p: (n: TestNode) => boolean): TestNode | null {
    const visit = (n: TestNode): TestNode | null => (p(n) ? n : n.childNodes.map(visit).find(Boolean) ?? null);
    return visit(this);
  }
}

const harness = vi.hoisted(() => ({
  user: { id: "synthetic-test-user" },
  workspace: null as { id: string } | null,
  locale: "en",
  api: {
    listProjects: vi.fn(),
    listFiles: vi.fn(),
    listAssets: vi.fn(),
    createDocumentGenerationJob: vi.fn(),
    getGenerationJob: vi.fn(),
    cancelGenerationJob: vi.fn(),
    downloadAssetRepresentation: vi.fn(),
  },
}));
vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => ({ user: harness.user, workspace: harness.workspace }),
}));
vi.mock("@/components/LocaleProvider", () => ({
  useLocale: () => ({
    locale: harness.locale,
    // The real catalog: the download label must resolve in en, ar and ku.
    t: (key: string, variables?: Record<string, string>) => translate(harness.locale as Locale, key, variables),
  }),
}));
vi.mock("next/navigation", () => ({ useSearchParams: () => new URLSearchParams() }));
vi.mock("next/link", () => ({ default: (p: { href: string; children?: unknown }) => <a href={p.href}>{p.children as never}</a> }));
vi.mock("@/lib/api", () => ({
  api: harness.api,
  ApiError: class ApiError extends Error {
    status: number | null; code: string | null;
    constructor(status: number | null, message: string, _f?: unknown, code?: string | null) {
      super(message); this.name = "ApiError"; this.status = status; this.code = code ?? null;
    }
  },
}));
vi.mock("lucide-react", () => {
  const Icon = (p: Record<string, unknown>) => <span {...p} />;
  return { ArrowLeft: Icon, Check: Icon, CheckCircle2: Icon, Clipboard: Icon, Download: Icon,
    FileText: Icon, FolderOpen: Icon, LoaderCircle: Icon, RefreshCw: Icon, Sparkles: Icon,
    WandSparkles: Icon, XCircle: Icon };
});
import { DocumentStudioView } from "./DocumentStudioView";

const WS = { id: "workspace-1" };
const ASSET_ID = "00000000-0000-4000-8000-000000000010";
const DOCX_ID = "00000000-0000-4000-8000-000000000011";
const PDF_ID = "00000000-0000-4000-8000-000000000012";
const JOB_ID = "00000000-0000-4000-8000-000000000001";
const pinnedInstant = "2026-11-19T00:00:00Z";

/**
 * The #1049 reproducible shape: the published snapshot still advertises both
 * renditions; only the companion field knows the PDF is gone. `null` produces
 * the byte-identical pre-#1117 payload (no `representationDownloads` key at
 * all), which must keep both buttons.
 */
function succeededDocumentJob(pdfPruned: boolean | null): GenerationJob {
  const job: Record<string, unknown> = {
    id: JOB_ID, workspaceId: WS.id, projectId: null, jobType: "document.generate", status: "Succeeded",
    title: "Synthetic brief", progressPercent: 100, errorCode: null, errorMessage: null,
    cancellationRequested: false, createdAt: pinnedInstant, queuedAt: pinnedInstant,
    startedAt: pinnedInstant, completedAt: pinnedInstant, failedAt: null, cancelledAt: null, outputs: [],
    resultJson: JSON.stringify({
      assetId: ASSET_ID,
      documentType: "report",
      title: "Synthetic brief",
      language: "en",
      summary: "Synthetic summary",
      sections: [],
      representations: [
        { id: DOCX_ID, type: "docx", fileName: "synthetic-brief.docx", contentType: "application/vnd.openxmlformats-officedocument.wordprocessingml.document" },
        { id: PDF_ID, type: "pdf", fileName: "synthetic-brief.pdf", contentType: "application/pdf" },
      ],
    }),
  };
  if (pdfPruned !== null) {
    job.representationDownloads = [
      { id: DOCX_ID, type: "docx", fileName: "synthetic-brief.docx", contentType: "application/vnd.openxmlformats-officedocument.wordprocessingml.document", canDownload: true },
      { id: PDF_ID, type: "pdf", fileName: "synthetic-brief.pdf", contentType: "application/pdf", canDownload: !pdfPruned },
    ];
  }
  return job as unknown as GenerationJob;
}

let doc: TestDocument;
let container: TestNode;
let root: Root;
async function drain() { await new Promise((r) => setTimeout(r, 0)); }
async function settle(fn: () => void) { await act(async () => { fn(); await drain(); }); }
function allNodes(node: TestNode): TestNode[] { return [node, ...node.childNodes.flatMap(allNodes)]; }
function findByClass(node: TestNode, cls: string) { return allNodes(node).filter((n) => (n.getAttribute("class")?.split(/\s+/) ?? []).includes(cls)); }
function findByTag(node: TestNode, tag: string) { return allNodes(node).filter((n) => n.nodeName === tag.toUpperCase()); }
function containerText(): string { return container.textContent ?? ""; }

async function mount() {
  harness.workspace = WS;
  await act(async () => { root = createRoot(container as unknown as Element); root.render(<DocumentStudioView />); await drain(); });
}
async function reachResultView() {
  await mount();
  const textarea = findByTag(container, "TEXTAREA")[0];
  const descriptor = Object.getOwnPropertyDescriptor(TestNode.prototype, "value");
  await settle(() => { textarea.dispatchEvent({ type: "focusin", bubbles: true }); });
  await settle(() => {
    descriptor!.set!.call(textarea, "quarterly synthesis brief");
    textarea.dispatchEvent({ type: "keyup", bubbles: true });
  });
  const form = findByTag(container, "FORM")[0];
  await settle(() => { form.dispatchEvent({ type: "submit", bubbles: true, cancelable: true }); });
  expect(findByClass(container, "document-workspace")).toHaveLength(1);
}

beforeEach(() => {
  doc = new TestDocument();
  container = doc.createElement("div");
  doc.body.appendChild(container);
  harness.locale = "en";
  const windowStub = {
    document: doc, HTMLElement: TestNode, HTMLIFrameElement: TestNode, Element: TestNode, Node: TestNode,
    addEventListener: vi.fn(), removeEventListener: vi.fn(),
    setTimeout: () => 0, clearTimeout: () => {},
    sessionStorage: { getItem: () => null, setItem: () => {}, removeItem: () => {} },
  };
  doc.defaultView = windowStub;
  vi.stubGlobal("document", doc);
  vi.stubGlobal("window", windowStub);
  vi.stubGlobal("navigator", { userAgent: "test" });
  vi.stubGlobal("IS_REACT_ACT_ENVIRONMENT", true);
  harness.api.listProjects.mockReset().mockResolvedValue([]);
  harness.api.listFiles.mockReset().mockResolvedValue([]);
  harness.api.listAssets.mockReset().mockResolvedValue({ items: [], page: 1, pageSize: 4, totalCount: 0, totalPages: 1 });
  harness.api.getGenerationJob.mockReset().mockResolvedValue(succeededDocumentJob(null));
  harness.api.cancelGenerationJob.mockReset().mockResolvedValue({ status: "Cancelled" });
  harness.api.downloadAssetRepresentation.mockReset();
  harness.workspace = null;
});
afterEach(() => { act(() => root?.unmount()); vi.unstubAllGlobals(); });

describe("Document Studio result downloads honour live availability (#1049)", () => {
  it("hides the pruned PDF button while the ready DOCX control stays, in EN", async () => {
    harness.api.createDocumentGenerationJob.mockReset().mockResolvedValue(succeededDocumentJob(true));
    await reachResultView();
    const labels = findByClass(container, "document-action-button")
      .map((n) => n.getAttribute("aria-label") ?? "")
      .filter((l) => /DOCX|PDF/.test(l));
    expect(labels.some((l) => l.includes("DOCX"))).toBe(true);
    expect(labels.some((l) => l.includes("PDF"))).toBe(false);
  });

  it("keeps both buttons on legacy payloads that carry no companion field (backwards compatibility)", async () => {
    harness.api.createDocumentGenerationJob.mockReset().mockResolvedValue(succeededDocumentJob(null));
    await reachResultView();
    const labels = findByClass(container, "document-action-button")
      .map((n) => n.getAttribute("aria-label") ?? "")
      .filter((l) => /DOCX|PDF/.test(l));
    expect(labels.some((l) => l.includes("DOCX"))).toBe(true);
    expect(labels.some((l) => l.includes("PDF"))).toBe(true);
  });

  it("withholds no metadata or URLs for the pruned rendition: the pdf name/id never reach the DOM", async () => {
    harness.api.createDocumentGenerationJob.mockReset().mockResolvedValue(succeededDocumentJob(true));
    await reachResultView();
    const text = containerText();
    expect(text).not.toContain("synthetic-brief.pdf");
    expect(text).not.toContain(PDF_ID);
    expect(text).not.toContain("application/pdf");
    // The companion field itself is a client-side fact, never rendered.
    expect(text).not.toContain("representationDownloads");
    expect(text).not.toContain("canDownload");
  });

  it.each(["en", "ar", "ku"] as const)(
    "renders the retained DOCX control with the %S localized download label and no PDF control",
    async (locale) => {
      harness.locale = locale;
      harness.api.createDocumentGenerationJob.mockReset().mockResolvedValue(succeededDocumentJob(true));
      await reachResultView();
      const expectedLabel = `${translate(locale, "document.downloadLabel")} DOCX`;
      expect(expectedLabel).not.toBe("document.downloadLabel DOCX");
      const labels = findByClass(container, "document-action-button")
        .map((n) => n.getAttribute("aria-label") ?? "");
      expect(labels).toContain(expectedLabel);
      expect(labels.filter((l) => l.includes("PDF"))).toHaveLength(0);
    },
  );
});
