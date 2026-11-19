import { describe, expect, it } from "vitest";
import { translations } from "./i18n";
import { simplifiedChineseLanguagePack } from "./languagePackZhCn";
import { defaultLanguagePackRegistry, languagePackIds, languagePackNames, languagePackTag } from "./languagePackRegistry";

/**
 * Adoption audit for the zh-CN pack over the Movie Studio static surfaces, plus a
 * synthetic locale-fallback regression. This lane deliberately reads the shipped
 * resources instead of restating them: the English catalog is the authoritative
 * key universe, so every number here is measured at test time.
 *
 * Scope boundary: timecode and duration *formatting* (HH:MM:SS over an hour) is a
 * separate lane and is not asserted here. This file covers static labels and
 * guidance only.
 */
const MOVIE_NAMESPACES = ["movie", "movieDeep", "movieModule", "movieShell", "movieAudio", "movieTeam", "movieExports", "movieQc"] as const;

/**
 * Movie-adjacent namespaces the pack does not translate at all and does not
 * disclose with a `module-untranslated` review flag. Each entry is a known audit
 * gap, not an accepted state: it exists so the omission is a named, countable
 * fact rather than something the earlier Movie namespace list silently walks
 * past. Closing a gap is a manifest edit in the zh-CN pack lane - adding the
 * matching review flag makes the entry stale and fails this file until it is
 * removed, which is the intended revisit signal.
 */
const UNDISCLOSED_MOVIE_NAMESPACES = ["movieBody"];

/**
 * Static labels and guidance the guided Movie Studio start flow renders, taken
 * from the keys the pack actually ships. Fallback-served guidance is checked
 * separately by namespace, so this list stays a copy assertion.
 */
const GUIDED_STEP_ZH_KEYS = [
  "movie.eyebrow",
  "movie.title",
  "movie.subtitle",
  "movie.workspaceEyebrow",
  "movie.workspaceTitle",
  "movie.workflowLabel",
  "movie.quick",
  "movie.quickHint",
  "movie.full",
  "movie.fullHint",
  "movie.briefEyebrow",
  "movie.briefTitle",
  "movie.titleLabel",
  "movie.descriptionLabel",
  "movie.instructions",
  "movie.createQuick",
  "movie.createFull",
  "movie.guideTitle",
  "movie.guideIntro",
  "movie.scenes",
  "movie.characters",
  "movie.locations",
] as const;

const EN_KEYS = Object.keys(translations.en);
const ZH_KEYS = Object.keys(simplifiedChineseLanguagePack.translations);

const namespaceOf = (key: string) => key.split(".")[0];

/** Per-namespace key counts, measured against both catalogs. */
function namespaceRows(): Map<string, { en: number; zh: number }> {
  const rows = new Map<string, { en: number; zh: number }>();
  const row = (namespace: string) => {
    const existing = rows.get(namespace) ?? { en: 0, zh: 0 };
    rows.set(namespace, existing);
    return existing;
  };
  for (const key of EN_KEYS) row(namespaceOf(key)).en += 1;
  for (const key of ZH_KEYS) row(namespaceOf(key)).zh += 1;
  return rows;
}

/** Every namespace whose first segment reads as Movie copy, measured live. */
function movieAdjacentNamespaces(rows: Map<string, { en: number; zh: number }>) {
  return [...rows.keys()]
    .filter((namespace) => namespace.toLowerCase().startsWith("movie"))
    .sort();
}

const zhChain = (key: string) => defaultLanguagePackRegistry.translate("zh-CN", key);

describe("zh-CN Movie Studio static-copy adoption", () => {
  it("exposes the zh-CN entry as a selectable pack with its own native name", () => {
    expect(languagePackIds).toContain("zh-CN");
    expect(languagePackNames["zh-CN"]).toBe("简体中文");
    expect(languagePackTag("zh-CN")).toBe("zh-CN");
    expect(defaultLanguagePackRegistry.metadata("zh-CN").direction).toBe("ltr");
  });

  it("names the language control and the studio in Simplified Chinese", () => {
    expect(zhChain("navigation.language")).toBe("语言");
    expect(zhChain("navigation.movieStudio")).toBe("电影工作室");
  });

  it("ships non-blank Chinese copy for the guided start-flow labels", () => {
    for (const key of GUIDED_STEP_ZH_KEYS) {
      const value = simplifiedChineseLanguagePack.translations[key];
      expect(value, `${key} must ship in the pack, not rely on fallback`).toBeTypeOf("string");
      expect(value.trim(), `${key} must not be blank`).not.toBe("");
      expect(zhChain(key), `${key} must resolve away from the raw key`).not.toBe(key);
    }
  });

  it("renders the guide heading from the pack rather than the English fallback", () => {
    const english = translations.en["movie.guideTitle"];
    expect(english).toBeDefined();
    expect(zhChain("movie.guideTitle")).not.toBe(english);
  });
});

