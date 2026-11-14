/**
 * Provider-neutral guided song-draft state for Music Studio (issue #708 slice).
 *
 * Music Studio already has, in separate open slices, a structured lyrics editor
 * (PR #772), a workspace reference-audio selection state (PR #745), a reference
 * video validation boundary (PR #751) and a draft-to-payload mapper (PR #802).
 * None of them owns the question the user actually faces: in what order may
 * lyrics, vocal language, vocal arrangement and consented reference media be
 * combined, and which of those are honestly unavailable today? This module
 * answers that, and nothing else.
 *
 * Contract it keeps:
 * - Fail-closed by default. Every gate in `MusicGuidedDraftCapabilities` starts
 *   closed, so an unreviewed provider cannot make a sung or reference-based
 *   step look submittable. The module never names a provider, never imports a
 *   provider client and never performs I/O: it only projects the capability
 *   snapshot it is handed (the API catalog in PR #709 is the intended source).
 * - Valid state after every operation. An operation either returns `applied`
 *   with a draft that still satisfies every structural rule below, or
 *   `rejected` with issues and the exact draft instance it was given. There is
 *   no partial write, so a bad section, dialect, reference or consent token can
 *   never land the draft in a state the mapper could misread.
 * - No silent downgrade. A song draft is only ever handed off as a song
 *   (the mapper pins `creationMode: "song_with_vocals"` and
 *   `vocalPreference: "vocal"`); an instrumental draft can never carry lyrics,
 *   a lyric language or references into a payload.
 * - Bilingual/RTL safe. Diagnostics are stable dotted keys, never English
 *   sentences, so Sorani and Arabic rendering and RTL direction are the
 *   renderer's job and no localized string can be parsed as state.
 * - Privacy preserving. References travel as private workspace asset identifiers
 *   only, consent timestamps are copied verbatim and never synthesized, and no
 *   projection echoes lyric text into issues, summaries or logs.
 *
 * Deep lyrics ceilings (per-line and total character budgets) belong to the PR
 * #802 mapper, which stays the single authority for them; this module only
 * checks the shape each guided step needs to remain valid.
 */

/** Creation modes pinned to the shared contract vocabulary (PR #709). */
export const MUSIC_GUIDED_INSTRUMENTAL_MODE = "instrumental";
export const MUSIC_GUIDED_SONG_MODE = "song_with_vocals";
export type MusicGuidedCreationMode =
  | typeof MUSIC_GUIDED_INSTRUMENTAL_MODE
  | typeof MUSIC_GUIDED_SONG_MODE;

/** Lyric languages accepted by the shared contract: English, Arabic, Sorani Kurdish. */
export type MusicGuidedLyricsLanguage = "en" | "ar" | "ku";

/**
 * Vocal arrangement choices for a sung draft. The shared request contract
 * carries no arrangement field yet (PR #709 models `vocalPreference` only), so
 * the arrangement is captured for the guided flow and deliberately kept out of
 * the handoff payload rather than inventing a field the validator would reject.
 */
export const musicGuidedVocalArrangements = Object.freeze(["solo", "duet", "choir"] as const);
export type MusicGuidedVocalArrangement = (typeof musicGuidedVocalArrangements)[number];

/** Section vocabulary mirrored from the shared contract (PR #709). */
export const musicGuidedLyricsSectionTypes = Object.freeze([
  "intro",
  "verse",
  "pre_chorus",
  "chorus",
  "post_chorus",
  "bridge",
  "outro",
  "refrain",
] as const);
export type MusicGuidedLyricsSectionType = (typeof musicGuidedLyricsSectionTypes)[number];

/** Reference media kinds the guided draft can carry, per issue #708 criterion 3. */
export const musicGuidedReferenceMediaTypes = Object.freeze(["audio", "video"] as const);
export type MusicGuidedReferenceMediaType = (typeof musicGuidedReferenceMediaTypes)[number];

