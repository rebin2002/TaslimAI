import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { localeTag } from "../lib/i18n";
import { musicLyricsLanguageTag, musicLyricsTextDirection } from "./MusicStudioView";

const musicSource = readFileSync(new URL("./MusicStudioView.tsx", import.meta.url), "utf8");

describe("Music Studio lyrics direction and language metadata", () => {
  it("keeps Arabic and Kurdish Sorani lyrics on the right-to-left base direction under an English shell", () => {
    expect(musicLyricsTextDirection("ku", "en")).toBe("rtl");
    expect(musicLyricsTextDirection("ar", "en")).toBe("rtl");
    expect(musicLyricsTextDirection("en", "en")).toBe("ltr");
    expect(musicLyricsTextDirection("en", "ku")).toBe("ltr");
  });

  it("resolves the auto lyric language from the interface locale instead of defaulting to ltr", () => {
    expect(musicLyricsTextDirection("auto", "ku")).toBe("rtl");
    expect(musicLyricsTextDirection("auto", "ar")).toBe("rtl");
    expect(musicLyricsTextDirection("auto", "en")).toBe("ltr");
  });

  it("publishes canonical script tags so assistive technology and fonts match the lyrics script", () => {
    expect(musicLyricsLanguageTag("ku", "en")).toBe("ckb");
    expect(musicLyricsLanguageTag("ar", "en")).toBe("ar");
    expect(musicLyricsLanguageTag("en", "ku")).toBe("en");
    expect(musicLyricsLanguageTag("auto", "ku")).toBe("ckb");
    expect(musicLyricsLanguageTag("auto", "en")).toBe("en");
    expect(musicLyricsLanguageTag("ku", "en")).toBe(localeTag("ku"));
  });

  it("binds both attributes to every lyrics field the user types into", () => {
    const binding = 'dir={musicLyricsTextDirection(language, locale)} lang={musicLyricsLanguageTag(language, locale)}';
    const lyricsFields = musicSource.split("\n").filter((line) => line.includes(binding));
    expect(lyricsFields.map((line) => (/value=\{description\}/.test(line) ? "description" : /value=\{purpose\}/.test(line) ? "purpose" : "other"))).toEqual(["description", "purpose"]);
  });
});
