import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { GenerationJob } from "@/lib/api";
import { translations } from "@/lib/i18n";

// Synthetic DOM harness, in the style established by DocumentStudioView.exportActionState.
//
// Scope: the Document Studio CONSUMER behaviour when the backing file of an authenticated,
// previously selected generated document becomes unavailable. The user's active job is
// restored from sessionStorage exactly like a returning session does, the result view then
// advertises both snapshot renditions, and the DOCX download dies at the route with a
// synthetic HTTP 409 ASSET_FILE_UNAVAILABLE (no real files, no provider, no network).
// The contract pinned here:
//   1. no false success claim — the object-URL/anchor-click success path must never run;
//   2. no error-payload leak — status, code, raw message, internal vault path and the
//      signed-storage host must never reach the DOM;
//   3. no stuck "working" affordance — the export actions must not stay busy/disabled as
//      if a download were progressing;
//   4. a localized retry/reselect state — the alert must carry the real en/ar/ku catalog
//      copy, the failed control must remain pressable as a live retry, and Back to brief
//      must actually return the user to the compose (reselect) surface;
//   5. ready files remain usable — the sibling PDF rendition must still download cleanly
//      afterwards, and the failure must not leak across representations;
//   6. the file-independent ResultJson reader preview keeps rendering.
//
// Ownership audit (2026-11-19): the click-time consumer path below is untested.
// #1122/#1126 (#1049 consumer halves) only gate buttons on the advertised
// representationDownloads.canDownload flag BEFORE any click and never exercise a
// download response; main's DocumentStudioView.exportActionState.test.tsx pins the busy
// claim with a bare Error("synthetic transport failure"), not a 409/ASSET_FILE_UNAVAILABLE
// envelope, and asserts no leak/locale/sibling behaviour; #1041/#1092 own the asset
// read model and AssetDetail; #722 owns API-side document download authorization;
// #1050/#1117/#1124 own the API seams. This file adds no source edits and touches no
// owned file.
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
  download = "";
  href = "";
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
  // The export success path builds an <a download> and clicks it; the stub records intent.
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
  createdElements: TestNode[] = [];
  constructor() {
    super(9, "#document", null as unknown as TestDocument);
    this.documentElement = new TestNode(1, "html", this);
    this.body = new TestNode(1, "body", this);
    this.documentElement.appendChild(this.body);
    this.appendChild(this.documentElement);
    this.activeElement = this.body;
    this.defaultView = {};
  }
  createElement(name: string) { const node = new TestNode(1, name.toUpperCase(), this); this.createdElements.push(node); return node; }
  createElementNS(_ns: string, name: string) { return this.createElement(name); }
  createTextNode(value: string) { return new TestNodeText(value, this.ownerDocument ?? this); }
  getElementById(id: string) { return this.find((n) => n.getAttribute("id") === id); }
  private find(p: (n: TestNode) => boolean): TestNode | null {
    const visit = (n: TestNode): TestNode | null => (p(n) ? n : n.childNodes.map(visit).find(Boolean) ?? null);
    return visit(this);
  }
}

const shared = vi.hoisted(() => {
  class ApiError extends Error {
    status: number;
    fields?: Record<string, string[]>;
    code?: string;
    retryAfterSeconds?: number;
    constructor(status: number, message: string, fields?: Record<string, string[]>, code?: string, retryAfterSeconds?: number) {
      super(message);
      this.name = "ApiError";
      this.status = status;
      this.fields = fields;
      this.code = code;
      this.retryAfterSeconds = retryAfterSeconds;
    }
  }
  return { ApiError };
});

const harness = vi.hoisted(() => ({
  locale: "en" as "en" | "ar" | "ku",
  user: { id: "synthetic-test-user" },
  workspace: null as { id: string } | null,
  api: {
    listProjects: vi.fn(),
    listFiles: vi.fn(),
    listAssets: vi.fn(),
    createDocumentGenerationJob: vi.fn(),
    getGenerationJob: vi.fn(),
    cancelGenerationJob: vi.fn(),
    downloadAssetRepresentation: vi.fn(),
  },
  objectUrl: { created: 0, revoked: 0 },
}));

vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => ({ user: harness.user, workspace: harness.workspace }),
}));
vi.mock("@/components/LocaleProvider", () => ({
  useLocale: () => ({
    locale: harness.locale,
    t: (key: string) => translations[harness.locale][key] ?? translations.en[key] ?? key,
  }),
}));
vi.mock("next/navigation", () => ({ useSearchParams: () => new URLSearchParams() }));
vi.mock("next/link", () => ({ default: (p: { href: string; children?: unknown }) => <a href={p.href}>{p.children as never}</a> }));
vi.mock("@/lib/api", () => ({
  api: harness.api,
  ApiError: shared.ApiError,
}));
vi.mock("lucide-react", () => {
  const Icon = (p: Record<string, unknown>) => <span {...p} />;
  return { ArrowLeft: Icon, Check: Icon, CheckCircle2: Icon, Clipboard: Icon, Download: Icon,
    FileText: Icon, FolderOpen: Icon, LoaderCircle: Icon, RefreshCw: Icon, Sparkles: Icon,
    WandSparkles: Icon, XCircle: Icon };
});

import { DocumentStudioView } from "./DocumentStudioView";

const WS = { id: "workspace-1" };
const DOCX_CONTENT_TYPE = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
const PDF_CONTENT_TYPE = "application/pdf";
const ASSET_ID = "00000000-0000-4000-8000-000000000010";
const DOCX_ID = "00000000-0000-4000-8000-000000000011";
const PDF_ID = "00000000-0000-4000-8000-000000000012";
const JOB_ID = "00000000-0000-4000-8000-000000000001";
const pinnedInstant = "2026-11-19T00:00:00Z";
const activeJobKey = `taslim:document-generation:${WS.id}`;

// Synthetic 409 payload sentinels: the route's ASSET_FILE_UNAVAILABLE answer carrying an
// internal vault path and a signed-storage host. None of these may ever render.
const UNAVAILABLE_CODE = "ASSET_FILE_UNAVAILABLE";
const VAULT_PATH = "/taslim-synthetic-vault/workspaces/workspace-1/renditions/00000000-0000-4000-8000-000000000099";
const SIGNED_HOST = "https://synthetic-object-storage.invalid/expiring-signature";
const RAW_MESSAGE = `Asset representation unavailable: ${VAULT_PATH} served from ${SIGNED_HOST} [${UNAVAILABLE_CODE}]`;
const LEAK_SENTINELS = [UNAVAILABLE_CODE, VAULT_PATH, SIGNED_HOST, "Asset representation unavailable", "ApiError", "409"];

function unavailableError() {
  return new shared.ApiError(409, RAW_MESSAGE, { file: [VAULT_PATH] }, UNAVAILABLE_CODE, 0);
}

/** The published snapshot of the previously selected document: both renditions are
 *  advertised (no companion availability field, exactly what a restoring client sees). */
function restoredSucceededJob(): GenerationJob {
  return {
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
      sections: [
        { heading: "Financials", blocks: [{ type: "paragraph", text: "Revenue held at twelve percent." }] },
      ],
      representations: [
        { id: DOCX_ID, type: "docx", fileName: "synthetic-brief.docx", contentType: DOCX_CONTENT_TYPE },
        { id: PDF_ID, type: "pdf", fileName: "synthetic-brief.pdf", contentType: PDF_CONTENT_TYPE },
      ],
    }),
  } as GenerationJob;
}

const storage = new Map<string, string>();
let timers: Map<number, () => void>;
let nextTimerId = 1;
let doc: TestDocument;
let container: TestNode;
let root: Root;

async function drain() { await new Promise((r) => setTimeout(r, 0)); }
async function settle(fn?: () => void) { await act(async () => { fn?.(); await drain(); }); }

function allNodes(node: TestNode): TestNode[] { return [node, ...node.childNodes.flatMap(allNodes)]; }
function findByClass(node: TestNode, cls: string) { return allNodes(node).filter((n) => (n.getAttribute("class")?.split(/\s+/) ?? []).includes(cls)); }
function isDisabled(node: TestNode): boolean { return node.disabled === true || node.getAttribute("disabled") !== null; }
function containerText(): string { return container.textContent ?? ""; }
function exportButton(suffix: "DOCX" | "PDF"): TestNode {
  const match = findByClass(container, "document-action-button")
    .find((n) => (n.getAttribute("aria-label") ?? "").trim().endsWith(suffix));
  if (!match) throw new Error(`No Document Studio export action for ${suffix}`);
  return match;
}
function clickedAnchors(): TestNode[] { return doc.createdElements.filter((n) => n.nodeName === "A" && n.clicked); }
function downloadAlert(): TestNode | undefined {
  return findByClass(container, "form-error").find((n) => n.getAttribute("role") === "alert"
    && (n.textContent ?? "").includes(translations[harness.locale]["document.downloadError"]));
}

