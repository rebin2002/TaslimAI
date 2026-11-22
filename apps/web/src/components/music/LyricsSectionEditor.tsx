"use client";

/**
 * Standalone, fully local structured lyrics editor for sung-song workflows
 * (issue #708 staged slice). Controlled component: all state lives with the
 * consumer and every localized string is supplied through `labels`. Nothing
 * in this file talks to the outside world: no network calls, no backend
 * endpoints, no media services, no song-writing services, no auto-generation
 * of any kind. Wire-up into Music Studio is intentionally out of scope so the
 * open Music pull requests keep sole ownership of their files, and no singing
 * capability is presented as enabled from this component.
 */

export type LyricsSectionType = "verse" | "chorus" | "bridge" | "outro";

export const lyricsSectionTypes: readonly LyricsSectionType[] = ["verse", "chorus", "bridge", "outro"];

export type LyricsSection = {
  /** Stable identifier supplied and preserved by the consumer. */
  id: string;
  type: LyricsSectionType;
  text: string;
};

export type LyricsSectionEditorLabels = {
  /** Heading/label for a section, e.g. (type, ordinal) => "Verse 1". */
  sectionType: (type: LyricsSectionType, ordinal: number) => string;
  addSection: (type: LyricsSectionType) => string;
  removeSection: (sectionOrdinal: number) => string;
  moveUp: (sectionOrdinal: number) => string;
  moveDown: (sectionOrdinal: number) => string;
  /** Placeholder shown inside an empty section. */
  lyricInput: string;
  /** Limit hints, e.g. "Maximum 2,000 characters per section". */
  charactersLimit: (maxCharsPerSection: number) => string;
  sectionsLimit: (maxSections: number) => string;
  /** Validation copy; the component renders them, it never generates text. */
  emptySectionError: string;
  tooLongError: (maxCharsPerSection: number) => string;
  tooManySectionsError: (maxSections: number) => string;
};

export const LYRICS_MAX_CHARS_PER_SECTION = 2_000;
export const LYRICS_MAX_SECTIONS = 12;

/** Accessible name for a lyric input, kept identical between render and tests. */
export const lyricsSectionAriaLabel = (
  labels: Pick<LyricsSectionEditorLabels, "sectionType">,
  type: LyricsSectionType,
  sectionOrdinal: number,
): string => `${labels.sectionType(type, sectionOrdinal)} · ${sectionOrdinal}`;

/**
 * Input reading direction for lyric text. Kurdish Sorani (ku/ckb/sorani) and
 * Arabic are right-to-left; English is left-to-right. Unknown codes fall back
 * to "auto" so the browser decides from the first strong character.
 */
export function lyricsInputDirection(languageCode: string): "ltr" | "rtl" | "auto" {
  const code = (languageCode ?? "").trim().toLowerCase();
  if (code === "en" || code.startsWith("en-")) return "ltr";
  if (
    code === "ar" || code.startsWith("ar-") ||
    code === "ku" || code.startsWith("ku-") ||
    code === "ckb" || code.startsWith("ckb-") || code.startsWith("ck-i") ||
    code === "sorani" || code.startsWith("ku-arab")
  ) return "rtl";
  return "auto";
}

/** HTML lang attribute for the lyric input so screen readers pick the right voice. */
export function lyricsInputLanguageTag(languageCode: string): string {
  const code = (languageCode ?? "").trim().toLowerCase();
  if (code === "ku") return "ckb";
  return (languageCode ?? "").trim();
}

export function isLyricsSectionType(value: string): value is LyricsSectionType {
  return (lyricsSectionTypes as readonly string[]).includes(value);
}

/**
 * Whitespace normalization applied when lyrics are readied for submission:
 * trims edges and collapses runs of more than two blank lines. Typing keeps
 * raw input (trailing spaces and newlines are valid draft text); validation
 * trims before comparing against limits.
 */
export function normalizeLyricsSectionText(text: string): string {
  return (text ?? "")
    .replace(/\r\n?/g, "\n")
    .replace(/[ \t]+$/gm, "")
    .replace(/(?:\n\s*){3,}/g, "\n\n")
    .trim();
}

function withSectionText(sections: readonly LyricsSection[], id: string, text: string): LyricsSection[] {
  return sections.map((section) => (section.id === id ? { ...section, text } : section));
}

export function setLyricsSectionText(sections: readonly LyricsSection[], id: string, text: string): LyricsSection[] {
  return withSectionText(sections, id, text);
}

