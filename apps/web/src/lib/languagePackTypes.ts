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
}>;

export type LanguagePackResource = Readonly<{
  manifest: LanguagePackManifest;
  translations: Readonly<Record<string, string>>;
}>;
