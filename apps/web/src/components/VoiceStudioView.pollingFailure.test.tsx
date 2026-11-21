import { createElement } from "react";
import { readFileSync } from "node:fs";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { locales, translate, type Locale } from "../lib/i18n";
// A namespace import keeps this file loadable on an unmodified branch, so each
// case reports its own failure instead of the suite aborting on a missing
// named export.
import * as voiceStudio from "./VoiceStudioView";

const VoiceStudioPollingNoticeView = (voiceStudio as Record<string, unknown>).VoiceStudioPollingNoticeView as
  | (({ t }: { t: (key: string) => string }) => ReturnType<typeof createElement>)
  | undefined;

// Independent audit of the Voice Studio route (/create/voice) against latest main.
//
// The view keeps one `error` string for four different failures. Three of them
// (create, cancel, terminal job) are reachable, because the compose `<form>`
// branch renders the `role="alert"` panel. The fourth is not: while a job is
// still in flight the component takes the `isGenerating` branch, which never
// reads `error` at all. A rejected status poll therefore leaves the screen
// reader repeating the *stale* progress announcement from the polite live
// region, as if nothing happened, for as long as the backoff keeps retrying.
//
// The same branch is the only place the message is shown, and it is fed
// `caught.message`. `ApiError.message` is an English, server-facing string
// ("Something went wrong.", "Streaming is unavailable."), so the localized
// `voice.pollError` copy that exists for English, Arabic and Kurdish is
// unreachable for the failures that actually happen.
//
// This file is provider-free and deterministic: no fetch, no timer, no DOM
// implementation, in the style of VoiceStudioView.test.tsx and the locale
// matrix in create/music/error.test.ts.

const voiceSource = readFileSync(new URL("./VoiceStudioView.tsx", import.meta.url), "utf8");

function sliceBetween(startMarker: string, endMarker: string): string {
  const start = voiceSource.indexOf(startMarker);
  expect(start, `missing marker: ${startMarker}`).toBeGreaterThanOrEqual(0);
  const end = voiceSource.indexOf(endMarker, start);
  expect(end, `missing marker after ${startMarker}: ${endMarker}`).toBeGreaterThan(start);
  return voiceSource.slice(start, end);
}

// The three-branch render tree: success -> generating -> compose form.
const generatingBranch = sliceBetween(") : isGenerating ? (", ') : (\n        <form className="voice-studio-workspace"');
// The status-polling effect only. Create and cancel failures keep their own
// copy and are already surfaced by the form branch.
const pollEffect = sliceBetween("if (!current || !shouldPollVoiceJob(current)) return;", "nextVoicePollDelay(current, pollRetry)");

const localize = (locale: Locale) => (key: string) => translate(locale, key);

describe("Voice Studio in-flight polling failure state", () => {
  it("exports a polling failure notice view", () => {
    expect(VoiceStudioPollingNoticeView).toBeTypeOf("function");
  });

  it("surfaces the polling failure inside the generating branch", () => {
    // The state must be reachable while the job is in flight, not only after it
    // has already terminated and the form branch is back.
    expect(generatingBranch).toContain("voice-generation-panel");
    expect(generatingBranch).toContain("{error &&");
    expect(generatingBranch).toContain("VoiceStudioPollingNoticeView");
  });

  it("feeds the polling notice the status-read slot, not the action slot", () => {
    // One string held four claims, so a refused cancel was announced with the
    // "could not refresh" copy: an alert that reads as true about the wrong
    // request. The two claims now live in two slots and render as two views.
    expect(generatingBranch).toContain("{pollError && <VoiceStudioPollingNoticeView t={t} />}");
    expect(generatingBranch).toContain("{error && <VoiceStudioActionNoticeView message={error} />}");
    expect(voiceSource).toContain('const [pollError, setPollError] = useState("");');
    expect(generatingBranch).not.toContain("{error && <VoiceStudioPollingNoticeView");
  });

  it("keeps a refused cancel out of the polling copy", () => {
    // The cancel handler owns its own localized claim. It no longer forwards the
    // rejection's English text either, because this message now renders inside
    // the in-flight panel as well as the compose form.
    const cancelArm = sliceBetween("await api.cancelGenerationJob(current.id);", "function createAnother()");
    expect(cancelArm).toContain('setError(t("voice.cancelError"))');
    expect(cancelArm).not.toContain("caught.message");
    expect(cancelArm).not.toContain('t("voice.pollError")');
  });

  it("clears only its own notice on a good read", () => {
    // A successful status read says nothing about whether an earlier cancel was
    // accepted, so it must not withdraw that claim - and it must not have to
    // guess which claim it holds by comparing translated strings.
    expect(pollEffect).toContain('setPollError("")');
    expect(pollEffect).not.toContain("setError(");
    expect(pollEffect).not.toContain("=== t(");
  });

  it("announces the failure without discarding the progress status region", () => {
    // The polite progress live region stays correct for status changes; the
    // alert is the assertive layer that must not be swallowed by it.
    expect(generatingBranch).toContain('role="status" aria-live="polite" aria-atomic="true"');
    expect(generatingBranch.indexOf("VoiceStudioPollingNoticeView")).toBeGreaterThan(generatingBranch.indexOf('aria-live="polite"'));
  });

  it.each(locales)("reports the %s failure with no raw provider message", (locale) => {
    expect(VoiceStudioPollingNoticeView).toBeTypeOf("function");
    const html = renderToStaticMarkup(createElement(VoiceStudioPollingNoticeView!, { t: localize(locale) }));
    const copy = translate(locale, "voice.pollError");
    expect(copy).not.toBe("voice.pollError");
    expect(html).toContain(copy);
    expect(html).toContain('role="alert"');
    expect(html).toContain('aria-live="assertive"');
    expect(html).not.toContain("voice.pollError");
    // No untranslated server-facing English may reach a localized interface.
    expect(html).not.toContain("Something went wrong.");
    expect(html).not.toContain("Streaming is unavailable.");
  });

  it("keeps the English, Arabic and Kurdish copies distinct", () => {
    const texts = locales.map((locale) => translate(locale, "voice.pollError"));
    expect(new Set(texts).size).toBe(3);
  });

  it("feeds the localized fallback rather than the raw error message", () => {
    expect(pollEffect).toContain('t("voice.pollError")');
    expect(pollEffect).not.toContain("caught.message");
  });

  it("states the create refusal in the member's own language", () => {
    // The compose form's create arm carried the same raw-provider catch as the
    // poll effect did, so `voice.createError` - which exists in English, Arabic
    // and Kurdish - was unreachable for the failures that actually happen, and
    // an ar or ku member read the server's English sentence inside a
    // `role="alert"` region. The cancel handler was already corrected by the
    // recovery batch; this closes the last action slot in the studio.
    const createArm = sliceBetween("const job = await api.createVoiceGenerationJob(input);", "async function cancel()");
    expect(createArm).toContain('setError(t("voice.createError"))');
    expect(createArm).not.toContain("caught.message");
    const texts = locales.map((locale) => translate(locale, "voice.createError"));
    expect(new Set(texts).size).toBe(3);
    expect(texts).not.toContain("Something went wrong.");
  });

  it("keeps the polling notice out of the route error boundary owned by #622", () => {
    // Different state, different file: a thrown render error is route-level.
    expect(voiceSource).not.toContain("VoiceStudioError");
  });
});
