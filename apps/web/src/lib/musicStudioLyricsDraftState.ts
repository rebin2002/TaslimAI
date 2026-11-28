/**
 * Music Studio structured-lyrics draft state.
 *
 * Gap this module closes: PR #772's section editor and the provider-free
 * preflight panel from `docs/MUSIC_STUDIO_PREFLIGHT_UI.md` both landed with no
 * consumer - follow-up step 1 of that document is exactly this wiring. The two
 * halves speak the same row shape already, so the studio needs no new model: it
 * needs a place to hold what the user authors, a way to hand it to the panel,
 * and a reason for the authored text to survive a tab switch. Sibling studios
 * keep their unsent brief browser-local (`documentStudioDraftState.ts`,
 * `presentationStudioState.ts`, `socialStudioState.ts`); this is the same
 * contract for structured lyrics, split into its own module so the lyrics draft
 * lifecycle can be read and tested apart from the job lifecycle helpers in
 * `musicStudioState.ts`.
 *
 * The draft is browser-local and keyed by the authenticated user and the
 * workspace that owns it, so one workspace's private lyrics are never replayed
 * into another. Reading is fail-closed and re-measured against the mirrored
 * contract rather than trusted: a value that is not readable JSON, a row whose
 * section token is not contract vocabulary, and anything past the mirrored
 * section or character ceilings are dropped or clamped instead of restored,
 * because putting text back in front of the user that the server would reject
 * would defeat the panel this feeds.
 *
 * Nothing here submits, and nothing here can. Sung lyrics are gated on provider
 * capability, and every configured provider reports `SupportsLyricsToVocals`
 * false in `apps/api/Music/MusicGenerationCapabilityCatalog.cs`, so the request
 * this assembles would be rejected by `MusicGenerationRequestValidator` before
 * any cost admission. The payload produced through `buildMusicLyricsDraft` is
 * therefore the draft's ready shape, recorded for the day follow-up step 2 and
 * step 4 of the preflight document clear; it is never sent from this lane.
 */
import {
  MUSIC_LYRICS_MAX_SECTION_ID_CHARACTERS,
  MUSIC_LYRICS_MAX_SECTIONS,
  MUSIC_LYRICS_MAX_TOTAL_CHARACTERS,
  musicLyricsSectionTypes,
  splitLyricsDraftLines,
  type MusicLyricsDraftReferenceInput,
  type MusicLyricsSectionTypeName,
} from "@/lib/musicLyricsDraftPayload";

const draftStoragePrefix = "taslim:music-lyrics-draft:";

type StorageLike = Pick<Storage, "getItem" | "setItem" | "removeItem">;

