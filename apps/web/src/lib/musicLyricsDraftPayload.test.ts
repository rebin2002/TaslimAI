import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import {
  MUSIC_LYRICS_MAX_LINE_CHARACTERS,
  MUSIC_LYRICS_MAX_LINES_PER_SECTION,
  MUSIC_LYRICS_MAX_SECTIONS,
  MUSIC_LYRICS_MAX_TOTAL_CHARACTERS,
  MUSIC_REFERENCES_MAX_ASSETS,
  buildMusicLyricsDraft,
  splitLyricsDraftLines,
  type MusicLyricsDraftInput,
} from "./musicLyricsDraftPayload";

const source = readFileSync(new URL("./musicLyricsDraftPayload.ts", import.meta.url), "utf8");

const AUDIO_ID = "7c9e6679-7425-40de-944b-e07fc1f90ae7";
const VIDEO_ID = "16fd2706-8baf-433b-82eb-8f7dfee5f463";

/** Sorani draft in the exact editor shape exported by PR #772 (verse/chorus/bridge/outro). */
const soraniSections = [
  { id: "s1", type: "verse" as const, text: "[TEST ONLY] ڕۆژ ئاوا دەبێت" },
  { id: "s2", type: "chorus" as const, text: "[TEST ONLY] ئەڵێم بگەڕێمەوە" },
  { id: "s3", type: "bridge" as const, text: "[TEST ONLY] چیاکان بەردەوامن" },
  { id: "s4", type: "outro" as const, text: "[TEST ONLY] خوا حافیز" },
];

const cleanSoraniInput: MusicLyricsDraftInput = {
  sections: soraniSections,
  vocalLanguage: "ku",
  lyricsRightsConfirmed: true,
};

describe("buildMusicLyricsDraft happy path", () => {
  it("maps a Sorani verse/chorus/bridge/outro draft into the typed song payload", () => {
    const outcome = buildMusicLyricsDraft(cleanSoraniInput);
    expect(outcome.issues).toEqual([]);
    expect(outcome.submittable).toBe(true);
    expect(outcome.payload).not.toBeNull();
    const payload = outcome.payload!;
    // Never a silent instrumental fallback: mode fields are pinned to song-with-vocals.
    expect(payload.creationMode).toBe("song_with_vocals");
    expect(payload.vocalPreference).toBe("vocal");
    expect(payload.lyricsLanguage).toBe("ku");
    expect(payload.lyricsDialect).toBe("sorani");
    expect(payload.lyricsRightsConfirmed).toBe(true);
    expect(payload.referenceAssets).toEqual([]);
    expect(payload.referenceRightsConfirmed).toBe(false);
    expect(payload.lyrics.map((section) => section.section)).toEqual(["verse", "chorus", "bridge", "outro"]);
    expect(payload.lyrics[0]!.lines).toEqual(["[TEST ONLY] ڕۆژ ئاوا دەبێت"]);
    expect(payload.lyrics[2]!.lines).toEqual(["[TEST ONLY] چیاکان بەردەوامن"]);
  });

  it("keeps a clean draft and a clean preflight byte-identical", () => {
    const draft = buildMusicLyricsDraft(cleanSoraniInput, "draft");
    const preflight = buildMusicLyricsDraft(cleanSoraniInput, "preflight");
    expect(preflight).toEqual({ ...draft, stage: "preflight" });
    expect(preflight.submittable).toBe(true);
    expect(preflight.payload).toEqual(draft.payload);
  });

  it("accepts every editor section blob verbatim beyond line boundaries", () => {
    expect(splitLyricsDraftLines("  Hello WORLD\r\n \t\n  سڵاو — بە خێرێك\n  two  spaces  kept\t \ntrailing\t").slice(1)).toEqual([
      "  سڵاو — بە خێرێك",
      "  two  spaces  kept",
      "trailing",
    ]);
    const lines = splitLyricsDraftLines("  Hello WORLD\r\n \t\n  سڵاو — بە خێرێك\n  two  spaces  kept\t \ntrailing\t");
    expect(lines[0]).toBe("  Hello WORLD"); // leading indentation and case preserved verbatim
    const outcome = buildMusicLyricsDraft({
      sections: [{ id: "a", type: "verse", text: lines.join("\n") }],
      vocalLanguage: "en",
      lyricsRightsConfirmed: true,
    });
    expect(outcome.payload!.lyrics[0]!.lines).toEqual(lines);
  });

  it("resolves documented language aliases and explicit dialect overrides", () => {
    expect(
      buildMusicLyricsDraft({ ...cleanSoraniInput, vocalLanguage: " Sorani " }).payload!.lyricsDialect,
    ).toBe("sorani");
    expect(buildMusicLyricsDraft({ ...cleanSoraniInput, vocalLanguage: "ckb" }).payload!.lyricsLanguage).toBe("ku");
    expect(
      buildMusicLyricsDraft({ ...cleanSoraniInput, dialect: "sorani-erbil" }).payload!.lyricsDialect,
    ).toBe("sorani_erbil");
    const arabic = buildMusicLyricsDraft({
      sections: [{ id: "a", type: "chorus", text: "[TEST ONLY] بغداد" }],
      vocalLanguage: "ar",
      dialect: "iraqi_baghdadi",
      lyricsRightsConfirmed: true,
    });
    expect(arabic.issues).toEqual([]);
    expect(arabic.payload!.lyricsLanguage).toBe("ar");
    expect(arabic.payload!.lyricsDialect).toBe("iraqi_baghdadi");
  });

  it("accepts the wider section vocabulary of the shared request contract", () => {
    const outcome = buildMusicLyricsDraft({
      sections: [
        { id: "a", type: "pre_chorus", text: "[TEST ONLY] line one" },
        { id: "b", type: "refrain", text: "[TEST ONLY] line two" },
      ],
      vocalLanguage: "en",
      lyricsRightsConfirmed: true,
    });
    expect(outcome.submittable).toBe(true);
    expect(outcome.payload!.lyrics.map((section) => section.section)).toEqual(["pre_chorus", "refrain"]);
  });
});

