import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { formatAdminNumber } from "@/lib/adminOperationsI18n";
import { presetRange } from "@/lib/adminUsageState";
import type { AdminOperationsDashboard, AdminProviderHealth } from "@/lib/api";
import type { Locale } from "@/lib/i18n";
import { AdminOperationsView } from "./AdminOperationsView";

// Synthetic DOM harness, in the style established by AssetsViewListing.test (#849).
// The operations dashboard answers every filter change with an overlapping read, so
// these cases hold the fence between the window the administrator selected and the
// window a reload asks for. Date is frozen so a window is a stable, comparable value.

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
  // React falls back to its input-event polyfill outside a real browser, where value changes are
  // watched through the legacy onpropertychange hook rather than the native input event.
  attachEvent() { return true; }
  detachEvent() { return true; }
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
    this.documentElement = new TestNode(1, "html", this);
    this.body = new TestNode(1, "body", this);
    this.documentElement.appendChild(this.body);
    this.appendChild(this.documentElement);
    this.activeElement = this.body;
    this.defaultView = {};
  }
  createElement(name: string) { return new TestNode(1, name.toUpperCase(), this); }
  createElementNS(_namespace: string, name: string) { return this.createElement(name); }
  createTextNode(value: string) { return new TestNodeText(value, this); }
  getElementById(id: string) { return this.find((node) => node.getAttribute("id") === id); }
  private find(predicate: (node: TestNode) => boolean): TestNode | null { const visit = (node: TestNode): TestNode | null => (predicate(node) ? node : node.childNodes.map(visit).find(Boolean) ?? null); return visit(this); }
}
type AdminOperationsRange = { fromUtc: string; toUtc: string };
type Deferred<T> = { promise: Promise<T>; resolve: (value: T) => void; reject: (error: unknown) => void };
type Request = { range: AdminOperationsRange; deferred: Deferred<AdminOperationsDashboard>; settled: boolean };

const harness = vi.hoisted(() => ({
  locale: "en" as Locale,
  api: {
    getAdminOperationsDashboard: vi.fn(),
    recoverAdminStuckJob: vi.fn(),
  },
}));

vi.mock("@/components/LocaleProvider", () => ({
  useLocale: () => ({ locale: harness.locale, t: (key: string) => key }),
}));
vi.mock("@/lib/api", () => ({ api: harness.api }));
vi.mock("next/link", () => ({
  default: (props: { href: string; children?: unknown }) => <a href={props.href}>{props.children as never}</a>,
}));
vi.mock("lucide-react", () => {
  const Icon = (props: Record<string, unknown>) => <span {...props} />;
  return {
    Activity: Icon, AlertTriangle: Icon, ArrowLeft: Icon, Boxes: Icon, CheckCircle2: Icon,
    CircleDollarSign: Icon, Clock3: Icon, Database: Icon, FileStack: Icon, Gauge: Icon,
    RefreshCw: Icon, ServerCog: Icon, ShieldCheck: Icon, UsersRound: Icon, Workflow: Icon, XCircle: Icon,
  };
});

const CUSTOM_RANGE: AdminOperationsRange = {
  fromUtc: new Date("2026-11-03T00:00:00.000Z").toISOString(),
  toUtc: new Date("2026-11-05T23:59:59.999Z").toISOString(),
};

function sameWindow(a: AdminOperationsRange, b: AdminOperationsRange) {
  return a.fromUtc === b.fromUtc && a.toUtc === b.toUtc;
}

// Each window is identified by a distinct job count, so a rendered figure always
// says which window it was read from.
function jobsInRangeFor(range: AdminOperationsRange) {
  if (sameWindow(range, presetRange("today"))) return 1;
  if (sameWindow(range, presetRange("sevenDays"))) return 7;
  if (sameWindow(range, presetRange("thirtyDays"))) return 30;
  return 512;
}

function providerHealth(key: string, category: string): AdminProviderHealth {
  return {
    key, category, status: "operational", enabled: true, configured: true,
    recentSuccessCount: 4, recentFailureCount: 0, averageLatencyMs: 900,
    rateLimitEventCount: 0, timeoutEventCount: 0, qualityControlFailureCount: 0,
    retryCount: 0, fallbackCount: 0, fallbackTelemetryRecorded: true,
    estimatedProviderCostUsd: 0.4, actualProviderCostUsd: 0.35,
    lastSuccessAt: "2026-11-17T09:00:00Z", lastFailureAt: null, lastFailureCode: null,
    recentFailures: [],
  };
}

