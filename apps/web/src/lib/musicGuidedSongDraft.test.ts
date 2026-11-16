import { describe, expect, it } from "vitest";
import {
  MUSIC_GUIDED_CONSENT_TOKEN,
  MUSIC_GUIDED_INSTRUMENTAL_MODE,
  MUSIC_GUIDED_SONG_MODE,
  applyMusicGuidedDraftOperation,
  canAdvanceMusicGuidedDraft,
  closedMusicGuidedDraftCapabilities,
  createEmptyMusicGuidedDraft,
  disabledMusicGuidedDraftFeatures,
  musicGuidedDraftStepIds,
  resolveMusicGuidedDraftDialect,
  summarizeMusicGuidedDraft,
  toMusicLyricsDraftInput,
  type MusicGuidedDraft,
  type MusicGuidedDraftCapabilities,
  type MusicGuidedDraftOperation,
  type MusicGuidedIssue,
} from "./musicGuidedSongDraft";

const openCapabilities: MusicGuidedDraftCapabilities = {
  sungLyricsSupported: true,
  referenceAudioSupported: true,
  referenceVideoSupported: true,
  voiceLikenessSupported: true,
};
const audioAssetId = "11111111-2222-3333-4444-555555555555";
const videoAssetId = "66666666-7777-8888-9999-000000000011";
/** Synthetic Sorani (Erbil) line, kept short; no real lyric content. */
const soraniLine = "بەڵێ، گۆرانییەک بنووسە";

/** Applies an operation that must succeed, so tests read as a happy path. */
function apply(draft: MusicGuidedDraft, operation: MusicGuidedDraftOperation): MusicGuidedDraft {
  const result = applyMusicGuidedDraftOperation(draft, operation);
  if (result.status !== "applied") throw new Error(`expected applied, got ${JSON.stringify(result.issues)}`);
  return result.draft;
}

/** Applies an operation that must be refused, and returns its issues. */
function refuse(draft: MusicGuidedDraft, operation: MusicGuidedDraftOperation): readonly MusicGuidedIssue[] {
  const result = applyMusicGuidedDraftOperation(draft, operation);
  if (result.status !== "rejected") throw new Error(`expected rejection, got ${result.status}`);
  return result.issues;
}

/** A draft with song mode chosen, i.e. past the mode step. */
function songModeDraft(): MusicGuidedDraft {
  return apply(createEmptyMusicGuidedDraft(), { type: "set_creation_mode", creationMode: MUSIC_GUIDED_SONG_MODE });
}

/** A complete, consented Sorani song draft with one audio reference. */
function songDraft(options: { capabilities?: MusicGuidedDraftCapabilities; atReview?: boolean } = {}): MusicGuidedDraft {
  const capabilities = options.capabilities ?? openCapabilities;
  let draft = apply(songModeDraft(), {
    type: "set_sections",
    sections: [
      { id: "verse-1", type: "verse", text: soraniLine },
      { id: "chorus-1", type: "chorus", text: soraniLine },
    ],
  });
  draft = apply(draft, { type: "set_language", language: "ckb", dialect: "sorani_erbil" });
  draft = apply(draft, { type: "set_vocal", vocalArrangement: "solo" });
  draft = apply(draft, { type: "set_references", references: [{ mediaType: "audio", assetId: audioAssetId }] });
  draft = apply(draft, {
    type: "set_consent",
    lyricsRightsConfirmed: true,
    referenceRightsConfirmed: true,
    consentToken: MUSIC_GUIDED_CONSENT_TOKEN,
    consentCapturedAt: "2026-11-14T00:00:00Z",
  });
  if (options.atReview === false) return draft;
  for (let guard = 0; guard < 10; guard += 1) {
    const result = applyMusicGuidedDraftOperation(draft, { type: "advance", capabilities });
    if (result.status !== "applied") return draft;
    draft = result.draft;
    if (draft.stepId === "review") return draft;
  }
  return draft;
}