export function normalizeLyricsSections(sections: readonly LyricsSection[]): LyricsSection[] {
  return sections.map((section) => ({ ...section, text: normalizeLyricsSectionText(section.text) }));
}

export function addLyricsSection(
  sections: readonly LyricsSection[],
  type: LyricsSectionType,
  id?: string,
): LyricsSection[] {
  return [...sections, { id: id ?? `lyrics-section-${sections.length + 1}`, type, text: "" }];
}

export function removeLyricsSection(sections: readonly LyricsSection[], id: string): LyricsSection[] {
  return sections.filter((section) => section.id !== id);
}

/**
 * Reorder helper used by both move buttons and drag/programmatic moves.
 * `offset` is relative (-1 up, +1 down); `targetIndex` is absolute. Out-of
 * range destinations leave the list untouched so buttons never throw.
 */
export function moveLyricsSection(
  sections: readonly LyricsSection[],
  id: string,
  options: { offset?: number; targetIndex?: number },
): LyricsSection[] {
  const from = sections.findIndex((section) => section.id === id);
  if (from < 0) return sections.slice();
  const to = options.targetIndex ?? from + (options.offset ?? 0);
  if (to < 0 || to >= sections.length || to === from) return sections.slice();
  const next = sections.slice();
  const [moved] = next.splice(from, 1);
  next.splice(to, 0, moved);
  return next;
}

export type LyricsSectionsErrorCode =
  | "sections_missing"
  | "sections_too_many"
  | "section_type_invalid"
  | "section_empty"
  | "section_too_long";

export type LyricsSectionsError = {
  code: LyricsSectionsErrorCode;
  sectionId?: string;
  params: Record<string, string | number>;
};

export type ValidateLyricsOptions = {
  maxCharsPerSection?: number;
  maxSections?: number;
  /** When true (default) at least one section with non-empty text is required. */
  requireNonEmpty?: boolean;
};

/**
 * Structural validation for trimmed, normalized lyrics. Pure and synchronous:
 * consumers can run it on submit without wiring anything into generation code.
 */
export function validateLyricsSections(
  sections: readonly LyricsSection[] | null | undefined,
  options?: ValidateLyricsOptions,
): LyricsSectionsError[] {
  const maxChars = options?.maxCharsPerSection ?? LYRICS_MAX_CHARS_PER_SECTION;
  const maxSections = options?.maxSections ?? LYRICS_MAX_SECTIONS;
  const requireNonEmpty = options?.requireNonEmpty ?? true;
  if (!Array.isArray(sections)) return [{ code: "sections_missing", params: {} }];
  const errors: LyricsSectionsError[] = [];
  if (sections.length > maxSections) errors.push({ code: "sections_too_many", params: { maxSections } });
  let hasContent = false;
  sections.forEach((section, index) => {
    const ordinal = index + 1;
    if (!section || !isLyricsSectionType(section.type)) {
      errors.push({ code: "section_type_invalid", sectionId: section?.id, params: { sectionOrdinal: ordinal } });
      return;
    }
    const trimmed = normalizeLyricsSectionText(section.text ?? "");
    if (!trimmed) {
      errors.push({ code: "section_empty", sectionId: section.id, params: { sectionOrdinal: ordinal } });
      return;
    }
    hasContent = true;
    if (trimmed.length > maxChars) {
      errors.push({
        code: "section_too_long",
        sectionId: section.id,
        params: { sectionOrdinal: ordinal, maxCharsPerSection: maxChars, actual: trimmed.length },
      });
    }
  });
  if (requireNonEmpty && !hasContent && errors.every((error) => error.code !== "section_empty")) {
    // Only flag a missing draft when sections exist but all are blank; an empty
    // list is handled by the consumer's own required-field rules.
    if (sections.length > 0) errors.push({ code: "section_empty", params: { sectionOrdinal: 0 } });
  }
  return errors;
}

/** Maps validation errors to localized copy; unknown codes fall back to a generic message. */
export function lyricsSectionsErrorMessage(error: LyricsSectionsError, labels: LyricsSectionEditorLabels): string {
  switch (error.code) {
    case "sections_missing":
    case "section_empty":
      return labels.emptySectionError;
    case "section_too_long":
      return labels.tooLongError(Number(error.params.maxCharsPerSection ?? LYRICS_MAX_CHARS_PER_SECTION));
    case "sections_too_many":
      return labels.tooManySectionsError(Number(error.params.maxSections ?? LYRICS_MAX_SECTIONS));
    case "section_type_invalid":
      return labels.emptySectionError;
    default:
      return labels.emptySectionError;
  }
}

