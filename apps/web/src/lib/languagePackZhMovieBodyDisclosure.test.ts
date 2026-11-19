import { describe, expect, it } from "vitest";
import { translations } from "./i18n";
import { simplifiedChineseLanguagePack } from "./languagePackZhCn";
import { defaultLanguagePackRegistry, LanguagePackRegistry } from "./languagePackRegistry";
import type { LanguagePackResource } from "./languagePackTypes";

/**
 * Follow-up to the Movie Studio zh-CN adoption audit (#1109), which measured nine
 * movie-adjacent namespaces in the shipped English catalog while the Movie
 * completeness suite audited a fixed list of eight. `movieBody` - the widest of
 * them, and the guided-step static guidance the studio actually renders - was
 * neither translated nor disclosed by any review flag.
 *
 * This lane closes the classification half of that gap only. It asserts that the
 * manifest names `movieBody` as an all-fallback module and that its copy resolves
 * through the declared English fallback. It deliberately does not add, or require,
 * Simplified Chinese copy for `movieBody`: inventing untranslated strings is a
 * native-speaker review task, not a test-lane task.
 *
 * Scope boundary: no component, timecode/duration formatting, general locale
 * catalog or billing file is read or asserted here.
 */

const MOVIE_BODY = "movieBody";

const namespaceOf = (key: string) => key.split(".")[0];

const EN = translations.en as Readonly<Record<string, string>>;
const EN_KEYS = Object.keys(EN);
const ZH_TRANSLATIONS = simplifiedChineseLanguagePack.translations;

/** Movie-adjacent namespaces, discovered from the catalog rather than a fixed list. */
function movieAdjacentNamespaces() {
  return [...new Set(EN_KEYS.map(namespaceOf))]
    .filter((namespace) => namespace.toLowerCase().startsWith("movie"))
    .sort();
}

/** English keys belonging to one namespace. */
function keysIn(namespace: string) {
  return EN_KEYS.filter((key) => namespaceOf(key) === namespace);
}

/** Keys the pack translates inside one namespace. */
function zhKeysIn(namespace: string, pack: Readonly<Record<string, string>> = ZH_TRANSLATIONS) {
  return Object.keys(pack).filter((key) => namespaceOf(key) === namespace);
}

function moduleFlag(namespace: string, pack: LanguagePackResource = simplifiedChineseLanguagePack) {
  return pack.manifest.reviewFlags.filter((flag) => flag.scope === "module" && flag.module === namespace);
}

/**
 * The audit the eight-namespace list could not perform: every movie-adjacent
 * namespace the pack leaves entirely untranslated must be disclosed by a module
 * review flag. Derived live from the catalog, so it neither hardcodes a count nor
 * goes stale when Movie copy grows.
 */
function undisclosedFallbackNamespaces(pack: LanguagePackResource) {
  const flagged = new Set(pack.manifest.reviewFlags.filter((flag) => flag.scope === "module").map((flag) => flag.module));
  return movieAdjacentNamespaces()
    .filter((namespace) => zhKeysIn(namespace, pack.translations).length === 0 && !flagged.has(namespace))
    .sort();
}

const zhChain = (key: string) => defaultLanguagePackRegistry.translate("zh-CN", key);

/** Install a synthetic pack over the real English root in an isolated registry. */
function installSyntheticPack(mutate: (pack: LanguagePackResource) => LanguagePackResource, id: string) {
  const reference = defaultLanguagePackRegistry.get("en")!;
  const registry = new LanguagePackRegistry([reference]);
  const resource = mutate(simplifiedChineseLanguagePack);
  registry.install({
    ...resource,
    manifest: { ...resource.manifest, id, version: "0.0.1", fallback: "en" },
  });
  return { registry, resource };
}

describe("zh-CN movieBody namespace disclosure", () => {
  it("treats movieBody as a real, populated namespace in the shipped catalog", () => {
    const enBodyKeys = keysIn(MOVIE_BODY);
    expect(enBodyKeys.length, "movieBody must exist in the English catalog").toBeGreaterThan(0);
    // The finding was that movieBody is the *widest* all-fallback Movie surface.
    const allFallback = movieAdjacentNamespaces().filter((namespace) => zhKeysIn(namespace).length === 0);
    expect(allFallback.length, "expected more than one fully untranslated Movie namespace").toBeGreaterThan(1);
    expect(
      enBodyKeys.length,
      `movieBody must stay the widest untranslated Movie namespace (${allFallback.join(", ")})`,
    ).toBe(Math.max(...allFallback.map((namespace) => keysIn(namespace).length)));
  });

  it("classifies movieBody as a module served entirely by the English fallback", () => {
    const flags = moduleFlag(MOVIE_BODY);
    expect(flags, "movieBody must be disclosed by exactly one module review flag").toHaveLength(1);
    expect(flags[0].code).toBe("module-untranslated");
    expect(flags[0].note, "the flag must name the fallback rather than imply review").toMatch(/English fallback/i);
  });

  it("discloses every fully untranslated Movie namespace, with no silent omissions", () => {
    expect(undisclosedFallbackNamespaces(simplifiedChineseLanguagePack)).toEqual([]);
  });

  it("ships no invented movieBody copy", () => {
    // Closing the disclosure gap must not become an excuse to fabricate Chinese
    // strings: movieBody stays untranslated until native-speaker review lands.
    expect(zhKeysIn(MOVIE_BODY)).toEqual([]);
    expect(simplifiedChineseLanguagePack.manifest.reviewState).toBe("needs-human-review");
    expect(simplifiedChineseLanguagePack.manifest.completeness).toBe("partial");
  });

  it("keeps a zero-translated namespace out of the coverage ledger", () => {
    // `measureCoverage` only reports namespaces with translated > 0, so declaring a
    // 0-of-N ledger row would be a claim the registry itself rejects as drift.
    expect((simplifiedChineseLanguagePack.manifest.coverage ?? []).some((row) => row.namespace === MOVIE_BODY)).toBe(false);
    expect(defaultLanguagePackRegistry.coverageDrift("zh-CN")).toEqual([]);
  });
});

