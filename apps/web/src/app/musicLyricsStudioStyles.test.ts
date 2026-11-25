import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

/**
 * Style contract for the Music Studio structured-lyrics composer (#708).
 *
 * PR #1253 put PR #772's `LyricsSectionEditor` and `MusicLyricsPreflightPanel` on the studio
 * surface without a single rule for the classes they render, and the studio scopes its field
 * styling to `.music-primary-field` / `.music-choice-field`, so nothing was inherited either:
 * lyric textareas fell back to browser defaults on a dark studio and the mirrored-contract
 * states the panel publishes through `data-*` attributes were invisible. These tests pin the
 * styling pass that closed that gap, and - unlike a literal class list - they read the classes
 * straight out of the three components, so a new class in any of them fails here until it is
 * styled rather than shipping unstyled again.
 *
 * The invariants below are not cosmetic. A stylesheet can silently undo an accessibility
 * decision made in markup (hiding or pointer-disabling the control the panel deliberately
 * keeps in the sequential focus order while it is `aria-disabled`), can generate prose that
 * assistive tech reads as if the product claimed it (`content`), and can break Sorani and
 * Arabic by using physical offsets instead of logical properties. Each is asserted.
 */

const styles = readFileSync(new URL("./globals.css", import.meta.url), "utf8");
const editorSource = readFileSync(new URL("../components/music/LyricsSectionEditor.tsx", import.meta.url), "utf8");
const panelSource = readFileSync(new URL("../components/music/MusicLyricsPreflightPanel.tsx", import.meta.url), "utf8");
const studioSource = readFileSync(new URL("../components/MusicStudioView.tsx", import.meta.url), "utf8");

/**
 * Comments are stripped before anything here parses the stylesheet, and that is load-bearing
 * rather than tidy. The house `ruleBody()` idiom matches `[^{}]*\{[^{}]*\}`, so the prose of a
 * block comment sitting directly above a rule gets swallowed into that rule's selector text and
 * an exact-selector comparison stops matching - the composer block's own header comment does
 * exactly that to `.music-lyrics-composition`. Stripping is also the only way the negative
 * invariants below mean anything, because that header comment discusses `pointer-events` and
 * `content` in order to explain why the CSS must not use them.
 */
const codeStyles = styles.replace(/\/\*[\s\S]*?\*\//g, "");

/** The composer block, from its first rule to the end of the stylesheet. */
function lyricsBlock(): string {
  const start = codeStyles.indexOf(".music-lyrics-composition {");
  expect(start, "globals.css must carry the lyrics composer block, opening with .music-lyrics-composition").toBeGreaterThanOrEqual(0);
  return codeStyles.slice(start);
}

/** First rule whose selector list matches exactly, the way presentationStudioStyles.test.ts reads them. */
function ruleBody(selectorList: string): string | undefined {
  const wanted = selectorList.trim();
  for (const match of codeStyles.matchAll(/[^{}]*\{[^{}]*\}/g)) {
    const [selector, body] = match[0].split("{");
    if (selector.trim() === wanted) return body.trim();
  }
  return undefined;
}

/** Every `lyrics-*` / `music-lyrics-*` class the three wired components actually render. */
function renderedLyricsClasses(): string[] {
  const classes = new Set<string>();
  for (const source of [editorSource, panelSource, studioSource]) {
    for (const match of source.matchAll(/className=(?:"([^"]*)"|\{`([^`]*)`\})/g)) {
      const raw = (match[1] ?? match[2]).replace(/\$\{[^}]*\}/g, " ");
      for (const token of raw.split(/\s+/)) {
        if (/^(?:lyrics-|music-lyrics-)/.test(token)) classes.add(token);
      }
    }
  }
  return [...classes].sort();
}