async function mount() {
  harness.workspace = WS;
  await act(async () => { root = createRoot(container as unknown as Element); root.render(<DocumentStudioView />); await drain(); });
}
/** Restore the previously selected job: the active-job sessionStorage entry drives the
 *  same mount path a returning authenticated user takes. */
async function restoreResultView() {
  storage.set(activeJobKey, JOB_ID);
  await mount();
  await settle();
  expect(findByClass(container, "document-workspace")).toHaveLength(1);
}

beforeEach(() => {
  doc = new TestDocument();
  container = doc.createElement("div");
  doc.body.appendChild(container);
  timers = new Map();
  nextTimerId = 1;
  storage.clear();
  harness.locale = "en";
  harness.objectUrl.created = 0;
  harness.objectUrl.revoked = 0;
  const windowStub = {
    document: doc, HTMLElement: TestNode, HTMLIFrameElement: TestNode, Element: TestNode, Node: TestNode,
    addEventListener: vi.fn(), removeEventListener: vi.fn(),
    setTimeout: (cb: () => void) => { const id = nextTimerId++; timers.set(id, cb); return id; },
    clearTimeout: (id: number) => { timers.delete(id); },
    setInterval: () => 0,
    clearInterval: () => {},
    sessionStorage: {
      getItem: (k: string) => storage.get(k) ?? null,
      setItem: (k: string, v: string) => { storage.set(k, v); },
      removeItem: (k: string) => { storage.delete(k); },
    },
  };
  doc.defaultView = windowStub;
  vi.stubGlobal("document", doc);
  vi.stubGlobal("window", windowStub);
  vi.stubGlobal("navigator", { userAgent: "test", clipboard: { writeText: vi.fn() } });
  vi.stubGlobal("IS_REACT_ACT_ENVIRONMENT", true);
  class UrlStub extends URL {
    static override createObjectURL() { harness.objectUrl.created += 1; return "blob:synthetic-object-url"; }
    static override revokeObjectURL() { harness.objectUrl.revoked += 1; }
  }
  vi.stubGlobal("URL", UrlStub);
  harness.api.listProjects.mockReset().mockResolvedValue([]);
  harness.api.listFiles.mockReset().mockResolvedValue([]);
  harness.api.listAssets.mockReset().mockResolvedValue({ items: [], page: 1, pageSize: 4, totalCount: 0, totalPages: 1 });
  harness.api.createDocumentGenerationJob.mockReset().mockResolvedValue(restoredSucceededJob());
  harness.api.getGenerationJob.mockReset().mockResolvedValue(restoredSucceededJob());
  harness.api.cancelGenerationJob.mockReset().mockResolvedValue({ status: "Cancelled" });
  harness.api.downloadAssetRepresentation.mockReset().mockResolvedValue(new Blob(["synthetic"], { type: DOCX_CONTENT_TYPE }));
  harness.workspace = null;
});
afterEach(() => { act(() => root?.unmount()); vi.unstubAllGlobals(); });

