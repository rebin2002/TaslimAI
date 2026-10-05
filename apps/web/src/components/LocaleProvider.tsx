"use client";

import { createContext, useCallback, useContext, useEffect, useMemo, useState } from "react";
import { localeDirection, localeNames, locales, translate, type Locale } from "@/lib/i18n";

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
  const [locale, setLocaleState] = useState<Locale>(storedLocale);

  // Only the document is updated here. Persisting must never happen on mount:
  // React reuses the server-rendered default during hydration, so writing the
  // current locale from this effect overwrote a saved Arabic or Kurdish choice
  // with "en" on every fresh page load and the document silently fell back to
  // left-to-right.
  useEffect(() => {
    document.documentElement.lang = locale;
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
