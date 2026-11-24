import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { GenerationJob } from "@/lib/api";
import { locales, translate, type Locale } from "@/lib/i18n";
import { voiceActiveJobStorageKey } from "@/lib/voiceStudioState";

// Independent, test-only acceptance of the RECOVERY half of the in-flight status
// poll failure on /create/voice. Nothing here overlaps
// VoiceStudioView.pollingFailure.test.tsx: that file is a static-source plus
// pure-component audit and never mounts the route, so it cannot see what happens
// AFTER a poll has failed. This file does, by driving the real component through
// a synthetic browser exactly in the style of SocialStudioCancellation and
// ImageStudioBrowserLifecycle.
//
// The path under test, in order:
//   1. a job is in flight and one status poll is rejected;
//   2. the localized `role="alert"` from #1166 becomes visible over the progress
//      panel, without replacing the polite status region;
//   3. the next poll SUCCEEDS, and the stale failure alert MUST disappear while
//      the studio is still watching the very same job (no second job, no lost
//      progress, and no English provider message left on screen);
//   4. when that same job then reaches its terminal Succeeded state the result is
//      still the one the alert interrupted, and the alert stays gone;
//   5. separately, a job cancelled after a poll failure MUST NOT be announced as
//      a success, and its terminal state clears the stale poll alert.
//
// Provider-free and deterministic: no network, no real clock, no customer data.
// Every job, asset id and message below is synthetic.

// ---- synthetic DOM harness, in the style of SocialStudioCancellation/ImageStudioBrowserLifecycle ----
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
  createTextNode(value: string) { return new TestNodeText(value, this.ownerDocument ?? this); }
  getElementById(id: string) { return this.find((n) => n.getAttribute("id") === id); }
  private find(p: (n: TestNode) => boolean): TestNode | null {
    const visit = (n: TestNode): TestNode | null => (p(n) ? n : n.childNodes.map(visit).find(Boolean) ?? null);
    return visit(this);
  }
}

const harness = vi.hoisted(() => ({
  locale: "en" as string,
  auth: { workspace: null as { id: string } | null, user: null as { id: string } | null },
  api: {
    listProjects: vi.fn(), listAssets: vi.fn(), getGenerationJob: vi.fn(), assetFileUrl: vi.fn(),
    createVoiceGenerationJob: vi.fn(), cancelGenerationJob: vi.fn(),
  },
}));

vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => ({ workspace: harness.auth.workspace, user: harness.auth.user }),
}));
// The real translation table, memoised per locale: `t` identity is in the polling
// effect's dependency list, so a fresh function per render would reschedule the
// poll on every paint and make the failure/recovery ordering unverifiable.
vi.mock("@/components/LocaleProvider", async () => {
  const i18n = await vi.importActual<typeof import("@/lib/i18n")>("@/lib/i18n");
  const cache = new Map<string, (key: string, variables?: Record<string, string>) => string>();
  const tFor = (locale: string) => {
    let fn = cache.get(locale);
    if (!fn) {
      fn = (key, variables) => i18n.translate(locale as Locale, key, variables);
      cache.set(locale, fn);
    }
    return fn;
  };
  return { useLocale: () => ({ locale: harness.locale, t: tFor(harness.locale) }) };
});
// A stable instance: the studio keeps searchParams in an effect dependency list.
const searchParams = vi.hoisted(() => new URLSearchParams());
vi.mock("next/navigation", () => ({ useSearchParams: () => searchParams }));
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
  const Icon = (props: Record<string, unknown>) => {
    // The pixel size is an icon-library prop, not a DOM attribute.
    const rest = { ...props };
    delete rest.size;
    return <span {...rest} />;
  };
  return {
    AlertCircle: Icon, Check: Icon, CheckCircle2: Icon, Clock3: Icon, Download: Icon, FileAudio: Icon,
    Headphones: Icon, LoaderCircle: Icon, Mic2: Icon, Pause: Icon, Play: Icon, RefreshCw: Icon,
    Sparkles: Icon, Volume2: Icon, XCircle: Icon,
  };
});

import { VoiceStudioView } from "./VoiceStudioView";

const ws = { id: "workspace-voice-qa" };
const user = { id: "user-voice-qa" };
const JOB_ID = "job-voice-synthetic-1";
const ASSET_ID = "asset-voice-synthetic-1";
// The English server-facing text a rejected status poll carries. #1166's point is
// that this must never reach the screen, and the recovery half must not leak it
// either.
const PROVIDER_ENGLISH = "Something went wrong.";

