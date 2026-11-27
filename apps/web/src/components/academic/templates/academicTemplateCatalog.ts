import type {
  AcademicFilterCriteria,
  AcademicFontToken,
  AcademicTemplateCatalog,
  AcademicTemplateConfiguration,
  AcademicTemplateSummary,
} from "./academicTemplateTypes";
import catalogSource from "./academicTemplateCatalog.generated.json";

/**
 * The bundled snapshot of the full registry (22 base templates, 230 distinct,
 * previewable configurations). `AcademicTemplateCatalogParityTests` on the API side
 * re-derives this file from the registry and fails CI on drift, so the studio can
 * render the entire library deterministically — even before any network request.
 */
export const bundledCatalog = catalogSource as unknown as AcademicTemplateCatalog;

/** Canonicalises gallery language facets exactly like `AcademicRtlPolicy.NormalizeLanguage`. */
export function normalizeAcademicLanguage(language: string): string {
  const value = language.trim().toLowerCase();
  if (value === "ckb" || value.startsWith("ckb-")) return "ku";
  if (value.startsWith("ku")) return "ku";
  if (value.startsWith("ar")) return "ar";
  if (value.startsWith("en")) return "en";
  return "en";
}

function includesIgnoreCase(list: string[], value: string): boolean {
  return list.some((entry) => entry.toLowerCase() === value.toLowerCase());
}

/**
 * Mirrors the API's `EnumerateMatches` semantics facet-for-facet (including the
 * friendly language aliases "ckb", "ar-EG", "en-GB"), so client-side filtering of
 * the snapshot returns the same set the `/api/academic-templates/configurations`
 * route would.
 */
export function filterConfigurations(
  catalog: AcademicTemplateCatalog,
  criteria: AcademicFilterCriteria,
): AcademicTemplateConfiguration[] {
  const query = criteria.query?.trim().toLowerCase();
  const normalizedLanguage = criteria.language ? normalizeAcademicLanguage(criteria.language) : null;
  return catalog.configurations.filter((configuration) => {
    const template = catalog.templates.find((entry) => entry.slug === configuration.templateSlug);
    if (!template) return false;
    if (criteria.documentKind && !includesIgnoreCase(template.documentKinds, criteria.documentKind)) return false;
    if (criteria.citationStyle && !includesIgnoreCase(template.citationStyles, criteria.citationStyle)) return false;
    if (
      criteria.language &&
      !includesIgnoreCase(template.languages, criteria.language) &&
      !template.languages.includes(normalizedLanguage!)
    ) {
      return false;
    }
    if (criteria.paperFormat && !includesIgnoreCase(template.paperFormats, criteria.paperFormat)) return false;
    if (criteria.direction && template.direction.toLowerCase() !== criteria.direction.toLowerCase()) return false;
    if (query) {
      const haystack = `${configuration.displayName} ${configuration.configurationId}`.toLowerCase();
      if (!haystack.includes(query)) return false;
    }
    return true;
  });
}

export function countConfigurations(catalog: AcademicTemplateCatalog, criteria: AcademicFilterCriteria): number {
  return filterConfigurations(catalog, criteria).length;
}

/** Registry-ordered per-template counts, matching `ConfigurationCountsByTemplate`. */
export function configurationCountsByTemplate(catalog: AcademicTemplateCatalog): Record<string, number> {
  const counts: Record<string, number> = {};
  for (const template of catalog.templates) counts[template.slug] = 0;
  for (const configuration of catalog.configurations) {
    counts[configuration.templateSlug] = (counts[configuration.templateSlug] ?? 0) + 1;
  }
  return counts;
}

/** Configuration ids deduplicated by design hash — the honest distinct-design count. */
export function distinctDesignHashes(catalog: AcademicTemplateCatalog): string[] {
  return [...new Set(catalog.configurations.map((configuration) => configuration.designHash))];
}

export function getConfiguration(
  catalog: AcademicTemplateCatalog,
  configurationId: string,
): AcademicTemplateConfiguration | undefined {
  return catalog.configurations.find((configuration) => configuration.configurationId === configurationId);
}

export function getTemplate(catalog: AcademicTemplateCatalog, slug: string): AcademicTemplateSummary | undefined {
  return catalog.templates.find((template) => template.slug === slug);
}

export function getFontToken(catalog: AcademicTemplateCatalog, token: string): AcademicFontToken | undefined {
  return catalog.fontTokens.find((entry) => entry.token === token);
}

/** Banner order mirrors the generator: [English, Arabic, Kurdish]. */
export function syntheticBannerFor(catalog: AcademicTemplateCatalog, languageCode: string): string {
  const normalized = normalizeAcademicLanguage(languageCode);
  if (normalized === "ar") return catalog.syntheticBanners[1] ?? catalog.syntheticBanners[0];
  if (normalized === "ku") return catalog.syntheticBanners[2] ?? catalog.syntheticBanners[0];
  return catalog.syntheticBanners[0];
}
