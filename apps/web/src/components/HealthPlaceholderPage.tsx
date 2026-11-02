"use client";

import Link from "next/link";
import { ArrowLeft, ArrowRight, HeartPulse } from "lucide-react";
import { useLocale } from "@/components/LocaleProvider";
import { healthCareCopy, healthCareDirection, healthCareLanguageTag } from "@/lib/healthCareI18n";
import styles from "./HealthCarePage.module.css";

/**
 * Informational only: no health-data input, upload, storage, analysis, or provider
 * integration is available from this page.
 */
export function HealthPlaceholderPage() {
  const { locale } = useLocale();
  const copy = healthCareCopy(locale);
  const BackIcon = locale === "en" ? ArrowLeft : ArrowRight;

  return (
    <div className="placeholder-page" dir={healthCareDirection(locale)} lang={healthCareLanguageTag(locale)}>
      <section
        className={`placeholder-panel ${styles.panel}`}
        aria-labelledby="health-care-title"
        aria-describedby="health-coming-soon health-privacy-note health-disclaimer"
      >
        <div className={styles.symbol} aria-hidden="true"><HeartPulse size={25} strokeWidth={1.8} /></div>
        <p className="section-eyebrow">{copy.eyebrow}</p>
        <h1 id="health-care-title">{copy.title}</h1>
        <p className={styles.intro}>{copy.description}</p>
        <p id="health-coming-soon" className={styles.status} role="status">{copy.status}</p>

        <div className={styles.notes}>
          <p id="health-privacy-note"><strong>{copy.privacyLabel}</strong> {copy.privacyNote}</p>
          <p id="health-disclaimer"><strong>{copy.safetyLabel}</strong> {copy.safetyNote}</p>
        </div>

        <section className={styles.planned} aria-labelledby="health-care-planned-title">
          <h2 id="health-care-planned-title">{copy.plannedHeading}</h2>
          <ul className={styles.featureGrid}>
            {copy.features.map((feature) => (
              <li key={feature.id}>
                <button className={styles.disabledAction} type="button" disabled aria-describedby="health-coming-soon">
                  <span>{feature.label}</span>
                  <small>{copy.actionUnavailable}</small>
                </button>
              </li>
            ))}
          </ul>
        </section>

        <div className={styles.actions}>
          <Link className={styles.backLink} href="/create">
            <BackIcon size={16} aria-hidden="true" />
            <span>{copy.backToCreate}</span>
          </Link>
        </div>
      </section>
    </div>
  );
}