/** The only consent token this module accepts; a real UI control must supply it. */
export const MUSIC_GUIDED_CONSENT_TOKEN = "explicit" as const;
export type MusicGuidedConsentToken = typeof MUSIC_GUIDED_CONSENT_TOKEN;

/** Studio language codes that unambiguously resolve to a contract language. */
const studioLanguageAliases: Readonly<Record<string, MusicGuidedLyricsLanguage>> = Object.freeze({
  en: "en",
  ar: "ar",
  ku: "ku",
  ckb: "ku",
  sorani: "ku",
  "ku-sorani": "ku",
});

/**
 * Contract dialect tokens and the language each belongs to (PR #709
 * `MusicGenerationValues.LyricsDialectLanguages`). Kurdish defaults to Sorani
 * because Sorani is the Kurdish dialect the product selection refers to; Arabic
 * has no default, so unqualified Arabic stays blocked until the user picks a
 * regional dialect.
 */
const contractDialectLanguages: Readonly<Record<string, MusicGuidedLyricsLanguage>> = Object.freeze({
  sorani: "ku",
  sorani_erbil: "ku",
  sorani_sulaymaniyah: "ku",
  iraqi_baghdadi: "ar",
  iraqi_mosul: "ar",
  iraqi_basra: "ar",
  gulf_arabic: "ar",
  levantine_arabic: "ar",
});
const defaultDialects: Readonly<Record<MusicGuidedLyricsLanguage, string | null>> = Object.freeze({
  en: null,
  ar: null,
  ku: "sorani",
});

/** Reference ceiling mirrored from the shared validator (PR #709, via #802). */
export const MUSIC_GUIDED_MAX_REFERENCES = 10;
/** Shape guards only; the PR #802 mapper owns per-line and total lyric budgets. */
export const MUSIC_GUIDED_MAX_SECTIONS = 24;
export const MUSIC_GUIDED_MAX_SECTION_TEXT_CHARACTERS = 12_000;

const canonicalGuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const zeroGuid = "00000000-0000-0000-0000-000000000000";

export type MusicGuidedConsent = {
  /** Requires the explicit consent token; never inferred from draft progress. */
  granted: boolean;
  /** Only ever set from a token the caller actually captured. */
  consentToken: MusicGuidedConsentToken | null;
  /** Copied verbatim from the caller; never generated or defaulted here. */
  capturedAt: string | null;
};

export type MusicGuidedLyricsSection = {
  /** Draft-local identity used for list stability; never leaves the draft. */
  id: string;
  type: MusicGuidedLyricsSectionType;
  /** Raw editor blob, exactly as the section editor (PR #772) holds it. */
  text: string;
};

export type MusicGuidedReference = {
  mediaType: MusicGuidedReferenceMediaType;
  /** Private workspace asset id, required; URLs and file names are not carried. */
  assetId: string;
};

export type MusicGuidedStepId = "mode" | "lyrics" | "language" | "vocal" | "reference" | "consent" | "review";

export type MusicGuidedDraft = {
  creationMode: MusicGuidedCreationMode;
  /** True only after the user has actively chosen a creation mode. */
  modeChosen: boolean;
  sections: readonly MusicGuidedLyricsSection[];
  language: MusicGuidedLyricsLanguage | null;
  /** Explicit dialect chosen by the user, or null to take the language default. */
  dialect: string | null;
  vocalArrangement: MusicGuidedVocalArrangement | null;
  lyricsRights: MusicGuidedConsent;
  referenceRights: MusicGuidedConsent;
  /** Set only when the draft targets a real, identifiable singer's voice. */
  voiceLikenessUsed: boolean;
  references: readonly MusicGuidedReference[];
  stepId: MusicGuidedStepId;
};

