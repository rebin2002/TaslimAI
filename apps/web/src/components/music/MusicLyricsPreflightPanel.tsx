/**
 * Provider-free end-to-end lyrics preflight panel (issue #708 staged slice).
 *
 * This component turns an authored structured-lyrics draft plus the selected
 * vocal language/dialect into the single typed song request shape that the
 * shared server contract admits, and shows the user - before any submit -
 * which mirrored rule or limit would stop that request. It is the UI half of
 * the roundtrip that the merged client mapper (PR #802) and the fail-closed
 * request contract landed from PR #709 by PR #826 already define:
 *
 *   authored sections -> buildMusicLyricsDraft("preflight") (rule/limit check)
 *   preflight outcome -> visible-glyph gate                 (this slice)
 *   clean outcome     -> typed payload handoff              (submit path)
 *
 * Deliberate boundaries, kept so this slice cannot collide with, or silently
 * replace, work owned by other open lanes:
 * - It does not render an editor. The section editor and its controlled model
 *   are owned by draft PR #772 (`LyricsSectionEditor.tsx`), whose section input
 *   fields (`id`, `type`, `text`) this panel consumes read-only.
 * - It does not import or modify `MusicStudioView.tsx` or its tests, which are
 *   owned by open PR #560 (stale workspace responses). Wiring this panel into
 *   the studio is a follow-up for whoever lands that lane.
 * - It calls nothing: no fetch, no API client, no provider, no cost endpoint,
 *   and it reads no secrets. Charging and provider activation are untouched.
 * - Its accessibility semantics are part of this lane: the handoff control is
 *   `aria-disabled` rather than natively `disabled`, because a natively
 *   disabled button leaves the sequential focus order and a keyboard-only
 *   user can then neither reach the preview's primary action nor find out why
 *   the draft is withheld. Activation while blocked reports the reason
 *   through `onBlocked` and never reaches the submit path, so the control
 *   stays fail-closed while staying reachable.
 * - Region ids come from `useId()`, so two previews on one page do not share
 *   one accessible name or one described-by target, and the ready/blocked
 *   sentence is a polite live region the control describes.
 * - It claims no provider capability. The capability catalog on current main
 *   reports `SupportsLyricsToVocals: false` with empty language and dialect
 *   sets for every provider key, including the third-party keys, so sung
 *   lyrics are presented as not enabled. A shaped payload is a request shape,
 *   not an execution promise, and `docs/MUSIC_STUDIO_PREFLIGHT_UI.md` records
 *   which blockers must clear before that wording can change.
 */
import { useId, type ReactElement } from "react";
import {
  buildMusicLyricsDraft,
  musicLyricsDefaultDialects,
  musicLyricsDialectLanguages,
  musicLyricsLanguages,
  MUSIC_LYRICS_SONG_CREATION_MODE,
  MUSIC_LYRICS_SONG_VOCAL_PREFERENCE,
  type MusicLyricsDraftInput,
  type MusicLyricsDraftIssueCode,
  type MusicLyricsDraftOutcome,
  type MusicLyricsDraftPayload,
  type MusicLyricsLanguage,
} from "@/lib/musicLyricsDraftPayload";

/** Editor section row, field-compatible with the PR #772 editor model. */
export type MusicLyricsPreflightSection = {
  id?: string;
  type: string;
  text: string;
};

export type MusicLyricsPreflightInput = Omit<MusicLyricsDraftInput, "sections"> & {
  sections: readonly MusicLyricsPreflightSection[];
};

/** Draft-local identity and measured shape of one authored section. */
export type MusicLyricsPreflightSectionSummary = {
  sectionId?: string;
  /** Contract section token, e.g. `chorus`; null when the row is unusable. */
  type: string | null;
  lineCount: number;
  characterCount: number;
  /** Counted with `musicLyricsVisibleGlyphCount` over the whole row text. */
  visibleGlyphCount: number;
  /** True when the row has text but no glyph a reader can see. */
  invisibleOnly: boolean;
  usable: boolean;
};

/**
 * Mirror of the shared issue codes plus the gate's own code. The gate code is
 * deliberately outside the mirrored vocabulary so nothing here pretends the
 * client mapper gained a server-side rule it does not have.
 */