describe("guided song draft fail-closed defaults", () => {
  it("starts with no mode chosen, every consent withheld, nothing submittable", () => {
    const draft = createEmptyMusicGuidedDraft();
    expect(draft.creationMode).toBe(MUSIC_GUIDED_INSTRUMENTAL_MODE);
    expect(draft.modeChosen).toBe(false);
    expect(draft.stepId).toBe("mode");
    expect(draft.lyricsRights).toEqual({ granted: false, consentToken: null, capturedAt: null });
    expect(draft.referenceRights.granted).toBe(false);
    const summary = summarizeMusicGuidedDraft(draft);
    expect(summary.canSubmit).toBe(false);
    expect(summary.steps.find((step) => step.id === "mode")?.status).toBe("incomplete");
    // An instrumental draft is the path the shipped API already supports, so it
    // must not be gated behind a sung-lyrics capability.
    expect(summary.blockedReasons).toEqual([]);
    expect(summary.steps.find((step) => step.id === "lyrics")).toBeUndefined();
  });

  it("never claims sung lyrics are submittable while the capability snapshot is closed", () => {
    const draft = songDraft({ capabilities: closedMusicGuidedDraftCapabilities, atReview: false });
    const summary = summarizeMusicGuidedDraft(draft, closedMusicGuidedDraftCapabilities);
    expect(summary.canSubmit).toBe(false);
    expect(summary.steps.find((step) => step.id === "lyrics")?.status).toBe("blocked");
    expect(summary.blockedReasons).toContain("music.guided.blocked.lyricsSingingUnsupported");
    expect(summary.disabledFeatures.lyricsSinging).toBe("music.guided.disabled.lyricsSinging");
  });

  it("disables every unsupported control and re-enables only what the snapshot opens", () => {
    const draft = songDraft();
    const closed = disabledMusicGuidedDraftFeatures(draft, closedMusicGuidedDraftCapabilities);
    expect(Object.keys(closed).sort()).toEqual(["lyricsSinging", "referenceRestyle", "referenceVideo", "voiceLikeness"]);
    expect(disabledMusicGuidedDraftFeatures(draft, openCapabilities)).toEqual({});
    const withoutVideo: MusicGuidedDraftCapabilities = { ...openCapabilities, referenceVideoSupported: false };
    expect(Object.keys(disabledMusicGuidedDraftFeatures(draft, withoutVideo))).toEqual(["referenceVideo"]);
  });
});

describe("guided song draft state validity", () => {
  it("returns the identical draft instance when an operation is refused", () => {
    const before = songDraft();
    const result = applyMusicGuidedDraftOperation(before, { type: "set_vocal", vocalArrangement: "beatbox" });
    if (result.status !== "rejected") throw new Error("expected rejection");
    // Identity, not just equality: nothing is partially written on refusal.
    expect(result.draft).toBe(before);
    expect(result.issues).toEqual([{ code: "vocal_arrangement_invalid", field: "vocalArrangement" }]);
    expect(result.draft.vocalArrangement).toBe("solo");
  });

  it("refuses lyric authoring in instrumental mode instead of silently queueing it", () => {
    const issues = refuse(createEmptyMusicGuidedDraft(), {
      type: "set_sections",
      sections: [{ id: "verse-1", type: "verse", text: soraniLine }],
    });
    expect(issues).toEqual([{ code: "lyrics_not_applicable", field: "sections" }]);
  });

  it("rejects unknown section types, blank text, duplicate ids and an over-long blob", () => {
    const base = songModeDraft();
    const cases: { sections: unknown[]; code: string }[] = [
      { sections: [{ id: "a", type: "hook", text: soraniLine }], code: "section_type_invalid" },
      { sections: [{ id: "a", type: "verse", text: "   " }], code: "section_text_invalid" },
      { sections: [{ id: "a", type: "verse", text: soraniLine }, { id: "a", type: "chorus", text: soraniLine }], code: "section_id_duplicate" },
      { sections: [{ id: "a", type: "verse", text: "خ".repeat(12_001) }], code: "section_too_long" },
    ];
    for (const entry of cases) {
      const issues = refuse(base, { type: "set_sections", sections: entry.sections });
      expect(issues.map((issue) => issue.code), entry.code).toContain(entry.code);
    }
    expect(apply(base, { type: "set_sections", sections: [{ id: "a", type: "verse", text: soraniLine }] }).sections).toHaveLength(1);
  });

  it("keeps the guided position valid across a mode switch", () => {
    const draft = songDraft({ atReview: false });
    const toInstrumental = apply(draft, { type: "set_creation_mode", creationMode: MUSIC_GUIDED_INSTRUMENTAL_MODE });
    // A song-only step does not exist in instrumental mode, so the position
    // moves forward to the first step both modes share.
    expect(toInstrumental.stepId).toBe("reference");
    expect(musicGuidedDraftStepIds(toInstrumental)).not.toContain("lyrics");

    const backToSong = apply(toInstrumental, { type: "set_creation_mode", creationMode: MUSIC_GUIDED_SONG_MODE });
    expect(backToSong.stepId).toBe("reference");
    expect(musicGuidedDraftStepIds(backToSong)).toContain("lyrics");
    // Authoring survives the round trip; only the position is reconciled.
    expect(backToSong.sections).toHaveLength(2);
  });

  it("reports the newly required song step when a cleared draft re-enters song mode", () => {
    const authored = songModeDraft();
    const cleared = apply(authored, { type: "set_sections", sections: [] });
    expect(cleared.stepId).toBe("lyrics");
    const summary = summarizeMusicGuidedDraft(cleared, openCapabilities);
    expect(summary.steps.find((step) => step.id === "lyrics")?.status).toBe("incomplete");
    expect(summary.canSubmit).toBe(false);
    expect(summary.nextActionableStepId).toBe("lyrics");
  });

  it("advances only from a chosen mode and stops at review", () => {
    const fresh = createEmptyMusicGuidedDraft();
    expect(canAdvanceMusicGuidedDraft(fresh)).toBe(false);
    expect(refuse(fresh, { type: "advance" })).toEqual([{ code: "step_incomplete", field: "step" }]);

    const chosen = apply(fresh, { type: "set_creation_mode", creationMode: MUSIC_GUIDED_INSTRUMENTAL_MODE });
    expect(chosen.stepId).toBe("reference");
    expect(canAdvanceMusicGuidedDraft(chosen)).toBe(true);

    const song = songDraft();
    expect(song.stepId).toBe("review");
    expect(refuse(song, { type: "advance" })).toEqual([{ code: "step_incomplete", field: "step" }]);
  });

  it("will not advance a song draft while sung lyrics are unsupported", () => {
    const draft = songDraft({ capabilities: closedMusicGuidedDraftCapabilities, atReview: false });
    expect(draft.stepId).toBe("lyrics");
    expect(refuse(draft, { type: "advance", capabilities: closedMusicGuidedDraftCapabilities })).toEqual([
      { code: "step_incomplete", field: "step" },
    ]);
  });
});

