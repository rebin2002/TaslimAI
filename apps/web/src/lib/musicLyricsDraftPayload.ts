/**
 * Provider-free mapper from a locally authored structured-lyrics draft plus the
 * selected Sorani/Arabic/English vocal language into a normalized, bounded,
 * typed song payload (issue #708 slice). Pure and synchronous: it talks to
 * nothing outside the browser, claims no execution capability, and never
 * invents support. Every lyric line is preserved verbatim apart from a
 * deterministic line-splitting normalization (CRLF to LF, per-line trailing
 * spaces/tabs removed, blank separator lines dropped, order kept).
 *
 * Vocabulary and limits mirror the shared song request contract and validator
 * established in open PR #709 so a draft that this helper reports as
 * submittable contains no client-visible structural violation. Final admission
 * stays fail-closed in that contract's validator: this helper only prepares
 * and checks the request shape before it is handed off. Reference media is
 * carried exclusively as private workspace asset identifiers (the selection
 * state owned by PR #745) — never URLs, file names, or inline bytes — and
 * lyrics never appear in issue payloads, so nothing beyond the user's own
 * draft text is exposed.
 */

/** Section vocabulary of the shared request contract (PR #709). */
export const musicLyricsSectionTypes = Object.freeze([
  "intro",
  "verse",
  "pre_chorus",
  "chorus",
  "post_chorus",
  "bridge",
  "outro",
  "refrain",
] as const);

export type MusicLyricsSectionTypeName = (typeof musicLyricsSectionTypes)[number];

/** Vocal lyric languages accepted by the shared contract: English, Arabic, Sorani Kurdish. */
export const musicLyricsLanguages = Object.freeze(["en", "ar", "ku"] as const);

export type MusicLyricsLanguage = (typeof musicLyricsLanguages)[number];

/** Known dialect tokens and the language each belongs to (PR #709 vocabulary). */
export const musicLyricsDialectLanguages: Readonly<Record<string, MusicLyricsLanguage>> = Object.freeze({
  sorani: "ku",
  sorani_erbil: "ku",
  sorani_sulaymaniyah: "ku",
  iraqi_baghdadi: "ar",
  iraqi_mosul: "ar",
  iraqi_basra: "ar",
  gulf_arabic: "ar",
  levantine_arabic: "ar",
});

/**
 * Dialect applied when the selection names a language without an explicit
 * dialect. Kurdish defaults to Sorani because Sorani is the Kurdish dialect
 * the product selection refers to (issue #708 wording). Arabic deliberately
 * has no default: choosing among its catalogued regional dialects is a user
 * decision, so an unqualified Arabic selection fails closed. English never
 * takes a dialect.
 */
export const musicLyricsDefaultDialects: Readonly<Record<MusicLyricsLanguage, string | null>> = Object.freeze({
  en: null,
  ar: null,
  ku: "sorani",
});

/** Studio language aliases that unambiguously identify a contract language. */
const languageAliases: Readonly<Record<string, MusicLyricsLanguage>> = Object.freeze({
  en: "en",
  ar: "ar",
  ku: "ku",
  ckb: "ku",
  sorani: "ku",
  "ku-sorani": "ku",
});

/* Limits mirrored from the shared validator (PR #709). Keep in sync there. */
export const MUSIC_LYRICS_MAX_SECTIONS = 24;
export const MUSIC_LYRICS_MAX_LINES_PER_SECTION = 30;
export const MUSIC_LYRICS_MAX_LINE_CHARACTERS = 200;
export const MUSIC_LYRICS_MAX_TOTAL_CHARACTERS = 3_500;
export const MUSIC_REFERENCES_MAX_ASSETS = 10;

/** Pinned song-mode values from the shared creation-mode vocabulary. */
export const MUSIC_LYRICS_SONG_CREATION_MODE = "song_with_vocals";
export const MUSIC_LYRICS_SONG_VOCAL_PREFERENCE = "vocal";

const canonicalGuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const zeroGuid = "00000000-0000-0000-0000-000000000000";

/** One editor row (the controlled model of the section editor in PR #772). */
export type MusicLyricsDraftSectionInput = {
  /** Draft-local identifier; never leaves the payload. */
  id?: string;
  type: string;
  /** Multi-line blob exactly as authored; split into contract lines here. */
  text: string;
};