describe("zh-CN locale-fallback regression over Movie static copy", () => {
  const movieKeys = EN_KEYS.filter((key) => namespaceOf(key).toLowerCase().startsWith("movie"));

  it("resolves every Movie key to non-blank, non-raw copy under zh-CN", () => {
    expect(movieKeys.length).toBeGreaterThan(0);
    for (const key of movieKeys) {
      const value = zhChain(key);
      expect(value, `${key} must resolve through the fallback chain`).not.toBe(key);
      expect(value.trim(), `${key} must never render blank`).not.toBe("");
    }
  });

  it("never lets a whitespace-only translation reach the screen", () => {
    const blank = Object.entries(simplifiedChineseLanguagePack.translations).filter(([, value]) => !value.trim());
    expect(blank).toEqual([]);
    expect(() =>
      defaultLanguagePackRegistry.install({
        manifest: { ...simplifiedChineseLanguagePack.manifest, id: "x-blank", version: "0.0.1", completeness: "partial", reviewState: "needs-human-review" },
        translations: { "movie.title": "   " },
      }),
    ).toThrow(/empty translation/);
  });

  it("keeps every declared fallback installed and surfaces an unknown key honestly", () => {
    expect(defaultLanguagePackRegistry.missingFallbacks()).toEqual([]);
    expect(zhChain("movie.notARealKey")).toBe("movie.notARealKey");
  });

  it("holds the declared Movie ledger against what the pack really translates", () => {
    expect(defaultLanguagePackRegistry.coverageDrift("zh-CN")).toEqual([]);
    const rows = namespaceRows();
    for (const namespace of MOVIE_NAMESPACES) {
      expect(rows.get(namespace), `expected a "${namespace}" namespace in the English catalog`).toBeDefined();
    }
  });

  it("serves fallback-served Movie guidance in English rather than a key", () => {
    const rows = namespaceRows();
    const englishOnly = movieAdjacentNamespaces(rows).filter((namespace) => rows.get(namespace)!.zh === 0);
    expect(englishOnly.length, "expected at least one fully untranslated Movie namespace").toBeGreaterThan(0);
    for (const namespace of englishOnly) {
      const sample = EN_KEYS.filter((key) => namespaceOf(key) === namespace).slice(0, 25);
      for (const key of sample) {
        expect(zhChain(key), `${key} must equal its English source`).toBe(translations.en[key]);
      }
    }
  });
});

describe("zh-CN Movie namespace disclosure", () => {
  const rows = namespaceRows();

  it("discloses every fully untranslated Movie namespace, or names the audit gap", () => {
    const flagged = new Set(simplifiedChineseLanguagePack.manifest.reviewFlags.map((flag) => flag.module));
    const undisclosed = movieAdjacentNamespaces(rows)
      .filter((namespace) => rows.get(namespace)!.zh === 0 && !flagged.has(namespace))
      .sort();
    expect(undisclosed).toEqual([...UNDISCLOSED_MOVIE_NAMESPACES].sort());
  });

  it("records the disclosed gap as a real untranslated Movie surface, not an empty one", () => {
    for (const namespace of UNDISCLOSED_MOVIE_NAMESPACES) {
      const row = rows.get(namespace);
      expect(row, `"${namespace}" must exist in the English catalog`).toBeDefined();
      expect(row!.en, `"${namespace}" must be a populated English namespace`).toBeGreaterThan(0);
      expect(row!.zh, `"${namespace}" is undisclosed only while it stays untranslated`).toBe(0);
    }
  });

  it("keeps every gap entry stale once the pack lane flags it", () => {
    const flagged = new Set(simplifiedChineseLanguagePack.manifest.reviewFlags.map((flag) => flag.module));
    for (const namespace of UNDISCLOSED_MOVIE_NAMESPACES) {
      expect(flagged.has(namespace), `"${namespace}" is now flagged; drop it from UNDISCLOSED_MOVIE_NAMESPACES`).toBe(false);
    }
  });
});