export type MusicGuidedIssueCode =
  | "creation_mode_invalid"
  | "step_incomplete"
  | "lyrics_not_applicable"
  | "section_type_invalid"
  | "section_text_invalid"
  | "section_too_long"
  | "sections_too_many"
  | "section_id_duplicate"
  | "language_unsupported"
  | "dialect_required"
  | "dialect_unknown"
  | "dialect_mismatch"
  | "vocal_arrangement_invalid"
  | "reference_media_type_invalid"
  | "reference_asset_invalid"
  | "reference_duplicate"
  | "references_too_many"
  | "consent_token_required";

export type MusicGuidedIssue = { code: MusicGuidedIssueCode; field: string };

export type MusicGuidedStepStatus = "complete" | "incomplete" | "not_required" | "not_applicable" | "blocked";

/** Capability snapshot projected from the API catalog; every gate starts closed. */
export type MusicGuidedDraftCapabilities = {
  sungLyricsSupported: boolean;
  referenceAudioSupported: boolean;
  referenceVideoSupported: boolean;
  voiceLikenessSupported: boolean;
};
export const closedMusicGuidedDraftCapabilities: MusicGuidedDraftCapabilities = Object.freeze({
  sungLyricsSupported: false,
  referenceAudioSupported: false,
  referenceVideoSupported: false,
  voiceLikenessSupported: false,
});

export type MusicGuidedDraftOperation =
  | { type: "set_creation_mode"; creationMode: string }
  | { type: "set_sections"; sections: readonly unknown[] }
  | { type: "set_language"; language: string | null; dialect?: string | null }
  | { type: "set_vocal"; vocalArrangement: string | null }
  | { type: "set_references"; references: readonly unknown[] }
  | {
      type: "set_consent";
      lyricsRightsConfirmed?: boolean;
      referenceRightsConfirmed?: boolean;
      voiceLikenessUsed?: boolean;
      consentToken?: string | null;
      consentCapturedAt?: string | null;
    }
  | { type: "advance"; capabilities?: MusicGuidedDraftCapabilities };

export type MusicGuidedDraftOperationResult =
  | { status: "applied"; draft: MusicGuidedDraft }
  | { status: "rejected"; draft: MusicGuidedDraft; issues: readonly MusicGuidedIssue[] };

export type MusicGuidedDraftStep = { id: MusicGuidedStepId; status: MusicGuidedStepStatus };

/** Controls a renderer must switch off, each mapped to its honest reason key. */
export type MusicGuidedDisabledFeature = "lyricsSinging" | "referenceRestyle" | "referenceVideo" | "voiceLikeness";
export type MusicGuidedDisabledFeatures = Partial<Record<MusicGuidedDisabledFeature, string>>;

export type MusicGuidedDraftSummary = {
  creationMode: MusicGuidedCreationMode;
  steps: readonly MusicGuidedDraftStep[];
  currentStepId: MusicGuidedStepId;
  /** The first step that cannot be satisfied as it stands, or null. */
  nextActionableStepId: MusicGuidedStepId | null;
  canSubmit: boolean;
  canAdvance: boolean;
  /**
   * Stable reason keys for gates the user cannot author their way past: a
   * capability the admitted provider set does not have, or an authorization
   * (reference rights) that has not been verified. Missing content shows up as
   * an `incomplete` step instead, so disabled-state copy never blames the user
   * for something the platform cannot do.
   */
  blockedReasons: readonly string[];
  disabledFeatures: MusicGuidedDisabledFeatures;
};

export type MusicGuidedDraftHandoffInput = {
  sections: readonly { id: string; type: string; text: string }[];
  vocalLanguage: string | null;
  dialect: string | null;
  lyricsRightsConfirmed: boolean;
  references: readonly { mediaType: string; assetId: string }[];
  referenceRightsConfirmed: boolean;
};

export type MusicGuidedDraftHandoff = {
  stage: "draft" | "preflight";
  ok: boolean;
  /** PR #802 `MusicLyricsDraftInput`, shaped for `buildMusicLyricsDraft`. */
  input: MusicGuidedDraftHandoffInput | null;
  issues: readonly MusicGuidedIssue[];
};