export const musicLyricsPreflightGateIssueCode = "lyrics_invisible_only" as const;
export type MusicLyricsPreflightGateIssueCode = typeof musicLyricsPreflightGateIssueCode;
export type MusicLyricsPreflightIssueCode = MusicLyricsDraftIssueCode | MusicLyricsPreflightGateIssueCode;

export type MusicLyricsPreflightIssue = {
  code: MusicLyricsPreflightIssueCode;
  sectionId?: string;
  params: Record<string, string | number>;
};

export type MusicLyricsPreflightResult = {
  /** Null for a selection no contract language can be read from. */
  language: MusicLyricsLanguage | null;
  /**
   * Dialect the contract would resolve for the selection, `null` for English
   * which never takes one, and `undefined` when it cannot be resolved at all.
   */
  dialect: string | null | undefined;
  direction: "ltr" | "rtl";
  /** BCP 47 tag for the lyric script, mirroring the studio's own mapping. */
  languageTag: string;
  totalCharacters: number;
  totalVisibleGlyphs: number;
  /** True when some lyric text exists but carries no visible glyph. */
  invisibleOnlyLyrics: boolean;
  sections: readonly MusicLyricsPreflightSectionSummary[];
  /** Rule and limit violations, mirrored contract first, then the gate. */
  issues: readonly MusicLyricsPreflightIssue[];
  /** Fail-closed violations raised by the visible-glyph gate alone. */
  gateIssues: readonly MusicLyricsPreflightIssue[];
  payload: MusicLyricsDraftPayload | null;
  /** Mirrored contract satisfied and the visible-glyph gate clean. */
  admissible: boolean;
  /** Machine-readable reason for withholding the handoff. */
  blockedReason: MusicLyricsPreflightIssueCode | "creation_mode_drift" | null;
};

/**
 * A glyph counts as visible unless it is a Cc control, a Cf format character
 * (bidi and joining controls, including U+061C ARABIC LETTER MARK and U+200F
 * RIGHT-TO-LEFT MARK), a space of any width including zero-width and BOM, or
 * a Zl/Zp line separator. Arabic harakat and other combining marks are only
 * visible beside a base letter, which the row-level gate expresses: a row
 * whose every character is invisible is not submittable.
 */
const invisibleGlyphPattern = /[\p{Cc}\p{Cf}\p{Zl}\p{Zp}\p{Zs}\u200B\u200C\uFEFF]/u;

export function musicLyricsVisibleGlyphCount(value: string | null | undefined): number {
  if (typeof value !== "string" || value.length === 0) return 0;
  let visible = 0;
  for (const character of value) {
    if (invisibleGlyphPattern.test(character)) continue;
    visible += 1;
  }
  return visible;
}

const languageTagByLyricsLanguage: Readonly<Record<MusicLyricsLanguage, string>> = Object.freeze({
  en: "en",
  ar: "ar",
  ku: "ckb",
});

const normalizeToken = (value: unknown): string | null =>
  typeof value === "string" && value.trim() ? value.trim().toLowerCase() : null;

const normalizeDialectToken = (value: unknown): string | null => {
  const token = normalizeToken(value);
  return token ? token.replace(/-/g, "_") : null;
};

const resolveLanguage = (value: unknown): MusicLyricsLanguage | null => {
  const token = normalizeToken(value);
  if (!token) return null;
  if ((musicLyricsLanguages as readonly string[]).includes(token)) return token as MusicLyricsLanguage;
  // Studio alias only: `ckb` is the ISO code the interface shows for Kurdish.
  // Dialect tokens such as `sorani` stay dialects and are never read as a
  // language, so this stays a faithful subset of the mapper's vocabulary.
  if (token === "ckb") return "ku";
  return null;
};

export type MusicLyricsDialectResolution = {
  /**
   * Resolved contract token, `null` when the language takes no dialect, and
   * `undefined` when the selection cannot be resolved faithfully.
   */
  dialect: string | null | undefined;
  /**
   * Contract issue code explaining an unresolvable selection, chosen from the
   * same vocabulary the mirrored mapper records, or `null` when the selection
   * resolved.
   */
  issueCode: MusicLyricsDraftIssueCode | null;
};

