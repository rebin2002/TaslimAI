/**
 * Lifecycle tests for the studio's structured-lyrics draft.
 *
 * This module is the half of the #772 wiring that survives a tab switch, so
 * every case below is a claim about what the user finds when they come back:
 * their own rows, in their own workspace, never another workspace's, never a
 * shape the mirrored contract would reject, and never a value that was only
 * ever a paste of junk.
 */
import { describe, expect, it } from "vitest";
import {
  MUSIC_LYRICS_MAX_SECTION_ID_CHARACTERS,
  MUSIC_LYRICS_MAX_SECTIONS,
  MUSIC_LYRICS_MAX_TOTAL_CHARACTERS,
  splitLyricsDraftLines,
} from "@/lib/musicLyricsDraftPayload";
import {
  buildMusicStudioLyricsPreflightInput,
  clearMusicStudioLyricsDraftState,
  emptyMusicStudioLyricsDraftState,
  isMusicStudioLyricsDraftEmpty,
  musicStudioLyricsDraftStorageKey,
  parseMusicStudioLyricsDraftState,
  persistMusicStudioLyricsDraftState,
  readMusicStudioLyricsDraftState,
  serializeMusicStudioLyricsDraftState,
  type MusicStudioLyricsDraftState,
} from "./musicStudioLyricsDraftState";

class MemoryStorage {
  private entries = new Map<string, string>();
  getItem(key: string) { return this.entries.has(key) ? this.entries.get(key)! : null; }
  setItem(key: string, value: string) { this.entries.set(key, value); }
  removeItem(key: string) { this.entries.delete(key); }
  size() { return this.entries.size; }
  raw(key: string) { return this.entries.get(key); }
  keys() { return [...this.entries.keys()]; }
}

const authored: MusicStudioLyricsDraftState = {
  sections: [
    { id: "s1", type: "verse", text: "Bist dengê çiyayan di şevê de" },
    { id: "s2", type: "chorus", text: "ئەڵێم بگەڕێمەوە بۆ ئاگری دڵمە" },
  ],
  lyricsRightsConfirmed: true,
  referenceRightsConfirmed: false,
};

function lyricsCharacterLoad(sections: readonly { text: string }[]): number {
  return sections.reduce(
    (total, section) => total + splitLyricsDraftLines(section.text).reduce((sum, line) => sum + line.length, 0),
    0,
  );
}

describe("empty and keyed draft defaults", () => {
  it("starts with no rows and no attestation", () => {
    const empty = emptyMusicStudioLyricsDraftState();
    expect(empty.sections).toEqual([]);
    expect(empty.lyricsRightsConfirmed).toBe(false);
    expect(empty.referenceRightsConfirmed).toBe(false);
    expect(isMusicStudioLyricsDraftEmpty(empty)).toBe(true);
  });

  it("keys the draft by member and workspace, with both ids escaped", () => {
    const key = musicStudioLyricsDraftStorageKey("user/one", "workspace 1");
    expect(key).toBe("taslim:music-lyrics-draft:user%2Fone:workspace%201");
    expect(key).not.toContain("user/one workspace");
  });

  it("treats a rights-only draft as work worth keeping", () => {
    const attested: MusicStudioLyricsDraftState = {
      sections: [],
      lyricsRightsConfirmed: true,
      referenceRightsConfirmed: false,
    };
    expect(isMusicStudioLyricsDraftEmpty(attested)).toBe(false);
  });
});

