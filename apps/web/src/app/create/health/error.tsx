"use client";

import { useEffect } from "react";
import Link from "next/link";
import { HeartPulse, RefreshCw } from "lucide-react";
import { useLocale } from "@/components/LocaleProvider";
import { healthCareCopy, healthCareDirection, healthCareLanguageTag } from "@/lib/healthCareI18n";
import styles from "@/components/HealthCarePage.module.css";

export default function HealthCareRouteError({
  error,
  reset,
}: Readonly<{
  error: Error & { digest?: string };
  reset: () => void;
}>) {
  const { locale } = useLocale();
  const copy = healthCareCopy(locale);

  useEffect(() => {
    console.error("Taslim Health Care route recovery", error.digest ? "digest-present" : "digest-unavailable");
  }, [error.digest]);

  return (
    <div className="placeholder-page" dir={healthCareDirection(locale)} lang={healthCareLanguageTag(locale)}>
      <section
        className={`placeholder-panel ${styles.panel}`}
        role="alert"
        aria-live="assertive"
        aria-labelledby="health-care-error-title"
        aria-describedby="health-care-error-description"
      >
        <div className={styles.symbol} aria-hidden="true"><HeartPulse size={25} strokeWidth={1.8} /></div>
        <p className="section-eyebrow">{copy.errorEyebrow}</p>
        <h1 id="health-care-error-title">{copy.errorTitle}</h1>
        <p id="health-care-error-description">{copy.errorDescription}</p>
        <div className={styles.errorActions}>
          <button className={styles.retry} type="button" onClick={reset}>
            <RefreshCw size={15} aria-hidden="true" />
            {copy.retry}
          </button>
          <Link className={styles.backLink} href="/create">{copy.backToCreate}</Link>
        </div>
      </section>
    </div>
  );
}