function voiceJob(overrides: Partial<GenerationJob> = {}): GenerationJob {
  return {
    id: JOB_ID, workspaceId: ws.id, projectId: null, jobType: "voice.generate", status: "Running",
    title: "Synthetic welcome note", progressPercent: 20, resultJson: null, errorCode: null,
    errorMessage: null, cancellationRequested: false, createdAt: "2026-11-19T00:00:00Z",
    queuedAt: "2026-11-19T00:00:01Z", startedAt: "2026-11-19T00:00:02Z", completedAt: null,
    failedAt: null, cancelledAt: null, outputs: [], ...overrides,
  } as GenerationJob;
}
function succeededVoiceJob(): GenerationJob {
  return voiceJob({
    status: "Succeeded", progressPercent: 100, completedAt: "2026-11-19T00:01:00Z",
    resultJson: JSON.stringify({
      assetId: ASSET_ID, assetType: "audio", contentType: "audio/mpeg", format: "mp3",
      language: "en", voiceStyle: "neutral", speakingStyle: "clear", durationMilliseconds: 4200, sizeBytes: 88_000,
    }),
  });
}
function cancelledVoiceJob(): GenerationJob {
  return voiceJob({ status: "Cancelled", errorCode: "VOICE_CANCELLED", cancelledAt: "2026-11-19T00:01:00Z", cancellationRequested: true });
}

type PollStep = { job?: GenerationJob; fail?: boolean };

const storage = new Map<string, string>();
let steps: PollStep[];
let requestedJobIds: string[];
let timers: Map<number, () => void>;
let nextTimerId = 1;
let doc: TestDocument;
let container: TestNode;
let root: Root;

async function drain() { await new Promise((r) => setTimeout(r, 0)); }
function flushTimers() { const pending = [...timers.values()]; timers.clear(); for (const cb of pending) cb(); }
/** Advances exactly one scheduled status poll and settles the React updates it causes. */
async function runPoll() { await act(async () => { flushTimers(); await drain(); }); }
async function settle(fn: () => void) { await act(async () => { fn(); await drain(); }); }

function allNodes(node: TestNode): TestNode[] { return [node, ...node.childNodes.flatMap(allNodes)]; }
function findByClass(node: TestNode, cls: string) { return allNodes(node).filter((n) => (n.getAttribute("class")?.split(/\s+/) ?? []).includes(cls)); }
function findByRole(role: string) { return allNodes(container).filter((n) => n.getAttribute("role") === role); }
function alerts() { return findByRole("alert"); }
function alertText() { return alerts().map((node) => node.textContent).join(" | "); }
function politeStatus() { return allNodes(container).filter((n) => n.getAttribute("aria-live") === "polite" && n.getAttribute("role") === "status"); }
function progressValue() { return findByClass(container, "generation-progress-track")[0]?.getAttribute("aria-valuenow") ?? null; }
function generatingPanels() { return findByClass(container, "voice-generation-panel"); }
function resultPanels() { return findByClass(container, "voice-result-panel"); }
function pageText() { return container.textContent; }
function downloadHref() {
  return findByClass(container, "voice-secondary-action")
    .map((node) => node.getAttribute("href") ?? "")
    .find((href) => href.includes(ASSET_ID)) ?? null;
}

async function mount(locale: Locale) {
  harness.locale = locale;
  await act(async () => {
    root = createRoot(container as unknown as Element);
    root.render(<VoiceStudioView />);
    await drain();
  });
}
function clickCancelButton() {
  const button = findByClass(container, "voice-cancel-action")[0];
  if (!button) throw new Error("no cancel button rendered while the job is in flight");
  return settle(() => { button.dispatchEvent({ type: "click", bubbles: true }); });
}

beforeEach(() => {
  doc = new TestDocument();
  container = doc.createElement("div");
  doc.body.appendChild(container);
  timers = new Map();
  nextTimerId = 1;
  storage.clear();
  steps = [];
  requestedJobIds = [];
  const windowStub = {
    document: doc, HTMLElement: TestNode, HTMLIFrameElement: TestNode, Element: TestNode, Node: TestNode,
    addEventListener: vi.fn(), removeEventListener: vi.fn(),
    setTimeout: (cb: () => void) => { const id = nextTimerId++; timers.set(id, cb); return id; },
    clearTimeout: (id: number) => { timers.delete(id); },
    sessionStorage: {
      getItem: (k: string) => storage.get(k) ?? null,
      setItem: (k: string, v: string) => { storage.set(k, v); },
      removeItem: (k: string) => { storage.delete(k); },
    },
  };
  doc.defaultView = windowStub;
  vi.stubGlobal("document", doc);
  vi.stubGlobal("window", windowStub);
  vi.stubGlobal("navigator", { userAgent: "test" });
  vi.stubGlobal("IS_REACT_ACT_ENVIRONMENT", true);

  harness.locale = "en";
  harness.auth.workspace = ws;
  harness.auth.user = user;

  harness.api.listProjects.mockReset().mockResolvedValue([]);
  harness.api.listAssets.mockReset().mockResolvedValue({ items: [], page: 1, pageSize: 100, totalCount: 0, totalPages: 1 });
  harness.api.assetFileUrl.mockReset().mockImplementation((assetId: string, inline?: boolean) => `/api/assets/${assetId}/download${inline ? "?inline=true" : ""}`);
  harness.api.createVoiceGenerationJob.mockReset();
  harness.api.cancelGenerationJob.mockReset().mockResolvedValue({ status: "Cancelled" });
  // The first read is the workspace restore; every read after it is one poll.
  harness.api.getGenerationJob.mockReset().mockImplementation(async (jobId: string) => {
    requestedJobIds.push(jobId);
    const step = steps.shift();
    if (!step) throw new Error("the synthetic status-poll queue ran dry");
    if (step.fail) throw new Error(PROVIDER_ENGLISH);
    return step.job as GenerationJob;
  });

  // An in-flight job restored from its durable pointer: this is the state a real
  // member returns to, and the only state in which the poll can fail.
  storage.set(voiceActiveJobStorageKey(ws.id), JOB_ID);
});
afterEach(() => {
  act(() => root?.unmount());
  vi.unstubAllGlobals();
});

