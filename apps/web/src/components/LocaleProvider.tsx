"use client";

import { createContext, useCallback, useContext, useEffect, useMemo, useState } from "react";
import { localeDirection, localeNames, localeTag, locales, translate, type Locale } from "@/lib/i18n";

type LocaleContextValue = {
  locale: Locale;
  setLocale: (locale: Locale, options?: { persist?: boolean }) => void;
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

  useEffect(() => {
    const saved = storedLocale();
    // eslint-disable-next-line react-hooks/set-state-in-effect -- restore persisted UI state after hydration.
    if (saved !== "en") setLocaleState(saved);
  }, []);

  useEffect(() => {
    document.documentElement.lang = localeTag(locale);
    document.documentElement.dir = localeDirection(locale);
  }, [locale]);

  const setLocale = useCallback((next: Locale, options?: { persist?: boolean }) => {
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
      setLocale,
      t: (key: string, variables?: Record<string, string>) => translate(locale, key, variables),
    }),
    [locale, setLocale],
  );

  return <LocaleContext.Provider value={value}>{children}</LocaleContext.Provider>;
}

export function useLocale() {
  const context = useContext(LocaleContext);
  if (!context) throw new Error("useLocale must be used within LocaleProvider");
  return context;
}

export { localeNames, locales };
