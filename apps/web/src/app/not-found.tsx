"use client";

import Link from "next/link";
import { FileQuestion, FolderOpen, House } from "lucide-react";
import { useLocale } from "@/components/LocaleProvider";
import { translateNotFound } from "@/lib/notFoundI18n";

export default function NotFound() {
  const { locale } = useLocale();
  const t = (key: Parameters<typeof translateNotFound>[1]) => translateNotFound(locale, key);

  return (
    <main className="placeholder-page">
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
    </main>
  );
}