describe("parse", () => {
  it("restores authored rows verbatim, including their draft-local ids", () => {
    const restored = parseMusicStudioLyricsDraftState(serializeMusicStudioLyricsDraftState(authored));
    expect(restored).toEqual(authored);
  });

  it("fails closed on anything that is not a readable draft", () => {
    for (const junk of [
      undefined,
      "",
      "not json",
      "null",
      "[]",
      '"draft"',
      JSON.stringify({ sections: "verse one" }),
      JSON.stringify({ sections: { 0: { type: "verse", text: "x" } } }),
      JSON.stringify({ lyricsRightsConfirmed: true }),
    ].map((value) => (value === undefined ? null : String(value)))) {
      expect(parseMusicStudioLyricsDraftState(junk).sections).toEqual([]);
      expect(parseMusicStudioLyricsDraftState(junk).lyricsRightsConfirmed).toBe(false);
    }
  });

  it("drops rows whose section token is not contract vocabulary", () => {
    const restored = parseMusicStudioLyricsDraftState(JSON.stringify({
      sections: [
        { id: "a", type: "hook", text: "A row the server cannot name" },
        { id: "b", type: "chorus", text: "Refrain that survives" },
        { id: "c", type: "", text: "No token at all" },
        { id: "d", text: "Token missing entirely" },
        { id: "e", type: 42, text: "Token not a string" },
        { id: "f", type: "Verse", text: "Capitalisation is not the contract" },
      ],
    }));
    expect(restored.sections.map((section) => section.id)).toEqual(["b"]);
  });

  it("drops rows past the mirrored section ceiling instead of restoring a rejectable draft", () => {
    const rows = Array.from({ length: MUSIC_LYRICS_MAX_SECTIONS + 6 }, (_, index) => ({
      id: `row-${index + 1}`,
      type: "verse",
      text: `Line for row ${index + 1}`,
    }));
    const restored = parseMusicStudioLyricsDraftState(JSON.stringify({ sections: rows }));
    expect(restored.sections).toHaveLength(MUSIC_LYRICS_MAX_SECTIONS);
    expect(restored.sections[0]?.id).toBe("row-1");
    expect(restored.sections.at(-1)?.id).toBe(`row-${MUSIC_LYRICS_MAX_SECTIONS}`);
  });

  it("clamps the restored text to the mirrored total, at line boundaries", () => {
    const restored = parseMusicStudioLyricsDraftState(JSON.stringify({
      sections: [
        { id: "full", type: "verse", text: "a".repeat(MUSIC_LYRICS_MAX_TOTAL_CHARACTERS - 100) },
        { id: "fits", type: "chorus", text: `${"b".repeat(90)}\n${"c".repeat(90)}` },
      ],
    }));
    expect(restored.sections.map((section) => section.id)).toEqual(["full", "fits"]);
    expect(restored.sections[1]?.text).toBe("b".repeat(90));
    expect(lyricsCharacterLoad(restored.sections)).toBeLessThanOrEqual(MUSIC_LYRICS_MAX_TOTAL_CHARACTERS);
  });

  it("drops a row that has no room left in the total at all", () => {
    const restored = parseMusicStudioLyricsDraftState(JSON.stringify({
      sections: [
        { id: "first", type: "verse", text: "d".repeat(MUSIC_LYRICS_MAX_TOTAL_CHARACTERS) },
        { id: "no-room", type: "outro", text: "e".repeat(200) },
      ],
    }));
    expect(restored.sections.map((section) => section.id)).toEqual(["first"]);
  });

  it("skips blank rows, because an empty row is not user work", () => {
    const restored = parseMusicStudioLyricsDraftState(JSON.stringify({
      sections: [
        { id: "blank", type: "verse", text: "   " },
        { id: "newline", type: "bridge", text: "\n\n" },
        { id: "missing", type: "chorus" },
        { id: "number", type: "refrain", text: 7 },
        { id: "kept", type: "intro", text: "Only this row is content" },
      ],
    }));
    expect(restored.sections.map((section) => section.id)).toEqual(["kept"]);
  });

  it("replaces an unusable draft-local id rather than echoing content as a name", () => {
    const oversized = "z".repeat(MUSIC_LYRICS_MAX_SECTION_ID_CHARACTERS + 1);
    const restored = parseMusicStudioLyricsDraftState(JSON.stringify({
      sections: [
        { id: oversized, type: "verse", text: "First row with an id shaped like a lyric" },
        { id: "   ", type: "chorus", text: "Second row with a blank id" },
        { type: "bridge", text: "Third row with no id" },
        { id: 12, type: "outro", text: "Fourth row with a numeric id" },
      ],
    }));
    expect(restored.sections.map((section) => section.id)).toEqual([
      "restored-1", "restored-2", "restored-3", "restored-4",
    ]);
  });

  it("keeps an attestation only when it is literally true", () => {
    for (const truthy of ["true", 1, "yes", {}]) {
      const restored = parseMusicStudioLyricsDraftState(JSON.stringify({
        sections: [{ id: "s1", type: "verse", text: "One line" }],
        lyricsRightsConfirmed: truthy,
        referenceRightsConfirmed: truthy,
      }));
      expect(restored.lyricsRightsConfirmed).toBe(false);
      expect(restored.referenceRightsConfirmed).toBe(false);
    }
    const explicit = parseMusicStudioLyricsDraftState(JSON.stringify({
      sections: [{ id: "s1", type: "verse", text: "One line" }],
      lyricsRightsConfirmed: true,
      referenceRightsConfirmed: true,
    }));
    expect(explicit.lyricsRightsConfirmed).toBe(true);
    expect(explicit.referenceRightsConfirmed).toBe(true);
  });

  it("serializes in a stable order so an unchanged draft never re-writes", () => {
    const first = serializeMusicStudioLyricsDraftState(authored);
    const second = serializeMusicStudioLyricsDraftState(parseMusicStudioLyricsDraftState(first));
    expect(second).toBe(first);
  });
});