describe("buildMusicLyricsDraft fail-closed language and dialect handling", () => {
  it("rejects vocal selections outside the supported Sorani/Arabic/English set", () => {
    for (const selection of ["auto", "fr", "zh-CN", "", null]) {
      const outcome = buildMusicLyricsDraft({ ...cleanSoraniInput, vocalLanguage: selection });
      expect(outcome.submittable).toBe(false);
      expect(outcome.payload).toBeNull();
      expect(outcome.issues.filter((issue) => issue.code === "language_unsupported")).toHaveLength(1);
    }
  });

  it("requires an Arabic dialect instead of guessing one, and no English dialect", () => {
    const noDialect = buildMusicLyricsDraft({
      sections: soraniSections,
      vocalLanguage: "ar",
      lyricsRightsConfirmed: true,
    });
    expect(noDialect.issues).toEqual([{ code: "dialect_required", params: { lyricsLanguage: "ar" } }]);
    expect(noDialect.payload).toBeNull();

    const englishWithDialect = buildMusicLyricsDraft({
      sections: [{ id: "a", type: "verse", text: "[TEST ONLY] line" }],
      vocalLanguage: "en",
      dialect: "gulf_arabic",
      lyricsRightsConfirmed: true,
    });
    expect(englishWithDialect.issues).toEqual([{ code: "dialect_not_allowed", params: { lyricsLanguage: "en" } }]);
    expect(englishWithDialect.payload).toBeNull();
  });

  it("fails closed on unknown and cross-language dialect tokens", () => {
    const unknown = buildMusicLyricsDraft({ ...cleanSoraniInput, dialect: "unknown_dialect" });
    expect(unknown.issues).toEqual([{ code: "dialect_unknown", params: { lyricsLanguage: "ku" } }]);
    expect(unknown.payload).toBeNull();

    const mismatched = buildMusicLyricsDraft({ ...cleanSoraniInput, dialect: "iraqi_baghdadi" });
    expect(mismatched.issues).toEqual([{ code: "dialect_mismatch", params: { lyricsLanguage: "ku" } }]);
    expect(mismatched.payload).toBeNull();
  });

  it("blocks submission until lyrics rights are attested", () => {
    const outcome = buildMusicLyricsDraft({ ...cleanSoraniInput, lyricsRightsConfirmed: false });
    expect(outcome.submittable).toBe(false);
    expect(outcome.issues).toEqual([{ code: "lyrics_rights_required", params: {} }]);
    expect(outcome.payload).not.toBeNull(); // draft stage still shapes the payload
    expect(buildMusicLyricsDraft({ ...cleanSoraniInput, lyricsRightsConfirmed: false }, "preflight").payload).toBeNull();
  });
});

