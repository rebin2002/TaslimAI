/**
 * Typed view over the committed academic template catalog snapshot
 * (`academicTemplateCatalog.generated.json`). The snapshot is regenerated from the
 * API registry by `AcademicTemplateCatalogParityTests` and CI fails on any drift,
 * so these types describe the exact same data the `/api/academic-templates` routes serve.
 *
 * Privacy note: nothing here (or in the JSON) carries provider, model, routing or
 * backend-cost information — the catalog is pure layout design data.
 */

export type AcademicDirection = "ltr" | "rtl";

export interface AcademicFontToken {
  token: string;
  displayName: string;
  cssStack: string;
  docxFamily: string;
  arabicScript: boolean;
}

export interface AcademicTemplateSummary {
  slug: string;
  templateId: string;
  definitionVersion: number;
  displayName: string;
  description: string;
  documentKinds: string[];
  citationStyles: string[];
  languages: string[];
  paperFormats: string[];
  direction: AcademicDirection;
  formattingLineage: string;
  configurationCount: number;
}

export interface AcademicDesignSpec {
  languageCode: string;
  direction: AcademicDirection;
  paperFormat: string;
  marginTopPt: number;
  marginBottomPt: number;
  marginLeftPt: number;
  marginRightPt: number;
  bindingEdgePt: number;
  bindingEdge: "left" | "right";
  columnCount: number;
  columnGapPt: number;
  bodyFontToken: string;
  headingFontToken: string;
  captionFontToken: string;
  monoFontToken: string;
  bodySizePt: number;
  typeScaleRatio: number;
  lineSpacing: number;
  paragraphAfterPt: number;
  firstLineIndentPt: number;
  justifyBody: boolean;
  headingsBold: boolean;
  h1Uppercase: boolean;
  headingNumbering: "decimal" | "outline" | "none" | string;
  headingLevels: number;
  digitSystem: "latin" | "arabic-indic" | string;
  coverStyle: "classic-title-page" | "institutional-band" | "minimal-centered" | "two-part" | "none" | string;
  includesApprovalPage: boolean;
  supportsRtlAbstractPage: boolean;
  includesAiUseDisclosureSlot: boolean;
  includeToc: boolean;
  tocDepth: number;
  tocDotLeaders: boolean;
  frontMatterNumbering: "roman-lower" | "roman-upper" | "arabic" | "none" | string;
  bodyNumbering: "roman-lower" | "roman-upper" | "arabic" | "none" | string;
  restartNumbersAfterFrontMatter: boolean;
  pageNumberPosition: "bottom-center" | "bottom-inside" | "bottom-outside" | "top-center" | "top-right" | string;
  runningHeadContent: "short-title" | "author" | "none" | string;
  defaultCitationStyle: string;
  citationMarkers: "author-date" | "numeric" | "notes" | string;
  footnotesEnabled: boolean;
  bibliographyHangingIndent: boolean;
  bibliographyIndentPt: number;
  figureCaptionPosition: string;
  tableCaptionPosition: string;
  textColorHex: string;
  backgroundColorHex: string;
  accentColorHex: string;
  paperWidthPt: number;
  paperHeightPt: number;
}

export interface AcademicTemplateConfiguration {
  configurationId: string;
  templateSlug: string;
  templateId: string;
  displayName: string;
  variantSelection: Record<string, string>;
  designHash: string;
  spec: AcademicDesignSpec;
}

export interface AcademicUniversityPreset {
  id: string;
  displayName: string;
  region: string;
  notes: string;
  defaultTemplateSlug: string;
  defaultConfigurationId: string;
  pinnedSpec: Record<string, string>;
  pinnedKeys: string[];
  studentEditableGroups: string[];
  verificationNote: string;
}

export interface AcademicTemplateCatalog {
  schema: number;
  catalogVersion: string;
  definitionVersion: number;
  baseTemplateCount: number;
  distinctConfigurationCount: number;
  documentKinds: string[];
  citationStyles: string[];
  languages: string[];
  paperFormats: string[];
  directions: string[];
  fontTokens: AcademicFontToken[];
  templates: AcademicTemplateSummary[];
  configurations: AcademicTemplateConfiguration[];
  presets: AcademicUniversityPreset[];
  syntheticBanners: string[];
}

export interface AcademicFilterCriteria {
  documentKind?: string;
  citationStyle?: string;
  language?: string;
  paperFormat?: string;
  direction?: string;
  query?: string;
}