export type LyricsSectionEditorProps = {
  /** Draft value; an absent or non-array value renders an empty-but-addable editor. */
  value?: readonly LyricsSection[] | null;
  onChange: (next: LyricsSection[]) => void;
  labels: LyricsSectionEditorLabels;
  /** Lyric language ("en" | "ar" | "ku" | richer BCP 47 codes); drives input `dir`. */
  language: string;
  maxCharsPerSection?: number;
  maxSections?: number;
  /** Hides add controls when false; structure buttons stay keyboard accessible. */
  allowEditing?: boolean;
};

export function LyricsSectionEditor({
  value,
  onChange,
  labels,
  language,
  maxCharsPerSection = LYRICS_MAX_CHARS_PER_SECTION,
  maxSections = LYRICS_MAX_SECTIONS,
  allowEditing = true,
}: LyricsSectionEditorProps) {
  const sections = Array.isArray(value) ? value : [];
  const direction = lyricsInputDirection(language);
  const languageTag = lyricsInputLanguageTag(language);
  const ordinals = new Map<string, number>();
  lyricsSectionTypes.forEach((type) => {
    ordinals.set(type, sections.filter((section) => section.type === type).length);
  });
  const atSectionsLimit = sections.length >= maxSections;
  const errors = validateLyricsSections(sections, { maxCharsPerSection, maxSections, requireNonEmpty: false });
  const errorBySection = new Map(errors.map((error) => [error.sectionId, error] as const));

  return (
    <div className="lyrics-section-editor">
      <div className="lyrics-section-editor-list">
        {sections.map((section, index) => {
          const ordinal = index + 1;
          const headingId = `lyrics-section-${section.id}-heading`;
          const sectionError = errorBySection.get(section.id);
          return (
            <section key={section.id} className="lyrics-section-editor-item">
              <div className="lyrics-section-editor-item-head">
                <h4 id={headingId} className="lyrics-section-editor-item-title">
                  {labels.sectionType(section.type, ordinal)}
                </h4>
                {allowEditing ? (
                  <div className="lyrics-section-editor-item-actions">
                  <button
                    type="button"
                    disabled={ordinal <= 1}
                    aria-label={labels.moveUp(ordinal)}
                    onClick={() => onChange(moveLyricsSection(sections, section.id, { offset: -1 }))}
                  >
                    ↑
                  </button>
                  <button
                    type="button"
                    disabled={ordinal >= sections.length}
                    aria-label={labels.moveDown(ordinal)}
                    onClick={() => onChange(moveLyricsSection(sections, section.id, { offset: 1 }))}
                  >
                    ↓
                  </button>
                  <button
                    type="button"
                    aria-label={labels.removeSection(ordinal)}
                    onClick={() => onChange(removeLyricsSection(sections, section.id))}
                  >
                    ×
                  </button>
                  </div>
                ) : null}
              </div>
              <textarea
                value={section.text}
                onChange={(event) => onChange(setLyricsSectionText(sections, section.id, event.target.value))}
                dir={direction}
                lang={languageTag || undefined}
                maxLength={maxCharsPerSection}
                placeholder={labels.lyricInput}
                aria-label={lyricsSectionAriaLabel(labels, section.type, ordinal)}
                aria-labelledby={headingId}
                aria-invalid={sectionError ? true : undefined}
                aria-describedby={sectionError ? `${headingId}-error` : undefined}
                rows={5}
              />
              {sectionError ? (
                <p id={`${headingId}-error`} role="alert" className="lyrics-section-editor-error">
                  {lyricsSectionsErrorMessage(sectionError, labels)}
                </p>
              ) : null}
            </section>
          );
        })}
      </div>
      {allowEditing ? (
        <div className="lyrics-section-editor-add">
          {lyricsSectionTypes.map((type) => (
            <button
              key={type}
              type="button"
              disabled={atSectionsLimit}
              aria-label={labels.addSection(type)}
              onClick={() => onChange(addLyricsSection(sections, type))}
            >
              {labels.addSection(type)}
            </button>
          ))}
        </div>
      ) : null}
      <p className="lyrics-section-editor-hints">
        <span>{labels.charactersLimit(maxCharsPerSection)}</span>
        <span>{labels.sectionsLimit(maxSections)}</span>
      </p>
    </div>
  );
}

export default LyricsSectionEditor;
