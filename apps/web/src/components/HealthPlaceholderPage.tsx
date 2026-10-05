"use client";

import Link from "next/link";
import { ArrowLeft, ArrowUpRight, Compass, ShieldCheck } from "lucide-react";
import { useLocale } from "@/components/LocaleProvider";

export function HealthPlaceholderPage() {
  const { t } = useLocale();

  return (
    <div className="placeholder-page">
      <section className="placeholder-panel health-placeholder-panel" aria-labelledby="health-placeholder-title" aria-describedby="health-privacy-note health-disclaimer">
        <div className="placeholder-symbol health-placeholder-symbol" aria-hidden="true"><ShieldCheck size={24} /></div>
        <p className="section-eyebrow">{t("health.eyebrow")}</p>
        <h1 id="health-placeholder-title">{t("health.title")}</h1>
        <p>{t("health.description")}</p>
        <div className="health-placeholder-notes">
          <p id="health-privacy-note"><strong>{t("health.privacyLabel")}</strong> {t("health.privacyNote")}</p>
          <p id="health-disclaimer"><strong>{t("health.disclaimerLabel")}</strong> {t("health.disclaimer")}</p>
        </div>
        <div className="placeholder-actions">
          <Link href="/" className="primary-button"><ArrowLeft size={16} aria-hidden="true" /> {t("placeholder.backHome")}</Link>
          <Link href="/#departments" className="secondary-button"><Compass size={16} aria-hidden="true" /> {t("placeholder.explore")} <ArrowUpRight size={15} aria-hidden="true" /></Link>
        </div>
      </section>
    </div>
  );
}