describe("Music Studio lyrics composer styling", () => {
  it("styles every class the editor, the panel and the composer render", () => {
    const classes = renderedLyricsClasses();
    // Guards the guard: the extraction must keep finding the wired surfaces.
    expect(classes).toEqual(expect.arrayContaining([
      "lyrics-section-editor",
      "lyrics-section-editor-item",
      "music-lyrics-composition",
      "music-lyrics-preflight",
      "music-lyrics-preflight-status",
    ]));
    const unstyled = classes.filter((name) => !lyricsBlock().includes(`.${name}`));
    expect(unstyled, `unstyled lyrics surfaces: ${unstyled.join(", ") || "none"}`).toEqual([]);
  });

  it("gives the composer and each authored row the studio card language", () => {
    const card = ruleBody(".music-lyrics-composition");
    expect(card).toBeDefined();
    expect(card).toContain("display: flex;");
    expect(card).toContain("border: 1px solid var(--music-line);");
    expect(card).toContain("border-radius: 14px;");
    const row = ruleBody(".lyrics-section-editor-item");
    expect(row).toContain("border: 1px solid var(--music-line);");
    expect(row).toContain("background: rgba(255, 255, 255, .035);");
    // Same card background the reference panel already uses, so the composer reads as one
    // surface of the studio rather than a foreign block dropped between panels.
    expect(card).toContain("background: rgba(4, 10, 16, .28);");
  });

  it("turns the lyric textarea into a studio field instead of a browser default", () => {
    const field = ruleBody(".lyrics-section-editor textarea");
    expect(field).toBeDefined();
    expect(field).toContain("width: 100%;");
    expect(field).toContain("color: var(--music-text);");
    expect(field).toContain("background: rgba(7, 14, 21, .56);");
    expect(field).toContain("min-height: 112px;");
    expect(field).toContain("resize: vertical;");
    // A lyric row is authored in the song's own script, so an explicit line height is part of
    // the field, not a nicety: harakat and Kurdish marks need the room or they collide.
    expect(field).toContain("line-height: 1.65;");
  });

  it("states an invalid row beyond colour alone", () => {
    const invalid = ruleBody('.lyrics-section-editor textarea[aria-invalid="true"]');
    expect(invalid).toBeDefined();
    expect(invalid).toContain("border-color: rgba(222, 119, 119, .55);");
    // The message itself is text rendered by the editor with role="alert"; the stylesheet must
    // style that node rather than replace it with a coloured border the user cannot interpret.
    const error = ruleBody(".lyrics-section-editor-error");
    expect(error).toContain("color: #f1b6b6;");
    expect(studioSource + editorSource).toContain('role="alert"');
  });

  it("keeps the blocked handoff control reachable, exactly as the panel decided", () => {
    // MusicLyricsPreflightPanel states a blocked handoff with aria-disabled instead of a native
    // disabled attribute so the control never leaves the sequential focus order. A rule that
    // hid it, or that swallowed its clicks, would undo that decision with no test in the
    // component to notice, which is why it is asserted here.
    const blocked = ruleBody('.music-lyrics-preflight > button[aria-disabled="true"]');
    expect(blocked).toBeDefined();
    expect(blocked).toContain("border-color: var(--music-line);");
    const block = lyricsBlock();
    expect(block).not.toContain("pointer-events:");
    expect(block).not.toContain("visibility: hidden");
    expect(block).not.toContain("display: none");
  });

  it("gives every contract state the panel publishes its own visible statement", () => {
    for (const selector of [
      '.music-lyrics-preflight-sections li[data-empty="true"]',
      '.music-lyrics-preflight-sections li[data-invisible-only="true"]',
      '.music-lyrics-preflight-status[data-ready="true"]',
      '.music-lyrics-preflight-status[data-ready="false"]',
      ".music-lyrics-preflight-issues-live li",
    ]) {
      expect(ruleBody(selector), `${selector} must be styled`).toBeDefined();
    }
    // Invisible-only lyrics are the panel's own gate, not a contract violation of the same
    // weight as the others, so it must not be styled identically to an empty row: the empty
    // row is italic muted prose, the invisible row is a bold statement in the issue colour.
    expect(ruleBody('.music-lyrics-preflight-sections li[data-empty="true"]')).toContain("font-style: italic;");
    expect(ruleBody('.music-lyrics-preflight-sections li[data-invisible-only="true"]')).not.toContain("font-style: italic;");
  });

  it("keeps keyboard focus visible inside the composer", () => {
    expect(styles).toContain(
      ".music-studio-page :is(.music-lyrics-composition, .lyrics-section-editor, .music-lyrics-preflight) :is(a, button, input, select, textarea, summary):focus-visible { outline: 2px solid #8de0d1; outline-offset: 3px; }",
    );
    // The studio already scoped its own focus-visible ring to the reference panel only, so the
    // composer needed the same treatment for every interactive element it adds, textarea
    // included: `:is(…, textarea)` is what makes this rule cover the lyric rows.
    expect(styles).toContain('.music-studio-page :is(.music-reference-panel) :is(a, button, input, select):focus-visible');
  });

  it("honors reduced motion and reaches a touch size on a phone", () => {
    const block = lyricsBlock();
    expect(block).toContain("@media (prefers-reduced-motion: reduce)");
    expect(block).toContain(".music-lyrics-composition :is(textarea, button, input) { transition-duration: 0.01ms !important; }");
    expect(block).toContain("@media (max-width: 560px)");
    // 40px on the row structure buttons and the eight add controls, which are 30px and 34px on
    // a desktop pointer: a lyric row is editable one-handed on a phone, and the move/remove
    // pair is the smallest target in the composer.
    expect(block).toContain(".lyrics-section-editor-item-actions button { min-width: 40px; height: 40px; }");
    expect(block).toContain(".lyrics-section-editor-add button { width: 100%; min-height: 40px; }");
    // The handoff control too: a phone must not end up with one sub-40px target in
    // the middle of a row of them, which is what `width: 100%` alone left behind.
    expect(block).toContain(".music-lyrics-preflight > button { width: 100%; min-height: 40px; }");
  });

  it("generates no prose and uses no physical offsets", () => {
    const block = lyricsBlock();
    // `content` inserts text that assistive tech reads, so a stylesheet could claim readiness,
    // a dialect or a provider capability the contract never publishes. Matched with a property
    // boundary so `justify-content` is not mistaken for it.
    expect(block).not.toMatch(/(?:^|[{;])\s*content\s*:/);
    expect(block).not.toMatch(/padding-(left|right)\s*:/);
    expect(block).not.toMatch(/margin-(left|right)\s*:/);
    expect(block).not.toMatch(/text-align:\s*(left|right)/);
    // Logical properties only, because Sorani and Arabic render the composer right-to-left.
    expect(block).toContain("padding-inline-start: 16px;");
    expect(block).toContain("border-inline-start-width: 3px;");
  });

  it("leaves the studio's own submit affordance untouched", () => {
    // The paused provider notice and the generate button carry the capability claim; restyling
    // them from the lyrics block would put a styling PR in charge of a product statement.
    const block = lyricsBlock();
    expect(block).not.toContain(".music-generate-button");
    expect(block).not.toContain(".music-studio-notice");
    expect(block).not.toContain(".music-reference-");
  });
});
