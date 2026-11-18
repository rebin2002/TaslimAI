import { describe, expect, it } from "vitest";
import { defaultLanguagePackRegistry, languagePackManifest, LanguagePackRegistry } from "./languagePackRegistry";

/**
 * The English catalog is the authoritative key universe for the shared `t()`
 * boundary, so Movie completeness is measured against it rather than against a
 * hand-written claim in the manifest or the architecture note.
 */
const MOVIE_NAMESPACES = ["movie", "movieDeep", "movieModule", "movieShell", "movieAudio", "movieTeam", "movieExports", "movieQc"] as const;

/**
 * Floors for translated Movie keys. They intentionally sit at the measured value:
 * raising them is how a translation expansion lands, and lowering one is a review
 * signal rather than a silent edit.
 */
const MOVIE_TRANSLATED_FLOOR: Readonly<Record<(typeof MOVIE_NAMESPACES)[number], number>> = {
  movie: 36,
  movieDeep: 10,
  movieModule: 0,
  movieShell: 0,
  movieAudio: 0,
  movieTeam: 0,
  movieExports: 0,
  movieQc: 0,
};

function namespaceRows(referenceId = "en") {
  const reference = defaultLanguagePackRegistry.get(referenceId)!;
  const pack = defaultLanguagePackRegistry.get("zh-CN")!;
  const rows = new Map<string, { translated: number; total: number }>();
  for (const key of Object.keys(reference.translations)) {
    const namespace = key.split(".")[0];
    const row = rows.get(namespace) ?? { translated: 0, total: 0 };
    row.total += 1;
    if (Object.prototype.hasOwnProperty.call(pack.translations, key)) row.translated += 1;
    rows.set(namespace, row);
  }
  return rows;
}

describe("zh-CN Movie Studio copy completeness", () => {
  const rows = namespaceRows();

  it("measures Movie coverage against the English catalog, not a claim", () => {
    for (const namespace of MOVIE_NAMESPACES) {
      const row = rows.get(namespace);
      expect(row, `expected a "${namespace}" namespace in the English catalog`).toBeDefined();
      expect(row!.translated, `"${namespace}" translated keys regressed below its floor`).toBeGreaterThanOrEqual(MOVIE_TRANSLATED_FLOOR[namespace]);
    }
  });

  it("publishes a ledger that agrees with the shipped resources", () => {
    expect(defaultLanguagePackRegistry.coverageDrift("zh-CN")).toEqual([]);
  });

  it("keeps the manifest honest about partial Movie coverage", () => {
    const manifest = languagePackManifest("zh-CN");
    const movieRows = MOVIE_NAMESPACES.map((namespace) => rows.get(namespace)!);
    const translated = movieRows.reduce((sum, row) => sum + row.translated, 0);
    const total = movieRows.reduce((sum, row) => sum + row.total, 0);

    // Movie Studio is far from translated, so the pack must not claim completeness
    // and must keep flagging the module for human review.
    expect(translated).toBeLessThan(total);
    expect(manifest.completeness).toBe("partial");
    expect(manifest.reviewState).toBe("needs-human-review");
    for (const namespace of MOVIE_NAMESPACES) {
      expect(manifest.reviewFlags.some((flag) => flag.module === namespace), `expected a Movie review flag for "${namespace}"`).toBe(true);
    }
  });

  it("resolves every untranslated Movie key through the English fallback", () => {
    const reference = defaultLanguagePackRegistry.get("en")!;
    const untranslated = Object.keys(reference.translations).filter(
      (key) => key.startsWith("movieModule.") && !Object.prototype.hasOwnProperty.call(defaultLanguagePackRegistry.get("zh-CN")!.translations, key),
    );
    expect(untranslated.length).toBeGreaterThan(0);
    for (const key of untranslated) {
      const resolved = defaultLanguagePackRegistry.translate("zh-CN", key);
      expect(resolved).toBe(reference.translations[key]);
      expect(resolved).not.toBe(key);
    }
  });

  it("never exposes a raw key for Movie copy the fallback can serve", () => {
    const probe = defaultLanguagePackRegistry.translate("zh-CN", "movie.title");
    expect(probe).toBe("电影工作室");
    expect(defaultLanguagePackRegistry.translate("zh-CN", "movie.notARealKey")).toBe("movie.notARealKey");
  });

  it("reports drift when a ledger over-claims translated Movie copy", () => {
    const reference = defaultLanguagePackRegistry.get("en")!;
    const registry = new LanguagePackRegistry([reference]);
    registry.install({
      manifest: {
        ...reference.manifest,
        id: "x-drift",
        version: "1.0.0",
        fallback: "en",
        completeness: "partial",
        reviewState: "needs-human-review",
        reviewFlags: [],
        // Claims the whole Movie namespace plus a namespace it never touches.
        coverage: [
          { namespace: "movie", translated: 151, total: 151 },
          { namespace: "movieQc", translated: 4, total: 6 },
        ],
      },
      // One real Movie key plus one the English catalog never had, so the ledger
      // over-claims coverage and ships unreachable copy at the same time.
      translations: { "movie.title": "标题", "movie.renamedAwayInMain": "遗留副本" },
    });
    const drift = registry.coverageDrift("x-drift");
    expect(drift.some((message) => message.includes("movie"))).toBe(true);
    expect(drift.some((message) => message.includes("movieQc"))).toBe(true);
    expect(drift.some((message) => message.includes("absent from the en catalog"))).toBe(true);
  });
});
