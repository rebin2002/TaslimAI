import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { GenerationJob } from "@/lib/api";
import { translations } from "@/lib/i18n";

/**
 * Independent synthetic QA lane for issue #1049 (asset-file-unavailable family): the
 * RESEARCH Studio click-time consumer contract.
 *
 * Scope. A Research job becomes a successful report in the active ResearchStudioView
 * after polling. Its published `ResultJson` renditions remain advertised, and one loses
 * its backing file after selection, so the click dies at the download route with
 * `409 ASSET_FILE_UNAVAILABLE`. Current main intentionally does not restore terminal
 * jobs across sessions; this fixture therefore completes an initially active synthetic
 * job in-view. Everything here is provider-free: fabricated job
 * payloads, stubbed `api`, a hand-built DOM, no network, no stored file, no customer
 * data, no real translation catalog edits.
 *
 * Pinned contract:
 *   1. no silent success - a rejected download must never run the object-URL/anchor
 *      click path, and must not be reported as a completed download;
 *   2. localized, non-leaking error - the failure is announced from the real en/ar/ku
 *      catalog and the 409 envelope (status, code, raw message, internal vault path,
 *      signed-storage host) never reaches the DOM;
 *   3. accessible retry state - the failed control stays pressable (a live retry that
 *      re-issues the request), it is announced as a download (not a bare format token)
 *      and it communicates its in-flight state to assistive technology, matching the
 *      Document/Presentation studios;
 *   4. accessible reselect semantics - "Start another" returns the user to the compose
 *      surface and drops the unavailable report, so the dead-end control is not
 *      re-advertised to the next session;
 *   5. per-representation isolation - a ready sibling stays fully downloadable after
 *      the unavailable failure, and the report body (which needs no file) keeps
 *      rendering.
 *
 * Ownership boundary: merged #1126 covers the pre-click availability gate; this existing
 * #1192 lane covers the distinct click-time response. The updated source hunk is limited
 * to Research rendition controls; it does not touch #1282's Academic Research header
 * hunk/test or #1283's dependency paths. #1049 remains the source issue and no duplicate
 * PR or API/provider change is introduced.
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
  // The download success path builds an <a download> and clicks it; the stub records intent.
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
    createResearchGenerationJob: vi.fn(),
    getGenerationJob: vi.fn(),
    cancelGenerationJob: vi.fn(),
    getResearchSources: vi.fn(),
    downloadAssetRepresentation: vi.fn(),
    downloadResearchSourcesExport: vi.fn(),
  },
  objectUrl: { created: 0, revoked: 0 },
}));

vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => ({ user: harness.user, workspace: harness.workspace }),
}));
vi.mock("@/components/LocaleProvider", () => ({
  useLocale: () => ({
    locale: harness.locale,
    // The real catalog, with the same {token} interpolation the provider performs, so a
    // missing or key-echoed localized string is a failure rather than a silent fallback.
    t: (key: string, values?: Record<string, string>) => {
      let text = translations[harness.locale][key] ?? translations.en[key] ?? key;
      for (const [token, value] of Object.entries(values ?? {})) text = text.replaceAll(`{${token}}`, value);
      return text;
    },
  }),
}));
vi.mock("next/navigation", () => ({ useSearchParams: () => new URLSearchParams() }));
vi.mock("next/link", () => ({ default: (p: { href: string; children?: unknown }) => <a href={p.href}>{p.children as never}</a> }));
vi.mock("@/lib/api", () => ({ api: harness.api, ApiError: shared.ApiError }));
vi.mock("lucide-react", async (importOriginal) => {
  const Icon = (p: Record<string, unknown>) => <span {...p} />;
  // Every icon this lane's render path can reach becomes an inert span: the contract under
  // test is about the download affordance and its alert, not about icon internals.
  const actual = await importOriginal<Record<string, unknown>>();
  const stubs: Record<string, unknown> = {};
  for (const [name, value] of Object.entries(actual)) {
    stubs[name] = typeof value === "function" ? Icon : value;
  }
  stubs.default = Icon;
  return stubs;
});

import { ResearchStudioView, sameResearchDownloadContext } from "./ResearchStudioView";

const WS = { id: "workspace-1" };
const DOCX_CONTENT_TYPE = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
const PDF_CONTENT_TYPE = "application/pdf";
const ASSET_ID = "00000000-0000-4000-8000-000000000030";
const OTHER_ASSET_ID = "00000000-0000-4000-8000-000000000033";
const DOCX_ID = "00000000-0000-4000-8000-000000000031";
const PDF_ID = "00000000-0000-4000-8000-000000000032";
const JOB_ID = "00000000-0000-4000-8000-000000000007";
const REPLACEMENT_JOB_ID = "00000000-0000-4000-8000-000000000008";
const OTHER_WORKSPACE_ID = "workspace-2";
const PINNED = "2026-11-19T00:00:00Z";
const activeJobKey = `taslim:research-generation:${WS.id}`;

// Synthetic 409 sentinels: the route's ASSET_FILE_UNAVAILABLE answer, the code the web
// client raises for it, an internal vault path and a signed-storage host. None of these
// may ever reach the DOM in any locale.
const SERVER_CODE = "ASSET_FILE_UNAVAILABLE";
const CLIENT_CODE = "ASSET_REPRESENTATION_UNAVAILABLE";
const VAULT_PATH = "/taslim-synthetic-vault/workspaces/workspace-1/renditions/00000000-0000-4000-8000-000000000099";
const SIGNED_HOST = "https://synthetic-object-storage.invalid/expiring-signature";
const RAW_MESSAGE = `Asset representation unavailable: ${VAULT_PATH} served from ${SIGNED_HOST} [${SERVER_CODE}]`;
const LEAK_SENTINELS = [SERVER_CODE, CLIENT_CODE, VAULT_PATH, SIGNED_HOST, "Asset representation unavailable", "ApiError", "409"];

function unavailableError() {
  return new shared.ApiError(409, RAW_MESSAGE, { file: [VAULT_PATH] }, CLIENT_CODE, 0);
}

/** The published snapshot of a completed report: both renditions remain advertised with
 *  identity only - no availability fact - exactly what `GET /api/generation/jobs/{id}` returns. */
