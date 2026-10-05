import type { Locale } from "./i18n";

/** Use the canonical browser/Intl tag for Sorani Kurdish. */
export function movieIntlLocale(locale: Locale): string {
  if (locale === "ar") return "ar-u-nu-arab";
  return locale === "ku" ? "ckb" : locale;
}

export function formatMovieNumber(value: number, locale: Locale, options?: Intl.NumberFormatOptions): string {
  return new Intl.NumberFormat(movieIntlLocale(locale), options).format(value);
}

export function formatMovieDateTime(value: string | number | Date, locale: Locale): string {
  const date = value instanceof Date ? value : new Date(value);
  if (Number.isNaN(date.getTime())) return "—";
  return new Intl.DateTimeFormat(movieIntlLocale(locale), {
    dateStyle: "medium",
    timeStyle: "short",
  }).format(date);
}

export function formatMovieDate(value: string | number | Date, locale: Locale): string {
  const date = value instanceof Date ? value : new Date(value);
  if (Number.isNaN(date.getTime())) return "—";
  return new Intl.DateTimeFormat(movieIntlLocale(locale), { dateStyle: "medium" }).format(date);
}
