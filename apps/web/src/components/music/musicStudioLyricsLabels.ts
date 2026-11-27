/**
 * Studio copy for the structured lyrics editor.
 *
 * The editor in `LyricsSectionEditor.tsx` renders no text of its own: PR #772
 * made every string a required prop so the component could never invent labels
 * that the rest of the product does not use. Follow-up step 3 of
 * `docs/MUSIC_STUDIO_PREFLIGHT_UI.md` moves this copy into the locale catalog
 * once `i18n.ts` is free of its open lanes; until then the studio supplies it
 * from here, in one place, with the same shape the locale keys will take. The
 * preflight panel already works this way (`defaultMusicLyricsPreflightLabels`),
 * and the mirrored limits below are formatted from the values the contract
 * exports, so this file can never promise a ceiling the server does not apply.
 */
import type {
  LyricsSectionEditorLabels,
  LyricsSectionType,
} from "@/components/music/LyricsSectionEditor";
import {
  MUSIC_LYRICS_MAX_LINES_PER_SECTION,
  MUSIC_LYRICS_MAX_LINE_CHARACTERS,
  MUSIC_LYRICS_MAX_SECTIONS,
  MUSIC_LYRICS_MAX_TOTAL_CHARACTERS,
} from "@/lib/musicLyricsDraftPayload";

/** Reader-facing names for the contract vocabulary, keyed by contract token. */
const sectionTypeNames: Record<LyricsSectionType, string> = {
  intro: "Intro",
  verse: "Verse",
  pre_chorus: "Pre-chorus",
  chorus: "Chorus",
  post_chorus: "Post-chorus",
  bridge: "Bridge",
  outro: "Outro",
  refrain: "Refrain",
};

const thousands = (value: number) => value.toLocaleString("en-US");

export const musicStudioLyricsSectionTypeName = (type: LyricsSectionType): string => sectionTypeNames[type];

/**
 * Ordinals are counted per type, so the second chorus reads "Chorus 2" while
 * the row order in the editor is untouched; the section editor owns only the
 * rows, never the naming.
 */
export const musicStudioLyricsEditorLabels: LyricsSectionEditorLabels = {
  sectionType: (type, ordinal) => `${sectionTypeNames[type]} ${ordinal}`,
  addSection: (type) => `Add ${sectionTypeNames[type].toLowerCase()}`,
  removeSection: (ordinal) => `Remove section ${ordinal}`,
  moveUp: (ordinal) => `Move section ${ordinal} up`,
  moveDown: (ordinal) => `Move section ${ordinal} down`,
  lyricInput: "Write the lyrics for this section, one line per lyric",
  charactersLimit: (maxCharsPerSection) => `Maximum ${thousands(maxCharsPerSection)} characters per section`,
  sectionsLimit: (maxSections) => `Maximum ${maxSections} sections`,
  emptySectionError: "Every section needs lyrics before this draft can be checked.",
  tooLongError: (maxCharsPerSection) => `A section cannot exceed ${thousands(maxCharsPerSection)} characters.`,
  tooManySectionsError: (maxSections) => `You can add at most ${maxSections} sections.`,
  tooManyLinesError: (maxLinesPerSection) => `A section cannot exceed ${maxLinesPerSection} lines.`,
  lineTooLongError: (maxLineCharacters) => `A lyric line cannot exceed ${maxLineCharacters} characters.`,
  totalTooLongError: (maxTotalCharacters) => `Lyrics cannot exceed ${thousands(maxTotalCharacters)} characters in total.`,
  totalCharactersLimit: (maxTotalCharacters) => `${thousands(maxTotalCharacters)} characters across all sections`,
};

/**
 * Panel copy as the studio means it. Only the handoff affordance is overridden:
 * the panel's default label reads "Preflight and submit", and this lane cannot
 * submit, because sung lyrics are still gated on provider capability. The
 * button therefore records the clean handoff into the saved draft.
 */
export const musicStudioLyricsPreflightLabelOverrides = {
  heading: "Lyrics preflight",
  submitLabel: "Check and keep this draft",
  readyNotice:
    "Preflight clean: the typed song payload is recorded in this draft. Sung lyrics stay gated on provider capability, so nothing is submitted.",
} as const;

/** Limits the studio passes down, named once so the view stays readable. */
export const musicStudioLyricsEditorLimits = {
  maxSections: MUSIC_LYRICS_MAX_SECTIONS,
  maxLinesPerSection: MUSIC_LYRICS_MAX_LINES_PER_SECTION,
  maxLineCharacters: MUSIC_LYRICS_MAX_LINE_CHARACTERS,
  maxTotalCharacters: MUSIC_LYRICS_MAX_TOTAL_CHARACTERS,
} as const;

/**
 * The strings the studio's lyrics composer renders, in one object so that
 * follow-up step 3 can replace the whole surface with locale keys in a single
 * edit instead of hunting literals through `MusicStudioView.tsx` - the most
 * contended file in this area, with 18 open claimants when this wiring landed.
 */
export const musicStudioLyricsComposerCopy = {
  eyebrow: "Structured lyrics",
  heading: "Write the song by section",
  subtitle:
    "Sections are checked against the same lyrics contract the server applies, and the draft stays on this device until sung lyrics are enabled for your provider.",
  lyricsRightsLabel: "I hold the rights to these lyrics.",
  referenceRightsLabel: "I have the rights to use the selected reference.",
  clearDraft: "Clear this lyrics draft",
  handoffRecorded: (sectionCount: number) =>
    `Draft recorded: ${sectionCount} section${sectionCount === 1 ? "" : "s"} shaped for a sung request. Nothing was submitted, because sung lyrics are not enabled for your provider yet.`,
  handoffWithheld: (issueCount: number) =>
    `Preflight withheld this draft: ${issueCount} contract issue${issueCount === 1 ? "" : "s"} to fix before it could ever be sent.`,
} as const;