describe("read, persist and clear against storage", () => {
  it("round-trips a draft for its own member and workspace", () => {
    const storage = new MemoryStorage();
    persistMusicStudioLyricsDraftState("user-1", "workspace-1", authored, storage);
    expect(storage.keys()).toEqual([musicStudioLyricsDraftStorageKey("user-1", "workspace-1")]);
    expect(readMusicStudioLyricsDraftState("user-1", "workspace-1", storage)).toEqual(authored);
  });

  it("never replays one workspace's lyrics into another, or one member's into another", () => {
    const storage = new MemoryStorage();
    persistMusicStudioLyricsDraftState("user-1", "workspace-1", authored, storage);
    expect(readMusicStudioLyricsDraftState("user-1", "workspace-2", storage).sections).toEqual([]);
    expect(readMusicStudioLyricsDraftState("user-2", "workspace-1", storage).sections).toEqual([]);
  });

  it("drops a stored value that restores to nothing instead of leaving trash behind", () => {
    const storage = new MemoryStorage();
    const key = musicStudioLyricsDraftStorageKey("user-1", "workspace-1");
    storage.setItem(key, JSON.stringify({ sections: [{ id: "s1", type: "hook", text: "Unusable token" }] }));
    const read = readMusicStudioLyricsDraftState("user-1", "workspace-1", storage);
    expect(read.sections).toEqual([]);
    expect(storage.raw(key)).toBeUndefined();
  });

  it("removes the key when an emptied draft is persisted, so a return trip is clean", () => {
    const storage = new MemoryStorage();
    persistMusicStudioLyricsDraftState("user-1", "workspace-1", authored, storage);
    persistMusicStudioLyricsDraftState("user-1", "workspace-1", emptyMusicStudioLyricsDraftState(), storage);
    expect(storage.size()).toBe(0);
    expect(readMusicStudioLyricsDraftState("user-1", "workspace-1", storage)).toEqual(emptyMusicStudioLyricsDraftState());
  });

  it("clears only the requested scope", () => {
    const storage = new MemoryStorage();
    persistMusicStudioLyricsDraftState("user-1", "workspace-1", authored, storage);
    persistMusicStudioLyricsDraftState("user-1", "workspace-2", authored, storage);
    clearMusicStudioLyricsDraftState("user-1", "workspace-1", storage);
    expect(storage.keys()).toEqual([musicStudioLyricsDraftStorageKey("user-1", "workspace-2")]);
  });

  it("stays silent when storage is unavailable or the scope is not known", () => {
    expect(readMusicStudioLyricsDraftState("user-1", "workspace-1", undefined)).toEqual(emptyMusicStudioLyricsDraftState());
    expect(readMusicStudioLyricsDraftState("", "workspace-1", new MemoryStorage()).sections).toEqual([]);
    expect(readMusicStudioLyricsDraftState("user-1", "  ", new MemoryStorage()).sections).toEqual([]);
    const storage = new MemoryStorage();
    persistMusicStudioLyricsDraftState("", "workspace-1", authored, storage);
    persistMusicStudioLyricsDraftState("user-1", "", authored, storage);
    expect(storage.size()).toBe(0);
    clearMusicStudioLyricsDraftState("", "", storage);
  });

  it("survives a storage backend that throws, keeping the draft out of the crash path", () => {
    const hostile: MemoryStorage = {
      getItem: () => { throw new Error("quota"); },
      setItem: () => { throw new Error("quota"); },
      removeItem: () => { throw new Error("quota"); },
      size: () => 0,
      raw: () => undefined,
      keys: () => [],
    } as unknown as MemoryStorage;
    expect(() => readMusicStudioLyricsDraftState("user-1", "workspace-1", hostile))
      .not.toThrow();
    expect(() => persistMusicStudioLyricsDraftState("user-1", "workspace-1", authored, hostile))
      .not.toThrow();
    expect(() => clearMusicStudioLyricsDraftState("user-1", "workspace-1", hostile)).not.toThrow();
  });
});