/** One reference selection carried by the workspace reference state (PR #745). */
export type MusicLyricsDraftReferenceInput = {
  mediaType: string;
  /** Private workspace asset id; null or malformed values fail closed. */
  assetId: string | null;
};

export type MusicLyricsDraftInput = {
  sections?: readonly MusicLyricsDraftSectionInput[] | null;
  /** Selected vocal language, e.g. "ku", "ckb" or "Sorani". */
  vocalLanguage?: string | null;
  /** Optional explicit dialect token; overrides the language default. */
  dialect?: string | null;
  lyricsRightsConfirmed?: boolean;
  references?: readonly MusicLyricsDraftReferenceInput[] | null;
  referenceRightsConfirmed?: boolean;
};

export type MusicLyricsDraftPayloadSection = {
  section: MusicLyricsSectionTypeName;
  /** Lyric lines in authored order, content verbatim. */
  lines: readonly string[];
};

export type MusicLyricsDraftPayloadReference = {
  mediaType: "audio" | "video";
  assetId: string;
};

/**
 * Typed song request shape for the shared contract (PR #709): field names and
 * normalized values match what the request validator consumes downstream.
 */
export type MusicLyricsDraftPayload = {
  creationMode: typeof MUSIC_LYRICS_SONG_CREATION_MODE;
  vocalPreference: typeof MUSIC_LYRICS_SONG_VOCAL_PREFERENCE;
  lyrics: readonly MusicLyricsDraftPayloadSection[];
  lyricsLanguage: MusicLyricsLanguage;
  lyricsDialect: string | null;
  lyricsRightsConfirmed: boolean;
  referenceAssets: readonly MusicLyricsDraftPayloadReference[];
  referenceRightsConfirmed: boolean;
};

export type MusicLyricsDraftIssueCode =
  | "sections_missing"
  | "sections_too_many"
  | "section_type_invalid"
  | "section_empty"
  | "lines_too_many"
  | "line_too_long"
  | "lyrics_total_too_long"
  | "language_unsupported"
  | "dialect_required"
  | "dialect_unknown"
  | "dialect_mismatch"
  | "dialect_not_allowed"
  | "lyrics_rights_required"
  | "reference_asset_invalid"
  | "reference_duplicate"
  | "references_too_many"
  | "reference_rights_required"
  | "reference_rights_without_assets";

/**
 * Deterministic, localizable issue. Never contains lyric text, dialect copy or
 * reference file names — only codes, ordinals and numeric limit parameters.
 */
export type MusicLyricsDraftIssue = {
  code: MusicLyricsDraftIssueCode;
  sectionId?: string;
  params: Record<string, string | number>;
};

export type MusicLyricsDraftStage = "draft" | "preflight";

export type MusicLyricsDraftOutcome = {
  stage: MusicLyricsDraftStage;
  /** True only when the mapped payload violates no mirrored limit or rule. */
  submittable: boolean;
  /**
   * Null when no faithful payload can be shaped at all (no lyrics, unknown
   * language or unresolved dialect), and always null at the preflight stage
   * while any issue remains. At the draft stage a payload is shaped whenever
   * its typed fields are all faithfully known.
   */
  payload: MusicLyricsDraftPayload | null;
  issues: readonly MusicLyricsDraftIssue[];
};

const normalizeToken = (value: unknown): string | null =>
  typeof value === "string" && value.trim() ? value.trim().toLowerCase() : null;

/** Contract dialect tokens are lowercase with underscores; hyphens normalize alike. */
const normalizeDialectToken = (value: unknown): string | null => {
  const token = normalizeToken(value);
  return token ? token.replace(/-/g, "_") : null;
};

/**
 * Deterministic line splitting for an editor section blob. Preserves each
 * non-blank line's content verbatim: only line endings, per-line trailing
 * spaces/tabs and blank separator lines are removed, never words, case,
 * leading indentation or internal spacing.
 */
export function splitLyricsDraftLines(text: string): string[] {
  return (text ?? "")
    .replace(/\r\n?/g, "\n")
    .split("\n")
    .map((line) => line.replace(/[ \t]+$/, ""))
    .filter((line) => line.length > 0);
}

type RetainedSection = { sectionId?: string; type: MusicLyricsSectionTypeName; lines: string[] };