describe("buildMusicLyricsDraft bounds", () => {
  it("mirrors the shared contract bounds without truncating any lyric text", () => {
    const tooManySections = buildMusicLyricsDraft({
      sections: Array.from({ length: MUSIC_LYRICS_MAX_SECTIONS + 1 }, (_, index) => ({
        id: `s${index}`,
        type: "verse" as const,
        text: "ok",
      })),
      vocalLanguage: "en",
      lyricsRightsConfirmed: true,
    });
    expect(tooManySections.issues).toEqual([
      { code: "sections_too_many", params: { maxSections: MUSIC_LYRICS_MAX_SECTIONS, actual: MUSIC_LYRICS_MAX_SECTIONS + 1 } },
    ]);
    expect(tooManySections.payload!.lyrics).toHaveLength(MUSIC_LYRICS_MAX_SECTIONS + 1);

    const tooManyLines = buildMusicLyricsDraft({
      sections: [{ id: "a", type: "verse", text: Array.from({ length: MUSIC_LYRICS_MAX_LINES_PER_SECTION + 1 }, () => "x").join("\n") }],
      vocalLanguage: "en",
      lyricsRightsConfirmed: true,
    });
    expect(tooManyLines.issues).toEqual([
      {
        code: "lines_too_many",
        sectionId: "a",
        params: { sectionOrdinal: 1, maxLinesPerSection: MUSIC_LYRICS_MAX_LINES_PER_SECTION, actual: MUSIC_LYRICS_MAX_LINES_PER_SECTION + 1 },
      },
    ]);

    const longLine = "k".repeat(MUSIC_LYRICS_MAX_LINE_CHARACTERS + 1);
    const tooLong = buildMusicLyricsDraft({
      sections: [{ id: "a", type: "verse", text: longLine }],
      vocalLanguage: "en",
      lyricsRightsConfirmed: true,
    });
    expect(tooLong.issues).toEqual([
      { code: "line_too_long", sectionId: "a", params: { sectionOrdinal: 1, maxLineCharacters: MUSIC_LYRICS_MAX_LINE_CHARACTERS, actual: longLine.length } },
    ]);
    expect(tooLong.payload!.lyrics[0]!.lines[0]).toBe(longLine); // verbatim, never truncated

    const totalOver = buildMusicLyricsDraft({
      sections: [
        { id: "a", type: "verse", text: Array.from({ length: 18 }, () => "k".repeat(MUSIC_LYRICS_MAX_LINE_CHARACTERS)).join("\n") },
      ],
      vocalLanguage: "en",
      lyricsRightsConfirmed: true,
    });
    expect(totalOver.issues).toEqual([
      {
        code: "lyrics_total_too_long",
        params: {
          maxTotalCharacters: MUSIC_LYRICS_MAX_TOTAL_CHARACTERS,
          actual: 18 * MUSIC_LYRICS_MAX_LINE_CHARACTERS,
        },
      },
    ]);
    expect(totalOver.submittable).toBe(false);
  });

  it("fails closed on missing, empty or malformed structure", () => {
    expect(buildMusicLyricsDraft({ vocalLanguage: "ku", lyricsRightsConfirmed: true }).issues).toEqual([
      { code: "sections_missing", params: {} },
    ]);
    expect(buildMusicLyricsDraft({ sections: [], vocalLanguage: "ku", lyricsRightsConfirmed: true }).payload).toBeNull();
    const malformed = buildMusicLyricsDraft({
      sections: [
        null as never,
        { id: "hook1", type: "hook", text: "[TEST ONLY] unsupported type" },
        { id: "blank", type: "verse", text: "   \n\t  " },
      ],
      vocalLanguage: "ku",
      lyricsRightsConfirmed: true,
    });
    expect(malformed.issues).toEqual([
      { code: "section_type_invalid", params: { sectionOrdinal: 1 } },
      { code: "section_type_invalid", sectionId: "hook1", params: { sectionOrdinal: 2 } },
      { code: "section_empty", sectionId: "blank", params: { sectionOrdinal: 3 } },
    ]);
    expect(malformed.payload).toBeNull(); // nothing faithfully retained
  });
});