describe("guided song draft language and dialect", () => {
  it("resolves Sorani from the studio codes the Music Studio already offers", () => {
    const draft = songModeDraft();
    for (const code of ["ku", "ckb", "Sorani", "ku-sorani"]) {
      const result = applyMusicGuidedDraftOperation(draft, { type: "set_language", language: code });
      if (result.status !== "applied") throw new Error(`expected ${code} to resolve`);
      expect(resolveMusicGuidedDraftDialect(result.draft), code).toBe("sorani");
    }
    expect(refuse(draft, { type: "set_language", language: "ckb-IQ" })).toEqual([{ code: "language_unsupported", field: "language" }]);
  });

  it("accepts an explicit Sorani or Arabic dialect and refuses a mismatched one", () => {
    const draft = songModeDraft();
    const matched = apply(draft, { type: "set_language", language: "ar", dialect: "iraqi_baghdadi" });
    expect(resolveMusicGuidedDraftDialect(matched)).toBe("iraqi_baghdadi");
    expect(refuse(draft, { type: "set_language", language: "ku", dialect: "gulf_arabic" })).toEqual([
      { code: "dialect_mismatch", field: "dialect" },
    ]);
    expect(refuse(draft, { type: "set_language", language: "ku", dialect: "kurmanci" })).toEqual([
      { code: "dialect_unknown", field: "dialect" },
    ]);
  });

  it("fails closed on unqualified Arabic instead of guessing a dialect", () => {
    const draft = songModeDraft();
    expect(refuse(draft, { type: "set_language", language: "ar" })).toEqual([{ code: "dialect_required", field: "dialect" }]);
    // English legitimately carries no dialect, so it must not read as blocked
    // once lyrics exist; only the not-yet-chosen vocal step should be open.
    const english = apply(draft, { type: "set_language", language: "en" });
    expect(resolveMusicGuidedDraftDialect(english)).toBeNull();
    const authored = apply(english, { type: "set_sections", sections: [{ id: "v1", type: "verse", text: soraniLine }] });
    const summary = summarizeMusicGuidedDraft(authored, openCapabilities);
    expect(summary.steps.find((step) => step.id === "language")?.status).toBe("complete");
    expect(summary.blockedReasons).not.toContain("music.guided.blocked.dialectRequired");
    expect(summary.nextActionableStepId).toBe("vocal");
  });

  it("rejects an out-of-contract lyric language such as Chinese", () => {
    expect(refuse(songDraft(), { type: "set_language", language: "zh" })).toEqual([{ code: "language_unsupported", field: "language" }]);
  });
});