describe("Document Studio click-time 409 ASSET_FILE_UNAVAILABLE consumer contract", () => {
  it("turns the unavailable backing file into a localized retry state with no false success and no payload leak", async () => {
    harness.api.downloadAssetRepresentation.mockImplementation((assetId: string, representationId: string) =>
      representationId === DOCX_ID
        ? Promise.reject(unavailableError())
        : Promise.resolve(new Blob(["synthetic"], { type: PDF_CONTENT_TYPE })));
    await restoreResultView();

    await settle(() => { exportButton("DOCX").dispatchEvent({ type: "click", bubbles: true }); });

    expect(harness.api.downloadAssetRepresentation).toHaveBeenCalledWith(ASSET_ID, DOCX_ID);
    // No false success: the object-URL/anchor-download success path never ran.
    expect(harness.objectUrl.created).toBe(0);
    expect(clickedAnchors()).toHaveLength(0);
    // No stuck "working" affordance: the exports must not keep presenting as in progress.
    for (const suffix of ["DOCX", "PDF"] as const) {
      expect(isDisabled(exportButton(suffix)), `${suffix} must not stay disabled after the failure`).toBe(false);
      expect(exportButton(suffix).getAttribute("aria-busy"), `${suffix} must not stay busy`).not.toBe("true");
    }
    // No payload leak: none of the synthetic 409 internals may reach the DOM.
    const text = containerText();
    for (const sentinel of LEAK_SENTINELS) {
      expect(text, `the DOM must not leak the error payload sentinel ${sentinel}`).not.toContain(sentinel);
    }
    // Localized retry state from the real catalog (EN leg; full sweep below).
    const alert = downloadAlert();
    expect(alert, "the failure must announce the localized retry message").toBeTruthy();
    expect(alert!.textContent).toContain(translations.en["document.downloadError"]);
    // The file-independent reader preview keeps rendering after the failure.
    expect(text).toContain("Revenue held at twelve percent.");
  });

  it.each(["en", "ar", "ku"] as const)(
    "offers a %S localized retry and reselect state after the unavailable click",
    async (locale) => {
      harness.locale = locale;
      harness.api.downloadAssetRepresentation.mockRejectedValue(unavailableError());
      await restoreResultView();

      await settle(() => { exportButton("DOCX").dispatchEvent({ type: "click", bubbles: true }); });

      const message = translations[locale]["document.downloadError"];
      const alert = downloadAlert();
      expect(alert, `${locale}: the alert must carry the localized catalog copy`).toBeTruthy();
      expect(alert!.textContent).toContain(message);
      // The raw key must not be the fallback and the payload must not leak in any locale.
      expect(alert!.textContent).not.toContain("document.downloadError");
      for (const sentinel of LEAK_SENTINELS) {
        expect(containerText(), `${locale}: must not leak ${sentinel}`).not.toContain(sentinel);
      }
      // Retry affordance stays live: pressing DOCX again issues a fresh download request.
      await settle(() => { exportButton("DOCX").dispatchEvent({ type: "click", bubbles: true }); });
      expect(harness.api.downloadAssetRepresentation).toHaveBeenCalledTimes(2);
      // Reselect affordance works: Back to brief returns the user to the compose (reselect) surface.
      const back = findByClass(container, "document-back-button")[0];
      expect(back, `${locale}: a reselect route must be offered`).toBeTruthy();
      expect(back.textContent).toContain(translations[locale]["document.backToBrief"]);
      await settle(() => { back.dispatchEvent({ type: "click", bubbles: true }); });
      expect(findByClass(container, "document-compose-layout")).toHaveLength(1);
    },
  );

  it("keeps the ready sibling rendition fully downloadable after the unavailable failure", async () => {
    const order: string[] = [];
    harness.api.downloadAssetRepresentation.mockImplementation((assetId: string, representationId: string) => {
      order.push(representationId === DOCX_ID ? "docx" : "pdf");
      return representationId === DOCX_ID
        ? Promise.reject(unavailableError())
        : Promise.resolve(new Blob(["synthetic"], { type: PDF_CONTENT_TYPE }));
    });
    await restoreResultView();

    await settle(() => { exportButton("DOCX").dispatchEvent({ type: "click", bubbles: true }); });
    expect(downloadAlert(), "the DOCX failure must be announced").toBeTruthy();

    // The sibling PDF is untouched by the DOCX unavailability: it still downloads.
    await settle(() => { exportButton("PDF").dispatchEvent({ type: "click", bubbles: true }); });
    expect(order).toEqual(["docx", "pdf"]);
    const clicked = clickedAnchors();
    expect(clicked, "the ready PDF must complete the anchor download").toHaveLength(1);
    expect(clicked[0].download).toBe("synthetic-brief.pdf");
    expect(harness.objectUrl.created).toBe(1);
    // A success for the ready file must not leave the stale unavailability banner up.
    expect(downloadAlert()).toBeFalsy();
  });
});
