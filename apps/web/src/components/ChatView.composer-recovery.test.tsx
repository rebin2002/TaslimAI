import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
/**
 * Wiring contract for the Chat composer failure recovery.
 *
 * Synthetic fixtures only: this reads the component source and asserts how it
 * binds the pure `chatComposerRecovery` decisions and the shared
 * `recoveryFocus` restoration into the inline alert and the editor. No
 * provider, network, API client, translation catalog, or user conversation
 * data is involved. Focus itself is verified through the decision modules
 * rather than a browser, matching how `ChatView.thread-focus.test.tsx`
 * verifies the thread switcher and the route boundaries verify
 * `recoveryFocus.ts`.
 */
const chatSource = readFileSync(new URL("./ChatView.tsx", import.meta.url), "utf8");
/** Return the inline failure alert block that carries the retry action. */
function inlineAlertSource(): string {
  const marker = chatSource.indexOf('className="chat-inline-error"');
  expect(marker, "ChatView must render a chat-inline-error alert block").toBeGreaterThan(-1);
  const start = chatSource.lastIndexOf("<div", marker);
  expect(start, "the inline alert must open on a div").toBeGreaterThan(-1);
  const end = chatSource.indexOf('<div className="chat-creator-handoff"', start);
  expect(end, "the inline alert must close before the creator handoff strip").toBeGreaterThan(start);
  return chatSource.slice(start, end);
}
/** Return the composer element that owns the draft and the send action. */
function composerSource(): string {
  const start = chatSource.indexOf("<textarea");
  expect(start, "ChatView must render the composer textarea").toBeGreaterThan(-1);
  const end = chatSource.indexOf('<div className="chat-composer-footer"', start);
  expect(end, "the composer footer must follow the textarea").toBeGreaterThan(start);
  return chatSource.slice(start, end);
}
describe("Chat composer send and retry focus on failure", () => {
  it("moves keyboard focus to the retry alert when a request fails", () => {
    // The bug: activating send disables the textarea and activating
    // regenerate unmounts its button, so by the time the failure alert
    // mounts focus already sits on document.body. The alert is rendered
    // below the message list and the app shell, so the retry action is not
    // next in tab order and a keyboard or screen-reader user has to
    // re-traverse the page to act on the failure. The recovery decision
    // shared with the route boundaries moves focus onto the alert panel
    // only when the failed request owns the retry action.
    expect(chatSource).toContain('import { restoreRecoveryFocus } from "@/lib/recoveryFocus";');
    expect(chatSource).toMatch(
      /useEffect\(\(\) => \{\s*if \(!composerFailure\.retryOffered\) return;\s*restoreRecoveryFocus\(inlineErrorRef\.current/,
    );
  });
  it("restores focus only for the retry state, never for a plain composer error", () => {
    // A failed copy or rename also renders this alert, but focus sits on the
    // control the user just activated, so the restore has to stay gated on
    // retryOffered and the shared call must appear exactly once.
    const calls = chatSource.match(/restoreRecoveryFocus\(/g) ?? [];
    expect(calls).toHaveLength(1);
    expect(chatSource).toContain("composerFailure.retryOffered");
    expect(chatSource).not.toMatch(/if \(!composerFailure\.alertVisible\) return;/);
  });
  it("makes the alert a focusable recovery panel", () => {
    const source = inlineAlertSource();
    // A plain div cannot receive programmatic focus, so the panel the
    // recovery decision moves focus onto has to opt into the tab sequence
    // without exposing every alert as a sequential-stop target.
    expect(source).toContain("ref={inlineErrorRef}");
    expect(source).toContain("tabIndex={-1}");
  });
  it("keeps the alert announced assertively and names it by the failure", () => {
    const source = inlineAlertSource();
    // Invariant: the alert already carries role="alert". It must keep it,
    // and the panel itself now needs an accessible name pointing at the
    // failure text, matching the recovery boundaries' aria-live wiring.
    expect(source).toContain('role="alert"');
    expect(source).toContain('aria-live="assertive"');
    expect(source).toContain('aria-labelledby="chat-error-message"');
    expect(source).toContain('<span id="chat-error-message">{error}</span>');
  });
  it("ties the retry action to the failure it repeats", () => {
    const source = inlineAlertSource();
    // The button reads only "Retry"; without the description a screen
    // reader landing on it cannot say which request is repeated.
    expect(source).toMatch(/className="chat-inline-retry"[^>]*aria-describedby="chat-error-message"/);
  });
  it("points the editor at the failure while a retry is pending", () => {
    const source = composerSource();
    // The draft survives the failed send, so assistive technology reading
    // the composer must surface that its last request failed and retry is
    // available - through the shared id, never the error text itself.
    expect(chatSource).toContain('from "@/lib/chatComposerRecovery"');
    expect(source).toContain("aria-describedby={chatComposerFailureDescriptionId(composerFailure)}");
    expect(inlineAlertSource()).not.toMatch(/aria-\w+="\{error\}/);
  });
  it("returns editor focus when the failure alert is dismissed", () => {
    const source = inlineAlertSource();
    // Dismissing unmounts the focused button, dropping focus to
    // document.body again. The draft is still in the editor, so the escape
    // hatch has to hand focus back to it.
    expect(source).toContain("composerRef.current?.focus()");
    expect(composerSource()).toContain("ref={composerRef}");
  });
  it("derives the failure state from the pure decision, not inlined guesses", () => {
    // The component binds the decision once, from presence inputs only, so
    // the alert gate, the focus effect and the composer description cannot
    // drift apart and no localized or user-authored text is compared.
    expect(chatSource).toMatch(
      /const composerFailure = resolveChatComposerFailure\(error, retryRequest, selected !== null\);/,
    );
  });
});
