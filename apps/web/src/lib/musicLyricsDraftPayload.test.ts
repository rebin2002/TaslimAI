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

describe("buildMusicLyricsDraft issue payloads never echo draft-local identifiers as content", () => {
  // The module documents `id` as a draft-local identifier that "never leaves the
  // payload", and the issue array as containing "never ... lyric text, dialect
  // copy or reference file names". The rest of the Music Studio family builds
  // identifiers straight out of private user content — `localReferenceAudioId`
  // is `local:<size>:<file name>` and `localReferenceVideoId` is
  // `local-video:<size>:<file name>` — and the guided handoff copies a caller's
  // `id` verbatim, so an unbounded identifier is a content channel into the
  // failure payload. Only opaque tokens may survive.
  const rejects = (sections: unknown[]) =>
    buildMusicLyricsDraft({ sections, vocalLanguage: "ku", lyricsRightsConfirmed: true } as MusicLyricsDraftInput).issues;

  it("drops an identifier shaped like a private reference file name", () => {
    const issues = rejects([{ id: "local:2048:MySecretStudioTake.mp3", type: "hook", text: "[TEST ONLY] ڕۆژ ئاوا دەبێت" }]);
    expect(issues).toEqual([{ code: "section_type_invalid", params: { sectionOrdinal: 1 } }]);
    expect(JSON.stringify(issues)).not.toContain("MySecretStudioTake");
    expect(JSON.stringify(issues)).not.toContain(".mp3");
  });

  it("drops an identifier derived from the lyric text itself", () => {
    const lyric = "[TEST ONLY] ئەڵێم بگەڕێمەوە";
    const issues = rejects([{ id: lyric, type: "hook", text: lyric }]);
    expect(issues).toEqual([{ code: "section_type_invalid", params: { sectionOrdinal: 1 } }]);
    expect(JSON.stringify(issues)).not.toContain("ئەڵێم");
  });

  it("drops an over-long identifier instead of truncating it into the payload", () => {
    const longId = `s-${"k".repeat(120)}`;
    const issues = rejects([{ id: longId, type: "verse", text: "x".repeat(201) }]);
    expect(issues.map((issue) => issue.code)).toContain("line_too_long");
    for (const issue of issues) expect(JSON.stringify(issue)).not.toContain(longId);
  });

  it("drops identifiers carrying control, zero-width or bidi format characters", () => {
    for (const id of ["s1\u0000s2", "ho\u200BKst", "verse\u202E", "s1\ns2", "s1\t"]) {
      for (const issue of rejects([{ id, type: "hook", text: "[TEST ONLY] line" }]))
        expect(JSON.stringify(issue)).not.toContain(id);
    }
  });

  it("keeps opaque draft-local tokens so existing rows still address their section", () => {
    const issues = rejects([
      { id: "a", type: "verse", text: "x".repeat(201) },
      { id: "hook1", type: "hook", text: "[TEST ONLY] line" },
      { id: "section-3", type: "chorus", text: "   \n\t " },
      { id: "row_7", type: "hook", text: "[TEST ONLY] unsupported type" },
    ]);
    expect(issues.map((issue) => issue.sectionId)).toEqual(["a", "hook1", "section-3", "row_7"]);
  });

  it("mirrors the reference channel, which already refuses to echo the offending value", () => {
    const outcome = buildMusicLyricsDraft({
      sections: [{ id: "local:2048:MySecretStudioTake.mp3", type: "hook", text: "[TEST ONLY] line" }],
      vocalLanguage: "ku",
      lyricsRightsConfirmed: true,
      references: [{ mediaType: "audio", assetId: "https://private.example/MySecretStudioTake.mp3" }],
      referenceRightsConfirmed: true,
    } as MusicLyricsDraftInput);
    const serialized = JSON.stringify(outcome.issues);
    expect(serialized).not.toContain("private.example");
    expect(serialized).not.toContain("MySecretStudioTake");
  });

  it("keeps the guarantee in the source, not only in the assertion", () => {
    expect(source).toMatch(/isSafeDraftSectionId|safeDraftSectionId/);
    expect(source).toMatch(/export const MUSIC_LYRICS_MAX_SECTION_ID_CHARACTERS = 64/);
  });
});

