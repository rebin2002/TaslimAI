import { localeDirection, localeNames, localeTags, locales, translate as legacyTranslate, translations, type Locale } from "./i18n";
import { simplifiedChineseLanguagePack } from "./languagePackZhCn";
import type { LanguagePackDirection, LanguagePackId, LanguagePackManifest, LanguagePackResource } from "./languagePackTypes";

const legacyLanguagePackVersion = "1.0.0";

type LanguagePackDocumentMetadata = Readonly<{ languageTag: string; direction: LanguagePackDirection }>;

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

function validateResource(resource: LanguagePackResource) {
  const { manifest, translations: resourceTranslations } = resource;
  if (manifest.schemaVersion !== 1) throw new Error(`Unsupported language-pack schema for ${manifest.id}.`);
  if (!manifest.id.trim() || !manifest.version.trim() || !manifest.languageTag.trim()) throw new Error("Language-pack identity fields are required.");
  if (manifest.fallback === manifest.id) throw new Error(`Language pack ${manifest.id} cannot fall back to itself.`);
  if (manifest.completeness === "partial" && manifest.reviewState !== "needs-human-review") throw new Error(`Partial language pack ${manifest.id} must require human review.`);
  for (const [key, value] of Object.entries(resourceTranslations)) {
    if (!key.trim() || !value.trim()) throw new Error(`Language pack ${manifest.id} contains an empty translation.`);
  }
}

function interpolate(value: string, variables?: Record<string, string>) {
  return value.replace(/\{(\w+)\}/g, (_, variable: string) => variables?.[variable] ?? `{${variable}}`);
}

export class LanguagePackRegistry {
  private readonly resources = new Map<LanguagePackId, LanguagePackResource>();

  constructor(resources: readonly LanguagePackResource[] = []) {
    for (const resource of resources) this.install(resource);
  }

  install(resource: LanguagePackResource) {
    validateResource(resource);
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

  translate(id: LanguagePackId, key: string, variables?: Record<string, string>) {
    const visited = new Set<LanguagePackId>();
    let current = this.get(id) ?? this.get("en");
    while (current && !visited.has(current.manifest.id)) {
      visited.add(current.manifest.id);
      const value = current.translations[key];
      if (typeof value === "string" && value.length > 0) return interpolate(value, variables);
      current = current.manifest.fallback ? this.get(current.manifest.fallback) : undefined;
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
