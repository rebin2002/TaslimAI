import { readFileSync } from "node:fs";
import { act } from "react";
import * as React from "react";
import { createElement } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { renderToStaticMarkup } from "react-dom/server";
import type { GenerationJob } from "@/lib/api";
import { translate } from "../lib/i18n";
import { locales, type Locale } from "../lib/i18n";
import {
  MusicStudioPollingNoticeView,
  MusicStudioView,
  musicPollIntervalMs,
  musicPollMaxDelayMs,
  nextMusicPollDelay,
} from "./MusicStudioView";

// Narrow QA lane for the Music Studio in-flight status poll.
//
// `#1166` fixed this failure class in `VoiceStudioView` and recorded that
// `MusicStudioView.tsx:342` carried the identical raw-provider catch, excluded
// from that Voice-lane deliverable. On `main` the studio wrote
// `caught instanceof Error ? caught.message : t("music.pollError")`, and
// `ApiError` always takes the first arm with the server's English text
// (`src/lib/api.ts`: `body?.error?.message ?? "Something went wrong."`), so the
// localized `music.pollError` copy was unreachable for the failures that
// actually occur. Separately, the only `error` render sat inside the `!current`
// `<form>` branch, so once a job existed the panel never read it at all, and
// the effect's `[current, t]` dependency list meant a rejected read left every
// dependency unchanged: the studio stopped polling and kept announcing a stale
// in-flight generation.
//
// Synthetic DOM harness in the style of `DocumentStudioView.listLoadStates` and
// `ProjectContextEditor`: no jsdom, no network, no provider call, no timer
// implementation and no language-pack storage. `api.getGenerationJob` is a
// fixed rejected promise.

const musicSource = readFileSync(new URL("./MusicStudioView.tsx", import.meta.url), "utf8");

/** The polling effect, sliced out of the source so the assertions stay local. */
function pollingEffect(source: string): string {
  const start = source.indexOf("const timer = window.setTimeout(async () => {");
  expect(start, "the in-flight polling effect must exist").toBeGreaterThan(-1);
  const end = source.indexOf("}, [current, pollRetry, t]);", start);
  expect(end, "the polling effect must own its retry dependency").toBeGreaterThan(-1);
  return source.slice(start, end);
}

/** The `isFailure`-else branch: the in-flight progress panel. */
function generatingBranch(source: string): string {
  const start = source.indexOf('<section className="music-state-panel" aria-live="polite">');
  expect(start, "the in-flight panel must exist").toBeGreaterThan(-1);
  return source.slice(start);
}

// ---------------------------------------------------------------------------
// Minimal DOM, so a rejected read can be driven through the real effects.
// ---------------------------------------------------------------------------
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
  get tagName() { return this.nodeName; }
  // React's `updateOptions` reads `select.options` and each option's
  // `selected`, so the stub has to expose the option list.
  get options() { return this.childNodes; }
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
// React's commit phase reads `containerInfo.HTMLIFrameElement` for the
// active-element walk, so the stub window has to expose it.
class TestHTMLElement extends TestNode {}
class TestHTMLIFrameElement extends TestNode {}

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

// ---------------------------------------------------------------------------
// Fixed seams. Nothing here reaches a network or a provider, and no music
// generation is ever requested from a real service.
// ---------------------------------------------------------------------------
const h = vi.hoisted(() => ({
  listProjects: vi.fn(),
  listAssets: vi.fn(),
  getGenerationJob: vi.fn(),
  createMusicGenerationJob: vi.fn(),
  cancelGenerationJob: vi.fn(),
  assetFileUrl: vi.fn((id: string) => `/api/assets/${id}/download`),
}));

vi.mock("@/lib/api", () => ({
  ApiError: class ApiError extends Error {},
  api: {
    listProjects: (workspaceId: string, status?: string) => h.listProjects(workspaceId, status),
    listAssets: (workspaceId: string, filters?: unknown) => h.listAssets(workspaceId, filters),
    getGenerationJob: (jobId: string) => h.getGenerationJob(jobId),
    createMusicGenerationJob: (input: unknown) => h.createMusicGenerationJob(input),
    cancelGenerationJob: (jobId: string) => h.cancelGenerationJob(jobId),
    assetFileUrl: (id: string) => h.assetFileUrl(id),
  },
}));

vi.mock("next/navigation", () => ({
  useSearchParams: () => new URLSearchParams(),
}));