function completedResearchJob(jobId = JOB_ID, assetId = ASSET_ID): GenerationJob {
  return {
    id: jobId, workspaceId: WS.id, projectId: null, jobType: "research.generate", status: "Succeeded",
    title: "Synthetic market scan", progressPercent: 100, errorCode: null, errorMessage: null,
    cancellationRequested: false, createdAt: PINNED, queuedAt: PINNED, startedAt: PINNED,
    completedAt: PINNED, failedAt: null, cancelledAt: null, outputs: [],
    resultJson: JSON.stringify({
      assetId,
      researchType: "research",
      title: "Synthetic market scan",
      language: "en",
      executiveSummary: "Synthetic executive summary.",
      keyFindings: [{ type: "text", text: "Revenue held at twelve percent.", items: null, rows: null, citationIds: ["S1"] }],
      sections: [{ heading: "Synthetic findings", blocks: [] }],
      conclusion: "Synthetic conclusion.",
      sourceCount: 1,
      sources: [{ citationId: "S1", url: "https://source.example/study", title: "Synthetic source", domain: "source.example", publisher: null, publishedAt: null, retrievedAt: PINNED, sourceType: "web", snippet: null, searchQuery: null, rank: 0, isSelected: false }],
      representations: [
        { id: DOCX_ID, type: "docx", fileName: "synthetic-market-scan.docx", contentType: DOCX_CONTENT_TYPE },
        { id: PDF_ID, type: "pdf", fileName: "synthetic-market-scan.pdf", contentType: PDF_CONTENT_TYPE },
      ],
    }),
  } as unknown as GenerationJob;
}