describe("zh-CN movieBody English fallback", () => {
  it("declares a reachable fallback root", () => {
    expect(simplifiedChineseLanguagePack.manifest.fallback).toBe("en");
    expect(defaultLanguagePackRegistry.missingFallbacks()).toEqual([]);
  });

  it("resolves every movieBody key to its English source, never a raw key or a blank", () => {
    const enBodyKeys = keysIn(MOVIE_BODY);
    expect(enBodyKeys.length).toBeGreaterThan(0);
    for (const key of enBodyKeys) {
      const value = zhChain(key);
      expect(value, `${key} must not render the raw key`).not.toBe(key);
      expect(value.trim(), `${key} must not render blank`).not.toBe("");
      expect(value, `${key} must equal its English source`).toBe(EN[key]);
    }
  });

  it("serves the guided-step labels the studio renders through the fallback", () => {
    // A named slice of the keys the audit found on the guided start flow, asserted
    // as values rather than as a count so the list can be extended safely.
    const guided = keysIn(MOVIE_BODY).filter((key) => key.startsWith(`movieBody.quick.`));
    expect(guided.length, "expected guided-step movieBody.quick labels in the catalog").toBeGreaterThan(0);
    for (const key of guided) {
      expect(zhChain(key)).toBe(EN[key]);
    }
  });

  it("still reports an unknown key honestly instead of guessing a fallback", () => {
    expect(zhChain(`${MOVIE_BODY}.notARealKey`)).toBe(`${MOVIE_BODY}.notARealKey`);
  });
});

describe("syntheticMovieBody regression guards", () => {
  it("fails the disclosure audit on the pre-fix manifest", () => {
    // Reproduces the documented defect without editing a shipped resource: dropping
    // the movieBody flag is exactly the state the audit found, and the helper names it.
    const preFix: LanguagePackResource = {
      ...simplifiedChineseLanguagePack,
      manifest: {
        ...simplifiedChineseLanguagePack.manifest,
        reviewFlags: simplifiedChineseLanguagePack.manifest.reviewFlags.filter(
          (flag) => !(flag.scope === "module" && flag.module === MOVIE_BODY),
        ),
      },
    };
    expect(undisclosedFallbackNamespaces(preFix)).toEqual([MOVIE_BODY]);
    expect(undisclosedFallbackNamespaces(simplifiedChineseLanguagePack), "the shipped pack must not repeat it").toEqual([]);
  });

  it("refuses invented movieBody copy that has no ledger entry", () => {
    const sample = keysIn(MOVIE_BODY)[0];
    const { registry } = installSyntheticPack((pack) => ({
      ...pack,
      translations: { ...pack.translations, [sample]: "标题" },
    }), "x-mb-invented");
    const drift = registry.coverageDrift("x-mb-invented");
    expect(drift.some((message) => message.includes(MOVIE_BODY)), drift.join("\n")).toBe(true);
  });

  it("refuses a zero-coverage movieBody ledger row the pack cannot honour", () => {
    const { registry } = installSyntheticPack((pack) => ({
      ...pack,
      manifest: {
        ...pack.manifest,
        coverage: [...(pack.manifest.coverage ?? []), { namespace: MOVIE_BODY, translated: 0, total: keysIn(MOVIE_BODY).length }],
      },
    }), "x-mb-fake-row");
    const drift = registry.coverageDrift("x-mb-fake-row");
    expect(
      drift.some((message) => message.includes(MOVIE_BODY) && /translates no key in it/.test(message)),
      drift.join("\n"),
    ).toBe(true);
  });

  it("keeps every Movie module flag pointed the right way, in both directions", () => {
    // A `module-untranslated` flag is only true while the module ships no key in
    // that namespace, and only complete while the module ships nothing at all.
    // Checked across every discovered Movie namespace so the invariant cannot be
    // satisfied by movieBody alone, and cannot survive a partial translation that
    // leaves the flag claiming the whole module is untranslated.
    for (const namespace of movieAdjacentNamespaces()) {
      const shipped = zhKeysIn(namespace).length;
      const claimsUntranslated = moduleFlag(namespace).some((flag) => flag.code === "module-untranslated");
      expect(claimsUntranslated, `${namespace} ships ${shipped} zh key(s)`).toBe(shipped === 0);
    }
  });
});