function mapSections(
  sections: readonly MusicLyricsDraftSectionInput[] | null | undefined,
  issues: MusicLyricsDraftIssue[],
): { retained: RetainedSection[]; isArray: boolean } {
  if (!Array.isArray(sections)) {
    issues.push({ code: "sections_missing", params: {} });
    return { retained: [], isArray: false };
  }
  if (sections.length === 0) {
    issues.push({ code: "sections_missing", params: {} });
    return { retained: [], isArray: true };
  }
  if (sections.length > MUSIC_LYRICS_MAX_SECTIONS) {
    issues.push({ code: "sections_too_many", params: { maxSections: MUSIC_LYRICS_MAX_SECTIONS, actual: sections.length } });
  }
  const retained: RetainedSection[] = [];
  sections.forEach((entry, index) => {
    const ordinal = index + 1;
    const type = normalizeToken(entry?.type);
    if (!type || !(musicLyricsSectionTypes as readonly string[]).includes(type)) {
      issues.push({
        code: "section_type_invalid",
        ...(typeof entry?.id === "string" ? { sectionId: entry.id } : {}),
        params: { sectionOrdinal: ordinal },
      });
      return;
    }
    const lines = splitLyricsDraftLines(typeof entry?.text === "string" ? entry.text : "");
    const sectionId = typeof entry?.id === "string" ? entry.id : undefined;
    if (lines.length === 0) {
      issues.push({
        code: "section_empty",
        ...(sectionId !== undefined ? { sectionId } : {}),
        params: { sectionOrdinal: ordinal },
      });
      return;
    }
    const extra: Record<string, string | number> = sectionId !== undefined ? { sectionId } : {};
    if (lines.length > MUSIC_LYRICS_MAX_LINES_PER_SECTION) {
      issues.push({
        code: "lines_too_many",
        ...extra,
        params: {
          sectionOrdinal: ordinal,
          maxLinesPerSection: MUSIC_LYRICS_MAX_LINES_PER_SECTION,
          actual: lines.length,
        },
      });
    }
    const longest = lines.reduce((max, line) => Math.max(max, line.length), 0);
    if (longest > MUSIC_LYRICS_MAX_LINE_CHARACTERS) {
      issues.push({
        code: "line_too_long",
        ...extra,
        params: {
          sectionOrdinal: ordinal,
          maxLineCharacters: MUSIC_LYRICS_MAX_LINE_CHARACTERS,
          actual: longest,
        },
      });
    }
    retained.push({ type: type as MusicLyricsSectionTypeName, lines, ...(sectionId !== undefined ? { sectionId } : {}) });
  });
  return { retained, isArray: true };
}

function resolveDialect(
  language: MusicLyricsLanguage,
  dialect: string | null | undefined,
  issues: MusicLyricsDraftIssue[],
): string | null | undefined {
  // Returns the resolved dialect, or undefined when it could not be resolved
  // faithfully (an issue has been recorded in that case).
  const explicit = normalizeDialectToken(dialect);
  if (language === "en") {
    if (explicit) issues.push({ code: "dialect_not_allowed", params: { lyricsLanguage: language } });
    return explicit ? undefined : null;
  }
  const token = explicit ?? musicLyricsDefaultDialects[language];
  if (!token) {
    issues.push({ code: "dialect_required", params: { lyricsLanguage: language } });
    return undefined;
  }
  const dialectLanguage = musicLyricsDialectLanguages[token];
  if (!dialectLanguage) {
    issues.push({ code: "dialect_unknown", params: { lyricsLanguage: language } });
    return undefined;
  }
  if (dialectLanguage !== language) {
    issues.push({ code: "dialect_mismatch", params: { lyricsLanguage: language } });
    return undefined;
  }
  return token;
}