describe("guided song draft references and consent", () => {
  it("carries references only as private workspace asset ids", () => {
    const draft = songDraft();
    expect(draft.references).toEqual([{ mediaType: "audio", assetId: audioAssetId }]);
    const handoff = toMusicLyricsDraftInput(draft, { stage: "preflight" });
    expect(handoff.input?.references).toEqual([{ mediaType: "audio", assetId: audioAssetId }]);
    expect(JSON.stringify(handoff)).not.toContain("http");
    expect(JSON.stringify(handoff)).not.toContain(".mp3");
  });

  it("refuses malformed, duplicated, wrong-kind and oversized reference sets", () => {
    const draft = songDraft();
    const cases: { references: unknown[]; code: string }[] = [
      { references: [{ mediaType: "audio", assetId: "https://cdn.example/a.mp3" }], code: "reference_asset_invalid" },
      { references: [{ mediaType: "audio", assetId: "00000000-0000-0000-0000-000000000000" }], code: "reference_asset_invalid" },
      { references: [{ mediaType: "sheet-music", assetId: audioAssetId }], code: "reference_media_type_invalid" },
      { references: [{ mediaType: "audio", assetId: audioAssetId }, { mediaType: "audio", assetId: audioAssetId.toUpperCase() }], code: "reference_duplicate" },
      {
        references: Array.from({ length: 11 }, (_unused, index) => ({
          mediaType: "audio",
          assetId: `${index.toString(16).padStart(8, "0")}-1111-2222-3333-444444444444`,
        })),
        code: "references_too_many",
      },
    ];
    for (const entry of cases) {
      const issues = refuse(draft, { type: "set_references", references: entry.references });
      expect(issues.map((issue) => issue.code), entry.code).toContain(entry.code);
    }
  });

  it("blocks a reference the snapshot does not admit, per media kind", () => {
    const withVideo = apply(songDraft(), {
      type: "set_references",
      references: [{ mediaType: "audio", assetId: audioAssetId }, { mediaType: "video", assetId: videoAssetId }],
    });
    const audioOnly: MusicGuidedDraftCapabilities = { ...openCapabilities, referenceVideoSupported: false };
    const summary = summarizeMusicGuidedDraft(withVideo, audioOnly);
    expect(summary.steps.find((step) => step.id === "reference")?.status).toBe("blocked");
    expect(summary.blockedReasons).toContain("music.guided.blocked.referenceVideoUnsupported");
    expect(summary.canSubmit).toBe(false);
    expect(summarizeMusicGuidedDraft(withVideo, openCapabilities).canSubmit).toBe(true);
  });

  it("will not record consent without the explicit token", () => {
    const before = songDraft();
    const result = applyMusicGuidedDraftOperation(before, {
      type: "set_consent",
      lyricsRightsConfirmed: true,
      referenceRightsConfirmed: true,
      consentToken: undefined,
    });
    if (result.status !== "rejected") throw new Error("expected rejection");
    expect(result.issues).toEqual([{ code: "consent_token_required", field: "consentToken" }]);
    // The refusal must not mutate consent the user had already given.
    expect(result.draft).toBe(before);
    expect(result.draft.lyricsRights.granted).toBe(true);
  });

  it("records consent metadata only when the caller captured it", () => {
    const applied = apply(songDraft(), {
      type: "set_consent",
      lyricsRightsConfirmed: true,
      referenceRightsConfirmed: true,
      consentToken: MUSIC_GUIDED_CONSENT_TOKEN,
    });
    expect(applied.lyricsRights).toEqual({ granted: true, consentToken: MUSIC_GUIDED_CONSENT_TOKEN, capturedAt: null });
    expect(applied.referenceRights.capturedAt).toBeNull();
  });

  it("re-opens the consent and review steps when lyrics consent is revoked", () => {
    const revoked = apply(songDraft(), {
      type: "set_consent",
      lyricsRightsConfirmed: false,
      referenceRightsConfirmed: true,
      consentToken: MUSIC_GUIDED_CONSENT_TOKEN,
    });
    const summary = summarizeMusicGuidedDraft(revoked, openCapabilities);
    expect(summary.steps.find((step) => step.id === "consent")?.status).toBe("incomplete");
    expect(summary.steps.find((step) => step.id === "review")?.status).toBe("incomplete");
    expect(summary.canSubmit).toBe(false);
    expect(toMusicLyricsDraftInput(revoked, { stage: "preflight" }).ok).toBe(false);
  });

  it("blocks a named-voice draft until voice likeness is actually supported", () => {
    const likeness = apply(songDraft(), {
      type: "set_consent",
      lyricsRightsConfirmed: true,
      referenceRightsConfirmed: true,
      voiceLikenessUsed: true,
      consentToken: MUSIC_GUIDED_CONSENT_TOKEN,
    });
    const summary = summarizeMusicGuidedDraft(likeness, { ...openCapabilities, voiceLikenessSupported: false });
    expect(summary.steps.find((step) => step.id === "vocal")?.status).toBe("blocked");
    expect(summary.blockedReasons).toContain("music.guided.blocked.voiceLikenessUnsupported");
    expect(summary.canSubmit).toBe(false);
  });
});

