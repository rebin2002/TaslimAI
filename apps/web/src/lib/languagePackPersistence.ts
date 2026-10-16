import { defaultLanguagePackRegistry } from "./languagePackRegistry";
import type { LanguagePackId } from "./languagePackTypes";

export const anonymousLanguagePackStorageKey = "taslim-language-pack";

export function accountLanguagePackStorageKey(userId: string) {
  return `${anonymousLanguagePackStorageKey}:user:${encodeURIComponent(userId)}`;
}

type StorageReader = Pick<Storage, "getItem">;
type StorageWriter = Pick<Storage, "setItem">;

export function readStoredLanguagePack(
  storage: StorageReader | null | undefined,
  key = anonymousLanguagePackStorageKey,
): LanguagePackId | null {
  try {
    const saved = storage?.getItem(key);
    return saved && defaultLanguagePackRegistry.has(saved) ? saved : null;
  } catch {
    return null;
  }
}

export function writeStoredLanguagePack(
  storage: StorageWriter | null | undefined,
  key: string,
  languagePackId: LanguagePackId,
) {
  if (!storage || !defaultLanguagePackRegistry.has(languagePackId)) return false;
  try {
    storage.setItem(key, languagePackId);
    return true;
  } catch {
    return false;
  }
}