vi.mock("next/link", async () => {
  const { createElement: create } = await import("react");
  type LinkProps = { href: string; className?: string; children?: React.ReactNode };
  return { default: ({ href, className, children }: LinkProps) => create("a", { href, className }, children) };
});

const stable = vi.hoisted(() => ({
  workspace: { id: "workspace-1" },
  // The locale seam is driven per case by `activeLocale`, so the same mounted
  // tree can be asserted in all three built-in locales.
  activeLocale: "en" as Locale,
}));

vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => ({ workspace: stable.workspace }),
}));

// The context object and its `t` must be stable per locale: the studio's
// load callbacks depend on `t`, so a fresh identity on every render would
// re-fire the mount effects forever and the mount would never settle.
const localeContexts = new Map<Locale, { locale: Locale; t: (key: string, variables?: Record<string, string>) => string }>();
function localeContext(locale: Locale) {
  let entry = localeContexts.get(locale);
  if (!entry) {
    entry = {
      locale,
      t: (key: string, variables?: Record<string, string>) => translate(locale, key, variables),
    };
    localeContexts.set(locale, entry);
  }
  return entry;
}

vi.mock("@/components/LocaleProvider", () => ({
  useLocale: () => localeContext(stable.activeLocale),
}));

// `MUSIC_STUDIO_PROVIDER_ACCESS` is published as "paused", so a real submit is
// refused before a job exists. The poll path under test only runs while a job is
// in flight, so the seam opens the decision without touching the shipped gate.
vi.mock("@/lib/musicStudioPreflight", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/musicStudioPreflight")>();
  return {
    ...actual,
    MUSIC_STUDIO_PROVIDER_ACCESS: "open" as const,
    musicStudioPreflightCopy: () => null,
    decideMusicStudioSubmit: () => ({ permitted: true, messageKey: null, reasonKey: null }),
  };
});

function inFlightJob(overrides: Partial<GenerationJob> = {}): GenerationJob {
  return {
    id: "job-music-1",
    workspaceId: "workspace-1",
    projectId: null,
    jobType: "music_generation",
    status: "Running",
    title: "Synthetic bed",
    progressPercent: 40,
    resultJson: null,
    errorCode: null,
    errorMessage: null,
    cancellationRequested: false,
    createdAt: "2026-11-19T00:00:00Z",
    queuedAt: "2026-11-19T00:00:00Z",
    startedAt: "2026-11-19T00:00:00Z",
    completedAt: null,
    failedAt: null,
    cancelledAt: null,
    outputs: [],
    ...overrides,
  };
}

let document_: TestDocument;
let container: TestNode;
let root: Root;
let timeouts: Array<{ id: number; handler: () => void; delay: number }> = [];
let nextTimeoutId = 1;

beforeEach(() => {
  document_ = new TestDocument();
  container = document_.createElement("div");
  document_.body.appendChild(container);
  timeouts = [];
  nextTimeoutId = 1;
  vi.stubGlobal("document", document_);
  const window = {
    document: document_,
    HTMLElement: TestHTMLElement,
    HTMLIFrameElement: TestHTMLIFrameElement,
    setTimeout: (handler: () => void, delay: number) => {
      const id = nextTimeoutId++;
      timeouts.push({ id, handler, delay });
      return id;
    },
    clearTimeout: (id: number) => { timeouts = timeouts.filter((entry) => entry.id !== id); },
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  };
  document_.defaultView = window;
  vi.stubGlobal("window", window);
  vi.stubGlobal("navigator", { userAgent: "test" });
  vi.stubGlobal("IS_REACT_ACT_ENVIRONMENT", true);
  Object.values(h).forEach((fn) => fn.mockReset());
  h.listProjects.mockResolvedValue([]);
  h.listAssets.mockResolvedValue({ items: [], page: 1, pageSize: 5, totalCount: 0, totalPages: 1 });
  h.assetFileUrl.mockImplementation((id: string) => `/api/assets/${id}/download`);
  stable.activeLocale = "en";
});

afterEach(() => {
  act(() => root?.unmount());
  vi.unstubAllGlobals();
});

async function drain() {
  await new Promise((resolve) => setTimeout(resolve, 0));
}

/** Runs every timer callback queued so far, then settles the microtask queue. */
async function runQueuedTimers() {
  const queued = timeouts.splice(0, timeouts.length);
  for (const entry of queued) {
    await act(async () => {
      entry.handler();
      await drain();
    });
  }
}

