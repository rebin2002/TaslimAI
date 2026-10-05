import { locales, type Locale } from "./i18n";

export const anonymousLocaleStorageKey = "taslim-locale";

export function accountLocaleStorageKey(userId: string) {
  return `${anonymousLocaleStorageKey}:user:${encodeURIComponent(userId)}`;
}

type LocaleStorageReader = Pick<Storage, "getItem">;
type LocaleStorageWriter = Pick<Storage, "setItem">;

export function readStoredLocale(storage: LocaleStorageReader | null | undefined, key = anonymousLocaleStorageKey): Locale | null {
  try {
    const saved = storage?.getItem(key);
    return saved && locales.includes(saved as Locale) ? (saved as Locale) : null;
  } catch {
    return null;
  }
}

export function writeStoredLocale(storage: LocaleStorageWriter | null | undefined, key: string, locale: Locale) {
  if (!storage) return false;
  try {
    storage.setItem(key, locale);
    return true;
  } catch {
    return false;
  }
}
