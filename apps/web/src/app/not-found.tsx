"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { FileQuestion, FolderOpen, House } from "lucide-react";
import { useOptionalLocale } from "@/components/LocaleProvider";
import { readBrowserRootErrorLocale } from "@/lib/globalErrorI18n";
import { localeDirection, localeTag, type Locale } from "@/lib/i18n";
import { translateNotFound } from "@/lib/notFoundI18n";

export default function NotFound() {
  /**
   * Next.js can render this root not-found boundary outside `layout.tsx` - the
   * legacy unrecognized-Server-Action fallback does (#697) - which means no
   * LocaleProvider ancestor and no AppShell `<main>`. Keep the provider
   * authoritative wherever it is present, but never let recovery itself throw
   * `useLocale must be used within LocaleProvider` and turn a 404 into a 500.
   */
  const localeContext = useOptionalLocale();
  // First paint is the English server shell in every render path, so client
  // hydration always sees the tree the server serialized; the browser locale is
  // restored afterwards, exactly as LocaleProvider does for its own subtree.
  const [fallbackLocale, setFallbackLocale] = useState<Locale>("en");

  useEffect(() => {
    if (localeContext) return;
    const recovered = readBrowserRootErrorLocale();
    if (recovered !== "en") {
      // eslint-disable-next-line react-hooks/set-state-in-effect -- restore the browser locale after hydration, keeping SSR markup deterministic.
      setFallbackLocale(recovered);
    }
  }, [localeContext]);

  const locale = localeContext?.locale ?? fallbackLocale;
  const t = (key: Parameters<typeof translateNotFound>[1]) => translateNotFound(locale, key);

  return (
    <div className="placeholder-page" lang={localeTag(locale)} dir={localeDirection(locale)}>
      <section className="placeholder-panel" aria-labelledby="not-found-title" aria-describedby="not-found-description">
        <div className="placeholder-symbol" aria-hidden="true"><FileQuestion size={24} /></div>
        <p className="section-eyebrow">{t("eyebrow")}</p>
        <h1 id="not-found-title">{t("title")}</h1>
        <p id="not-found-description">{t("description")}</p>
        <div className="placeholder-actions">
          <Link href="/" className="primary-button"><House size={16} aria-hidden="true" /> {t("backHome")}</Link>
          <Link href="/assets" className="secondary-button"><FolderOpen size={16} aria-hidden="true" /> {t("openAssets")}</Link>
        </div>
      </section>
    </div>
  );
}