const noConsent = (): MusicGuidedConsent => ({ granted: false, consentToken: null, capturedAt: null });

/** A valid starting draft: instrumental, first step, every consent withheld. */
export function createEmptyMusicGuidedDraft(): MusicGuidedDraft {
  return {
    creationMode: MUSIC_GUIDED_INSTRUMENTAL_MODE,
    modeChosen: false,
    sections: [],
    language: null,
    dialect: null,
    vocalArrangement: null,
    lyricsRights: noConsent(),
    referenceRights: noConsent(),
    voiceLikenessUsed: false,
    references: [],
    stepId: "mode",
  };
}

const normalizeToken = (value: unknown): string | null => {
  if (typeof value !== "string") return null;
  const trimmed = value.trim().toLowerCase();
  return trimmed.length > 0 ? trimmed : null;
};

/** Contract dialect tokens are underscore-lowercase; hyphenated input normalizes alike. */
const normalizeDialect = (value: unknown): string | null => {
  const token = normalizeToken(value);
  return token ? token.replace(/-/g, "_") : null;
};

const resolveLanguage = (value: unknown): MusicGuidedLyricsLanguage | null => {
  const token = normalizeToken(value);
  return token ? studioLanguageAliases[token] ?? null : null;
};

const isConsentGrant = (confirmed: boolean | undefined, token: unknown): boolean =>
  confirmed === true && normalizeToken(token) === MUSIC_GUIDED_CONSENT_TOKEN;

const songOnlySteps: readonly MusicGuidedStepId[] = ["lyrics", "language", "vocal"];

/**
 * Keeps the guided position valid when the creation mode changes: a song-only
 * step does not exist in instrumental mode, so the position moves forward to
 * the first step both modes share. Switching *into* song mode leaves the
 * position alone; the review gate then reports the newly required song steps,
 * and nothing teleports the user backwards past work they have already done.
 */
const reconcileStepId = (stepId: MusicGuidedStepId, mode: MusicGuidedCreationMode): MusicGuidedStepId =>
  mode === MUSIC_GUIDED_INSTRUMENTAL_MODE && songOnlySteps.includes(stepId) ? "reference" : stepId;

const isGuid = (value: unknown): value is string => {
  if (typeof value !== "string") return false;
  const normalized = value.trim().toLowerCase();
  return canonicalGuidPattern.test(normalized) && normalized !== zeroGuid;
};

function validateSections(draft: MusicGuidedDraft, raw: readonly unknown[]): readonly MusicGuidedIssue[] {
  const issues: MusicGuidedIssue[] = [];
  if (draft.creationMode !== MUSIC_GUIDED_SONG_MODE) {
    return raw.length > 0 ? [{ code: "lyrics_not_applicable", field: "sections" }] : issues;
  }
  if (raw.length > MUSIC_GUIDED_MAX_SECTIONS) issues.push({ code: "sections_too_many", field: "sections" });
  const seenIds = new Set<string>();
  for (const [index, entry] of raw.entries()) {
    const field = `sections[${index}]`;
    if (typeof entry !== "object" || entry === null) {
      issues.push({ code: "section_type_invalid", field });
      continue;
    }
    const candidate = entry as Record<string, unknown>;
    const type = typeof candidate.type === "string" ? candidate.type.trim() : "";
    if (!(musicGuidedLyricsSectionTypes as readonly string[]).includes(type)) {
      issues.push({ code: "section_type_invalid", field });
    }
    const text = candidate.text;
    if (typeof text !== "string" || text.trim().length === 0) {
      issues.push({ code: "section_text_invalid", field });
    } else if (text.length > MUSIC_GUIDED_MAX_SECTION_TEXT_CHARACTERS) {
      issues.push({ code: "section_too_long", field });
    }
    const id = typeof candidate.id === "string" ? candidate.id : "";
    if (id.length > 0) {
      if (seenIds.has(id)) issues.push({ code: "section_id_duplicate", field });
      seenIds.add(id);
    }
  }
  return issues;
}

