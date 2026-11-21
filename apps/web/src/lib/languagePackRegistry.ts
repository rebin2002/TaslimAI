import { localeDirection, localeNames, localeTags, locales, translate as legacyTranslate, translations, type Locale } from "./i18n";
import { simplifiedChineseLanguagePack } from "./languagePackZhCn";
import type { LanguagePackDirection, LanguagePackId, LanguagePackManifest, LanguagePackNamespaceCoverage, LanguagePackResource } from "./languagePackTypes";

const legacyLanguagePackVersion = "1.0.0";

type LanguagePackDocumentMetadata = Readonly<{ languageTag: string; direction: LanguagePackDirection }>;

/**
 * A pack id doubles as a storage-key segment and an identifier in diagnostics, so
 * it is kept to a conservative shape instead of any non-empty string.
 */
const languagePackIdPattern = /^[A-Za-z0-9][A-Za-z0-9._-]{0,31}$/;

/** Namespace segment of a key: `movieDeep.sceneTitle` resolves to `movieDeep`. */
function keyNamespace(key: string) {
  const segment = key.split(".")[0];
  return segment.length > 0 ? segment : key;
}

function hasOwn(target: object, property: string) {
  return Object.prototype.hasOwnProperty.call(target, property);
}

function legacyResource(locale: Locale): LanguagePackResource {
  return {
    manifest: {
      schemaVersion: 1,
      id: locale,
      version: legacyLanguagePackVersion,
      displayName: localeNames[locale],
      nativeName: localeNames[locale],
      languageTag: localeTags[locale],
      direction: localeDirection(locale),
      fallback: locale === "en" ? null : "en",
      completeness: "complete",
      reviewState: "reviewed",
      reviewFlags: [],
    },
    translations: translations[locale],
  };
}

/** Count a pack's own translated keys per namespace against a reference catalog. */
function measureCoverage(resource: LanguagePackResource, reference: LanguagePackResource): readonly LanguagePackNamespaceCoverage[] {
  const buckets = new Map<string, { translated: number; total: number }>();
  for (const key of Object.keys(reference.translations)) {
    const namespace = keyNamespace(key);
    const bucket = buckets.get(namespace) ?? { translated: 0, total: 0 };
    bucket.total += 1;
    if (hasOwn(resource.translations, key)) bucket.translated += 1;
    buckets.set(namespace, bucket);
  }
  return [...buckets.entries()]
    .filter(([, bucket]) => bucket.translated > 0)
    .map(([namespace, bucket]) => ({ namespace, translated: bucket.translated, total: bucket.total }))
    .sort((left, right) => right.total - left.total || left.namespace.localeCompare(right.namespace));
}

function validateResource(resource: LanguagePackResource) {
  const { manifest, translations: resourceTranslations } = resource;
  if (manifest.schemaVersion !== 1) throw new Error(`Unsupported language-pack schema for ${manifest.id}.`);
  if (!languagePackIdPattern.test(manifest.id)) throw new Error(`Language-pack id "${manifest.id}" is not a safe identifier.`);
  if (!manifest.version.trim() || !manifest.languageTag.trim()) throw new Error("Language-pack identity fields are required.");
  if (manifest.direction !== "ltr" && manifest.direction !== "rtl") throw new Error(`Language pack ${manifest.id} declares an unsupported direction.`);
  if (manifest.fallback === manifest.id) throw new Error(`Language pack ${manifest.id} cannot fall back to itself.`);
  if (manifest.completeness === "partial" && manifest.reviewState !== "needs-human-review") throw new Error(`Partial language pack ${manifest.id} must require human review.`);
  if (manifest.completeness === "complete" && (manifest.coverage ?? []).some((entry) => entry.translated < entry.total)) {
    throw new Error(`Language pack ${manifest.id} claims completeness while declaring partial coverage.`);
  }
  for (const [key, value] of Object.entries(resourceTranslations)) {
    if (!key.trim() || !value.trim()) throw new Error(`Language pack ${manifest.id} contains an empty translation.`);
  }
}

/**
 * Substitute `{name}` placeholders from own properties of `variables` only.
 * An unsupplied placeholder - including one naming an inherited `Object.prototype`
 * member such as `toString` or `constructor` - stays literal rather than being
 * coerced into user-visible text.
 */
function interpolate(value: string, variables?: Record<string, string>) {
  return value.replace(/\{(\w+)\}/g, (placeholder, variable: string) => {
    if (!variables || !hasOwn(variables, variable)) return placeholder;
    const replacement = variables[variable];
    return typeof replacement === "string" ? replacement : placeholder;
  });
}

export class LanguagePackRegistry {
  private readonly resources = new Map<LanguagePackId, LanguagePackResource>();

  constructor(resources: readonly LanguagePackResource[] = []) {
    for (const resource of resources) this.install(resource);
  }

  /**
   * Install a pack. Re-installing a known id would silently retarget every pack
   * that falls back to it - including the `en` root - so a replacement has to name
   * the version it is superseding.
   */
  install(resource: LanguagePackResource, options?: { replaceVersion?: string }) {
    validateResource(resource);
    const existing = this.resources.get(resource.manifest.id);
    if (existing && options?.replaceVersion !== existing.manifest.version) {
      throw new Error(`Language pack ${resource.manifest.id} version ${existing.manifest.version} is already installed; pass replaceVersion to supersede it.`);
    }
    this.resources.set(resource.manifest.id, resource);
    return resource.manifest;
  }

  has(id: LanguagePackId) {
    return this.resources.has(id);
  }

  get(id: LanguagePackId) {
    return this.resources.get(id);
  }

