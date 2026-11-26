import { describe, expect, it } from "vitest";
import { translations } from "./i18n";
import { simplifiedChineseLanguagePack } from "./languagePackZhCn";

const priorityMovieKeys = [
  "movie.title",
  "movie.subtitle",
  "movie.briefTitle",
  "movie.titleLabel",
  "movie.descriptionLabel",
  "movie.createQuick",
  "movie.createFull",
] as const;

function interpolationTokens(value: string) {
  return [...value.matchAll(/\{(\w+)\}/g)].map(([, token]) => token).sort();
}

describe("zh-CN language-pack resource validation", () => {
  const { manifest, translations: zhCn } = simplifiedChineseLanguagePack;

  it("keeps every translated resource ID and interpolation token aligned with English", () => {
    const english = translations.en;

    for (const [key, localizedValue] of Object.entries(zhCn)) {
      expect(Object.hasOwn(english, key), `${key} must exist in the English source catalog`).toBe(true);
      expect(localizedValue.trim(), `${key} must not be empty`).not.toBe("");
      expect(interpolationTokens(localizedValue), `${key} interpolation tokens`).toEqual(
        interpolationTokens(english[key]),
      );
    }
  });

  it("keeps a narrow Movie Studio essentials slice while disclosing partial, human-review-required coverage", () => {
    expect(manifest.id).toBe("zh-CN");
    expect(manifest.fallback).toBe("en");
    expect(manifest.direction).toBe("ltr");
    expect(manifest.completeness).toBe("partial");
    expect(manifest.reviewState).toBe("needs-human-review");
    expect(manifest.reviewFlags).toEqual(
      expect.arrayContaining([
        expect.objectContaining({ code: "incomplete-coverage" }),
        expect.objectContaining({ code: "native-speaker-review-required" }),
      ]),
    );

    // These few labels are a prioritized smoke slice, not a completeness or linguistic-accuracy claim.
    for (const key of priorityMovieKeys) {
      expect(zhCn[key]?.trim().length ?? 0, `${key} prioritized sample`).toBeGreaterThan(0);
    }
  });
});