describe("guided song draft handoff to the PR #802 mapper", () => {
  it("accepts English lyrics without an artificial dialect requirement", () => {
    const english = apply(songDraft(), { type: "set_language", language: "en" });
    expect(resolveMusicGuidedDraftDialect(english)).toBeNull();
    expect(summarizeMusicGuidedDraft(english, openCapabilities).canSubmit).toBe(true);
    const handoff = toMusicLyricsDraftInput(english, { stage: "preflight" });
    expect(handoff.ok).toBe(true);
    expect(handoff.issues).toEqual([]);
    expect(handoff.input).toMatchObject({ vocalLanguage: "en", dialect: null, lyricsRightsConfirmed: true });
  });

  it("produces the mapper input shape only for a fully resolved song draft", () => {
    const handoff = toMusicLyricsDraftInput(songDraft(), { stage: "preflight" });
    expect(handoff.ok).toBe(true);
    expect(Object.keys(handoff.input ?? {}).sort()).toEqual([
      "dialect",
      "lyricsRightsConfirmed",
      "referenceRightsConfirmed",
      "references",
      "sections",
      "vocalLanguage",
    ]);
    expect(handoff.input?.vocalLanguage).toBe("ku");
    expect(handoff.input?.dialect).toBe("sorani_erbil");
    expect(handoff.input?.sections.map((section) => section.type)).toEqual(["verse", "chorus"]);
    // The mapper owns normalization, so raw editor blobs are passed untouched.
    expect(handoff.input?.sections[0]?.text).toBe(soraniLine);
  });

  it("never hands off an instrumental draft as a song", () => {
    const instrumental = apply(songDraft(), { type: "set_creation_mode", creationMode: MUSIC_GUIDED_INSTRUMENTAL_MODE });
    const handoff = toMusicLyricsDraftInput(instrumental, { stage: "preflight" });
    expect(handoff.ok).toBe(false);
    expect(handoff.input).toBeNull();
    expect(handoff.issues).toEqual([{ code: "lyrics_not_applicable", field: "creationMode" }]);
  });

  it("keeps lyric text out of every issue and summary projection", () => {
    const draft = createEmptyMusicGuidedDraft();
    const summary = summarizeMusicGuidedDraft(draft);
    const issues = [
      refuse(draft, { type: "set_sections", sections: [{ id: "a", type: "hook", text: soraniLine }] }),
      refuse(draft, { type: "set_language", language: "xx" }),
      refuse(draft, { type: "set_consent", lyricsRightsConfirmed: true }),
    ];
    // Diagnostics are what a screen or a log would surface; the untouched draft
    // legitimately still holds the user's own words.
    const serialized = JSON.stringify({ summary, issues });
    expect(serialized).not.toContain(soraniLine);
    expect(serialized).not.toMatch(/provider|suno|mubert|bearer|api[_ ]?key/i);
  });

  it("summarizes a finished Sorani song as submittable only when the snapshot allows it", () => {
    const draft = songDraft();
    expect(draft.stepId).toBe("review");
    const summary = summarizeMusicGuidedDraft(draft, openCapabilities);
    expect(summary.canSubmit).toBe(true);
    expect(summary.blockedReasons).toEqual([]);
    expect(summary.nextActionableStepId).toBeNull();
    expect(summary.steps.map((step) => step.id)).toEqual(["mode", "lyrics", "language", "vocal", "reference", "consent", "review"]);
  });

  it("marks reference media optional so an instrumental draft still completes", () => {
    const chosen = apply(createEmptyMusicGuidedDraft(), { type: "set_creation_mode", creationMode: MUSIC_GUIDED_INSTRUMENTAL_MODE });
    const summary = summarizeMusicGuidedDraft(chosen, closedMusicGuidedDraftCapabilities);
    expect(summary.steps.find((step) => step.id === "reference")?.status).toBe("not_required");
    expect(summary.canSubmit).toBe(true);
    expect(summary.disabledFeatures.lyricsSinging).toBeUndefined();
  });
});