function validateLanguage(language: unknown, dialect: unknown): readonly MusicGuidedIssue[] {
  const resolved = resolveLanguage(language);
  if (!resolved) return [{ code: "language_unsupported", field: "language" }];
  if (dialect !== null && dialect !== undefined) {
    const token = normalizeDialect(dialect);
    if (!token || !(token in contractDialectLanguages)) return [{ code: "dialect_unknown", field: "dialect" }];
    if (contractDialectLanguages[token] !== resolved) return [{ code: "dialect_mismatch", field: "dialect" }];
    return [];
  }
  // Arabic dialects are a user decision; unqualified Arabic fails closed.
  if (resolved === "ar") return [{ code: "dialect_required", field: "dialect" }];
  return [];
}

function validateReferences(raw: readonly unknown[]): readonly MusicGuidedIssue[] {
  const issues: MusicGuidedIssue[] = [];
  if (raw.length > MUSIC_GUIDED_MAX_REFERENCES) issues.push({ code: "references_too_many", field: "references" });
  const seen = new Set<string>();
  for (const [index, entry] of raw.entries()) {
    const field = `references[${index}]`;
    if (typeof entry !== "object" || entry === null) {
      issues.push({ code: "reference_asset_invalid", field });
      continue;
    }
    const candidate = entry as Record<string, unknown>;
    const mediaType = normalizeToken(candidate.mediaType);
    if (mediaType !== "audio" && mediaType !== "video") {
      issues.push({ code: "reference_media_type_invalid", field });
      continue;
    }
    if (!isGuid(candidate.assetId)) {
      issues.push({ code: "reference_asset_invalid", field });
      continue;
    }
    const assetId = candidate.assetId.trim().toLowerCase();
    if (seen.has(assetId)) {
      issues.push({ code: "reference_duplicate", field });
      continue;
    }
    seen.add(assetId);
  }
  return issues;
}

const canonicalizeSections = (raw: readonly unknown[]): readonly MusicGuidedLyricsSection[] =>
  raw.map((entry, index) => {
    const candidate = entry as Record<string, unknown>;
    const type = String(candidate.type).trim() as MusicGuidedLyricsSectionType;
    const id = typeof candidate.id === "string" && candidate.id.trim().length > 0 ? candidate.id.trim() : `section-${index}`;
    return { id, type, text: String(candidate.text) };
  });

const canonicalizeReferences = (raw: readonly unknown[]): readonly MusicGuidedReference[] =>
  raw.map((entry) => {
    const candidate = entry as Record<string, unknown>;
    return {
      mediaType: normalizeToken(candidate.mediaType) as MusicGuidedReferenceMediaType,
      assetId: String(candidate.assetId).trim().toLowerCase(),
    };
  });

/**
 * Applies one guided operation. Rejections return the same draft instance, so a
 * caller can render `issues` without wondering what was written.
 */