function dashboardFor(range: AdminOperationsRange): AdminOperationsDashboard {
  const jobs = jobsInRangeFor(range);
  return {
    range,
    generation: {
      totalJobsInRange: jobs,
      byStatus: [{ key: "Succeeded", count: jobs }],
      byStudio: [{ key: "movie", count: jobs }],
      recentFailures: [{ jobId: "job-failed-1", jobType: "movie.render", errorCode: "provider.timeout", failedAt: "2026-11-17T08:40:00Z" }],
      failuresByCode: [{ key: "provider.timeout", count: 1 }],
      runningJobs: [{ jobId: "job-running-1", jobType: "movie.render", progressPercent: 20, queuedAt: "2026-11-17T08:00:00Z", startedAt: "2026-11-17T08:05:00Z", createdAt: "2026-11-17T08:00:00Z", retryCount: 0, claimExpiresAt: null, isLongRunning: false }],
      queuedOrPendingCount: 1, longRunningJobCount: 0, totalRetryCount: 1,
    },
    usage: {
      requestCount: 120, completedRequestCount: 110, failedRequestCount: 6, pendingRequestCount: 4,
      inputTokens: 1000, cachedInputTokens: 200, outputTokens: 500, imageInputTokens: 10, imageOutputTokens: 20,
      providerCostUsd: 1.25, customerChargesUsd: 2.5, pendingEstimatedProviderCostUsd: 0.1,
      byFeature: [{ feature: "movie", requestCount: 40, completedRequestCount: 38, failedRequestCount: 2, pendingRequestCount: 0, inputTokens: 400, cachedInputTokens: 20, outputTokens: 120, imageInputTokens: 0, imageOutputTokens: 8, providerCostUsd: 0.5, customerChargesUsd: 1 }],
    },
    usersAndWorkspaces: { totalUsers: 50, activeUsers: 40, disabledUsers: 10, totalWorkspaces: 20, personalWorkspaces: 15, businessWorkspaces: 4, archivedWorkspaces: 1 },
    assetsAndStorage: {
      totalAssets: 300, assetsByType: [{ key: "image", count: 200 }], totalStoredFiles: 250, storedBytes: 2048,
      filesByStatus: [{ key: "Stored", count: 250 }], filesByStorageProvider: [{ key: "Local", count: 250 }],
      configuredStorageProvider: "Local", persistentStorageConfigured: true,
    },
    billing: { customerChargingEnabled: false, configuredProvider: "none", paymentProviderConfigured: false, subscriptions: [], paymentAttemptsByStatus: [], pendingReconciliationCount: 0 },
    signals: { runningJobCount: 2, queuedOrPendingJobCount: 3, recentFailureCount: 1, anomalousUsageCountInRange: 0, lastCompletedGenerationAt: "2026-11-17T09:10:00Z" },
    providers: [providerHealth("openai-image", "image")],
    movie: {
      movieJobCountInRange: jobs, jobsByStatus: [{ key: "Succeeded", count: jobs }],
      queuedOrPendingCount: 1, oldestQueuedAt: "2026-11-17T08:00:00Z", oldestQueueAgeSeconds: 120, averageQueueAgeSeconds: 60,
      totalRetryCount: 1, retriedJobCount: 1, maxRetryCount: 3, failuresByCode: [{ key: "provider.timeout", count: 1 }],
      providerDisabledFailureCount: 0, qualityControlFailureCount: 0, qualityControlByStatus: [],
      movieAssetCount: 5, movieAssetWithStoredFileCount: 5, movieAssetIngestionGapCount: 0, completedJobsWithoutAssetCount: 0,
      accountingTransactionCount: 4, pendingAccountingCount: 0, missingAccountingEvidenceCount: 0,
      estimatedProviderCostUsd: 0.2, actualProviderCostUsd: 0.18,
      providerStatus: "operational", providerEnabled: true, providerConfigured: true,
      stuckJobCount: 1,
      stuckJobs: [{ jobId: "job-stuck-1", jobType: "movie.render", status: "Running", claimExpiresAt: "2026-11-17T09:00:00Z", startedAt: "2026-11-17T08:00:00Z", retryCount: 1, errorCode: null }],
    },
    workers: {
      configuredConcurrency: 20, observedWorkerCount: 2, healthyWorkerCount: 2, staleWorkerCount: 0,
      workers: [{ workerId: "worker-1", status: "Idle", startedAt: "2026-11-17T07:00:00Z", lastSeenAt: "2026-11-17T09:14:00Z", lastClaimedAt: null, lastCompletedAt: "2026-11-17T09:00:00Z", activeJobId: null, consecutiveIterationFailures: 0, workerConcurrency: 1, isStale: false }],
    },
    recentAdminActions: [{ id: "audit-1", actorUserId: "user-admin", action: "job.lease.recovered", targetType: "generation_job", targetId: "job-1", outcome: "Success", reason: null, createdAt: "2026-11-17T09:05:00Z" }],
  };
}

