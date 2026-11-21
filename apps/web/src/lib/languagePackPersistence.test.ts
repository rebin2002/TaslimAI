import { describe, expect, it } from "vitest";
import { accountLanguagePackStorageKey, anonymousLanguagePackStorageKey, readStoredLanguagePack, writeStoredLanguagePack } from "./languagePackPersistence";

function memoryStorage() {
  const values = new Map<string, string>();
  return {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value); },
  };
}

describe("language-pack preference persistence", () => {
  it("keeps account preferences isolated and round-trips Simplified Chinese", () => {
    const storage = memoryStorage();
    const ownerKey = accountLanguagePackStorageKey("user/a");
    const otherKey = accountLanguagePackStorageKey("user/b");
    expect(ownerKey).toBe("taslim-language-pack:user:user%2Fa");
    expect(ownerKey).not.toBe(otherKey);
    expect(writeStoredLanguagePack(storage, ownerKey, "zh-CN")).toBe(true);
    expect(readStoredLanguagePack(storage, ownerKey)).toBe("zh-CN");
  });

  it("rejects unknown language packs instead of persisting an unsafe selection", () => {
    const storage = memoryStorage();
    expect(writeStoredLanguagePack(storage, anonymousLanguagePackStorageKey, "fr-FR")).toBe(false);
    storage.setItem(anonymousLanguagePackStorageKey, "fr-FR");
    expect(readStoredLanguagePack(storage)).toBeNull();
  });

  it("fails closed when browser storage is unavailable", () => {
    const unavailable = {
      getItem: () => { throw new Error("blocked"); },
      setItem: () => { throw new Error("blocked"); },
    };
    expect(readStoredLanguagePack(unavailable)).toBeNull();
    expect(writeStoredLanguagePack(unavailable, anonymousLanguagePackStorageKey, "zh-CN")).toBe(false);
  });
});
