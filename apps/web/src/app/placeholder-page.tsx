"use client";

import Link from "next/link";
import { ArrowLeft, ArrowUpRight, Compass, Sparkles } from "lucide-react";
import { useLocale } from "@/components/LocaleProvider";

export function PlaceholderPage({ name, nameKey }: Readonly<{ name?: string; nameKey?: string }>) {
  const { t } = useLocale();
  const displayName = nameKey ? t(nameKey) : name ?? "Taslim";
  return (
    <div className="placeholder-page">
      <div className="placeholder-panel">
        <div className="placeholder-symbol"><Sparkles size={24} /></div>
        <p className="section-eyebrow">{t("placeholder.eyebrow")}</p>
        <h1>{t("placeholder.title", { name: displayName })}</h1>
        <p>{t("placeholder.description")}</p>
        <div className="placeholder-actions">
          <Link href="/" className="primary-button"><ArrowLeft size={16} /> {t("placeholder.backHome")}</Link>
          <Link href="/#departments" className="secondary-button"><Compass size={16} /> {t("placeholder.explore")} <ArrowUpRight size={15} /></Link>
        </div>
      </div>
    </div>
  );
}
