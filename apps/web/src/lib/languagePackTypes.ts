export type LanguagePackId = string;
export type LanguagePackDirection = "ltr" | "rtl";
export type LanguagePackCompleteness = "complete" | "partial";
export type LanguagePackReviewState = "reviewed" | "needs-human-review";

export type LanguagePackReviewFlag = Readonly<{
  code: string;
  scope: "pack" | "module";
  module?: "ui" | "auth" | "account" | "movie" | string;
  note?: string;
}>;

/**
 * Declared completeness for one translation namespace. A namespace is the first
 * dot-separated segment of a key (`movie`, `movieDeep`, ...), so a pack can state
 * exactly which part of a module it translates and which part is still served by
 * its fallback pack.
 */
export type LanguagePackNamespaceCoverage = Readonly<{
  namespace: string;
  /** Keys this pack translates inside the namespace. */
  translated: number;
  /** Keys the reference fallback pack holds inside the namespace. */
  total: number;
}>;

export type LanguagePackManifest = Readonly<{
  schemaVersion: 1;
  id: LanguagePackId;
  version: string;
  displayName: string;
  nativeName: string;
  languageTag: string;
  direction: LanguagePackDirection;
  fallback: LanguagePackId | null;
  completeness: LanguagePackCompleteness;
  reviewState: LanguagePackReviewState;
  reviewFlags: readonly LanguagePackReviewFlag[];
  /**
   * Optional self-declared coverage. When present the registry verifies it against
   * the installed reference pack, so a manifest can never claim more translated
   * copy than its resources actually contain.
   */
  coverage?: readonly LanguagePackNamespaceCoverage[];
}>;

export type LanguagePackResource = Readonly<{
  manifest: LanguagePackManifest;
  translations: Readonly<Record<string, string>>;
}>;