function mapReferences(
  references: readonly MusicLyricsDraftReferenceInput[] | null | undefined,
  referenceRightsConfirmed: boolean,
  issues: MusicLyricsDraftIssue[],
): MusicLyricsDraftPayloadReference[] {
  const retained: MusicLyricsDraftPayloadReference[] = [];
  const seen = new Set<string>();
  if (Array.isArray(references)) {
    references.forEach((entry, index) => {
      const ordinal = index + 1;
      const mediaType = normalizeToken(entry?.mediaType);
      if (mediaType !== "audio" && mediaType !== "video") {
        issues.push({ code: "reference_asset_invalid", params: { referenceOrdinal: ordinal, reason: "media_type" } });
        return;
      }
      const assetId = normalizeToken(entry?.assetId);
      if (!assetId || !canonicalGuidPattern.test(assetId) || assetId === zeroGuid) {
        // A not-yet-stored local selection (null id) and any non-asset value
        // (URLs included) fail closed here; only private asset ids continue.
        issues.push({ code: "reference_asset_invalid", params: { referenceOrdinal: ordinal, reason: "asset_id" } });
        return;
      }
      if (seen.has(assetId)) {
        issues.push({ code: "reference_duplicate", params: { referenceOrdinal: ordinal } });
        return;
      }
      seen.add(assetId);
      retained.push({ mediaType, assetId });
    });
  }
  if (retained.length > MUSIC_REFERENCES_MAX_ASSETS) {
    issues.push({ code: "references_too_many", params: { maxReferences: MUSIC_REFERENCES_MAX_ASSETS, actual: retained.length } });
  }
  if (retained.length > 0 && referenceRightsConfirmed !== true) {
    issues.push({ code: "reference_rights_required", params: {} });
  }
  if (referenceRightsConfirmed === true && retained.length === 0) {
    issues.push({ code: "reference_rights_without_assets", params: {} });
  }
  return retained;
}

/**
 * Maps one structured-lyrics draft plus the vocal language selection into the
 * typed song payload. `draft` shapes the payload whenever its fields are all
 * faithfully known; `preflight` additionally withholds the payload unless no
 * mirrored rule or limit is violated. Deterministic and side-effect free.
 */
export function buildMusicLyricsDraft(
  input: MusicLyricsDraftInput,
  stage: MusicLyricsDraftStage = "draft",
): MusicLyricsDraftOutcome {
  const issues: MusicLyricsDraftIssue[] = [];

  const { retained } = mapSections(input.sections, issues);

  const languageToken = normalizeToken(input.vocalLanguage);
  const language = languageToken ? languageAliases[languageToken] : undefined;
  if (!language) {
    issues.push({
      code: "language_unsupported",
      params: { supported: musicLyricsLanguages.join("/") },
    });
  }

  let dialect: string | null | undefined = null;
  if (language) {
    dialect = resolveDialect(language, input.dialect, issues);
  }

  if (input.lyricsRightsConfirmed !== true) {
    issues.push({ code: "lyrics_rights_required", params: {} });
  }

  const referenceAssets = mapReferences(
    input.references,
    input.referenceRightsConfirmed === true,
    issues,
  );

  let totalCharacters = 0;
  for (const section of retained) {
    for (const line of section.lines) totalCharacters += line.length;
  }
  if (totalCharacters > MUSIC_LYRICS_MAX_TOTAL_CHARACTERS) {
    issues.push({
      code: "lyrics_total_too_long",
      params: { maxTotalCharacters: MUSIC_LYRICS_MAX_TOTAL_CHARACTERS, actual: totalCharacters },
    });
  }

  const shapeable = Boolean(language) && dialect !== undefined && retained.length > 0;
  const payload: MusicLyricsDraftPayload | null = !shapeable || (stage === "preflight" && issues.length > 0)
    ? null
    : Object.freeze({
        creationMode: MUSIC_LYRICS_SONG_CREATION_MODE,
        vocalPreference: MUSIC_LYRICS_SONG_VOCAL_PREFERENCE,
        lyrics: Object.freeze(
          retained.map((section) =>
            Object.freeze({
              section: section.type,
              lines: Object.freeze(section.lines.slice()),
            }),
          ),
        ),
        lyricsLanguage: language as MusicLyricsLanguage,
        lyricsDialect: dialect as string | null,
        lyricsRightsConfirmed: input.lyricsRightsConfirmed === true,
        referenceAssets: Object.freeze(referenceAssets.map((reference) => Object.freeze({ ...reference }))),
        referenceRightsConfirmed: input.referenceRightsConfirmed === true,
      });

  return Object.freeze({
    stage,
    submittable: issues.length === 0,
    payload,
    issues: Object.freeze(issues),
  });
}