export function applyMusicGuidedDraftOperation(
  draft: MusicGuidedDraft,
  operation: MusicGuidedDraftOperation,
): MusicGuidedDraftOperationResult {
  switch (operation.type) {
    case "set_creation_mode": {
      const mode = normalizeToken(operation.creationMode);
      if (mode !== MUSIC_GUIDED_INSTRUMENTAL_MODE && mode !== MUSIC_GUIDED_SONG_MODE) {
        return { status: "rejected", draft, issues: [{ code: "creation_mode_invalid", field: "creationMode" }] };
      }
      // Choosing a mode records the choice the mode step requires and moves the
      // user to that mode's first remaining step. Lyrics, language and vocal
      // selections survive a later switch to instrumental so mode tinkering
      // never destroys authoring; the instrumental handoff path structurally
      // cannot pick them up.
      const stepId = draft.stepId === "mode" ? (mode === MUSIC_GUIDED_SONG_MODE ? "lyrics" : "reference") : reconcileStepId(draft.stepId, mode);
      return { status: "applied", draft: { ...draft, creationMode: mode, modeChosen: true, stepId } };
    }
    case "set_sections": {
      const issues = validateSections(draft, operation.sections);
      if (issues.length > 0) return { status: "rejected", draft, issues };
      return { status: "applied", draft: { ...draft, sections: canonicalizeSections(operation.sections) } };
    }
    case "set_language": {
      const issues = validateLanguage(operation.language, operation.dialect ?? null);
      if (issues.length > 0) return { status: "rejected", draft, issues };
      return {
        status: "applied",
        draft: { ...draft, language: resolveLanguage(operation.language), dialect: normalizeDialect(operation.dialect ?? null) },
      };
    }
    case "set_vocal": {
      const arrangement = normalizeToken(operation.vocalArrangement);
      if (arrangement === null) return { status: "applied", draft: { ...draft, vocalArrangement: null } };
      if (!(musicGuidedVocalArrangements as readonly string[]).includes(arrangement)) {
        return { status: "rejected", draft, issues: [{ code: "vocal_arrangement_invalid", field: "vocalArrangement" }] };
      }
      return { status: "applied", draft: { ...draft, vocalArrangement: arrangement as MusicGuidedVocalArrangement } };
    }
    case "set_references": {
      const issues = validateReferences(operation.references);
      if (issues.length > 0) return { status: "rejected", draft, issues };
      return { status: "applied", draft: { ...draft, references: canonicalizeReferences(operation.references) } };
    }
    case "set_consent": {
      const wantsLyrics = operation.lyricsRightsConfirmed === true;
      const wantsReferences = operation.referenceRightsConfirmed === true;
      if ((wantsLyrics || wantsReferences) && !isConsentGrant(true, operation.consentToken)) {
        return { status: "rejected", draft, issues: [{ code: "consent_token_required", field: "consentToken" }] };
      }
      const capturedAt =
        typeof operation.consentCapturedAt === "string" && operation.consentCapturedAt.trim().length > 0
          ? operation.consentCapturedAt.trim()
          : null;
      const grant = (confirmed: boolean | undefined): MusicGuidedConsent =>
        confirmed === true ? { granted: true, consentToken: MUSIC_GUIDED_CONSENT_TOKEN, capturedAt } : noConsent();
      return {
        status: "applied",
        draft: {
          ...draft,
          lyricsRights: grant(operation.lyricsRightsConfirmed),
          referenceRights: grant(operation.referenceRightsConfirmed),
          voiceLikenessUsed: operation.voiceLikenessUsed === true,
        },
      };
    }
    case "advance": {
      const summary = summarizeMusicGuidedDraft(draft, operation.capabilities ?? closedMusicGuidedDraftCapabilities);
      if (!summary.canAdvance) {
        return { status: "rejected", draft, issues: [{ code: "step_incomplete", field: "step" }] };
      }
      const steps = musicGuidedDraftStepIds(draft);
      const index = steps.indexOf(draft.stepId);
      if (index < 0) {
        // Defensive: a hand-built draft outside the guided sequence cannot
        // advance, rather than silently jumping to an arbitrary step.
        return { status: "rejected", draft, issues: [{ code: "step_incomplete", field: "step" }] };
      }
      return { status: "applied", draft: { ...draft, stepId: steps[Math.min(index + 1, steps.length - 1)] } };
    }
    default:
      return { status: "rejected", draft, issues: [{ code: "creation_mode_invalid", field: "type" }] };
  }
}

/** Dialect a language/dialect selection resolves to, or null where none applies. */
export function resolveMusicGuidedDraftDialect(draft: MusicGuidedDraft): string | null {
  if (!draft.language) return null;
  return draft.dialect ?? defaultDialects[draft.language];
}

