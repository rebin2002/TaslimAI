"use client";

import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from "react";
import { localeNames, locales, type Locale } from "@/lib/i18n";
import { readStoredLocale } from "@/lib/localePersistence";
import { anonymousLanguagePackStorageKey, readStoredLanguagePack, writeStoredLanguagePack } from "@/lib/languagePackPersistence";
import { defaultLanguagePackRegistry, languagePackDirection, languagePackIds, languagePackManifest, languagePackNames, languagePackTag, legacyLocaleForLanguagePack, translateLanguagePack } from "@/lib/languagePackRegistry";
import type { LanguagePackId } from "@/lib/languagePackTypes";

export type LocaleSource = "default" | "anonymous-storage" | "user" | "account";
type LocaleUpdateOptions = { persist?: boolean; source?: LocaleSource };

type LocaleContextValue = {
  locale: Locale;
  languagePackId: LanguagePackId;
  languagePackIds: readonly LanguagePackId[];
  languagePackNames: Readonly<Record<LanguagePackId, string>>;
  languagePackManifest: ReturnType<typeof languagePackManifest>;
  getLocaleSource: () => LocaleSource;
  setLocale: (locale: Locale, options?: LocaleUpdateOptions) => void;
  setLanguagePack: (languagePackId: LanguagePackId, options?: LocaleUpdateOptions) => void;
  t: (key: string, variables?: Record<string, string>) => string;
};

const LocaleContext = createContext<LocaleContextValue | null>(null);

function storedLanguagePack(): LanguagePackId {
  if (typeof window === "undefined") return "en";
  const savedPack = readStoredLanguagePack(window.localStorage);
  if (savedPack) return savedPack;
  // Migrate the existing anonymous legacy preference without changing its key.
  return readStoredLocale(window.localStorage) ?? "en";
}

export function LocaleProvider({ children }: Readonly<{ children: React.ReactNode }>) {
  // Keep the first client render identical to the server-rendered English shell.
  // The persisted choice is applied after hydration to avoid a locale-dependent
  // tree mismatch while still restoring legacy and installable language packs.
  const [languagePackId, setLanguagePackState] = useState<LanguagePackId>("en");
  const [locale, setLocaleState] = useState<Locale>("en");
  const localeSource = useRef<LocaleSource>("default");

  useEffect(() => {
    const saved = storedLanguagePack();
    if (saved !== "en") {
      localeSource.current = "anonymous-storage";
      // eslint-disable-next-line react-hooks/set-state-in-effect -- restore persisted UI state after hydration.
      setLanguagePackState(saved);
      setLocaleState(legacyLocaleForLanguagePack(saved));
    }
  }, []);

  useEffect(() => {
    document.documentElement.lang = languagePackTag(languagePackId);
    document.documentElement.dir = languagePackDirection(languagePackId);
  }, [languagePackId]);

  const getLocaleSource = useCallback(() => localeSource.current, []);
  const setLanguagePack = useCallback((requested: LanguagePackId, options?: LocaleUpdateOptions) => {
    const next = defaultLanguagePackRegistry.has(requested) ? requested : "en";
    localeSource.current = options?.source ?? "user";
    setLanguagePackState(next);
    setLocaleState(legacyLocaleForLanguagePack(next));
    if (options?.persist === false) return;
    if (typeof window !== "undefined") writeStoredLanguagePack(window.localStorage, anonymousLanguagePackStorageKey, next);
  }, []);
  const setLocale = useCallback((next: Locale, options?: LocaleUpdateOptions) => {
    setLanguagePack(next, options);
  }, [setLanguagePack]);

  const value = useMemo(
    () => ({
      locale,
      languagePackId,
      languagePackIds,
      languagePackNames,
      languagePackManifest: languagePackManifest(languagePackId),
      getLocaleSource,
      setLocale,
      setLanguagePack,
      t: (key: string, variables?: Record<string, string>) => translateLanguagePack(languagePackId, key, variables),
    }),
    [getLocaleSource, languagePackId, locale, setLanguagePack, setLocale],
  );

  return <LocaleContext.Provider value={value}>{children}</LocaleContext.Provider>;
}

export function useLocale() {
  const context = useContext(LocaleContext);
  if (!context) throw new Error("useLocale must be used within LocaleProvider");
  return context;
}

export { languagePackIds, languagePackNames, localeNames, locales };