const storage = new Map<string, string>();
let timers: Map<number, () => void>;
let nextTimerId = 1;
let doc: TestDocument;
let container: TestNode;
let root: Root;

async function drain() { await new Promise((r) => setTimeout(r, 0)); }
async function settle(fn?: () => void) { await act(async () => { fn?.(); await drain(); }); }
function runTimers() { const pending = [...timers.values()]; timers.clear(); for (const run of pending) run(); }

function allNodes(node: TestNode): TestNode[] { return [node, ...node.childNodes.flatMap(allNodes)]; }
function findByClass(node: TestNode, cls: string) { return allNodes(node).filter((n) => (n.getAttribute("class")?.split(/\s+/) ?? []).includes(cls)); }
function isDisabled(node: TestNode): boolean { return node.disabled === true || node.getAttribute("disabled") !== null; }
function containerText(): string { return container.textContent ?? ""; }
/** The format token is the whole visible caption of a Research rendition control. */
function downloadButton(suffix: "DOCX" | "PDF"): TestNode {
  const match = findByClass(container, "secondary-button")
    .find((n) => (n.textContent ?? "").trim() === suffix || (n.getAttribute("aria-label") ?? "").trim().endsWith(suffix));
  if (!match) throw new Error(`No Research Studio download control for ${suffix}`);
  return match;
}
function accessibleName(node: TestNode): string {
  const label = node.getAttribute("aria-label");
  const text = (node.textContent ?? "").replace(/\s+/g, " ").trim();
  return label !== null ? label.trim() : text;
}
function clickedAnchors(): TestNode[] { return doc.createdElements.filter((n) => n.nodeName === "A" && n.clicked); }
function downloadAlertNode(): TestNode | undefined {
  return findByClass(container, "form-error").find((n) => n.getAttribute("role") === "alert");
}
function alertText(): string {
  const alert = downloadAlertNode();
  return alert ? (alert.textContent ?? "").replace(/\s+/g, " ").trim() : "";
}

async function mount() {
  harness.workspace = WS;
  await act(async () => { root = createRoot(container as unknown as Element); root.render(<ResearchStudioView />); await drain(); });
  runTimers();
  await settle();
}
/** Restore only an active synthetic job, then let polling complete it in the current view. */
async function selectCompletedResultInView() {
  const activeJob = { ...completedResearchJob(), status: "Running" as const, progressPercent: 80 };
  harness.api.getGenerationJob.mockReset()
    .mockResolvedValueOnce(activeJob)
    .mockResolvedValue(completedResearchJob());
  storage.set(activeJobKey, JOB_ID);
  await mount();
  expect(findByClass(container, "research-report-shell")).toHaveLength(1);
}

async function rerenderWithWorkspace(workspaceId: string) {
  harness.workspace = { id: workspaceId };
  await act(async () => { root.render(<ResearchStudioView />); await drain(); });
  await settle();
  runTimers();
  await settle();
}

async function restoreCompletedResultInView(jobId: string) {
  const completed = completedResearchJob(jobId);
  const active = { ...completed, status: "Running" as const, progressPercent: 80 };
  harness.api.getGenerationJob.mockReset().mockResolvedValueOnce(active).mockResolvedValue(completed);
  storage.set(activeJobKey, jobId);
  await rerenderWithWorkspace(WS.id);
  expect(findByClass(container, "research-report-shell")).toHaveLength(1);
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
  harness.api.getGenerationJob.mockReset().mockResolvedValue(completedResearchJob());
  harness.api.getResearchSources.mockReset().mockResolvedValue({ jobId: JOB_ID, sources: [] });
  harness.api.createResearchGenerationJob.mockReset().mockResolvedValue(completedResearchJob());
  harness.api.cancelGenerationJob.mockReset().mockResolvedValue({ status: "Cancelled" });
  harness.api.downloadAssetRepresentation.mockReset().mockResolvedValue(new Blob(["synthetic"], { type: DOCX_CONTENT_TYPE }));
  harness.api.downloadResearchSourcesExport.mockReset().mockResolvedValue(new Blob(["a,b\n1,2"], { type: "text/csv" }));
  harness.workspace = null;
});
afterEach(() => { act(() => root?.unmount()); vi.unstubAllGlobals(); });