/** Steps in guided order: lyrics, language and vocal only exist for song mode. */
export function musicGuidedDraftStepIds(draft: MusicGuidedDraft): readonly MusicGuidedStepId[] {
  const steps: MusicGuidedStepId[] = ["mode"];
  if (draft.creationMode === MUSIC_GUIDED_SONG_MODE) steps.push("lyrics", "language", "vocal");
  steps.push("reference", "consent", "review");
  return steps;
}

const isSatisfied = (status: MusicGuidedStepStatus): boolean =>
  status === "complete" || status === "not_required" || status === "not_applicable";

/**
 * Projects the draft onto the PR #802 mapper input. Never partially populated:
 * an instrumental draft yields `input: null` instead of a payload that would
 * silently drop the user's lyrics, and a song draft yields null until its
 * lyrics and dialect are actually resolved.
 */
export function toMusicLyricsDraftInput(
  draft: MusicGuidedDraft,
  options: { stage?: "draft" | "preflight" } = {},
): MusicGuidedDraftHandoff {
  const stage = options.stage ?? "draft";
  if (draft.creationMode !== MUSIC_GUIDED_SONG_MODE) {
    return { stage, ok: false, input: null, issues: [{ code: "lyrics_not_applicable", field: "creationMode" }] };
  }
  const issues: MusicGuidedIssue[] = [];
  if (draft.sections.length === 0) issues.push({ code: "section_text_invalid", field: "sections" });
  if (!draft.language) issues.push({ code: "language_unsupported", field: "language" });
  else if (resolveMusicGuidedDraftDialect(draft) === null) issues.push({ code: "dialect_required", field: "dialect" });
  if (draft.references.length > 0 && !draft.referenceRights.granted) {
    issues.push({ code: "consent_token_required", field: "referenceRights" });
  }
  if (stage === "preflight" && !draft.lyricsRights.granted) {
    issues.push({ code: "consent_token_required", field: "lyricsRights" });
  }
  if (issues.length > 0) return { stage, ok: false, input: null, issues };
  return {
    stage,
    ok: true,
    input: {
      sections: draft.sections.map((section) => ({ id: section.id, type: section.type, text: section.text })),
      vocalLanguage: draft.language,
      dialect: resolveMusicGuidedDraftDialect(draft),
      lyricsRightsConfirmed: draft.lyricsRights.granted,
      references: draft.references.map((reference) => ({ mediaType: reference.mediaType, assetId: reference.assetId })),
      referenceRightsConfirmed: draft.referenceRights.granted,
    },
    issues: [],
  };
}

const referenceGateReasons = (
  draft: MusicGuidedDraft,
  capabilities: MusicGuidedDraftCapabilities,
): string[] => {
  const reasons: string[] = [];
  const hasAudio = draft.references.some((reference) => reference.mediaType === "audio");
  const hasVideo = draft.references.some((reference) => reference.mediaType === "video");
  if (hasAudio && !capabilities.referenceAudioSupported) reasons.push("music.guided.blocked.referenceAudioUnsupported");
  if (hasVideo && !capabilities.referenceVideoSupported) reasons.push("music.guided.blocked.referenceVideoUnsupported");
  return reasons;
};

/** Which controls a renderer must switch off, and the honest reason for each. */
export function disabledMusicGuidedDraftFeatures(
  draft: MusicGuidedDraft,
  capabilities: MusicGuidedDraftCapabilities = closedMusicGuidedDraftCapabilities,
): MusicGuidedDisabledFeatures {
  const features: MusicGuidedDisabledFeatures = {};
  if (draft.creationMode === MUSIC_GUIDED_SONG_MODE && !capabilities.sungLyricsSupported) {
    features.lyricsSinging = "music.guided.disabled.lyricsSinging";
  }
  if (!capabilities.referenceAudioSupported) features.referenceRestyle = "music.guided.disabled.referenceRestyle";
  if (!capabilities.referenceVideoSupported) features.referenceVideo = "music.guided.disabled.referenceVideo";
  if (!capabilities.voiceLikenessSupported) features.voiceLikeness = "music.guided.disabled.voiceLikeness";
  return features;
}

