import { describe, expect, it } from "vitest";
import { accountLocaleStorageKey, anonymousLocaleStorageKey, readStoredLocale, writeStoredLocale } from "./localePersistence";

function memoryStorage() {
  const values = new Map<string, string>();
  return {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value); },
  };
}

describe("locale persistence", () => {
  it("keeps account preferences in distinct storage keys", () => {
    expect(accountLocaleStorageKey("user/a")).toBe("taslim-locale:user:user%2Fa");
    expect(accountLocaleStorageKey("user/a")).not.toBe(accountLocaleStorageKey("user/b"));
  });

  it("round-trips supported locales and rejects invalid values", () => {
    const storage = memoryStorage();
    expect(writeStoredLocale(storage, anonymousLocaleStorageKey, "ku")).toBe(true);
    expect(readStoredLocale(storage)).toBe("ku");
    expect(writeStoredLocale(storage, accountLocaleStorageKey("user/a"), "ar")).toBe(true);
    expect(writeStoredLocale(storage, accountLocaleStorageKey("user/b"), "en")).toBe(true);
    expect(readStoredLocale(storage, accountLocaleStorageKey("user/a"))).toBe("ar");
    expect(readStoredLocale(storage, accountLocaleStorageKey("user/b"))).toBe("en");
    storage.setItem(anonymousLocaleStorageKey, "fr");
    expect(readStoredLocale(storage)).toBeNull();
  });

  it("fails closed when browser storage is unavailable", () => {
    const unavailable = {
      getItem: () => { throw new Error("blocked"); },
      setItem: () => { throw new Error("blocked"); },
    };
    expect(readStoredLocale(unavailable)).toBeNull();
    expect(writeStoredLocale(unavailable, anonymousLocaleStorageKey, "ar")).toBe(false);
  });
});