describe("buildMusicLyricsDraft reference mapping stays private and fail-closed", () => {
  it("maps only media type plus canonical asset ids", () => {
    const outcome = buildMusicLyricsDraft({
      ...cleanSoraniInput,
      references: [
        { mediaType: "Audio", assetId: AUDIO_ID },
        { mediaType: "video", assetId: VIDEO_ID.toUpperCase() },
      ],
      referenceRightsConfirmed: true,
    });
    expect(outcome.issues).toEqual([]);
    expect(outcome.payload!.referenceAssets).toEqual([
      { mediaType: "audio", assetId: AUDIO_ID },
      { mediaType: "video", assetId: VIDEO_ID },
    ]);
    expect(outcome.payload!.referenceRightsConfirmed).toBe(true);
  });

  it("rejects URLs, placeholders, duplicates and unsupported media types", () => {
    const outcome = buildMusicLyricsDraft({
      ...cleanSoraniInput,
      references: [
        { mediaType: "url", assetId: AUDIO_ID },
        { mediaType: "audio", assetId: "https://cdn.example.com/reference.mp3" },
        { mediaType: "audio", assetId: null },
        { mediaType: "audio", assetId: "00000000-0000-0000-0000-000000000000" },
        { mediaType: "audio", assetId: AUDIO_ID },
        { mediaType: "video", assetId: AUDIO_ID.toUpperCase() },
      ],
      referenceRightsConfirmed: true,
    });
    expect(outcome.issues).toEqual([
      { code: "reference_asset_invalid", params: { referenceOrdinal: 1, reason: "media_type" } },
      { code: "reference_asset_invalid", params: { referenceOrdinal: 2, reason: "asset_id" } },
      { code: "reference_asset_invalid", params: { referenceOrdinal: 3, reason: "asset_id" } },
      { code: "reference_asset_invalid", params: { referenceOrdinal: 4, reason: "asset_id" } },
      { code: "reference_duplicate", params: { referenceOrdinal: 6 } },
    ]);
    expect(outcome.payload!.referenceAssets).toEqual([{ mediaType: "audio", assetId: AUDIO_ID }]);
    expect(outcome.submittable).toBe(false);
    expect(buildMusicLyricsDraft(
      {
        ...cleanSoraniInput,
        references: [
          { mediaType: "url", assetId: AUDIO_ID },
          { mediaType: "audio", assetId: "https://cdn.example.com/reference.mp3" },
          { mediaType: "audio", assetId: null },
          { mediaType: "audio", assetId: "00000000-0000-0000-0000-000000000000" },
          { mediaType: "audio", assetId: AUDIO_ID },
          { mediaType: "video", assetId: AUDIO_ID.toUpperCase() },
        ],
        referenceRightsConfirmed: true,
      },
      "preflight",
    ).payload).toBeNull();
  });

  it("enforces the reference bound and both rights directions", () => {
    const many = buildMusicLyricsDraft({
      ...cleanSoraniInput,
      references: Array.from({ length: MUSIC_REFERENCES_MAX_ASSETS + 1 }, (_, index) => {
        const hex = index.toString(16);
        return { mediaType: "audio", assetId: `0000000${hex}-0000-4000-8000-00000000000${hex}` };
      }),
      referenceRightsConfirmed: true,
    });
    expect(many.issues).toEqual([
      {
        code: "references_too_many",
        params: { maxReferences: MUSIC_REFERENCES_MAX_ASSETS, actual: MUSIC_REFERENCES_MAX_ASSETS + 1 },
      },
    ]);

    const noRights = buildMusicLyricsDraft({
      ...cleanSoraniInput,
      references: [{ mediaType: "audio", assetId: AUDIO_ID }],
      referenceRightsConfirmed: false,
    });
    expect(noRights.issues).toEqual([{ code: "reference_rights_required", params: {} }]);

    const rightsWithoutAssets = buildMusicLyricsDraft({
      ...cleanSoraniInput,
      references: [],
      referenceRightsConfirmed: true,
    });
    expect(rightsWithoutAssets.issues).toEqual([{ code: "reference_rights_without_assets", params: {} }]);
  });
});