function queuedDelays(): number[] {
  return timeouts.map((entry) => entry.delay);
}

async function mountWithInFlightJob() {
  h.createMusicGenerationJob.mockResolvedValue(inFlightJob());
  await act(async () => {
    root = createRoot(container as unknown as Element);
    root.render(<MusicStudioView />);
    await drain();
  });
  const form = findByClass(container, "music-studio-workspace")[0];
  if (!form) throw new Error("the studio form is not mounted");
  await act(async () => {
    form.dispatchEvent({ type: "submit", bubbles: true });
    await drain();
  });
}

function findByClass(node: TestNode, className: string): TestNode[] {
  return [
    ...(node.getAttribute("class")?.split(/\s+/).includes(className) ? [node] : []),
    ...node.childNodes.flatMap((child) => findByClass(child, className)),
  ];
}

function findByRole(node: TestNode, role: string): TestNode[] {
  return [
    ...(node.getAttribute("role") === role ? [node] : []),
    ...node.childNodes.flatMap((child) => findByRole(child, role)),
  ];
}

function panel(): TestNode {
  const [section] = findByClass(container, "music-state-panel");
  if (!section) throw new Error("the in-flight panel is not mounted");
  return section;
}

const rawProviderMessage = "Something went wrong.";

describe("Music Studio in-flight status poll failure", () => {
  it("gives a failed read its own retry dependency", () => {
    // On main the effect closed over [current, t] only, so a rejected read
    // changed nothing the effect depends on and polling never resumed.
    expect(musicSource).toContain("}, [current, pollRetry, t]);");
    expect(pollingEffect(musicSource)).toContain("setPollRetry((attempt) => attempt + 1);");
    expect(pollingEffect(musicSource)).toContain("nextMusicPollDelay(pollRetry)");
  });

  /**
   * The catch arm alone, with comments removed so an explanatory sentence
   * cannot satisfy or defeat a check on the code.
   */
  function pollCatch(source: string): string {
    const effect = pollingEffect(source);
    const start = effect.indexOf("} catch {");
    expect(start, "the polling catch must not bind the provider error").toBeGreaterThan(-1);
    const arm = effect.slice(start, effect.indexOf("}, nextMusicPollDelay", start));
    return arm.split("\n").filter((line) => !line.trimStart().startsWith("//")).join("\n");
  }

  it("withholds the provider exception text from the member", () => {
    const effect = pollingEffect(musicSource);
    expect(effect).toContain('t("music.pollError")');
    // No property read of the rejection at all, and no bound identifier, so no
    // arm of the provider's English message can reach the interface.
    expect(pollCatch(musicSource)).not.toMatch(/\.\s*message/);
    expect(pollCatch(musicSource)).not.toMatch(/instanceof/);
    expect(effect).not.toMatch(/catch \(/);
  });

  it("keeps the last known status while the read is failing", () => {
    const branch = generatingBranch(musicSource);
    // The panel still announces the in-flight status; the notice is additive.
    expect(branch).toContain("{t(`jobs.status${current.status}`)}");
    expect(branch).toContain('aria-live="polite"');
    expect(branch.indexOf('aria-live="polite"')).toBeLessThan(branch.indexOf("MusicStudioPollingNoticeView"));
    expect(branch).toContain("{error && <MusicStudioPollingNoticeView message={error} />}");
  });

  it("backs off within the studio cadence", () => {
    expect(nextMusicPollDelay(0)).toBe(musicPollIntervalMs);
    expect(nextMusicPollDelay(1)).toBe(musicPollIntervalMs * 2);
    expect(nextMusicPollDelay(2)).toBe(musicPollIntervalMs * 3);
    // The ceiling is reached only once the linear run would pass it.
    expect(Math.ceil(musicPollMaxDelayMs / musicPollIntervalMs)).toBe(13);
    expect(nextMusicPollDelay(11)).toBe(musicPollIntervalMs * 12);
    expect(nextMusicPollDelay(12)).toBe(musicPollMaxDelayMs);
    expect(nextMusicPollDelay(40)).toBe(musicPollMaxDelayMs);
  });

  it.each(locales)("announces the failure in %s with no provider text", (locale) => {
    const copy = translate(locale, "music.pollError");
    expect(copy).not.toBe("music.pollError");
    const html = renderToStaticMarkup(createElement(MusicStudioPollingNoticeView, { message: copy }));
    expect(html).toContain(copy);
    expect(html).toContain('role="alert"');
    expect(html).toContain('aria-live="assertive"');
    expect(html).not.toContain(rawProviderMessage);
    expect(html).not.toContain("music.pollError");
  });

  it("keeps the English, Arabic and Kurdish copies distinct", () => {
    const texts = locales.map((locale) => translate(locale, "music.pollError"));
    expect(new Set(texts).size).toBe(3);
  });

  it.each(locales)("renders the localized poll failure to a %s member while generating", async (locale) => {
    stable.activeLocale = locale;
    // A synthetic rejected promise carrying the server's English text.
    h.getGenerationJob.mockRejectedValue(new Error(rawProviderMessage));
    await mountWithInFlightJob();
    expect(queuedDelays()).toEqual([musicPollIntervalMs]);

    await runQueuedTimers();

    const copy = translate(locale, "music.pollError");
    const alerts = findByRole(panel(), "alert");
    expect(alerts).toHaveLength(1);
    expect(alerts[0].getAttribute("aria-live")).toBe("assertive");
    expect(alerts[0].textContent).toContain(copy);
    // The provider's own message never reaches the interface.
    expect(panel().textContent).not.toContain(rawProviderMessage);
    // The status claim itself is untouched: still in-flight, still the stale
    // progress, because a failed read is not a failed generation.
    expect(panel().textContent).toContain(translate(locale, "jobs.statusRunning"));
    expect(panel().textContent).toContain("40");
  });

  it("re-reads the job after a failed read instead of falling silent", async () => {
    h.getGenerationJob.mockRejectedValueOnce(new Error(rawProviderMessage));
    await mountWithInFlightJob();
    await runQueuedTimers();
    expect(h.getGenerationJob).toHaveBeenCalledTimes(1);
    // The backoff queued the next read rather than leaving the panel frozen.
    expect(queuedDelays()).toEqual([musicPollIntervalMs * 2]);

    h.getGenerationJob.mockResolvedValueOnce(inFlightJob({ progressPercent: 70 }));
    await runQueuedTimers();
    expect(h.getGenerationJob).toHaveBeenCalledTimes(2);
    // A good read dismisses the notice and refreshes the status.
    expect(findByRole(panel(), "alert")).toHaveLength(0);
    expect(panel().textContent).toContain("70");
  });

  it("does not dismiss a failed cancel when a later read succeeds", async () => {
    h.getGenerationJob.mockRejectedValueOnce(new Error(rawProviderMessage));
    await mountWithInFlightJob();
    await runQueuedTimers();
    expect(findByRole(panel(), "alert")).toHaveLength(1);

    // Clear the notice with a good read, then fail a cancel.
    h.getGenerationJob.mockResolvedValueOnce(inFlightJob({ progressPercent: 60 }));
    await runQueuedTimers();
    expect(findByRole(panel(), "alert")).toHaveLength(0);

    h.cancelGenerationJob.mockRejectedValue(new Error(rawProviderMessage));
    const cancel = findByClass(panel(), "music-action-button").find((node) => node.nodeName === "BUTTON");
    if (!cancel) throw new Error("the cancel action is not mounted");
    await act(async () => {
      cancel.dispatchEvent({ type: "click", bubbles: true });
      await drain();
    });
    const alerts = findByRole(panel(), "alert");
    expect(alerts).toHaveLength(1);
    expect(alerts[0].textContent).toContain(translate("en", "music.cancelError"));
    expect(panel().textContent).not.toContain(rawProviderMessage);

    // The next good read leaves that claim alone.
    h.getGenerationJob.mockResolvedValueOnce(inFlightJob({ progressPercent: 80 }));
    await runQueuedTimers();
    expect(findByRole(panel(), "alert")).toHaveLength(1);
    expect(panel().textContent).toContain(translate("en", "music.cancelError"));
  });

  it("stays clear of the Voice lane, shared CSS and the other studios", () => {
    expect(musicSource).not.toContain("VoiceStudioPollingNoticeView");
    // The notice reuses the class the studio already ships, so globals.css is
    // untouched and no cross-studio stylesheet is widened.
    expect(musicSource).toContain('<div className="music-inline-error" role="alert"');
    const styles = readFileSync(new URL("../app/globals.css", import.meta.url), "utf8");
    expect(styles).toContain(".music-inline-error");
    expect(readFileSync(new URL("./VoiceStudioView.tsx", import.meta.url), "utf8")).toContain("caught.message");
  });
});
