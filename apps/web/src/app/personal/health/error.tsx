"use client";

import { useEffect } from "react";
import Link from "next/link";
import { RefreshCw } from "lucide-react";
import { useLocale } from "@/components/LocaleProvider";

/**
 * Keep unexpected Health render failures generic. Error objects can contain
 * provider, request, or user-content details and must never be rendered here.
 */
export default function HealthRouteError({
  error,
  reset,
}: {
  error: Error & { digest?: string };
  reset: () => void;
}) {
  const { t } = useLocale();

  useEffect(() => {
    console.error("Taslim health route recovery", error.digest ? "digest-present" : "digest-unavailable");
  }, [error.digest]);

  return (
    <main className="placeholder-page">
      <section className="placeholder-panel" role="alert" aria-live="assertive" aria-labelledby="health-error-title" aria-describedby="health-error-description">
        <RefreshCw className="placeholder-symbol" size={28} aria-hidden="true" />
        <p className="section-eyebrow">{t("health.eyebrow")}</p>
        <h1 id="health-error-title">{t("error.title")}</h1>
        <p id="health-error-description">{t("error.description")}</p>
        <div className="placeholder-actions">
          <button className="primary-button" type="button" onClick={reset}>
            <RefreshCw size={15} aria-hidden="true" /> {t("error.retry")}
          </button>
          <Link className="secondary-button" href="/">{t("placeholder.backHome")}</Link>
        </div>
      </section>
    </main>
  );
}