/**
 * Explains a dialect selection the way the contract would, without inventing
 * one: `ku` defaults to Sorani, Arabic and English never take a silent
 * default, and a token that belongs to another language is a mismatch rather
 * than a fallback.
 */
export function explainMusicLyricsDraftDialect(
  language: MusicLyricsLanguage,
  dialect: string | null | undefined,
): MusicLyricsDialectResolution {
  const explicit = normalizeDialectToken(dialect);
  if (language === "en") {
    if (explicit) return { dialect: undefined, issueCode: "dialect_not_allowed" };
    return { dialect: null, issueCode: null };
  }
  const token = explicit ?? musicLyricsDefaultDialects[language];
  if (!token) return { dialect: undefined, issueCode: "dialect_required" };
  const dialectLanguage = musicLyricsDialectLanguages[token];
  if (!dialectLanguage) return { dialect: undefined, issueCode: "dialect_unknown" };
  if (dialectLanguage !== language) return { dialect: undefined, issueCode: "dialect_mismatch" };
  return { dialect: token, issueCode: null };
}

/** Convenience projection for hosts that only need the resolved token. */
export function resolveMusicLyricsDraftDialect(
  language: MusicLyricsLanguage,
  dialect: string | null | undefined,
): string | null | undefined {
  return explainMusicLyricsDraftDialect(language, dialect).dialect;
}

/**
 * Pure preflight projection: one mirrored-contract pass at the preflight stage
 * plus the visible-glyph gate. Deterministic, side-effect free, and it performs
 * no network or provider work, so it is safe to run on every keystroke.
 */
export function evaluateMusicLyricsPreflight(input: MusicLyricsPreflightInput): MusicLyricsPreflightResult {
  const preflightOutcome: MusicLyricsDraftOutcome = buildMusicLyricsDraft(input, "preflight");
  const language = resolveLanguage(input?.vocalLanguage);
  const resolution = language
    ? explainMusicLyricsDraftDialect(language, input?.dialect)
    : { dialect: undefined as string | null | undefined, issueCode: null as MusicLyricsDraftIssueCode | null };

  // Explicit local type because Array.isArray narrows a readonly array to
  // `any[]`, which would silently erase every downstream inference.
  const authored: readonly MusicLyricsPreflightSection[] = Array.isArray(input.sections)
    ? input.sections
    : [];
  const sections: MusicLyricsPreflightSectionSummary[] = authored.map((entry) => {
    const text = typeof entry?.text === "string" ? entry.text : "";
    const lines = text
      .replace(/\r\n?/g, "\n")
      .split("\n")
      .map((line) => line.replace(/[ \t]+$/, ""))
      .filter((line) => line.length > 0);
    const characterCount = lines.reduce((total, line) => total + line.length, 0);
    const visibleGlyphCount = musicLyricsVisibleGlyphCount(text);
    const sectionId = typeof entry?.id === "string" ? entry.id : undefined;
    return Object.freeze({
      ...(sectionId === undefined ? {} : { sectionId }),
      type: normalizeToken(entry?.type),
      lineCount: lines.length,
      characterCount,
      visibleGlyphCount,
      invisibleOnly: text.length > 0 && visibleGlyphCount === 0,
      usable: lines.length > 0,
    });
  });

  const gateIssues: MusicLyricsPreflightIssue[] = [];
  for (const section of sections) {
    if (section.invisibleOnly) {
      gateIssues.push(
        Object.freeze({
          code: musicLyricsPreflightGateIssueCode,
          params: { visibleGlyphs: section.visibleGlyphCount, characters: section.characterCount },
          ...(section.sectionId === undefined ? {} : { sectionId: section.sectionId }),
        }),
      );
    }
  }

  const issues: MusicLyricsPreflightIssue[] = [
    ...preflightOutcome.issues.map((issue) =>
      Object.freeze({
        code: issue.code as MusicLyricsPreflightIssueCode,
        params: issue.params,
        ...(issue.sectionId === undefined ? {} : { sectionId: issue.sectionId }),
      }),
    ),
    ...gateIssues,
  ];

  const totalCharacters = sections.reduce((total, section) => total + section.characterCount, 0);
  const totalVisibleGlyphs = sections.reduce((total, section) => total + section.visibleGlyphCount, 0);
  const invisibleOnlyLyrics = sections.some((section) => section.invisibleOnly);

  const payload = preflightOutcome.payload;
  // The handoff shape is pinned here so a future mapper that drifts away from
  // the song-mode vocabulary fails loudly instead of submitting silently.
  const modePinned =
    payload === null ||
    (payload.creationMode === MUSIC_LYRICS_SONG_CREATION_MODE &&
      payload.vocalPreference === MUSIC_LYRICS_SONG_VOCAL_PREFERENCE);
  const admissible = preflightOutcome.submittable && payload !== null && modePinned && gateIssues.length === 0;

  let blockedReason: MusicLyricsPreflightResult["blockedReason"] = null;
  if (language === null) blockedReason = "language_unsupported";
  else if (resolution.issueCode !== null) blockedReason = resolution.issueCode;
  else if (preflightOutcome.issues.length > 0) blockedReason = preflightOutcome.issues[0].code;
  else if (!modePinned) blockedReason = "creation_mode_drift";
  else if (gateIssues.length > 0) blockedReason = musicLyricsPreflightGateIssueCode;

  return Object.freeze({
    language,
    dialect: resolution.dialect,
    direction: language === "en" || language === null ? "ltr" : "rtl",
    languageTag: language ? languageTagByLyricsLanguage[language] : "und",
    totalCharacters,
    totalVisibleGlyphs,
    invisibleOnlyLyrics,
    sections: Object.freeze(sections),
    issues: Object.freeze(issues),
    gateIssues: Object.freeze(gateIssues),
    payload: admissible ? payload : null,
    admissible,
    blockedReason,
  });
}