function browserSessionStorage(): StorageLike | undefined {
  if (typeof window === "undefined") return undefined;
  try {
    return window.sessionStorage;
  } catch {
    return undefined;
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}

function isCanonicalSectionType(value: unknown): value is MusicLyricsSectionTypeName {
  return typeof value === "string"
    && (musicLyricsSectionTypes as readonly string[]).includes(value);
}

/**
 * One authored section row. Structurally the controlled model of PR #772's
 * `LyricsSection`, restated here so the browser-local contract stays free of
 * component imports; `type` is already narrowed to the mirrored vocabulary.
 */
export type MusicStudioLyricsDraftSection = {
  id: string;
  type: MusicLyricsSectionTypeName;
  text: string;
};

export type MusicStudioLyricsDraftState = Readonly<{
  sections: readonly MusicStudioLyricsDraftSection[];
  /**
   * The contract's lyrics-rights attestation. Never implied by the presence of
   * authored text: the server refuses a song request without an explicit
   * confirmation, and so does the panel this draft feeds.
   */
  lyricsRightsConfirmed: boolean;
  /**
   * The contract's reference-rights attestation, kept with the draft for the
   * same reason. It only becomes real while a stored reference is selected -
   * see `buildMusicStudioLyricsPreflightInput`.
   */
  referenceRightsConfirmed: boolean;
}>;

/** Studio selections the mirrored input still needs beyond the authored rows. */
export type MusicStudioLyricsSelection = Readonly<{
  /** The studio's vocal-language select value: `auto`, `en`, `ar`, `ku`. */
  vocalLanguage: string;
  /** Explicit dialect token when the studio carries one; null leaves it to the contract. */
  dialect?: string | null;
  /** The selected reference, already shaped as `{ mediaType, assetId }`. */
  reference?: MusicLyricsDraftReferenceInput | null;
}>;

/**
 * Fully-shaped preflight input. Structurally a subset of
 * `MusicLyricsPreflightInput` from the panel, so it can be passed straight in,
 * and every field is decided rather than optional: a draft must say what it
 * means about language, dialect and both rights attestations.
 */
export type MusicStudioLyricsPreflightInput = {
  sections: readonly MusicStudioLyricsDraftSection[];
  vocalLanguage: string;
  dialect: string | null;
  lyricsRightsConfirmed: boolean;
  references: readonly MusicLyricsDraftReferenceInput[];
  referenceRightsConfirmed: boolean;
};

export function emptyMusicStudioLyricsDraftState(): MusicStudioLyricsDraftState {
  return { sections: [], lyricsRightsConfirmed: false, referenceRightsConfirmed: false };
}

export function musicStudioLyricsDraftStorageKey(userId: string, workspaceId: string) {
  return `${draftStoragePrefix}${encodeURIComponent(userId)}:${encodeURIComponent(workspaceId)}`;
}

/** Character count exactly as the mirrored contract measures it: summed line lengths. */
function lyricsCharacterLoad(text: string): number {
  return splitLyricsDraftLines(text).reduce((total, line) => total + line.length, 0);
}

function restoredSectionId(value: unknown, ordinal: number): string {
  if (typeof value === "string") {
    const id = value.trim();
    if (id.length > 0 && id.length <= MUSIC_LYRICS_MAX_SECTION_ID_CHARACTERS) return id;
  }
  // A missing, blank, oversized or punctuation-heavy id is content-shaped, not
  // name-shaped, so it is replaced rather than echoed back into an issue.
  return `restored-${ordinal}`;
}

/**
 * Clamp one authored row so the restored draft can never exceed the mirrored
 * total. Line boundaries are respected because a line longer than the contract
 * allows is a violation the panel would only have to report back.
 */
function clampRowToRemainingBudget(text: string, remainingCharacters: number): string {
  if (remainingCharacters <= 0) return "";
  const lines = splitLyricsDraftLines(text);
  const load = lyricsCharacterLoad(text);
  if (load <= remainingCharacters) return text;
  const kept: string[] = [];
  let used = 0;
  for (const line of lines) {
    if (used + line.length > remainingCharacters) break;
    used += line.length;
    kept.push(line);
  }
  return kept.join("\n");
}

export function parseMusicStudioLyricsDraftState(raw: string | null): MusicStudioLyricsDraftState {
  if (raw === null) return emptyMusicStudioLyricsDraftState();
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return emptyMusicStudioLyricsDraftState();
  }
  if (!isRecord(parsed) || !Array.isArray(parsed.sections)) return emptyMusicStudioLyricsDraftState();

  const sections: MusicStudioLyricsDraftSection[] = [];
  let usedCharacters = 0;
  for (const row of parsed.sections) {
    if (sections.length >= MUSIC_LYRICS_MAX_SECTIONS) break;
    if (!isRecord(row) || !isCanonicalSectionType(row.type)) continue;
    const text = typeof row.text === "string" ? row.text : "";
    // A blank row is not user work, and the editor can always add one.
    if (text.trim() === "") continue;
    const kept = clampRowToRemainingBudget(text, MUSIC_LYRICS_MAX_TOTAL_CHARACTERS - usedCharacters);
    if (kept.trim() === "") continue;
    usedCharacters += lyricsCharacterLoad(kept);
    sections.push({
      id: restoredSectionId(row.id, sections.length + 1),
      type: row.type,
      text: kept,
    });
  }
  return {
    sections,
    lyricsRightsConfirmed: parsed.lyricsRightsConfirmed === true,
    referenceRightsConfirmed: parsed.referenceRightsConfirmed === true,
  };
}