describe("buildMusicStudioLyricsPreflightInput", () => {
  it("shapes the authored rows and the studio's vocal language for the panel", () => {
    const input = buildMusicStudioLyricsPreflightInput(authored, { vocalLanguage: "ku" });
    expect(input.sections).toEqual(authored.sections);
    expect(input.vocalLanguage).toBe("ku");
    expect(input.dialect).toBeNull();
    expect(input.lyricsRightsConfirmed).toBe(true);
    expect(input.references).toEqual([]);
    expect(input.referenceRightsConfirmed).toBe(false);
  });

  it("passes the studio's `auto` through untouched instead of guessing a language", () => {
    // `auto` is a real studio selection and is not a contract lyric language;
    // the contract, not this adapter, has to be what reports it.
    const input = buildMusicStudioLyricsPreflightInput(authored, { vocalLanguage: "auto" });
    expect(input.vocalLanguage).toBe("auto");
  });

  it("carries the selected stored reference as one audio asset", () => {
    const input = buildMusicStudioLyricsPreflightInput(
      { ...authored, referenceRightsConfirmed: true },
      { vocalLanguage: "ar", dialect: "gulf_arabic", reference: { mediaType: "audio", assetId: "8f1c0d2e-3b4a-5c6d-7e8f-9a0b1c2d3e4f" } },
    );
    expect(input.references).toEqual([{ mediaType: "audio", assetId: "8f1c0d2e-3b4a-5c6d-7e8f-9a0b1c2d3e4f" }]);
    expect(input.dialect).toBe("gulf_arabic");
    expect(input.referenceRightsConfirmed).toBe(true);
  });

  it("does not let a reference attestation stand without a storable asset", () => {
    // A local pick has no asset id yet. Reporting the attestation as true would
    // make the contract complain about rights with no assets behind them, which
    // is the wrong claim about the user's state.
    const localOnly = buildMusicStudioLyricsPreflightInput(
      { ...authored, referenceRightsConfirmed: true },
      { vocalLanguage: "en", reference: { mediaType: "audio", assetId: null } },
    );
    expect(localOnly.references).toEqual([{ mediaType: "audio", assetId: null }]);
    expect(localOnly.referenceRightsConfirmed).toBe(false);

    const attestedWithoutReference = buildMusicStudioLyricsPreflightInput(
      { ...authored, referenceRightsConfirmed: true },
      { vocalLanguage: "en" },
    );
    expect(attestedWithoutReference.references).toEqual([]);
    expect(attestedWithoutReference.referenceRightsConfirmed).toBe(false);
  });
});
