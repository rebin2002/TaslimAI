"use client";

import { useEffect, useRef } from "react";
import Link from "next/link";
import { HeartPulse, RefreshCw } from "lucide-react";
import { useLocale } from "@/components/LocaleProvider";
import { restoreRecoveryFocus } from "@/lib/recoveryFocus";
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
  const panelRef = useRef<HTMLElement | null>(null);

  useEffect(() => {
    console.error("Taslim Health Care route recovery", error.digest ? "digest-present" : "digest-unavailable");
  }, [error.digest]);

  // Recovery focus restoration. React unmounts the crashed Health Care preview
  // together with the element that held focus, so focus silently drops to
  // document.body; keyboard and screen-reader users would then have to
  // re-traverse the app shell to reach "Try again", and the assertive
  // recovery message is easy to miss. The shared decision moves focus onto
  // the panel only when it is not already inside it - the same restore used
  // by the app, Music Studio, Document Studio, and Personal Health
  // boundaries. It is keyed on the error identity rather than the digest
  // because a retry that fails again can report the same digest, which would
  // skip the restore on exactly the attempt the user retried.
  useEffect(() => {
    restoreRecoveryFocus(panelRef.current, typeof document === "undefined" ? null : document.activeElement);
  }, [error]);

  return (
    <div className="placeholder-page" dir={healthCareDirection(locale)} lang={healthCareLanguageTag(locale)}>
      <section
        ref={panelRef}
        className={`placeholder-panel ${styles.panel}`}
        tabIndex={-1}
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