/**
 * Human-readable summaries are literal English with a prop-injected override:
 * `i18n.ts` is claimed by other open lanes, so this slice ships no locale keys
 * and adds nothing to that queue.
 */
export type MusicLyricsPreflightLabels = {
  heading: string;
  summaryHeading: string;
  languageLabel: string;
  dialectLabel: string;
  directionLabel: string;
  sectionsHeading: string;
  sectionLine: string;
  sectionEmpty: string;
  issuesHeading: string;
  noIssues: string;
  invisibleOnlyIssue: string;
  capabilityHeading: string;
  capabilityNotice: string;
  readyNotice: string;
  blockedNotice: string;
  submitLabel: string;
};

export const defaultMusicLyricsPreflightLabels: MusicLyricsPreflightLabels = Object.freeze({
  heading: "Lyrics preflight",
  summaryHeading: "Request shape",
  languageLabel: "Vocal language",
  dialectLabel: "Dialect",
  directionLabel: "Direction",
  sectionsHeading: "Sections",
  sectionLine: "{type} - {lines} lines, {characters} characters",
  sectionEmpty: "(empty section)",
  issuesHeading: "Blocking issues",
  noIssues: "No mirrored contract violations in this draft.",
  invisibleOnlyIssue: "A section contains no visible glyphs; controls-only text is not submittable.",
  capabilityHeading: "Provider capability",
  capabilityNotice:
    "Sung lyrics are not enabled for any configured provider, so this preflight checks the request shape only and starts no generation.",
  readyNotice: "Preflight clean: the typed song payload is ready to hand off to the submit path.",
  blockedNotice: "Preflight withheld: this draft would be rejected before cost admission.",
  submitLabel: "Preflight and submit",
});

export type MusicLyricsPreflightPanelProps = {
  /** Controlled authored state; the panel owns none of it. */
  input: MusicLyricsPreflightInput;
  labels?: Partial<MusicLyricsPreflightLabels>;
  /** Called with the mirrored-contract payload only when preflight is clean. */
  onHandoff?: (payload: MusicLyricsDraftPayload) => void;
  /** Called with the full result when preflight withholds the payload. */
  onBlocked?: (result: MusicLyricsPreflightResult) => void;
};

export type MusicLyricsPreflightDecision = {
  result: MusicLyricsPreflightResult;
  /** True when the payload may be handed to a submit path at all. */
  handoff: boolean;
};

/**
 * Single decision point shared by the panel and any future wiring, so a host
 * cannot accidentally submit a draft the mirrored contract would reject.
 */