describe("buildMusicLyricsDraft privacy and determinism", () => {
  it("never repeats lyric content inside issues and keeps issues free of raw user text", () => {
    const outcome = buildMusicLyricsDraft({
      sections: [
        { id: "a", type: "hook", text: "[TEST ONLY] ڕۆژئاوا نه‌هاوه‌ڕێنراوه‌" },
        { id: "b", type: "verse", text: "k".repeat(MUSIC_LYRICS_MAX_LINE_CHARACTERS + 1) },
      ],
      vocalLanguage: "ar",
      dialect: "gulf_arabic",
      lyricsRightsConfirmed: true,
      references: [{ mediaType: "audio", assetId: AUDIO_ID }],
      referenceRightsConfirmed: true,
    });
    const serializedIssues = JSON.stringify(outcome.issues);
    for (const phrase of ["ڕۆژ", "نه‌هاوه‌ڕێنراوه‌", "[TEST ONLY]"]) {
      expect(serializedIssues).not.toContain(phrase);
    }
    // The payload exposes only the fixed contract fields; no URL, file name or
    // storage metadata can ride along, and lyric text stays in lyrics lines only.
    expect(Object.keys(outcome.payload!)).toEqual([
      "creationMode",
      "vocalPreference",
      "lyrics",
      "lyricsLanguage",
      "lyricsDialect",
      "lyricsRightsConfirmed",
      "referenceAssets",
      "referenceRightsConfirmed",
    ]);
    expect(Object.keys(outcome.payload!.lyrics[0]!)).toEqual(["section", "lines"]);
    expect(Object.keys(outcome.payload!.referenceAssets[0]!)).toEqual(["mediaType", "assetId"]);
    const serializedPayload = JSON.stringify(outcome.payload);
    expect(serializedPayload).not.toContain("://");
  });

  it("is deterministic, side-effect free and deeply immutable", () => {
    const input: MusicLyricsDraftInput = {
      sections: soraniSections,
      vocalLanguage: "ku",
      lyricsRightsConfirmed: true,
      references: [{ mediaType: "audio", assetId: AUDIO_ID }],
      referenceRightsConfirmed: true,
    };
    const snapshot = structuredClone(input);
    const first = buildMusicLyricsDraft(input);
    const second = buildMusicLyricsDraft(input, "preflight");
    expect(input).toEqual(snapshot); // never mutates caller state
    expect(second.payload).toEqual({ ...first.payload! });
    expect(first.issues).toEqual([]);
    expect(() => {
      (first as { submittable: boolean }).submittable = true;
    }).toThrow(TypeError);
    expect(() => {
      (first.payload!.lyrics[0]!.lines as string[]).push("mutated");
    }).toThrow(TypeError);
  });
});

describe("buildMusicLyricsDraft scope guard", () => {
  it("is provider-free: no network, no capability claims, no randomness", () => {
    expect(source).not.toMatch(/suno|mubert|stable[- _]?audio|lyria|capability[- ]matrix/i);
    expect(source).not.toMatch(/\bfetch\s*\(|XMLHttpRequest|WebSocket|navigator\.|localStorage|sessionStorage|indexedDB/);
    expect(source).not.toMatch(/import .*from .*["']@\/lib\/(api|i18n)["']/);
    expect(source).not.toMatch(/generateLyrics|generateSong|startGeneration|createGenerationJob/);
    expect(source).not.toMatch(/Math\.random|Date\.now|performance\.now|new Date/);
    expect(source).not.toMatch(/\bwindow\b|\bdocument\b/);
  });

  it("emits song mode only — the instrumental path is never silently substituted", () => {
    for (const input of [
      cleanSoraniInput,
      { ...cleanSoraniInput, lyricsRightsConfirmed: false },
      { ...cleanSoraniInput, vocalLanguage: "Sorani" },
    ]) {
      const payload = buildMusicLyricsDraft(input).payload;
      if (payload) {
        expect(payload.creationMode).toBe("song_with_vocals");
        expect(payload.vocalPreference).toBe("vocal");
      }
    }
  });
});