let document: TestDocument;
let root: Root;
let container: TestNode;
let timers: Map<number, () => void>;
let nextTimerId = 1;
let requests: Request[];

function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}
async function drain() { await new Promise((resolve) => setTimeout(resolve, 0)); }
async function settle(run: () => void) { await act(async () => { run(); await drain(); }); }

async function flushTimers() {
  await act(async () => {
    const pending = [...timers.values()];
    timers.clear();
    for (const callback of pending) callback();
    await drain();
  });
}
async function mountView() {
  await act(async () => {
    root = createRoot(container as unknown as Element);
    root.render(<AdminOperationsView />);
    await drain();
  });
  await flushTimers();
}
// A language switch re-creates the dashboard loader, which re-runs the mount effect.
async function switchLocale(next: Locale) {
  harness.locale = next;
  await act(async () => {
    root.render(<AdminOperationsView />);
    await drain();
  });
  await flushTimers();
}
async function settleRequest(index: number) {
  const entry = requests[index];
  if (!entry) throw new Error(`no operations request #${index + 1}`);
  await settle(() => { if (!entry.settled) { entry.settled = true; entry.deferred.resolve(dashboardFor(entry.range)); } });
  await drain();
  await act(async () => { await drain(); });
}
function requestedRange(index: number): AdminOperationsRange {
  const entry = requests[index];
  if (!entry) throw new Error(`no operations request #${index + 1}`);
  return entry.range;
}
function findByClass(node: TestNode, className: string): TestNode[] {
  return [
    ...(node.getAttribute("class")?.split(/\s+/).includes(className) ? [node] : []),
    ...node.childNodes.flatMap((child) => findByClass(child, className)),
  ];
}
function findByTag(node: TestNode, tagName: string): TestNode[] {
  return [
    ...(node.nodeName === tagName ? [node] : []),
    ...node.childNodes.flatMap((child) => findByTag(child, tagName)),
  ];
}
function rangePresetButtons() {
  return findByClass(container, "admin-range-presets").flatMap((node) => findByTag(node, "BUTTON"));
}
// The harness types textContent as a writable property, so reads are normalised here.
function textOf(node: TestNode) { return node.textContent ?? ""; }
function applyButton() {
  const match = findByClass(container, "admin-custom-range").flatMap((node) => findByTag(node, "BUTTON"))
    .find((node) => textOf(node).trim() === "Apply");
  if (!match) throw new Error("no Apply button rendered");
  return match;
}
function recoverLeaseButton() {
  const match = findByTag(container, "BUTTON").find((node) => textOf(node).trim() === "Recover lease");
  if (!match) throw new Error("no Recover lease button rendered");
  return match;
}
async function click(node: TestNode) { await settle(() => node.dispatchEvent({ type: "click", bubbles: true })); }
async function typeDateInput(index: number, value: string) {
  const input = findByTag(container, "INPUT")[index];
  if (!input) throw new Error(`no date input #${index + 1} rendered`);
  const descriptor = Object.getOwnPropertyDescriptor(TestNode.prototype, "value");
  await settle(() => { input.dispatchEvent({ type: "focusin", bubbles: true }); });
  await settle(() => {
    descriptor!.set!.call(input, value);
    input.dispatchEvent({ type: "keyup", bubbles: true });
  });
}
function renderedJobsInRange() {
  const cell = findByClass(container, "usage-stat")[0];
  if (!cell) return null;
  return findByTag(cell, "STRONG")[0]?.textContent ?? null;
}

