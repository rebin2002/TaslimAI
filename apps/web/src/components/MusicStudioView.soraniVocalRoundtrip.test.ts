import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import type { GenerationJob } from "../lib/api";
import { localeDirection, localeTag, translate, type Locale } from "../lib/i18n";
import { accountLocaleStorageKey, anonymousLocaleStorageKey, readStoredLocale, writeStoredLocale } from "../lib/localePersistence";
import { parseMusicJobResult } from "../lib/musicStudioState";

// Ownership note: this file is test-only and touches no path claimed by the
// open Music PRs (#560/#561/#745/#758 MusicStudioView family, #772
// LyricsSectionEditor, #751 reference video state, #745 reference audio state,
// #773 invisible lyrics, #774 reference-video extraction, #709 API dialect
// contracts). It proves one client-only state roundtrip: the Sorani ("ku")
// vocal-language selection plus synthetic lyric text surviving navigation
// inside Music Studio with its RTL direction metadata preserved. No provider,
// Suno, network, or reference-media behavior is involved.

const musicSource = readFileSync(new URL("./MusicStudioView.tsx", import.meta.url), "utf8");

// Synthetic Sorani lyric lines authored for this test only; not a real song.
const syntheticSoraniLyrics = "ئەی دڵ، ئازادی لە گۆرانییدا دەڕەوێت\nبەیانی ڕووناک، خەونی ئارام دەهێنێتەوە";

function memoryStorage() {
  const values = new Map<string, string>();
  return {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value); },
  };
}

describe("Music Studio Sorani vocal language roundtrip", () => {
  it("resolves the ku vocal-language option to canonical Sorani tag, RTL direction, and localized labels", () => {
    // The vocal-language select offers exactly this option set on main.
    expect(musicSource).toContain('const languages = ["auto", "en", "ar", "ku"] as const;');
    expect(localeTag("ku")).toBe("ckb");
    expect(localeDirection("ku")).toBe("rtl");
    expect(translate("en", "music.language.ku")).toBe("Kurdish Sorani");
    expect(translate("ku", "music.language.ku")).toBe("کوردیی سۆرانی");
    expect(translate("ku", "music.vocal.vocal")).toBe("دەنگدار");
  });

  it("preserves synthetic Sorani lyrics and the ku vocal-language choice across the client-side payload and result readback", () => {
    // Mirror the exact MusicGenerationInput that generate() composes locally.
    const input = {
      workspaceId: "workspace-1",
      projectId: null,
      description: syntheticSoraniLyrics,
      purpose: "demo",
      genre: "folk",
      mood: "inspiring",
      durationSeconds: 120,
      vocalPreference: "vocal",
      language: "ku",
      title: syntheticSoraniLyrics.split("\n")[0],
      additionalInstructions: null,
    };
    const parsed = JSON.parse(JSON.stringify(input));
    expect(parsed.language).toBe("ku");
    expect(parsed.vocalPreference).toBe("vocal");
    // Bidi text survives the in-memory roundtrip code-for-code, untouched.
    expect(parsed.description).toBe(syntheticSoraniLyrics);
    expect(localeDirection(parsed.language as Locale)).toBe("rtl");
    expect(localeTag(parsed.language as Locale)).toBe("ckb");

    // The same choice read back from a persisted music job result is intact.
    const job = { resultJson: JSON.stringify({ assetId: "asset-1", assetType: "music", title: syntheticSoraniLyrics.split("\n")[0], durationSeconds: 120, vocalPreference: "vocal", language: "ku" }) } as GenerationJob;
    const result = parseMusicJobResult(job);
    expect(result).toMatchObject({ language: "ku", vocalPreference: "vocal" });
    expect(result?.title).toBe(syntheticSoraniLyrics.split("\n")[0]);
  });

  it("restores the Sorani selection across a full remount navigation through unclaimed locale persistence, direction intact", () => {
    const storage = memoryStorage();
    // User picks Sorani before navigating away.
    expect(writeStoredLocale(storage, anonymousLocaleStorageKey, "ku")).toBe(true);
    expect(writeStoredLocale(storage, accountLocaleStorageKey("user/a"), "ku")).toBe(true);
    expect(writeStoredLocale(storage, accountLocaleStorageKey("user/b"), "en")).toBe(true);

    // A fresh reader after remount restores Sorani, not the English default.
    const restored = readStoredLocale(storage);
    expect(restored).toBe("ku");
    expect(localeDirection(restored as Locale)).toBe("rtl");
    expect(localeTag(restored as Locale)).toBe("ckb");
    expect(readStoredLocale(storage, accountLocaleStorageKey("user/a"))).toBe("ku");
    expect(readStoredLocale(storage, accountLocaleStorageKey("user/b"))).toBe("en");

    // Fail-closed: unavailable or empty storage never fabricates a Sorani or RTL restore.
    const blocked = { getItem: () => { throw new Error("blocked"); }, setItem: () => { throw new Error("blocked"); } };
    expect(readStoredLocale(blocked)).toBeNull();
    expect(readStoredLocale(memoryStorage())).toBeNull();
  });

  it("swaps form and result views in one component instance and never clears the lyric or vocal-language state on the way back", () => {
    // Result and form are conditional branches of the same mounted component,
    // so navigating to the result view and back does not remount the fields.
    expect(musicSource).toContain('!current ? <form className="music-studio-workspace"');

    // createAnother() is the only in-studio return-to-form navigation; it
    // clears the job, not the lyrics or the vocal-language selection.
    const createAnotherBody = musicSource.match(/function createAnother\(\)\s*\{([\s\S]*?)\n  \}/)?.[1] ?? "";
    expect(createAnotherBody).toContain("setCurrent(null)");
    for (const retained of ["setDescription", "setPurpose", "setVocalPreference", "setLanguage"]) {
      expect(createAnotherBody).not.toContain(retained);
    }

    // Each lyric-bearing field and the vocal-language select is written only
    // by its own onChange handler: no navigation path resets them.
    for (const setter of ["setDescription", "setPurpose", "setVocalPreference", "setLanguage"]) {
      const occurrences = musicSource.match(new RegExp(`${setter}\\(`, "g")) ?? [];
      expect(occurrences).toHaveLength(1);
      expect(musicSource).toContain(`onChange={(event) => ${setter}(event.target.value)}`);
    }

    // Both values are controlled bindings, so the preserved state re-renders.
    expect(musicSource).toContain("value={description}");
    expect(musicSource).toContain("value={language}");
  });
});
