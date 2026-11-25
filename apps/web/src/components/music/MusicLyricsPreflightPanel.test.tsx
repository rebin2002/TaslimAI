import { readFileSync } from "node:fs";
import type { ReactElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import {
  MUSIC_LYRICS_SONG_CREATION_MODE,
  MUSIC_LYRICS_SONG_VOCAL_PREFERENCE,
} from "@/lib/musicLyricsDraftPayload";
import {
  decideMusicLyricsPreflightHandoff,
  defaultMusicLyricsPreflightLabels,
  evaluateMusicLyricsPreflight,
  explainMusicLyricsDraftDialect,
  musicLyricsPreflightGateIssueCode,
  musicLyricsVisibleGlyphCount,
  MusicLyricsPreflightPanel,
  resolveMusicLyricsDraftDialect,
  type MusicLyricsPreflightInput,
} from "./MusicLyricsPreflightPanel";

const assetId = "3f2a6b48-1d0e-5a7c-9b3f-2c4d6e8a0b1c";
const soraniVerse = "بەینەوتنی شةوعیەوە دەست پێکات\nدڵم بە خۆشەویستی پڕە";
const soraniChorus = "ئەی ڕۆژهەڵات، ئەی نیشتمانی ئازیز";

function input(overrides: Partial<MusicLyricsPreflightInput> = {}): MusicLyricsPreflightInput {
  return {
    sections: [
      { id: "s1", type: "verse", text: soraniVerse },
      { id: "s2", type: "chorus", text: soraniChorus },
    ],
    vocalLanguage: "ku",
    lyricsRightsConfirmed: true,
    ...overrides,
  };
}

const arabicVerse = "يا سلام على الصباح";

/** Visible glyphs in a fixture: everything but spaces and line breaks. */
const visibleOf = (text: string): number => [...text].filter((character) => !/\s/.test(character)).length;

describe("Sorani lyrics preflight happy path", () => {
  it("admits a complete Sorani draft and shapes the pinned song-mode payload", () => {
    const result = evaluateMusicLyricsPreflight(input());
    expect(result.language).toBe("ku");
    expect(result.dialect).toBe("sorani");
    expect(result.issues).toEqual([]);
    expect(result.admissible).toBe(true);
    expect(result.blockedReason).toBe(null);
    expect(result.payload).not.toBe(null);
    expect(result.payload?.creationMode).toBe(MUSIC_LYRICS_SONG_CREATION_MODE);
    expect(result.payload?.vocalPreference).toBe(MUSIC_LYRICS_SONG_VOCAL_PREFERENCE);
    expect(result.payload?.lyricsLanguage).toBe("ku");
    expect(result.payload?.lyricsDialect).toBe("sorani");
    expect(result.payload?.lyrics.map((section) => section.section)).toEqual(["verse", "chorus"]);
    expect(result.payload?.lyrics[0].lines).toEqual(soraniVerse.split("\n"));
  });

  it("accepts the studio's Kurdish code alias but never a dialect token as a language", () => {
    for (const alias of ["ku", "ckb", "CKB", "ku "]) {
      const result = evaluateMusicLyricsPreflight(input({ vocalLanguage: alias }));
      expect(result.language, alias).toBe("ku");
      expect(result.dialect, alias).toBe("sorani");
      expect(result.admissible, alias).toBe(true);
    }
    // Mirrors the contract vocabulary: `sorani` is a dialect, not a language.
    const asLanguage = evaluateMusicLyricsPreflight(input({ vocalLanguage: "Sorani" }));
    expect(asLanguage.language).toBe(null);
    expect(asLanguage.blockedReason).toBe("language_unsupported");
  });

  it("keeps Sorani regional dialect tokens valid for Kurdish and rejects foreign tokens", () => {
    expect(evaluateMusicLyricsPreflight(input({ dialect: "sorani_erbil" })).dialect).toBe("sorani_erbil");
    expect(evaluateMusicLyricsPreflight(input({ dialect: "sorani-sulaymaniyah" })).dialect).toBe(
      "sorani_sulaymaniyah",
    );
    const mismatch = evaluateMusicLyricsPreflight(input({ dialect: "iraqi_baghdadi" }));
    expect(mismatch.admissible).toBe(false);
    expect(mismatch.blockedReason).toBe("dialect_mismatch");
    const unknown = evaluateMusicLyricsPreflight(input({ dialect: "hkodi" }));
    expect(unknown.admissible).toBe(false);
    expect(unknown.blockedReason).toBe("dialect_unknown");
  });

  it("reports the lyric script direction and tag the editor must bind", () => {
    const sorani = evaluateMusicLyricsPreflight(input());
    expect(sorani.direction).toBe("rtl");
    expect(sorani.languageTag).toBe("ckb");
    const english = evaluateMusicLyricsPreflight(
      input({ vocalLanguage: "en", sections: [{ id: "s1", type: "verse", text: "Morning light on the citadel" }] }),
    );
    expect(english.direction).toBe("ltr");
    expect(english.languageTag).toBe("en");
    expect(english.dialect).toBe(null);
    expect(english.admissible).toBe(true);
  });
});

describe("Arabic dialect selection fails closed", () => {
  it("refuses to invent a default dialect for Arabic", () => {
    const unqualified = input({ vocalLanguage: "ar", sections: [{ id: "s1", type: "verse", text: arabicVerse }] });
    const result = evaluateMusicLyricsPreflight(unqualified);
    expect(result.language).toBe("ar");
    expect(result.dialect).toBe(undefined);
    expect(result.admissible).toBe(false);
    expect(result.blockedReason).toBe("dialect_required");
    expect(result.payload).toBe(null);
    expect(decideMusicLyricsPreflightHandoff(unqualified).handoff).toBe(false);
  });

  it("admits an explicit Iraqi dialect and rejects a Sorani token on Arabic", () => {
    const iraqi = evaluateMusicLyricsPreflight(
      input({
        vocalLanguage: "ar",
        dialect: "iraqi_baghdadi",
        sections: [{ id: "s1", type: "verse", text: arabicVerse }],
      }),
    );
    expect(iraqi.admissible).toBe(true);
    expect(iraqi.dialect).toBe("iraqi_baghdadi");
    expect(iraqi.languageTag).toBe("ar");
    expect(iraqi.direction).toBe("rtl");
    const wrongLanguage = evaluateMusicLyricsPreflight(
      input({
        vocalLanguage: "ar",
        dialect: "sorani",
        sections: [{ id: "s1", type: "verse", text: arabicVerse }],
      }),
    );
    expect(wrongLanguage.blockedReason).toBe("dialect_mismatch");
  });

  it("never lets English carry a dialect", () => {
    expect(resolveMusicLyricsDraftDialect("en", undefined)).toBe(null);
    expect(resolveMusicLyricsDraftDialect("en", "levantine_arabic")).toBe(undefined);
    expect(explainMusicLyricsDraftDialect("ku", undefined)).toEqual({ dialect: "sorani", issueCode: null });
    expect(explainMusicLyricsDraftDialect("ar", undefined)).toEqual({
      dialect: undefined,
      issueCode: "dialect_required",
    });
    const result = evaluateMusicLyricsPreflight(
      input({ vocalLanguage: "en", dialect: "iraqi_mosul", sections: [{ id: "s1", type: "verse", text: "hello there" }] }),
    );
    expect(result.blockedReason).toBe("dialect_not_allowed");
  });

  it("rejects an unrecognized vocal language outright", () => {
    const result = evaluateMusicLyricsPreflight(input({ vocalLanguage: "tr" }));
    expect(result.language).toBe(null);
    expect(result.admissible).toBe(false);
    expect(result.blockedReason).toBe("language_unsupported");
    expect(result.issues.some((issue) => issue.code === "language_unsupported")).toBe(true);
  });
});

describe("visible-glyph admission gate", () => {
  it("counts Sorani and Arabic letters and ignores controls, marks alone, and spaces", () => {
    expect(musicLyricsVisibleGlyphCount("دڵم")).toBe(3);
    expect(musicLyricsVisibleGlyphCount(arabicVerse)).toBe(visibleOf(arabicVerse));
    expect(musicLyricsVisibleGlyphCount("\u064E\u0650")).toBe(2);
    expect(musicLyricsVisibleGlyphCount("   ")).toBe(0);
    expect(musicLyricsVisibleGlyphCount("\n\t ")).toBe(0);
    expect(musicLyricsVisibleGlyphCount(null)).toBe(0);
    expect(musicLyricsVisibleGlyphCount(undefined)).toBe(0);
  });

  it("blocks a draft whose lyric text is only bidi controls and zero-width characters", () => {
    const invisible = "\u200F\u061C\u200E\u202A\u200B\u200C\u202C\u200F";
    expect(musicLyricsVisibleGlyphCount(invisible)).toBe(0);
    const result = evaluateMusicLyricsPreflight(input({ sections: [{ id: "ghost", type: "chorus", text: invisible }] }));
    expect(result.invisibleOnlyLyrics).toBe(true);
    expect(result.totalCharacters).toBe(invisible.length);
    expect(result.totalVisibleGlyphs).toBe(0);
    expect(result.admissible).toBe(false);
    expect(result.blockedReason).toBe(musicLyricsPreflightGateIssueCode);
    expect(result.gateIssues).toHaveLength(1);
    expect(result.gateIssues[0].sectionId).toBe("ghost");
    expect(result.payload).toBe(null);
    expect(decideMusicLyricsPreflightHandoff(
      input({ sections: [{ id: "ghost", type: "chorus", text: invisible }] }),
    ).handoff).toBe(false);
  });

  it("blocks even when every invisible character survives the contract line splitter", () => {
    const zeroWidth = "\u200B\u200B\u200B\n\u200C\u200C\u200C";
    const result = evaluateMusicLyricsPreflight(input({ sections: [{ id: "zw", type: "verse", text: zeroWidth }] }));
    // The mirrored mapper admits these lines; only the gate stops them.
    expect(result.sections[0].lineCount).toBe(2);
    expect(result.sections[0].usable).toBe(true);
    expect(result.issues.filter((issue) => issue.code !== musicLyricsPreflightGateIssueCode)).toEqual([]);
    expect(result.admissible).toBe(false);
    expect(result.blockedReason).toBe(musicLyricsPreflightGateIssueCode);
  });

  it("still admits real lyrics that merely contain bidi marks", () => {
    const marked = `\u200F${soraniVerse}\u202C`;
    const result = evaluateMusicLyricsPreflight(input({ sections: [{ id: "mixed", type: "verse", text: marked }] }));
    expect(result.admissible).toBe(true);
    expect(result.gateIssues).toEqual([]);
    expect(result.totalVisibleGlyphs).toBe(visibleOf(soraniVerse));
  });

  it("separates a blank row, which the contract already rejects, from an invisible row", () => {
    // The contract splitter drops a line that trims to nothing, so a blank row
    // is the mapper's own violation and it keeps precedence over the gate.
    const blank = evaluateMusicLyricsPreflight(input({ sections: [{ id: "blank", type: "verse", text: "   " }] }));
    expect(blank.sections[0].usable).toBe(false);
    expect(blank.sections[0].lineCount).toBe(0);
    expect(blank.sections[0].visibleGlyphCount).toBe(0);
    expect(blank.blockedReason).toBe("section_empty");
    expect(blank.gateIssues).toHaveLength(1);
  });

  it("reports an empty row as the contract's own empty-section violation", () => {
    const result = evaluateMusicLyricsPreflight(input({ sections: [{ id: "none", type: "verse", text: "" }] }));
    expect(result.sections[0].usable).toBe(false);
    expect(result.sections[0].invisibleOnly).toBe(false);
    expect(result.blockedReason).toBe("section_empty");
  });
});

describe("mirrored limits and rights", () => {
  it("flags an over-long draft with the mirrored total limit", () => {
    const line = "د".repeat(200);
    const sections = Array.from({ length: 18 }, (_, index) => ({
      id: `s${index}`,
      type: "verse",
      text: Array.from({ length: 10 }, () => line).join("\n"),
    }));
    const result = evaluateMusicLyricsPreflight(input({ sections }));
    expect(result.admissible).toBe(false);
    expect(result.issues.some((issue) => issue.code === "lyrics_total_too_long")).toBe(true);
    expect(result.totalCharacters).toBeGreaterThan(3_500);
  });

  it("surfaces a per-section limit violation with its draft-local id", () => {
    const tooManyLines = Array.from({ length: 31 }, () => "ڕووناکی").join("\n");
    const result = evaluateMusicLyricsPreflight(input({ sections: [{ id: "big", type: "bridge", text: tooManyLines }] }));
    expect(result.admissible).toBe(false);
    expect(result.blockedReason).toBe("lines_too_many");
    const offending = result.issues.find((issue) => issue.code === "lines_too_many");
    expect(offending?.sectionId).toBe("big");
  });

  it("requires the lyrics rights attestation before any handoff", () => {
    const result = evaluateMusicLyricsPreflight(input({ lyricsRightsConfirmed: false }));
    expect(result.admissible).toBe(false);
    expect(result.blockedReason).toBe("lyrics_rights_required");
  });

  it("carries reference media only as private workspace asset ids", () => {
    const ok = evaluateMusicLyricsPreflight(
      input({ references: [{ mediaType: "audio", assetId }], referenceRightsConfirmed: true }),
    );
    expect(ok.admissible).toBe(true);
    expect(ok.payload?.referenceAssets).toEqual([{ mediaType: "audio", assetId }]);
    const malformed = evaluateMusicLyricsPreflight(
      input({
        references: [{ mediaType: "audio", assetId: "https://cdn.example/loop.mp3" }],
        referenceRightsConfirmed: true,
      }),
    );
    expect(malformed.admissible).toBe(false);
    expect(malformed.blockedReason).toBe("reference_asset_invalid");
    const rightsless = evaluateMusicLyricsPreflight(input({ references: [{ mediaType: "audio", assetId }] }));
    expect(rightsless.blockedReason).toBe("reference_rights_required");
  });
});

describe("panel rendering", () => {
  it("renders an RTL, ckb-tagged clean preflight with the capability notice", () => {
    const html = renderToStaticMarkup(<MusicLyricsPreflightPanel input={input()} /> as ReactElement);
    expect(html).toContain('class="music-lyrics-preflight"');
    expect(html).toContain('dir="rtl"');
    expect(html).toContain('lang="ckb"');
    expect(html).toContain('data-admissible="true"');
    expect(html).toContain('data-blocked-reason="none"');
    expect(html).toContain(defaultMusicLyricsPreflightLabels.heading);
    expect(html).toContain(defaultMusicLyricsPreflightLabels.capabilityNotice);
    expect(html).toContain(defaultMusicLyricsPreflightLabels.readyNotice);
    expect(html).toContain("sorani");
    expect(html).toContain("verse - 2 lines");
    expect(html).toContain('<button type="button"');
    expect(html).not.toContain("disabled");
  });

  it("disables the handoff control and reports the reason for a controls-only draft", () => {
    const html = renderToStaticMarkup(
      <MusicLyricsPreflightPanel
        input={input({ sections: [{ id: "ghost", type: "chorus", text: "\u200F\u200E\u061C" }] })}
      /> as ReactElement,
    );
    expect(html).toContain('data-admissible="false"');
    expect(html).toContain(`data-blocked-reason="${musicLyricsPreflightGateIssueCode}"`);
    expect(html).toContain(defaultMusicLyricsPreflightLabels.invisibleOnlyIssue);
    expect(html).toContain(defaultMusicLyricsPreflightLabels.blockedNotice);
    expect(html).toContain("disabled");
  });

  it("names the mirrored issue code for a dialect that needs a product decision", () => {
    const html = renderToStaticMarkup(
      <MusicLyricsPreflightPanel
        input={input({ vocalLanguage: "ar", sections: [{ id: "s1", type: "verse", text: arabicVerse }] })}
      /> as ReactElement,
    );
    expect(html).toContain('data-blocked-reason="dialect_required"');
    expect(html).toContain('data-issue-code="dialect_required"');
    expect(html).toContain("Dialect: unresolved");
  });

  it("never echoes lyric text, so no authored content leaks into the summary", () => {
    const html = renderToStaticMarkup(
      <MusicLyricsPreflightPanel
        input={input({
          sections: [{ id: "s1", type: "verse", text: "کەسێکی نهێنی ناوی ئەمە" }],
          references: [{ mediaType: "audio", assetId }],
          referenceRightsConfirmed: true,
        })}
      /> as ReactElement,
    );
    expect(html).not.toContain("کەسێکی نهێنی");
    expect(html).not.toContain(assetId);
  });

  it("hands off only a clean preflight and reports the block otherwise", () => {
    const handoff = vi.fn();
    const blocked = vi.fn();
    const clean = renderToStaticMarkup(
      <MusicLyricsPreflightPanel input={input()} onHandoff={handoff} onBlocked={blocked} /> as ReactElement,
    );
    expect(clean).toContain('data-ready="true"');
    const dirty = renderToStaticMarkup(
      <MusicLyricsPreflightPanel
        input={input({ lyricsRightsConfirmed: false })}
        onHandoff={handoff}
        onBlocked={blocked}
      /> as ReactElement,
    );
    expect(dirty).toContain('data-ready="false"');
    expect(dirty).toContain('data-blocked-reason="lyrics_rights_required"');
  });
});

describe("provider-free boundary", () => {
  const source = readFileSync(new URL("./MusicLyricsPreflightPanel.tsx", import.meta.url), "utf8");

  it("performs no network, storage or provider work of any kind", () => {
    for (const forbidden of ["fetch(", "XMLHttpRequest", "localStorage", "sessionStorage", "@/lib/api", "apiBase"]) {
      expect(source, forbidden).not.toContain(forbidden);
    }
  });

  it("names no singing provider and states that sung lyrics are not enabled", () => {
    expect(source.toLowerCase()).not.toContain("suno");
    expect(defaultMusicLyricsPreflightLabels.capabilityNotice).toContain("not enabled for any configured provider");
  });
});