beforeEach(() => {
  vi.useFakeTimers({ toFake: ["Date"] });
  vi.setSystemTime(new Date("2026-11-17T09:15:00Z"));
  document = new TestDocument();
  container = document.createElement("div");
  document.body.appendChild(container);
  vi.stubGlobal("document", document);
  timers = new Map();
  nextTimerId = 1;
  const window = {
    document,
    HTMLElement: TestNode,
    HTMLIFrameElement: TestNode,
    Node: TestNode,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    setTimeout: (callback: () => void) => { const id = nextTimerId++; timers.set(id, callback); return id; },
    clearTimeout: (id: number) => { timers.delete(id); },
  };
  document.defaultView = window;
  vi.stubGlobal("window", window);
  vi.stubGlobal("navigator", { userAgent: "test" });
  vi.stubGlobal("IS_REACT_ACT_ENVIRONMENT", true);
  harness.locale = "en";
  requests = [];
  harness.api.getAdminOperationsDashboard.mockReset().mockImplementation((range: AdminOperationsRange) => {
    const entry: Request = { range, deferred: deferred<AdminOperationsDashboard>(), settled: false };
    requests.push(entry);
    return entry.deferred.promise;
  });
  harness.api.recoverAdminStuckJob.mockReset().mockResolvedValue({ jobId: "job-stuck-1", status: "Queued", retryCount: 2, queuedAt: "2026-11-17T09:15:00Z", auditAction: "job.lease.recovered" });
});

afterEach(() => {
  act(() => root?.unmount());
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe("AdminOperationsView selected range across overlapping reloads", () => {
  it("re-reads the selected window, not the default window, after the dashboard loader changes", async () => {
    await mountView();
    expect(requests).toHaveLength(1);
    expect(requestedRange(0)).toEqual(presetRange("thirtyDays"));
    await settleRequest(0);

    const [todayButton, sevenDaysButton, thirtyDaysButton] = rangePresetButtons();
    await click(todayButton);
    expect(requests).toHaveLength(2);
    expect(requestedRange(1)).toEqual(presetRange("today"));
    await settleRequest(1);
    expect(renderedJobsInRange()).toBe(formatAdminNumber(1, "en"));

    // The administrator changes the interface language while the "Today" window is selected.
    await switchLocale("ar");

    expect(requests).toHaveLength(3);
    // The reload must answer the filter on screen, not a fixed preset.
    expect(requestedRange(2)).toEqual(presetRange("today"));
    expect(requestedRange(2)).not.toEqual(presetRange("thirtyDays"));
    await settleRequest(2);

    // The control state still claims "Today": the reloaded figures must be the Today window.
    expect(todayButton.getAttribute("class") ?? "").toContain("is-active");
    expect(sevenDaysButton.getAttribute("class") ?? "").not.toContain("is-active");
    expect(thirtyDaysButton.getAttribute("class") ?? "").not.toContain("is-active");
    expect(renderedJobsInRange()).toBe(formatAdminNumber(1, harness.locale));
  });

  it("keeps a hand-picked custom window across an overlapping automatic reload", async () => {
    await mountView();
    await settleRequest(0);

    await typeDateInput(0, "2026-11-03");
    await typeDateInput(1, "2026-11-05");
    await click(applyButton());
    expect(requests).toHaveLength(2);
    expect(requestedRange(1)).toEqual(CUSTOM_RANGE);
    await settleRequest(1);
    expect(renderedJobsInRange()).toBe(formatAdminNumber(512, "en"));

    await switchLocale("ku");

    expect(requests).toHaveLength(3);
    expect(requestedRange(2)).toEqual(CUSTOM_RANGE);
    await settleRequest(2);
    expect(renderedJobsInRange()).toBe(formatAdminNumber(512, harness.locale));
  });

  it("drops the frame of a filter request that a newer filter request superseded", async () => {
    await mountView();
    await settleRequest(0);

    const [todayButton, sevenDaysButton] = rangePresetButtons();
    await click(todayButton);
    await click(sevenDaysButton);
    expect(requests).toHaveLength(3);

    // The older request settles first: its frame must never reach the dashboard.
    await settleRequest(1);
    expect(findByClass(container, "admin-kpi-grid")).toHaveLength(0);

    await settleRequest(2);
    expect(renderedJobsInRange()).toBe(formatAdminNumber(7, "en"));
    expect(requestedRange(2)).toEqual(presetRange("sevenDays"));
  });

  it("refreshes a lease recovery on the window the administrator is reading", async () => {
    await mountView();
    await settleRequest(0);
    const [todayButton] = rangePresetButtons();
    await click(todayButton);
    await settleRequest(1);

    await click(recoverLeaseButton());
    expect(harness.api.recoverAdminStuckJob).toHaveBeenCalledTimes(1);
    expect(requests).toHaveLength(3);
    expect(requestedRange(2)).toEqual(presetRange("today"));
    await settleRequest(2);
    expect(renderedJobsInRange()).toBe(formatAdminNumber(1, "en"));
  });
});