/**
 * Renders no prose and no lyric text: a screen-reader label, an RTL panel and a
 * disabled control all read the same stable keys, and no diagnostic can leak
 * the user's words into a log.
 */
export function summarizeMusicGuidedDraft(
  draft: MusicGuidedDraft,
  capabilities: MusicGuidedDraftCapabilities = closedMusicGuidedDraftCapabilities,
): MusicGuidedDraftSummary {
  const isSong = draft.creationMode === MUSIC_GUIDED_SONG_MODE;
  const blocked = new Set<string>();

  const lyricsStep: MusicGuidedStepStatus = !isSong
    ? "not_applicable"
    : draft.sections.length === 0
      ? "incomplete"
      : capabilities.sungLyricsSupported
        ? "complete"
        : (blocked.add("music.guided.blocked.lyricsSingingUnsupported"), "blocked");

  const languageStep: MusicGuidedStepStatus = !isSong
    ? "not_applicable"
    : !draft.language
      ? "incomplete"
      : !resolveMusicGuidedDraftDialect(draft) && draft.language === "ar"
        ? (blocked.add("music.guided.blocked.dialectRequired"), "blocked")
        : "complete";

  const vocalStep: MusicGuidedStepStatus = !isSong
    ? "not_applicable"
    : !draft.vocalArrangement
      ? "incomplete"
      : draft.voiceLikenessUsed && !capabilities.voiceLikenessSupported
        ? (blocked.add("music.guided.blocked.voiceLikenessUnsupported"), "blocked")
        : "complete";

  const referenceGateReasonsList = referenceGateReasons(draft, capabilities);
  const referenceStep: MusicGuidedStepStatus =
    draft.references.length === 0
      ? "not_required"
      : referenceGateReasonsList.length > 0
        ? (referenceGateReasonsList.forEach((reason) => blocked.add(reason)), "blocked")
        : !draft.referenceRights.granted
          ? (blocked.add("music.guided.blocked.referenceRightsRequired"), "blocked")
          : "complete";

  const consentStep: MusicGuidedStepStatus =
    (isSong && !draft.lyricsRights.granted) || (draft.references.length > 0 && !draft.referenceRights.granted)
      ? (blocked.add("music.guided.blocked.consentRequired"), "incomplete")
      : "complete";

  const statuses: Record<MusicGuidedStepId, MusicGuidedStepStatus> = {
    mode: draft.modeChosen ? "complete" : "incomplete",
    lyrics: lyricsStep,
    language: languageStep,
    vocal: vocalStep,
    reference: referenceStep,
    consent: consentStep,
    review: "incomplete",
  };
  const priorSteps = musicGuidedDraftStepIds(draft).filter((id) => id !== "review");
  statuses.review = priorSteps.every((id) => isSatisfied(statuses[id])) && blocked.size === 0 ? "complete" : "incomplete";

  const steps = musicGuidedDraftStepIds(draft).map((id) => ({ id, status: statuses[id] }));
  const actionable = steps.find((step) => step.status === "blocked") ?? steps.find((step) => step.status === "incomplete") ?? null;
  const current = statuses[draft.stepId];

  return {
    creationMode: draft.creationMode,
    steps,
    currentStepId: draft.stepId,
    nextActionableStepId: actionable ? actionable.id : null,
    canSubmit: statuses.review === "complete",
    canAdvance: isSatisfied(current) && draft.stepId !== "review",
    blockedReasons: [...blocked],
    disabledFeatures: disabledMusicGuidedDraftFeatures(draft, capabilities),
  };
}

/** True when the draft's current step is satisfiable under the snapshot. */
export function canAdvanceMusicGuidedDraft(
  draft: MusicGuidedDraft,
  capabilities: MusicGuidedDraftCapabilities = closedMusicGuidedDraftCapabilities,
): boolean {
  return summarizeMusicGuidedDraft(draft, capabilities).canAdvance;
}