  list(): readonly LanguagePackManifest[] {
    return [...this.resources.values()].map((resource) => resource.manifest);
  }

  metadata(id: LanguagePackId): LanguagePackDocumentMetadata {
    const resource = this.get(id) ?? this.get("en");
    if (!resource) return { languageTag: "en", direction: "ltr" };
    return { languageTag: resource.manifest.languageTag, direction: resource.manifest.direction };
  }

  /** Resolution order for a pack: itself, then each declared fallback, cycle-guarded. */
  private chain(id: LanguagePackId): readonly LanguagePackResource[] {
    const visited = new Set<LanguagePackId>();
    const chain: LanguagePackResource[] = [];
    let current = this.get(id) ?? this.get("en");
    while (current && !visited.has(current.manifest.id)) {
      visited.add(current.manifest.id);
      chain.push(current);
      current = current.manifest.fallback ? this.get(current.manifest.fallback) : undefined;
    }
    return chain;
  }

  /**
   * Namespaces in which a pack translates at least one key, measured against the
   * reference catalog. Published so completeness is a verifiable number rather
   * than an unaudited claim about which modules a pack covers.
   */
  coverage(id: LanguagePackId, referenceId: LanguagePackId = "en") {
    const resource = this.get(id);
    const reference = this.get(referenceId);
    if (!resource || !reference) return [];
    return measureCoverage(resource, reference);
  }

  /** Fallback targets that are not installed, which would strand every miss. */
  missingFallbacks(): readonly LanguagePackId[] {
    return [...this.resources.values()]
      .map((resource) => resource.manifest.fallback)
      .filter((fallback): fallback is LanguagePackId => Boolean(fallback) && !this.resources.has(fallback as string));
  }

  /**
   * Differences between a pack's declared coverage ledger and what it really
   * translates. Checked in CI rather than at install time: the English catalog
   * gains keys during ordinary feature work, and a stale ledger must degrade to a
   * failing test instead of throwing while the app boots.
   */
  coverageDrift(id: LanguagePackId, referenceId: LanguagePackId = "en"): readonly string[] {
    const resource = this.get(id);
    const reference = this.get(referenceId);
    if (!resource || !reference) return [`Language pack "${id}" or reference "${referenceId}" is not installed.`];
    const measured = new Map(this.coverage(id, referenceId).map((entry) => [entry.namespace, entry]));
    const declared = resource.manifest.coverage ?? [];
    const drift: string[] = [];
    for (const entry of declared) {
      const actual = measured.get(entry.namespace);
      if (!actual) {
        drift.push(`"${entry.namespace}" is declared as ${entry.translated}/${entry.total} but the pack translates no key in it.`);
        continue;
      }
      if (actual.translated !== entry.translated || actual.total !== entry.total) {
        drift.push(`"${entry.namespace}" is declared as ${entry.translated}/${entry.total} but measures ${actual.translated}/${actual.total}.`);
      }
    }
    for (const entry of measured.values()) {
      if (!declared.some((declaredEntry) => declaredEntry.namespace === entry.namespace)) {
        drift.push(`"${entry.namespace}" is translated ${entry.translated}/${entry.total} but is missing from the declared ledger.`);
      }
    }
    // Every key the pack ships must exist in the reference catalog, otherwise the
    // entry is unreachable copy that will never render.
    const unreachable = Object.keys(resource.translations).filter((key) => !hasOwn(reference.translations, key));
    if (unreachable.length > 0) drift.push(`Keys absent from the ${referenceId} catalog: ${unreachable.sort().join(", ")}.`);
    return drift;
  }

  translate(id: LanguagePackId, key: string, variables?: Record<string, string>) {
    for (const resource of this.chain(id)) {
      // Own-property lookup: an inherited Object.prototype member is never a translation.
      if (!hasOwn(resource.translations, key)) continue;
      const value = resource.translations[key];
      if (typeof value === "string" && value.length > 0) return interpolate(value, variables);
    }
    return key;
  }
}

export const defaultLanguagePackRegistry = new LanguagePackRegistry([
  ...locales.map(legacyResource),
  simplifiedChineseLanguagePack,
]);

export const languagePackIds = defaultLanguagePackRegistry.list().map((manifest) => manifest.id);
export const languagePackNames: Readonly<Record<LanguagePackId, string>> = Object.fromEntries(
  defaultLanguagePackRegistry.list().map((manifest) => [manifest.id, manifest.nativeName]),
);

export function isLanguagePackInstalled(id: string): id is LanguagePackId {
  return defaultLanguagePackRegistry.has(id);
}

export function languagePackManifest(id: LanguagePackId) {
  return defaultLanguagePackRegistry.get(id)?.manifest ?? defaultLanguagePackRegistry.get("en")!.manifest;
}

export function languagePackTag(id: LanguagePackId) {
  return defaultLanguagePackRegistry.metadata(id).languageTag;
}

export function languagePackDirection(id: LanguagePackId): LanguagePackDirection {
  return defaultLanguagePackRegistry.metadata(id).direction;
}

export function translateLanguagePack(id: LanguagePackId, key: string, variables?: Record<string, string>) {
  return defaultLanguagePackRegistry.translate(id, key, variables);
}

export function legacyLocaleForLanguagePack(id: LanguagePackId): Locale {
  return id === "ar" || id === "ku" ? id : "en";
}

/** Compatibility helper for code that still uses the legacy catalog directly. */
export function translateLegacyLocale(locale: Locale, key: string, variables?: Record<string, string>) {
  return legacyTranslate(locale, key, variables);
}