describe("buildMusicLyricsDraft Arabic and Sorani synthetic rejection payloads stay content-free", () => {
  // The rejection payload's privacy claim is script-independent, but the corpus
  // that exercises it was Latin-only plus one incidental Sorani line. Arabic and
  // Sorani are the two RTL contract languages (#708) and the realistic private
  // content in this product's lyric drafts, so an identifier formed from them —
  // with diacritics, Sorani-only letters or bidi embeds — needs its own lock:
  // every one of these words must reach the payload as zero bytes. All material
  // here is synthetic test copy; it quotes no user data.
  const arabicLine = "[TEST ONLY] لَا تُعِدْ كِتَابَةَ هَذَا السَّطْر";
  const soraniLine = "[TEST ONLY] ئەم دێڕە تەنها بۆ تاقیکردنەوەیە";
  const soraniGlyphLine = "[TEST ONLY] ڕەڵاڤۆژێ کێشچەرەپگ"; // ڕ ڵ ۆ ژ ێ ە چ پ گ
  const arabicDialectCopy = "عِرَاقِي";
  const arabicFileName = "أغنية_السر";
  const soraniFileName = "نهێنی_ستۆدیۆ";

  const rejects = (input: Partial<MusicLyricsDraftInput>) =>
    buildMusicLyricsDraft({ lyricsRightsConfirmed: true, vocalLanguage: "ku", ...input } as MusicLyricsDraftInput)
      .issues;

  const containsNonAscii = (value: string) => Array.from(value).some((ch) => ch.codePointAt(0)! > 0x7f);

  it("drops an Arabic lyric line used as the section identifier", () => {
    const issues = rejects({ sections: [{ id: arabicLine, type: "hook", text: soraniLine }] });
    expect(issues.length).toBeGreaterThan(0);
    for (const issue of issues) {
      expect(issue.sectionId).toBeUndefined();
      expect(JSON.stringify(issue)).not.toContain("تُعِدْ");
    }
  });

  it("drops a Sorani identifier built from the script's own letters", () => {
    const issues = rejects({
      sections: [
        { id: soraniGlyphLine.replace("[TEST ONLY] ", ""), type: "chorus", text: soraniGlyphLine },
        { id: "بەیانی_نەهێشتن", type: "bridge", text: "   " },
      ],
    });
    expect(issues.map((issue) => issue.code)).toEqual(["section_empty"]);
    for (const issue of issues) expect(JSON.stringify(issue)).not.toContain("نەهێشتن");
  });

  it("drops identifiers shaped like private Arabic or Sorani reference file names", () => {
    const issues = rejects({
      sections: [
        { id: `local:4096:${arabicFileName}.mp3`, type: "hook", text: arabicLine },
        { id: `local-video:8192:${soraniFileName}.mp4`, type: "hook", text: soraniLine },
      ],
    });
    const serialized = JSON.stringify(issues);
    expect(issues.map((issue) => issue.code)).toEqual(["section_type_invalid", "section_type_invalid"]);
    expect(serialized).not.toContain(arabicFileName);
    expect(serialized).not.toContain(soraniFileName);
    expect(serialized).not.toContain(".mp3");
    expect(serialized).not.toContain(".mp4");
  });

  it("drops identifiers carrying bidi embeds or overrides around Arabic words", () => {
    for (const id of [`\u202b${arabicFileName}\u202c`, `\u202eڕۆژ\u202c`, `s1\u200f\u2026${soraniFileName}`]) {
      for (const issue of rejects({ sections: [{ id, type: "hook", text: "x" }] }))
        expect(JSON.stringify(issue)).not.toContain(id.trim());
    }
  });

  it("reports dialect failures without the dialect copy itself", () => {
    const unknown = rejects({ vocalLanguage: "ar", dialect: arabicDialectCopy, sections: [{ id: "s1", type: "verse", text: "x" }] });
    expect(unknown.map((issue) => issue.code)).toContain("dialect_unknown");
    expect(JSON.stringify(unknown)).not.toContain(arabicDialectCopy);

    // Arabic has no default dialect by design; the requirement is reported as a
    // code, never as Arabic-script prose.
    const required = rejects({ vocalLanguage: "ar", sections: [{ id: "s1", type: "verse", text: "x" }] });
    expect(required.map((issue) => issue.code)).toContain("dialect_required");
    expect(JSON.stringify(required)).not.toMatch(/[؀-ۿ]/);
  });

  it("keeps every issue record pure ASCII across a full Arabic/Sorani violation sweep", () => {
    // Structural lock, independent of any single word list: an issue may carry
    // codes, ordinals, numeric limits and opaque ASCII tokens — nothing else.
    // Non-ASCII inside `params` values is excluded by codepoint, so no lyric,
    // dialect or file-name fragment in any script can ride along.
    const sections = [
      { id: arabicLine, type: "verse", text: "x".repeat(201) },
      { id: soraniLine, type: "chorus", text: Array.from({ length: 31 }, (_, i) => `l${i} ${soraniGlyphLine}`).join("\n") },
      { id: "local:4096:" + arabicFileName + ".mp3", type: "hook", text: soraniLine },
      { id: "بەیانی", type: "bridge", text: arabicLine },
    ];
    const issues = [
      ...rejects({ sections, vocalLanguage: "ckb" }),
      ...rejects({ sections, vocalLanguage: "ar", dialect: arabicDialectCopy }),
      ...rejects({ sections, vocalLanguage: "nope" }),
    ];
    expect(issues.length).toBeGreaterThan(8);
    for (const issue of issues) expect(containsNonAscii(JSON.stringify(issue))).toBe(false);
    for (const word of ["تُعِدْ", "دێڕە", "ڕەڵاڤۆژێ", "السر", "ستۆدیۆ", arabicDialectCopy])
      expect(JSON.stringify(issues)).not.toContain(word);
  });

  it("leaves legitimate Arabic/Sorani payload content untouched — the filter guards identifiers, not lyrics", () => {
    const arabic = buildMusicLyricsDraft({
      sections: [{ id: "s1", type: "verse", text: arabicLine }],
      vocalLanguage: "ar",
      dialect: "iraqi_baghdadi",
      lyricsRightsConfirmed: true,
    } as MusicLyricsDraftInput);
    expect(arabic.issues).toEqual([]);
    expect(arabic.submittable).toBe(true);
    expect(arabic.payload!.lyrics[0].lines).toEqual([arabicLine]);
    expect(arabic.payload!.lyricsLanguage).toBe("ar");
    expect(arabic.payload!.lyricsDialect).toBe("iraqi_baghdadi");

    const sorani = buildMusicLyricsDraft({
      sections: [{ id: "s1", type: "verse", text: soraniGlyphLine }],
      vocalLanguage: "ckb",
      lyricsRightsConfirmed: true,
    } as MusicLyricsDraftInput);
    expect(sorani.issues).toEqual([]);
    expect(sorani.payload!.lyricsLanguage).toBe("ku");
    expect(sorani.payload!.lyricsDialect).toBe("sorani");
    expect(sorani.payload!.lyrics[0].lines).toEqual([soraniGlyphLine]);
  });
});