describe("Voice Studio polling failure recovery", () => {
  it.each(locales)("clears the %s failure alert on the next good poll while keeping the same job and its result", async (locale) => {
    steps = [
      { job: voiceJob({ progressPercent: 20 }) }, // restore
      { fail: true },                             // poll 1: the single failure
      { job: voiceJob({ progressPercent: 45 }) }, // poll 2: recovery, same job id
      { job: succeededVoiceJob() },               // poll 3: the same job completes
    ];
    await mount(locale);
    const copy = translate(locale, "voice.pollError");
    expect(copy).not.toBe("voice.pollError");

    // Phase 1: the failure is announced over the progress panel.
    await runPoll();
    expect(alerts(), "one failure alert while the job is still in flight").toHaveLength(1);
    expect(alertText()).toContain(copy);
    expect(alerts()[0].getAttribute("aria-live")).toBe("assertive");
    expect(generatingPanels()).toHaveLength(1);
    expect(politeStatus(), "the polite progress region stays for status changes").toHaveLength(1);
    expect(alertText(), "the English provider message never reaches a localized screen").not.toContain(PROVIDER_ENGLISH);

    // Phase 2: a later successful poll MUST retire the stale alert.
    await runPoll();
    expect(alerts(), "the stale failure alert must not outlive the failure it reported").toHaveLength(0);
    expect(pageText()).not.toContain(copy);
    expect(pageText(), "and must not be replaced by the provider message").not.toContain(PROVIDER_ENGLISH);
    // Same job, still in flight, further along: the recovery did not restart or drop it.
    expect(generatingPanels(), "still the one in-flight panel, not a fresh one").toHaveLength(1);
    expect(progressValue()).toBe("45");
    expect(politeStatus()).toHaveLength(1);

    // Phase 3: that same job reaching its terminal state keeps its own result.
    await runPoll();
    expect(generatingPanels()).toHaveLength(0);
    expect(resultPanels(), "the interrupted job is the one that finishes").toHaveLength(1);
    expect(downloadHref(), "the result is the artifact of the same job").toBe(`/api/assets/${ASSET_ID}/download`);
    expect(alerts(), "no failure alert survives into the result").toHaveLength(0);
    expect(pageText()).not.toContain(copy);
    // Only ever one job: the failure, the recovery and the completion all read the same pointer.
    expect([...new Set(requestedJobIds)], "the studio never watched another job").toEqual([JOB_ID]);
  });

  it("keeps a job cancelled after a poll failure from being announced as a success", async () => {
    steps = [
      { job: voiceJob({ progressPercent: 20 }) }, // restore
      { fail: true },                              // poll 1: the failure is announced
      { job: cancelledVoiceJob() },                // the cancel re-read settles on Cancelled
    ];
    await mount("en");
    await runPoll();
    expect(alerts(), "the poll failure is on screen before the member cancels").toHaveLength(1);
    expect(alertText()).toContain(translate("en", "voice.pollError"));

    await clickCancelButton();

    expect(resultPanels(), "a cancelled job must never render the success panel").toHaveLength(0);
    expect(pageText(), "no success copy is announced for a cancelled job").not.toContain(translate("en", "voice.resultTitle"));
    expect(pageText()).not.toContain(translate("en", "voice.savedToAssets"));
    expect(downloadHref()).toBeNull();
    expect(generatingPanels(), "and it is no longer presented as in flight").toHaveLength(0);
    // The terminal state retires the stale poll alert and says what actually happened.
    expect(alerts(), "the cancellation is reported in place of the stale poll failure").toHaveLength(1);
    expect(alertText()).toContain(translate("en", "voice.cancelled"));
    expect(alertText()).not.toContain(translate("en", "voice.pollError"));
    expect([...new Set(requestedJobIds)]).toEqual([JOB_ID]);

    // Cancellation also ends the polling loop: nothing further is announced.
    const reads = harness.api.getGenerationJob.mock.calls.length;
    await runPoll();
    expect(harness.api.getGenerationJob).toHaveBeenCalledTimes(reads);
  });
});