export function decideMusicLyricsPreflightHandoff(input: MusicLyricsPreflightInput): MusicLyricsPreflightDecision {
  const result = evaluateMusicLyricsPreflight(input);
  return Object.freeze({ result, handoff: result.admissible && result.payload !== null });
}

const substitute = (template: string, values: Record<string, string | number>): string =>
  template.replace(/\{(\w+)\}/g, (match, key: string) =>
    Object.prototype.hasOwnProperty.call(values, key) ? String(values[key]) : match,
  );

export function MusicLyricsPreflightPanel({
  input,
  labels,
  onHandoff,
  onBlocked,
}: MusicLyricsPreflightPanelProps): ReactElement {
  const text: MusicLyricsPreflightLabels = { ...defaultMusicLyricsPreflightLabels, ...(labels ?? {}) };
  const result = evaluateMusicLyricsPreflight(input);
  const handoff = result.admissible && result.payload !== null;
  const dialectShown = result.dialect === undefined ? "unresolved" : result.dialect === null ? "none" : result.dialect;
  // Instance-unique ids: `aria-labelledby` and `aria-describedby` resolve
  // document-wide, so a shared literal id would name every panel after the
  // first one and point every control at the first status sentence.
  const panelId = useId();
  const headingId = `${panelId}-heading`;
  const statusId = `${panelId}-status`;

  return (
    <section
      className="music-lyrics-preflight"
      aria-labelledby={headingId}
      data-admissible={handoff ? "true" : "false"}
      data-blocked-reason={result.blockedReason ?? "none"}
      dir={result.direction}
      lang={result.languageTag}
    >
      <h2 id={headingId}>{text.heading}</h2>

      <div className="music-lyrics-preflight-summary">
        <h3>{text.summaryHeading}</h3>
        <p>
          {text.languageLabel}: {result.language ?? "unrecognized"}
        </p>
        <p>
          {text.dialectLabel}: {dialectShown}
        </p>
        <p>
          {text.directionLabel}: {result.direction}
        </p>
        <p>
          {text.sectionsHeading}: {result.sections.length}
        </p>
      </div>

      <ul className="music-lyrics-preflight-sections">
        {result.sections.map((section, index) => (
          <li
            key={section.sectionId ?? `${section.type ?? "section"}-${index}`}
            data-empty={section.usable ? "false" : "true"}
            data-invisible-only={section.invisibleOnly ? "true" : "false"}
          >
            {section.usable
              ? substitute(text.sectionLine, {
                  type: section.type ?? "section",
                  lines: section.lineCount,
                  characters: section.characterCount,
                })
              : text.sectionEmpty}
          </li>
        ))}
      </ul>

      <div className="music-lyrics-preflight-issues">
        <h3>{text.issuesHeading}</h3>
        {/* A stable live region: the heading stays out of it, so an AT does
            not re-read the block title on every keystroke, and the region
            element itself is never swapped when the draft flips between the
            clean sentence and the issue list. */}
        <div className="music-lyrics-preflight-issues-live" aria-live="polite">
          {result.issues.length === 0 ? (
            <p>{text.noIssues}</p>
          ) : (
            <ul>
              {result.issues.map((issue, index) => (
                <li key={`${issue.code}-${issue.sectionId ?? "global"}-${index}`} data-issue-code={issue.code}>
                  {issue.code === musicLyricsPreflightGateIssueCode
                    ? text.invisibleOnlyIssue
                    : `${issue.code}${issue.sectionId ? ` (${issue.sectionId})` : ""}`}
                </li>
              ))}
            </ul>
          )}
        </div>
      </div>

      <div className="music-lyrics-preflight-capability">
        <h3>{text.capabilityHeading}</h3>
        <p>{text.capabilityNotice}</p>
      </div>

      <p
        className="music-lyrics-preflight-status"
        id={statusId}
        role="status"
        aria-live="polite"
        data-ready={handoff ? "true" : "false"}
      >
        {handoff ? text.readyNotice : text.blockedNotice}
      </p>

      <button
        type="button"
        aria-disabled={handoff ? undefined : "true"}
        aria-describedby={statusId}
        onClick={() => {
          if (handoff && result.payload) {
            onHandoff?.(result.payload);
            return;
          }
          onBlocked?.(result);
        }}
      >
        {text.submitLabel}
      </button>
    </section>
  );
}
