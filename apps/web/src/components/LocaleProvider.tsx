"use client";

import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from "react";
import { localeDirection, localeNames, localeTag, locales, translate, type Locale } from "@/lib/i18n";

export type LocaleSource = "default" | "anonymous-storage" | "user" | "account";
type LocaleUpdateOptions = { persist?: boolean; source?: LocaleSource };

type LocaleContextValue = {
  locale: Locale;
  getLocaleSource: () => LocaleSource;
  setLocale: (locale: Locale, options?: LocaleUpdateOptions) => void;
  t: (key: string, variables?: Record<string, string>) => string;
};

const LocaleContext = createContext<LocaleContextValue | null>(null);

function storedLocale(): Locale {
  if (typeof window === "undefined") return "en";
  try {
    const saved = window.localStorage.getItem("taslim-locale") as Locale | null;
    return saved && locales.includes(saved) ? saved : "en";
  } catch {
    return "en";
  }
}

export function LocaleProvider({ children }: Readonly<{ children: React.ReactNode }>) {
  // Keep the first client render identical to the server-rendered English
  // shell. The persisted choice is applied after hydration, which avoids a
  // locale-dependent tree mismatch while still restoring Arabic/Sorani.
  const [locale, setLocaleState] = useState<Locale>("en");
  const localeSource = useRef<LocaleSource>("default");

  useEffect(() => {
    const saved = storedLocale();
    if (saved !== "en") {
      localeSource.current = "anonymous-storage";
      // eslint-disable-next-line react-hooks/set-state-in-effect -- restore persisted UI state after hydration.
      setLocaleState(saved);
    }
  }, []);

  useEffect(() => {
    document.documentElement.lang = localeTag(locale);
    document.documentElement.dir = localeDirection(locale);
  }, [locale]);

  const getLocaleSource = useCallback(() => localeSource.current, []);
  const setLocale = useCallback((next: Locale, options?: LocaleUpdateOptions) => {
    localeSource.current = options?.source ?? "user";
    setLocaleState(next);
    if (options?.persist === false) return;
    try {
      window.localStorage.setItem("taslim-locale", next);
    } catch {
      // Storage can be unavailable; the in-memory locale still applies.
    }
  }, []);

  const value = useMemo(
    () => ({
      locale,
      getLocaleSource,
      setLocale,
      t: (key: string, variables?: Record<string, string>) => translate(locale, key, variables),
    }),
    [getLocaleSource, locale, setLocale],
  );

  return <LocaleContext.Provider value={value}>{children}</LocaleContext.Provider>;
}

export function useLocale() {
  const context = useContext(LocaleContext);
  if (!context) throw new Error("useLocale must be used within LocaleProvider");
  return context;
}

export { localeNames, locales };