describe("Research Studio click-time 409 ASSET_FILE_UNAVAILABLE consumer contract (#1049)", () => {
  it("treats changed assets and renditions as a new context even when job and workspace IDs match", () => {
    const currentJob = completedResearchJob();
    expect(sameResearchDownloadContext(currentJob, completedResearchJob())).toBe(true);
    expect(sameResearchDownloadContext(currentJob, completedResearchJob(JOB_ID, OTHER_ASSET_ID))).toBe(false);
    const changedResult = JSON.parse(currentJob.resultJson ?? "{}") as { representations: Array<{ id: string; type: string; fileName: string; contentType: string }> };
    changedResult.representations[0].fileName = "updated-market-scan.docx";
    expect(sameResearchDownloadContext(currentJob, { ...currentJob, resultJson: JSON.stringify(changedResult) })).toBe(false);
  });

  it("turns the pruned backing file into a localized alert with no silent success and no payload leak", async () => {
    harness.api.downloadAssetRepresentation.mockImplementation((_assetId: string, representationId: string) =>
      representationId === DOCX_ID ? Promise.reject(unavailableError()) : Promise.resolve(new Blob(["synthetic"], { type: PDF_CONTENT_TYPE })));
    await selectCompletedResultInView();

    await settle(() => { downloadButton("DOCX").dispatchEvent({ type: "click", bubbles: true }); });

    expect(harness.api.downloadAssetRepresentation).toHaveBeenCalledWith(ASSET_ID, DOCX_ID);
    // No silent success: the object-URL/anchor-download path never ran.
    expect(harness.objectUrl.created, "a 409 must not fabricate a download").toBe(0);
    expect(clickedAnchors(), "a 409 must not click a download anchor").toHaveLength(0);
    // The failure is announced from the real catalog, not as a raw key or raw payload.
    const alert = downloadAlertNode();
    expect(alert, 'the download failure must be announced with role="alert"').toBeTruthy();
    expect(alertText()).toContain(translations.en["research.downloadError"]);
    expect(alertText()).not.toContain("research.downloadError");
    // No leak of the 409 envelope anywhere in the rendered tree.
    const text = containerText();
    for (const sentinel of LEAK_SENTINELS) {
      expect(text, `the DOM must not leak the error payload sentinel ${sentinel}`).not.toContain(sentinel);
    }
    // No stuck busy affordance after the rejection, and the report body (file-independent)
    // keeps rendering: the user is not thrown out of the report they selected.
    for (const suffix of ["DOCX", "PDF"] as const) {
      expect(isDisabled(downloadButton(suffix)), `${suffix} must stay pressable after the failure`).toBe(false);
    }
    expect(text).toContain("Revenue held at twelve percent.");
  });

  it.each(["en", "ar", "ku"] as const)("%S: the unavailable click is answered in locale with a live retry and a reselect exit", async (locale) => {
    harness.locale = locale;
    harness.api.downloadAssetRepresentation.mockRejectedValue(unavailableError());
    await selectCompletedResultInView();

    await settle(() => { downloadButton("DOCX").dispatchEvent({ type: "click", bubbles: true }); });

    const message = translations[locale]["research.downloadError"];
    expect(alertText(), `${locale}: the alert must carry the localized catalog copy`).toContain(message);
    expect(alertText(), `${locale}: the raw i18n key must never render`).not.toContain("research.downloadError");
    for (const sentinel of LEAK_SENTINELS) {
      expect(containerText(), `${locale}: must not leak ${sentinel}`).not.toContain(sentinel);
    }
    // Accessible retry: pressing the failed control again re-issues the download request.
    await settle(() => { downloadButton("DOCX").dispatchEvent({ type: "click", bubbles: true }); });
    expect(harness.api.downloadAssetRepresentation, `${locale}: the failed control must remain a live retry`).toHaveBeenCalledTimes(2);
    // Accessible reselect: "Start another" returns the user to the compose surface and
    // drops the unavailable report so its dead-end control is not re-advertised.
    const reselect = findByClass(container, "primary-button").find((n) => (n.textContent ?? "").includes(translations[locale]["research.createAnother"]));
    expect(reselect, `${locale}: a localized reselect control must be offered`).toBeTruthy();
    // Current main clears completed-job markers automatically; seed a legacy stale value to
    // verify the explicit reselect action still removes it before returning to compose.
    storage.set(activeJobKey, JOB_ID);
    await settle(() => { reselect!.dispatchEvent({ type: "click", bubbles: true }); });
    expect(findByClass(container, "research-studio-layout"), `${locale}: reselect must reach the compose surface`).toHaveLength(1);
    expect(findByClass(container, "research-report-shell")).toHaveLength(0);
    expect(storage.has(activeJobKey), `${locale}: the unavailable report must not persist for the next session`).toBe(false);
    expect(downloadAlertNode(), `${locale}: the stale failure alert must not follow the user`).toBeFalsy();
  });

  it("keeps the ready sibling rendition downloadable and clears the stale banner", async () => {
    const order: string[] = [];
    let resolvePdf: ((blob: Blob) => void) | undefined;
    harness.api.downloadAssetRepresentation.mockImplementation((_assetId: string, representationId: string) => {
      order.push(representationId === DOCX_ID ? "docx" : "pdf");
      return representationId === DOCX_ID
        ? Promise.reject(unavailableError())
        : new Promise<Blob>((resolve) => { resolvePdf = resolve; });
    });
    await selectCompletedResultInView();

    await settle(() => { downloadButton("DOCX").dispatchEvent({ type: "click", bubbles: true }); });
    expect(downloadAlertNode(), "the DOCX failure must be announced").toBeTruthy();

    // The unavailability is per representation: the ready PDF is untouched.
    await settle(() => { downloadButton("PDF").dispatchEvent({ type: "click", bubbles: true }); });
    expect(downloadButton("PDF").getAttribute("aria-busy"), "the successful download must be busy while its response is pending").toBe("true");
    await settle(() => { resolvePdf?.(new Blob(["synthetic"], { type: PDF_CONTENT_TYPE })); });
    await settle(() => { runTimers(); });
    expect(order).toEqual(["docx", "pdf"]);
    expect(downloadButton("PDF").getAttribute("aria-busy"), "a successful download must leave the busy state").not.toBe("true");
    const clicked = clickedAnchors();
    expect(clicked, "the ready PDF must complete the anchor download").toHaveLength(1);
    expect(clicked[0].download).toBe("synthetic-market-scan.pdf");
    expect(harness.objectUrl.created).toBe(1);
    expect(downloadAlertNode(), "a later success must not leave the stale unavailability banner up").toBeFalsy();
  });

  it("announces the in-flight rendition download with a busy state, as the sibling studios do", async () => {
    // Busy status belongs to the requested rendition, not the ready sibling, and must clear
    // after either a successful response or the click-time unavailable-file rejection.
    let rejectDownload: ((cause: unknown) => void) | undefined;
    harness.api.downloadAssetRepresentation.mockImplementation(() => new Promise((_resolve, reject) => { rejectDownload = reject; }));
    await selectCompletedResultInView();

    await settle(() => { downloadButton("DOCX").dispatchEvent({ type: "click", bubbles: true }); });

    expect(downloadButton("DOCX").getAttribute("aria-busy"),
      'a download in flight must be announced with aria-busy="true"').toBe("true");

    await settle(() => { rejectDownload?.(unavailableError()); });
    // Whatever the fix shape is, a rejected download must never leave a stuck busy promise.
    expect(downloadButton("DOCX").getAttribute("aria-busy"), "a rejected download must leave the busy state").not.toBe("true");
    expect(downloadButton("PDF").getAttribute("aria-busy"), "the sibling must never inherit the busy state").not.toBe("true");
  });

  it("does not let an old job's late success download or clear the replacement job's busy state", async () => {
    let resolveOld: ((blob: Blob) => void) | undefined;
    harness.api.downloadAssetRepresentation.mockImplementationOnce(() => new Promise<Blob>((resolve) => { resolveOld = resolve; }));
    await selectCompletedResultInView();

    await settle(() => { downloadButton("DOCX").dispatchEvent({ type: "click", bubbles: true }); });
    expect(downloadButton("DOCX").getAttribute("aria-busy")).toBe("true");
    const createAnother = findByClass(container, "primary-button").find((n) => (n.textContent ?? "").includes(translations.en["research.createAnother"]));
    expect(createAnother).toBeTruthy();
    await settle(() => { createAnother!.dispatchEvent({ type: "click", bubbles: true }); });
    expect(findByClass(container, "research-studio-layout")).toHaveLength(1);
    expect(findByClass(container, "research-report-shell")).toHaveLength(0);

    await restoreCompletedResultInView(REPLACEMENT_JOB_ID);
    let resolveNew: ((blob: Blob) => void) | undefined;
    harness.api.downloadAssetRepresentation.mockImplementation(() => new Promise<Blob>((resolve) => { resolveNew = resolve; }));
    await settle(() => { downloadButton("DOCX").dispatchEvent({ type: "click", bubbles: true }); });
    expect(downloadButton("DOCX").getAttribute("aria-busy"), "the replacement job's operation must be active").toBe("true");

    await settle(() => { resolveOld?.(new Blob(["old job"], { type: DOCX_CONTENT_TYPE })); });
    expect(clickedAnchors(), "the old job must not create a download link").toHaveLength(0);
    expect(downloadButton("DOCX").getAttribute("aria-busy"), "the old operation must not clear the replacement operation").toBe("true");
    expect(downloadAlertNode(), "the old operation must not write into the replacement report").toBeFalsy();

    await settle(() => { resolveNew?.(new Blob(["new job"], { type: DOCX_CONTENT_TYPE })); });
    await settle(() => { runTimers(); });
    expect(clickedAnchors(), "only the replacement job's completed request may download").toHaveLength(1);
    expect(clickedAnchors()[0].download).toBe("synthetic-market-scan.docx");
    expect(downloadButton("DOCX").getAttribute("aria-busy")).not.toBe("true");
  });

  it("does not let a late unavailable-file error or finally clear a newer replacement-job operation", async () => {
    let rejectOld: ((cause: unknown) => void) | undefined;
    harness.api.downloadAssetRepresentation.mockImplementationOnce(() => new Promise<Blob>((_resolve, reject) => { rejectOld = reject; }));
    await selectCompletedResultInView();

    await settle(() => { downloadButton("DOCX").dispatchEvent({ type: "click", bubbles: true }); });
    const createAnother = findByClass(container, "primary-button").find((n) => (n.textContent ?? "").includes(translations.en["research.createAnother"]));
    await settle(() => { createAnother!.dispatchEvent({ type: "click", bubbles: true }); });
    await restoreCompletedResultInView(REPLACEMENT_JOB_ID);
    let resolveNew: ((blob: Blob) => void) | undefined;
    harness.api.downloadAssetRepresentation.mockImplementation(() => new Promise<Blob>((resolve) => { resolveNew = resolve; }));
    await settle(() => { downloadButton("DOCX").dispatchEvent({ type: "click", bubbles: true }); });
    expect(downloadButton("DOCX").getAttribute("aria-busy"), "the replacement operation must remain active").toBe("true");

    await settle(() => { rejectOld?.(unavailableError()); });
    expect(downloadAlertNode(), "a stale 409 must not be announced in the replacement report").toBeFalsy();
    expect(downloadButton("DOCX").getAttribute("aria-busy"), "the stale rejection must not clear the new operation").toBe("true");
    expect(findByClass(container, "research-report-shell")).toHaveLength(1);

    await settle(() => { resolveNew?.(new Blob(["new job"], { type: DOCX_CONTENT_TYPE })); });
    await settle(() => { runTimers(); });
    expect(clickedAnchors()).toHaveLength(1);
    expect(downloadButton("DOCX").getAttribute("aria-busy")).not.toBe("true");
  });

  it("invalidates old downloads across workspace A→B→A and protects the re-entered operation", async () => {
    let resolveOld: ((blob: Blob) => void) | undefined;
    harness.api.downloadAssetRepresentation.mockImplementationOnce(() => new Promise<Blob>((resolve) => { resolveOld = resolve; }));
    await selectCompletedResultInView();

    await settle(() => { downloadButton("DOCX").dispatchEvent({ type: "click", bubbles: true }); });
    expect(downloadButton("DOCX").getAttribute("aria-busy")).toBe("true");
    await rerenderWithWorkspace(OTHER_WORKSPACE_ID);
    expect(findByClass(container, "research-report-shell")).toHaveLength(0);

    await restoreCompletedResultInView(JOB_ID);
    expect(downloadButton("DOCX").getAttribute("aria-busy"), "a workspace round-trip must clear the previous busy state").not.toBe("true");
    let resolveFresh: ((blob: Blob) => void) | undefined;
    harness.api.downloadAssetRepresentation.mockImplementation(() => new Promise<Blob>((resolve) => { resolveFresh = resolve; }));
    await settle(() => { downloadButton("DOCX").dispatchEvent({ type: "click", bubbles: true }); });
    expect(downloadButton("DOCX").getAttribute("aria-busy")).toBe("true");

    await settle(() => { resolveOld?.(new Blob(["old workspace"], { type: DOCX_CONTENT_TYPE })); });
    expect(clickedAnchors(), "the old workspace response must not create a link").toHaveLength(0);
    expect(downloadButton("DOCX").getAttribute("aria-busy"), "the old response must not clear the re-entered workspace operation").toBe("true");

    await settle(() => { resolveFresh?.(new Blob(["current workspace"], { type: DOCX_CONTENT_TYPE })); });
    await settle(() => { runTimers(); });
    expect(clickedAnchors()).toHaveLength(1);
    expect(downloadButton("DOCX").getAttribute("aria-busy")).not.toBe("true");
  });

  it.each(["en", "ar", "ku"] as const)("%s: names each Research download control as a download, not as a bare format token", async (locale) => {
    // The action name uses the existing localized download verb from the studio catalog,
    // and remains stable after an unavailable-file response so the same control can be retried.
    harness.locale = locale;
    harness.api.downloadAssetRepresentation.mockRejectedValue(unavailableError());
    await selectCompletedResultInView();

    const verb = translations[locale]["document.downloadLabel"];
    for (const suffix of ["DOCX", "PDF"] as const) {
      const name = accessibleName(downloadButton(suffix));
      expect(name, `${locale} ${suffix}: the accessible name must include its localized action and format`).toBe(`${verb} ${suffix}`);
    }
    // And the naming must survive the failure: the retry affordance keeps the same name.
    await settle(() => { downloadButton("DOCX").dispatchEvent({ type: "click", bubbles: true }); });
    expect(accessibleName(downloadButton("DOCX"))).toBe(`${verb} DOCX`);
  });
});