export function serializeMusicStudioLyricsDraftState(state: MusicStudioLyricsDraftState): string {
  return JSON.stringify({
    sections: state.sections.map((section) => ({ id: section.id, type: section.type, text: section.text })),
    lyricsRightsConfirmed: state.lyricsRightsConfirmed === true,
    referenceRightsConfirmed: state.referenceRightsConfirmed === true,
  });
}

export function isMusicStudioLyricsDraftEmpty(state: MusicStudioLyricsDraftState): boolean {
  return state.sections.length === 0
    && state.lyricsRightsConfirmed !== true
    && state.referenceRightsConfirmed !== true;
}

export function readMusicStudioLyricsDraftState(
  userId: string,
  workspaceId: string,
  storage: StorageLike | undefined = browserSessionStorage(),
): MusicStudioLyricsDraftState {
  if (!storage || !userId.trim() || !workspaceId.trim()) return emptyMusicStudioLyricsDraftState();
  try {
    const key = musicStudioLyricsDraftStorageKey(userId, workspaceId);
    const raw = storage.getItem(key);
    if (raw === null) return emptyMusicStudioLyricsDraftState();
    const draft = parseMusicStudioLyricsDraftState(raw);
    // Whatever restored to nothing is dropped, so a later mount cannot read the
    // same unreadable or emptied value back.
    if (isMusicStudioLyricsDraftEmpty(draft)) storage.removeItem(key);
    return draft;
  } catch {
    return emptyMusicStudioLyricsDraftState();
  }
}

export function persistMusicStudioLyricsDraftState(
  userId: string,
  workspaceId: string,
  state: MusicStudioLyricsDraftState,
  storage: StorageLike | undefined = browserSessionStorage(),
): void {
  if (!storage || !userId.trim() || !workspaceId.trim()) return;
  try {
    if (isMusicStudioLyricsDraftEmpty(state)) {
      storage.removeItem(musicStudioLyricsDraftStorageKey(userId, workspaceId));
      return;
    }
    storage.setItem(musicStudioLyricsDraftStorageKey(userId, workspaceId), serializeMusicStudioLyricsDraftState(state));
  } catch {
    // Storage may be unavailable or full; the in-memory draft stays usable.
  }
}

export function clearMusicStudioLyricsDraftState(
  userId: string,
  workspaceId: string,
  storage: StorageLike | undefined = browserSessionStorage(),
): void {
  if (!storage || !userId.trim() || !workspaceId.trim()) return;
  try {
    storage.removeItem(musicStudioLyricsDraftStorageKey(userId, workspaceId));
  } catch {
    // Clearing is best effort.
  }
}

/**
 * Assemble the mirrored preflight input from what the studio holds. Pure and
 * side-effect free: the studio's vocal language, dialect and reference
 * selections are read, never rewritten, and `auto` is passed through unchanged
 * so the contract - not this adapter - decides that `auto` cannot be sung.
 */
export function buildMusicStudioLyricsPreflightInput(
  state: MusicStudioLyricsDraftState,
  selection: MusicStudioLyricsSelection,
): MusicStudioLyricsPreflightInput {
  const references = selection.reference ? [selection.reference] : [];
  // An attestation with nothing behind it is a contract violation in its own
  // right (`reference_rights_without_assets`), so the saved value is only
  // handed on while a private workspace asset is actually selected. A local,
  // not-yet-stored file therefore surfaces as `reference_asset_invalid` on the
  // reference itself, which is the truthful reading of that state.
  const hasStorableReference = references.some((entry) => typeof entry.assetId === "string" && entry.assetId.trim() !== "");
  return {
    sections: state.sections,
    vocalLanguage: selection.vocalLanguage,
    dialect: selection.dialect ?? null,
    lyricsRightsConfirmed: state.lyricsRightsConfirmed === true,
    references,
    referenceRightsConfirmed: state.referenceRightsConfirmed === true && hasStorableReference,
  };
}
